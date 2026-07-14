#pragma warning disable MAAI001 // experimental MAF AIContextProvider.InvokingContext/InvokedContext ctors; inventoried in Internal/ExperimentalApiSuppressions.cs
#pragma warning disable TA001 // IDurableToolSource is experimental; internal consumption here is intentional
using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Temporalio.Activities;
using Temporalio.Exceptions;
using TemporalCommunity.Extensions.Agents.Approvals;
using TemporalCommunity.Extensions.Agents.Internal;
using TemporalCommunity.Extensions.Agents.Session;
using TemporalCommunity.Extensions.Agents.State;
using TemporalCommunity.Extensions.Agents.Tools;
using TemporalCommunity.Extensions.AI.Approvals;
using TemporalCommunity.Extensions.AI.Exceptions;
using TemporalCommunity.Extensions.AI.Session;
using TemporalCommunity.Extensions.AI.Tools;
using Temporalio.Workflows;

namespace TemporalCommunity.Extensions.Agents.Workflows;

/// <summary>
/// Immutable blueprint for a durable agent registered via <c>TemporalAgentsOptions.AddDurableAgent</c>.
/// Computed once at first activity dispatch (lazy) and reused for the lifetime of the worker.
/// Contains only things that depend on registration <em>shape</em>, not live DI instances:
/// the tool registry, frozen per-tool activity options, structural chain-walk booleans, and the
/// source-of-truth <see cref="DurableAgentRegistration"/>. Live DI instances
/// (<see cref="IChatClient"/>, context providers, tool interceptor, approval scope
/// store) are resolved fresh per activity call via an <see cref="IServiceScope"/> so that scoped
/// services (e.g. <c>DbContext</c>) are never captured as implicit captive singletons.
/// </summary>
/// <param name="Tools">Resolved per-agent tool registry keyed by case-insensitive name.</param>
/// <param name="Registration">Source-of-truth registration snapshot from the builder.</param>
/// <param name="AgentsOptions">Reference to the shared agents-options snapshot.</param>
/// <param name="SuppressAgentTurnSpan">
/// Step 3c.3 (2b-enriched OTel): when <see langword="true"/>, the activity skips emitting its
/// own <c>agent.turn</c> span and instead tags <c>Activity.Current</c> with the
/// Temporal-namespaced correlation ID — deferring the canonical GenAI span to MAF's
/// <c>OpenTelemetryAgent</c> (or MEAI's <c>OpenTelemetryChatClient</c>) if either is present in
/// the pipeline. Computed once at blueprint-build time via <c>AgentChainWalker</c> so the per-turn
/// dispatch path does no extra walks.
/// </param>
internal sealed record AgentBlueprint(
    IReadOnlyDictionary<string, AIFunction> Tools,
    DurableAgentRegistration Registration,
    TemporalAgentsOptions AgentsOptions,
    bool SuppressAgentTurnSpan,
    IReadOnlyList<AITool> ToolsAsAITools);

/// <summary>
/// Temporal activities that perform the actual AI inference for agent sessions.
/// All AI inference must run inside an activity to preserve workflow determinism.
/// </summary>
internal sealed class AgentActivities(
    IServiceProvider services,
    IServiceScopeFactory serviceScopeFactory,
    ILoggerFactory? loggerFactory = null)
{
    private readonly ILogger _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<AgentActivities>();

    // Per-durable-agent blueprint cache. Computed lazily at first dispatch and reused for the
    // lifetime of the worker. Contains only frozen config (tool registry, chain-walk booleans,
    // registration shape) — no live DI instances. Live instances are resolved per call via scope.
    private readonly ConcurrentDictionary<string, AgentBlueprint> _blueprintCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Builds the activity summary value (visible in the Temporal Web UI activity list).
    /// Uses the agent name when available; returns null otherwise so the SDK omits the field.
    /// </summary>
    internal static string? BuildActivitySummary(string? agentName) =>
        string.IsNullOrWhiteSpace(agentName) ? null : agentName;

    /// <summary>
    /// Applies a keyed history reducer to the supplied history list and returns the result.
    /// Dispatched by <see cref="AgentWorkflow"/> at continue-as-new time when
    /// <see cref="TemporalCommunity.Extensions.AI.DurableChatWorkflowInput.HistoryReducerKey"/> is set.
    /// The reducer delegate is resolved from DI via <see cref="IServiceProvider.GetKeyedService{T}"/>,
    /// applied to the history, and the trimmed list is returned to the workflow.
    /// </summary>
    [Activity("TemporalCommunity.Extensions.Agents.ReduceHistoryByKey")]
    public Task<List<DurableSessionEntry>> ReduceHistoryByKeyAsync(
        TemporalCommunity.Extensions.AI.ReduceHistoryByKeyInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var reducer = services.GetKeyedService<
            Func<IList<DurableSessionEntry>, IList<DurableSessionEntry>>>(
            input.ReducerKey)
            ?? throw new InvalidOperationException(
                $"No history reducer registered under key '{input.ReducerKey}'. " +
                $"Register a Func<IList<DurableSessionEntry>, IList<DurableSessionEntry>> " +
                $"via services.AddKeyedSingleton(\"{input.ReducerKey}\", ...).");

        var result = reducer(input.History);
        return Task.FromResult(result as List<DurableSessionEntry> ?? result.ToList());
    }

    /// <summary>
    /// Step activity used by durable agents. Performs ONE LLM call without invoking any tools.
    /// Returns either a final assistant message or one or more <see cref="FunctionCallContent"/>
    /// items that the workflow then dispatches in parallel as separate
    /// <c>TemporalCommunity.Extensions.Agents.InvokeAgentTool</c> activities.
    /// </summary>
    [Activity("TemporalCommunity.Extensions.Agents.RunDurableAgentStep")]
    public async Task<AgentStepResult> RunDurableAgentStepAsync(AgentStepInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var ctx = ActivityExecutionContext.Current;
        var ct = ctx.CancellationToken;

        var blueprint = ResolveBlueprint(input.AgentName);
        var registration = blueprint.Registration;
        var agentsOptions = blueprint.AgentsOptions;

        // Resolve all live DI instances fresh per call from a scoped service provider.
        // The scope is disposed at the end of this activity call, ensuring scoped services
        // (e.g. DbContext) are not captured as captive singletons.
        using var scope = serviceScopeFactory.CreateScope();
        var scopedServices = scope.ServiceProvider;

        var chatClient = registration.ChatClient(scopedServices);
        AIContextProvider[] contextProviders = registration.ContextProviderFactories.Count == 0
            ? []
            : registration.ContextProviderFactories.Select(f => f(scopedServices)).ToArray();

        var interceptorFactory = registration.ToolInterceptorFactory ?? agentsOptions.DefaultToolInterceptor;
        var toolInterceptor = interceptorFactory?.Invoke(scopedServices);

        // Build a fresh AIAgent from the scoped IChatClient (and optional decorator pipeline).
        var agent = BuildLiveAgent(blueprint, chatClient, scopedServices);

        // When the workflow was started by a proxy-only client, resolve
        // and return worker-side settings so the workflow can patch its input on the first turn.
        Dictionary<string, ActivityOptions>? resolvedToolOpts = null;
        if (input.NeedsWorkerSettingsResolution)
        {
            var effectiveActivityTimeout = registration.ActivityTimeout
                ?? agentsOptions.DefaultActivityTimeout;
            var effectiveHeartbeatTimeout = registration.HeartbeatTimeout
                ?? agentsOptions.DefaultHeartbeatTimeout;
            var effectiveRetryPolicy = registration.RetryPolicy
                ?? agentsOptions.DefaultRetryPolicy;

            resolvedToolOpts = DefaultTemporalAgentClient.BuildDurableAgentToolActivityOptions(
                registration,
                effectiveActivityTimeout,
                effectiveHeartbeatTimeout,
                effectiveRetryPolicy);
        }
        var sessionId = input.SessionId ?? TemporalAgentSessionId.Parse(ctx.Info.WorkflowId!);

        // Restore the StateBag so AIContextProvider state survives across step iterations.
        var session = TemporalAgentSession.FromStateBag(sessionId, input.SerializedStateBag);

        IReadOnlyList<ChatMessage> messagesForLlm = input.AccumulatedMessages;

        var chatOptions = registration.ChatOptions?.Clone() ?? new ChatOptions();
        chatOptions.Instructions = registration.Instructions;
        // Spread [..] makes a per-call copy so downstream mutation (EnableToolNames filter below)
        // cannot corrupt the cached IReadOnlyList.
        chatOptions.Tools = blueprint.ToolsAsAITools.Count > 0 ? [.. blueprint.ToolsAsAITools] : null;
        chatOptions.ResponseFormat = input.Request.ResponseFormat;

        if (!input.Request.EnableToolCalls)
        {
            chatOptions.Tools = null;
        }
        else if (input.Request.EnableToolNames is { Count: > 0 } enabledNames && chatOptions.Tools is not null)
        {
            chatOptions.Tools = [.. chatOptions.Tools.Where(t => enabledNames.Contains(t.Name, StringComparer.OrdinalIgnoreCase))];
        }

        // LLM call goes through agent.RunStreamingAsync (NOT chatClient directly),
        // so any DelegatingAIAgent decorators the user added via ConfigureAgentPipeline fire
        // around the call. The agent is built fresh per call via BuildLiveAgent above.

        var augmentedMessages = messagesForLlm;
        if (contextProviders.Length > 0)
        {
            // Seed the aggregated context with the current chatOptions state so providers see the
            // agent's registered instructions and tools as the starting point — matching
            // ChatClientAgent.cs:774-779 (MAF's PrepareSessionAndMessagesAsync).
            // Each provider receives the PREVIOUS provider's output via InvokingContext, so provider
            // N+1 sees provider N's contributions to Messages, Instructions, and Tools. This is the
            // chaining pattern from ChatClientAgent.cs:784 (`aiContext = await provider.InvokingAsync(...)`).
            var aggregated = new Microsoft.Agents.AI.AIContext
            {
                Messages = messagesForLlm,
                Instructions = chatOptions.Instructions,
            };

            // Track the first provider that returns tools so we can emit a single targeted warning.
            string? firstToolProviderType = null;
            int firstToolCount = 0;

            foreach (var provider in contextProviders)
            {
                var invokingCtx = new Microsoft.Agents.AI.AIContextProvider.InvokingContext(
                    agent, session, aggregated);
                aggregated = await provider.InvokingAsync(invokingCtx, ct).ConfigureAwait(false);

                // Strip tools from IDurableToolSource providers immediately after their iteration.
                // Their tools are already registered as durable activities; leaving them in aggregated
                // would contaminate downstream providers' InvokingContext and cause the LogError sentinel
                // to fire on the wrong provider.
                // AIContext is a sealed class (not a record), so use explicit property copy instead of `with`.
                if (provider is IDurableToolSource)
                    aggregated = new Microsoft.Agents.AI.AIContext
                    {
                        Instructions = aggregated.Instructions,
                        Messages = aggregated.Messages,
                        Tools = null,
                    };

                // Capture the first non-IDurableToolSource provider that returned tools for the per-turn warning below.
                if (firstToolProviderType is null && provider is not IDurableToolSource
                    && aggregated.Tools is { } tools)
                {
                    var count = tools is ICollection<Microsoft.Extensions.AI.AITool> c ? c.Count : tools.Count();
                    if (count > 0)
                    {
                        firstToolProviderType = provider.GetType().Name;
                        firstToolCount = count;
                    }
                }
            }

            // Apply the final aggregated messages. AIContext.Messages is already materialized by
            // the last provider in the chain; no need to enumerate intermediate contexts.
            augmentedMessages = aggregated.Messages != null
                ? (aggregated.Messages as IReadOnlyList<ChatMessage> ?? aggregated.Messages.ToList())
                : messagesForLlm;

            // Apply the final aggregated instructions to chatOptions so the LLM call sees them.
            // Provider instructions replace (not append to) the agent's registered instructions,
            // matching ChatClientAgent.cs:797-801 (MAF pattern). The agent's own instructions are
            // already in aggregated.Instructions via the seed above — providers may extend them.
            if (aggregated.Instructions is not null)
            {
                chatOptions.Instructions = aggregated.Instructions;
            }

            // Provider-contributed tools are NOT dispatched as durable activities and are ignored,
            // unless the provider implements IDurableToolSource (in which case tools were stripped
            // above and are already registered as durable activities).
            // Emit one LogError per turn (not per provider, not per tool) when any non-IDurableToolSource
            // provider returned tools — this is a misconfiguration: a registered feature is completely
            // non-functional until the provider is updated. IDurableToolSource providers are excluded.
            if (firstToolProviderType is not null)
            {
                _logger.LogError(
                    "Context provider {ProviderType} returned {ToolCount} tool(s) for agent {AgentName}. " +
                    "Provider-contributed tools are not dispatched as durable activities and are ignored. " +
                    "To register these tools with durable execution: " +
                    "(a) implement IDurableToolSource on the provider type and use AddContextProvider(provider), or " +
                    "(b) pass tools explicitly via AddContextProvider(provider, durableTools: [new DurableToolRegistrationSpec(yourTool, opts => opts.NoRetry())]).",
                    firstToolProviderType, firstToolCount, input.AgentName);
            }
        }

        var temporalContext = new TemporalAgentContext(ctx.TemporalClient, session, scopedServices);
        TemporalAgentContext.SetCurrent(temporalContext);

        // When the user's pipeline installs OpenTelemetryAgent or
        // OpenTelemetryChatClient, suppress our own agent.turn span to avoid duplicate gen_ai.*
        // attributes (downstream cost-aggregation queries would double-count tokens). Instead
        // tag Activity.Current — which will be MAF's invoke_agent span when present, or the
        // Temporal SDK's RunActivity span otherwise — with the Temporal-namespaced correlation
        // ID so the canonical GenAI semconv data (from MAF) carries our additive context too.
        // The `using var` keeps disposal correct: when suppressed, span is null and the using
        // statement is a no-op; when emitted, the span is disposed at method exit.
        using var span = blueprint.SuppressAgentTurnSpan
            ? null
            : TemporalAgentTelemetry.ActivitySource.StartActivity(
                TemporalAgentTelemetry.AgentTurnSpanName,
                ActivityKind.Client);

        if (span is not null)
        {
            span.SetTag(TemporalAgentTelemetry.AgentNameAttribute, input.AgentName);
            span.SetTag(TemporalAgentTelemetry.AgentSessionIdAttribute, sessionId.WorkflowId);
            span.SetTag(TemporalAgentTelemetry.AgentCorrelationIdAttribute, input.Request.CorrelationId);
        }
        else
        {
            // Survives suppression by attaching the Temporal-namespaced correlation ID to the
            // canonical span the user's OpenTelemetryAgent / OpenTelemetryChatClient emitted.
            Activity.Current?.SetTag(
                TemporalAgentTelemetry.AgentCorrelationIdAttribute,
                input.Request.CorrelationId);
        }

        try
        {
            _logger.LogAgentActivityStarted(input.AgentName, sessionId.WorkflowId);

            // Carry the turn's ChatOptions through ChatClientAgentRunOptions so the inner
            // ChatClientAgent uses them (tools, response format, etc.). The options flow through
            // any user-installed DelegatingAIAgent decorators unchanged.
            var runOptions = new ChatClientAgentRunOptions
            {
                ChatOptions = chatOptions,
            };

            var collected = new List<AgentResponseUpdate>();
            // MAF's ChatClientAgent.PrepareSessionAndMessagesAsync requires `session is
            // ChatClientAgentSession` and that class is sealed — our TemporalAgentSession
            // cannot satisfy that contract. Pass null so MAF mints a fresh transient
            // ChatClientAgentSession per turn. Our own session state (StateBag,
            // AIContextProvider invocation) is managed outside MAF and passed explicitly
            // via the TemporalAgentSession to context providers at the call site above
            // (AIContextProvider.InvokingContext) and via TemporalAgentContext for tools.
            await foreach (var update in agent.RunStreamingAsync(
                    augmentedMessages, session: null, runOptions, ct).WithCancellation(ct).ConfigureAwait(false))
            {
                collected.Add(update);
                ctx.Heartbeat(update.Text);
            }

            var response = collected.ToAgentResponse();
            var assistantMessage = response.Messages.Count > 0
                ? response.Messages[^1]
                : new ChatMessage(ChatRole.Assistant, string.Empty);

            var toolCalls = assistantMessage.Contents
                .OfType<FunctionCallContent>()
                .ToList();

            if (span?.IsAllDataRequested == true)
            {
                span.SetTag(TemporalAgentTelemetry.InputTokensAttribute, response.Usage?.InputTokenCount);
                span.SetTag(TemporalAgentTelemetry.OutputTokensAttribute, response.Usage?.OutputTokenCount);
                span.SetTag(TemporalAgentTelemetry.TotalTokensAttribute, response.Usage?.TotalTokenCount);
            }

            _logger.LogAgentActivityCompleted(input.AgentName, sessionId.WorkflowId,
                response.Usage?.InputTokenCount, response.Usage?.OutputTokenCount, response.Usage?.TotalTokenCount);

            if (contextProviders.Length > 0)
            {
                var invokedCtx = new Microsoft.Agents.AI.AIContextProvider.InvokedContext(
                    agent,
                    session,
                    requestMessages: augmentedMessages,
                    responseMessages: response.Messages);
                foreach (var provider in contextProviders)
                {
                    await provider.InvokedAsync(invokedCtx, ct).ConfigureAwait(false);
                }
            }

            var serializedStateBag = session.SerializeStateBag();
            var isFinal = toolCalls.Count == 0;


            int? resolvedMaxToolCalls = input.NeedsWorkerSettingsResolution
                ? registration.MaxToolCallsPerTurn
                : null;


            // Bundle resolved settings into ProxyResolvedWorkerConfig only when this was a
            // resolution-request step (NeedsWorkerSettingsResolution). Non-resolution steps return
            // null for the config; consumer forwarding properties handle the null safely.
            ProxyResolvedWorkerConfig? resolvedConfig = null;
            if (input.NeedsWorkerSettingsResolution)
            {
                // Feature L: build interceptor-related lists from tool registrations.
                ActivityOptions? interceptorActivityOpts = null;
                List<string>? interceptorSkippedTools = null;
                List<string>? requiresApprovalTools = null;
                List<string>? scopeAwareTools = null;
                List<string>? scopeAwareApprovalTools = null;

                // requiresApprovalTools is populated unconditionally — RequireApproval() is an
                // absolute floor that must be enforced even when no tool interceptor is registered
                // (BLOCK-2 fix). Only interceptorActivityOpts and the skip list need the interceptor guard.
                // Feature B: RequiresApprovalTools exclusion guard — scope-aware required tools go into
                // scopeAwareApprovalTools, NOT requiresApprovalTools (spec Section 13, critical guard).
                foreach (var toolReg in registration.Tools)
                {
                    var toolOpts = toolReg.Options;
                    if (toolOpts.RequireApprovalFlag && !toolOpts.ScopeAwareFlag)
                    {
                        (requiresApprovalTools ??= new List<string>()).Add(toolReg.Name);
                    }

                    if (toolOpts.ScopeAwareFlag)
                    {
                        (scopeAwareTools ??= new List<string>()).Add(toolReg.Name);
                        if (toolOpts.RequireApprovalFlag)
                        {
                            (scopeAwareApprovalTools ??= new List<string>()).Add(toolReg.Name);
                        }
                    }
                }

                // Feature B: DefaultToolInterceptor incompatibility check at proxy-start resolution.
                if (registration.UseApprovalScopes && agentsOptions.DefaultToolInterceptor is not null)
                {
                    throw new InvalidOperationException(
                        "UseApprovalScopes() cannot be combined with TemporalAgentsOptions.DefaultToolInterceptor. " +
                        "This release does not compose approval scopes with worker-default tool interceptors. " +
                        "Remove DefaultToolInterceptor from TemporalAgentsOptions or do not call UseApprovalScopes() on this agent.");
                }

                // Feature B: startup validation for scope-aware required tools at proxy-start resolution.
                foreach (var toolReg in registration.Tools)
                {
                    var toolOpts = toolReg.Options;
                    if (toolOpts.RequireApprovalFlag && toolOpts.ScopeAwareFlag && !registration.UseApprovalScopes)
                    {
                        throw new InvalidOperationException(
                            $"Tool '{toolReg.Name}' has ScopeAware() set but approval scopes are not enabled on agent '{registration.Name}'. " +
                            "Call UseApprovalScopes() before registering scope-aware required tools.");
                    }

                    if (toolOpts.RequireApprovalFlag && toolOpts.ScopeAwareFlag && toolOpts.SkipInterceptorFlag)
                    {
                        throw new InvalidOperationException(
                            $"Tool '{toolReg.Name}' cannot combine RequireApproval(), ScopeAware(), and SkipInterceptor(); approval " +
                            "scopes require the interceptor to enforce the missing-scope approval gate.");
                    }
                }

                Dictionary<string, ActivityOptions>? perToolInterceptorOpts = null;

                if (toolInterceptor is not null)
                {
                    var effectiveTimeout = registration.ActivityTimeout
                        ?? agentsOptions.DefaultActivityTimeout;
                    var effectiveHeartbeat = registration.HeartbeatTimeout
                        ?? agentsOptions.DefaultHeartbeatTimeout;
                    var effectiveRetry = registration.RetryPolicy
                        ?? agentsOptions.DefaultRetryPolicy;

                    interceptorActivityOpts = new ActivityOptions
                    {
                        StartToCloseTimeout = effectiveTimeout,
                        HeartbeatTimeout = effectiveHeartbeat,
                        RetryPolicy = effectiveRetry,
                        // Summary is set per-tool at dispatch time: $"intercept:{toolName}"
                    };

                    foreach (var toolReg in registration.Tools)
                    {
                        if (toolReg.Options.SkipInterceptorFlag)
                        {
                            (interceptorSkippedTools ??= new List<string>()).Add(toolReg.Name);
                        }

                        // Wire per-tool interceptor timeout when set (F2 fix).
                        if (toolReg.Options.InterceptorTimeout.HasValue)
                        {
                            perToolInterceptorOpts ??= new Dictionary<string, ActivityOptions>(StringComparer.OrdinalIgnoreCase);
                            perToolInterceptorOpts[toolReg.Name] = new ActivityOptions
                            {
                                StartToCloseTimeout = toolReg.Options.InterceptorTimeout,
                                HeartbeatTimeout = effectiveHeartbeat,
                                RetryPolicy = effectiveRetry,
                            };
                        }
                    }
                }

                // Feature B: resolve approval-scopes config for proxy-start resolution.
                var useApprovalScopes = registration.UseApprovalScopes;
                bool useApprovalScopeStoreMode = false;
                string? alwaysScopesStoreKey = null;
                bool applyAlwaysScopesAtSessionStart = false;
                int maxAlwaysScopeCacheRecords = 0;
                int maxAlwaysScopeCacheBytes = 0;
                TimeSpan approvalScopeActivityTimeout = TimeSpan.Zero;
                int approvalScopeActivityMaximumAttempts = 0;

                if (useApprovalScopes)
                {
                    var scopeOpts = registration.ApprovalScopesOptions!;

                    // Options validation (positive bounds) — same as direct-start path.
                    if (scopeOpts.MaxAlwaysScopeCacheRecords <= 0)
                        throw new InvalidOperationException($"ApprovalScopesOptions.MaxAlwaysScopeCacheRecords for agent '{registration.Name}' must be a positive integer.");
                    if (scopeOpts.MaxAlwaysScopeCacheBytes <= 0)
                        throw new InvalidOperationException($"ApprovalScopesOptions.MaxAlwaysScopeCacheBytes for agent '{registration.Name}' must be a positive integer.");
                    if (scopeOpts.ApprovalScopeActivityMaximumAttempts <= 0)
                        throw new InvalidOperationException($"ApprovalScopesOptions.ApprovalScopeActivityMaximumAttempts for agent '{registration.Name}' must be a positive integer.");
                    if (scopeOpts.ApprovalScopeActivityTimeout <= TimeSpan.Zero)
                        throw new InvalidOperationException($"ApprovalScopesOptions.ApprovalScopeActivityTimeout for agent '{registration.Name}' must be greater than TimeSpan.Zero.");

                    useApprovalScopeStoreMode = scopeOpts.ApprovalScopeStore is not null
                                             || agentsOptions.ApprovalScopeStore is not null;
                    alwaysScopesStoreKey = scopeOpts.AlwaysScopesStoreKey;
                    applyAlwaysScopesAtSessionStart = scopeOpts.ApplyAlwaysScopesAtSessionStart;
                    maxAlwaysScopeCacheRecords = scopeOpts.MaxAlwaysScopeCacheRecords;
                    maxAlwaysScopeCacheBytes = scopeOpts.MaxAlwaysScopeCacheBytes;
                    approvalScopeActivityTimeout = scopeOpts.ApprovalScopeActivityTimeout;
                    approvalScopeActivityMaximumAttempts = scopeOpts.ApprovalScopeActivityMaximumAttempts;
                }

                resolvedConfig = new ProxyResolvedWorkerConfig
                {
                    MaxToolCallsPerTurn = resolvedMaxToolCalls ?? registration.MaxToolCallsPerTurn,
                    ToolActivityOptions = resolvedToolOpts ?? new Dictionary<string, ActivityOptions>(),
                    InterceptorActivityOptions = interceptorActivityOpts,
                    InterceptorToolActivityOptions = perToolInterceptorOpts,
                    InterceptorSkippedTools = interceptorSkippedTools,
                    RequiresApprovalTools = requiresApprovalTools,
                    ScopeAwareTools = scopeAwareTools,
                    ScopeAwareApprovalTools = scopeAwareApprovalTools,
                    UseApprovalScopes = useApprovalScopes,
                    UseApprovalScopeStoreMode = useApprovalScopeStoreMode,
                    AlwaysScopesStoreKey = alwaysScopesStoreKey,
                    ApplyAlwaysScopesAtSessionStart = applyAlwaysScopesAtSessionStart,
                    MaxAlwaysScopeCacheRecords = maxAlwaysScopeCacheRecords,
                    MaxAlwaysScopeCacheBytes = maxAlwaysScopeCacheBytes,
                    ApprovalScopeActivityTimeout = approvalScopeActivityTimeout,
                    ApprovalScopeActivityMaximumAttempts = approvalScopeActivityMaximumAttempts,
                };
            }

            return new AgentStepResult
            {
                IsFinal = isFinal,
                AssistantMessage = assistantMessage,
                ToolCalls = isFinal ? null : toolCalls,
                UpdatedStateBag = serializedStateBag,
                Usage = response.Usage,
                ResponseId = response.ResponseId,
                ResolvedWorkerConfig = resolvedConfig,
            };
        }
        catch (Exception ex)
        {
            span?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogAgentActivityFailed(input.AgentName, sessionId.WorkflowId, ex);

            if (contextProviders.Length > 0)
            {
                var invokedCtx = new Microsoft.Agents.AI.AIContextProvider.InvokedContext(
                    agent, session, requestMessages: augmentedMessages, invokeException: ex);
                foreach (var provider in contextProviders)
                {
                    try
                    {
                        await provider.InvokedAsync(invokedCtx, ct).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Suppressed — re-throwing the original exception below is more useful.
                    }
                }
            }

            // Retry-hardening: a deterministic LLM error (HTTP 400/401/403/404/422) never succeeds
            // on retry. With RetryPolicy defaults (unlimited attempts) it loops forever and hangs the
            // agent workflow. Rethrow as a non-retryable ApplicationFailure so Temporal stops
            // immediately; retryable/transient errors propagate unchanged for the RetryPolicy to
            // govern. Cancellation is never reclassified. Uses the same classifier + ErrorType as the
            // MEAI path (DurableChatActivities.LlmNonRetryableErrorType).
            if (ex is not OperationCanceledException
                && TemporalCommunity.Extensions.AI.Internal.LlmErrorClassifier.IsNonRetryable(ex))
            {
                throw new ApplicationFailureException(
                    $"Non-retryable LLM error: {ex.Message}",
                    ex,
                    errorType: "LlmNonRetryable",
                    nonRetryable: true);
            }

            throw;
        }
        finally
        {
            TemporalAgentContext.SetCurrent(null);
        }
    }

    /// <summary>
    /// Resolves (and lazily builds) the immutable blueprint for a durable agent.
    /// The blueprint contains only frozen config — live DI instances are resolved per call.
    /// </summary>
    /// <exception cref="AgentNotRegisteredException">
    /// Thrown when no durable agent with this name is registered.
    /// </exception>
    internal AgentBlueprint ResolveBlueprint(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return _blueprintCache.GetOrAdd(name, static (n, ctx) =>
        {
            var (self, providerServices) = ctx;
            return self.BuildAgentBlueprint(n, providerServices);
        }, (this, services));
    }

    /// <summary>
    /// Builds an <see cref="AgentBlueprint"/> for the named agent. Called at most once per agent
    /// name (per worker lifetime) — subsequent calls reuse the cached blueprint.
    /// <para>
    /// Resolves a temporary <see cref="IChatClient"/> from the root provider solely to perform the
    /// structural chain-walk checks (function-invocation conflict, OTel suppression detection).
    /// That temporary client is discarded immediately after the checks; it is NOT stored on the
    /// blueprint. All per-call live instances are resolved fresh from an <see cref="IServiceScope"/>
    /// inside each activity method.
    /// </para>
    /// </summary>
    private AgentBlueprint BuildAgentBlueprint(string name, IServiceProvider providerServices)
    {
        var agentsOptions = providerServices.GetService<TemporalAgentsOptions>()
            ?? throw new InvalidOperationException(
                "TemporalAgentsOptions is not registered in DI. Call AddTemporalAgents on the worker " +
                "builder before invoking the durable-agent dispatch path.");

        if (!agentsOptions.DurableAgentRegistrations.TryGetValue(name, out var registration))
        {
            throw new AgentNotRegisteredException(name);
        }

        // ── Tool registry (Bucket 1 — frozen, stateless) ──────────────────────────────
        // AIFunction is a delegate wrapper; it holds no scoped state. Resolve tools from
        // the root provider so the registry is available for all per-call invocations.
        var resolvedTools = new Dictionary<string, AIFunction>(StringComparer.OrdinalIgnoreCase);
        var toolList = new List<AIFunction>(registration.Tools.Count);
        foreach (var tool in registration.Tools)
        {
            var resolved = tool.Factory(providerServices);
            if (resolved is null)
            {
                throw new InvalidOperationException(
                    $"Tool factory for '{tool.Name}' on agent '{name}' returned null.");
            }

            if (!string.Equals(resolved.Name, tool.Name, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Tool factory for '{tool.Name}' on agent '{name}' returned an AIFunction with " +
                    $"name '{resolved.Name}'. The factory's resolved name must match the name declared " +
                    "on AddTool.");
            }

            resolvedTools[tool.Name] = resolved;
            toolList.Add(resolved);
        }

        // ── Structural chain-walk checks (computed once, result cached as bool) ────────
        // Resolve a temporary IChatClient from the root provider only for structural inspection.
        // This client is used to build a temporary ChatClientAgent for pipeline validation and
        // OTel detection; it is discarded immediately after — NOT stored on the blueprint.
        var tempChatClient = registration.ChatClient(providerServices);

        var chatOptions = registration.ChatOptions?.Clone() ?? new ChatOptions();
        chatOptions.Instructions = registration.Instructions;
        chatOptions.Tools = toolList.Count > 0 ? toolList.Cast<AITool>().ToList() : null;

        var agentOptions = new ChatClientAgentOptions
        {
            Name = registration.Name,
            Description = registration.Description,
            ChatOptions = chatOptions,
            AIContextProviders = null,
            UseProvidedChatClientAsIs = true,
        };

        var tempChatClientAgent = new ChatClientAgent(tempChatClient, agentOptions);

        var configurePipeline = registration.ConfigureAgentPipeline
            ?? agentsOptions.DefaultConfigureAgentPipeline;

        AIAgent tempAgent;
        if (configurePipeline is null)
        {
            tempAgent = tempChatClientAgent;
        }
        else
        {
            var agentBuilder = new AIAgentBuilder(tempChatClientAgent);
            configurePipeline.Invoke(agentBuilder);
            tempAgent = agentBuilder.Build(providerServices);
        }

        // Step 3c.3: B-check (runtime fallback to startup C-check). Walk the composed agent
        // chain for FunctionInvocationDelegatingAgent (matched by Type.FullName because the
        // type is internal sealed in Microsoft.Agents.AI). This catches misconfigurations the
        // C-check at IPostConfigureOptions time couldn't reach — e.g., factory-deferred DI
        // patterns where the chat-client factory isn't resolvable at host build time, or
        // worker-only paths that bypass the IPostConfigureOptions hook entirely. Same exception
        // shape, same OffendingType field as the C-check.
        foreach (var link in AgentChainWalker.WalkAIAgent(tempAgent))
        {
            if (link.GetType().FullName == FunctionInvocationDelegatingAgentFullName)
            {
                throw new DurableFunctionInvocationConflictException(
                    $"Agent '{name}' has '{FunctionInvocationDelegatingAgentFullName}' in its pipeline. " +
                    "The durable agent library handles tool invocation as separate Temporal activities " +
                    "(InvokeAgentTool); installing agent-side function-invocation middleware would " +
                    "conflict with this contract and silently break per-tool durability. Remove the " +
                    ".Use(functionInvocationCallback) / UseFunctionInvocation() call from your " +
                    "ConfigureAgentPipeline configuration.")
                {
                    OffendingType = FunctionInvocationDelegatingAgentFullName,
                };
            }
        }

        // Step 3c.3: 2b-enriched OTel suppression detection. When the user installed
        // OpenTelemetryAgent (agent-pipeline level) OR OpenTelemetryChatClient (chat-client
        // level), suppress our own agent.turn span — otherwise downstream consumers receive
        // duplicate gen_ai.usage.* attributes and cost-aggregation queries double-count.
        // Computed once here using the temporary agent; the per-turn dispatch path reads the bool.
        var hasOTelAgent = AgentChainWalker.Contains<OpenTelemetryAgent>(tempAgent);
        var hasOTelChatClient = AgentChainWalker.Contains<OpenTelemetryChatClient>(tempChatClient);
        var suppressAgentTurnSpan = hasOTelAgent || hasOTelChatClient;

        // Discard tempAgent and tempChatClient — they are NOT stored on the blueprint.
        // Per-call live instances are resolved fresh from an IServiceScope in each activity method.

        IReadOnlyList<AITool> toolsAsAITools = [.. resolvedTools.Values.Cast<AITool>()];

        // Audit-log provider-contributed tools so operators can confirm durable registration.
        if (registration.ProviderContributedTools is { Count: > 0 } providerTools)
        {
            foreach (var (toolName, providerType) in providerTools)
                _logger.LogInformation(
                    "Agent '{AgentName}': tool '{ToolName}' contributed by context provider {ProviderType} registered as durable activity.",
                    name, toolName, providerType);
        }

        return new AgentBlueprint(
            resolvedTools,
            registration,
            agentsOptions,
            suppressAgentTurnSpan,
            toolsAsAITools);
    }

    // ── Per-call helper: build a live AIAgent from a scoped IChatClient ──────────────────────
    // Creates a fresh ChatClientAgent (+ optional decorator pipeline) per activity call.
    // This is the per-call equivalent of what BuildAgentBlueprint does once for structural checks.
    private AIAgent BuildLiveAgent(AgentBlueprint blueprint, IChatClient chatClient, IServiceProvider scopedServices)
    {
        var registration = blueprint.Registration;
        var agentsOptions = blueprint.AgentsOptions;

        var chatOptions = registration.ChatOptions?.Clone() ?? new ChatOptions();
        chatOptions.Instructions = registration.Instructions;
        chatOptions.Tools = blueprint.ToolsAsAITools.Count > 0
            ? [.. blueprint.ToolsAsAITools]
            : null;

        var agentOptions = new ChatClientAgentOptions
        {
            Name = registration.Name,
            Description = registration.Description,
            ChatOptions = chatOptions,
            // Q1 (β): keep our explicit AIContextProvider loop inside RunDurableAgentStepAsync
            // rather than delegating to MAF's ChatClientAgent. Setting null here suppresses MAF's
            // own provider lifecycle so providers fire exactly once per turn from our loop.
            AIContextProviders = null,
            UseProvidedChatClientAsIs = true,
        };

        var chatClientAgent = new ChatClientAgent(chatClient, agentOptions);

        var configurePipeline = registration.ConfigureAgentPipeline
            ?? agentsOptions.DefaultConfigureAgentPipeline;

        if (configurePipeline is null)
        {
            return chatClientAgent;
        }

        var agentBuilder = new AIAgentBuilder(chatClientAgent);
        configurePipeline.Invoke(agentBuilder);
        return agentBuilder.Build(scopedServices);
    }

    /// <summary>Fully-qualified type name of MAF's internal function-invocation decorator.</summary>
    private const string FunctionInvocationDelegatingAgentFullName =
        Internal.AgentInternalConstants.FunctionInvocationDelegatingAgentFullName;

    /// <summary>
    /// Pre-tool lifecycle activity. Fires before <c>InvokeAgentTool</c> for each tool call in a
    /// turn. Resolves the agent's <see cref="IAgentToolInterceptor"/> (per-agent or worker default),
    /// calls <see cref="IAgentToolInterceptor.BeforeToolCallAsync"/>, and returns a serializable
    /// <see cref="DurableToolInterceptorResult"/> DTO for the workflow to act on.
    /// </summary>
    /// <remarks>
    /// <b>Missing-interceptor security posture (fail-CLOSED — intentional asymmetry with MEAI).</b>
    /// When no interceptor is resolved at activity time (e.g. worker restart without
    /// re-registration), a <c>ScopeAware + RequiresApproval</c> tool returns
    /// <see cref="DurableToolOutcome.PauseForApproval"/> rather than proceeding: those tools were
    /// excluded from the unconditional approval list and rely on the interceptor to enforce the
    /// missing-scope approval gate, so silently proceeding would bypass a security control. All
    /// other tools degrade to <see cref="DurableToolOutcome.Proceed"/> to keep the session live.
    /// This differs deliberately from the MEAI <c>DurableChatActivities.RunToolInterceptorAsync</c>
    /// path, which always fails OPEN (Proceed) because MEAI has no built-in approval floor to fall
    /// back to.
    /// </remarks>
    [Activity("TemporalCommunity.Extensions.Agents.RunToolInterceptor")]
    public async Task<DurableToolInterceptorResult> RunToolInterceptorAsync(DurableToolInterceptorInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var ctx = ActivityExecutionContext.Current;
        var ct = ctx.CancellationToken;

        var blueprint = ResolveBlueprint(input.AgentName);

        // Resolve tool interceptor fresh per call from a scoped service provider.
        using var scope = serviceScopeFactory.CreateScope();
        var interceptorFactory = blueprint.Registration.ToolInterceptorFactory
            ?? blueprint.AgentsOptions.DefaultToolInterceptor;
        var toolInterceptor = interceptorFactory?.Invoke(scope.ServiceProvider);

        if (toolInterceptor is null)
        {
            // Interceptor was removed between workflow dispatch and activity execution
            // (e.g. worker restart without interceptor re-registration).
            // Scope-aware required tools must not silently proceed — they were excluded from
            // RequiresApprovalTools and rely on the interceptor for the approval gate.
            if (input.ScopeAware && input.RequiresApproval)
            {
                _logger.LogWarning(
                    "RunToolInterceptor dispatched for agent '{AgentName}' tool '{ToolName}' " +
                    "(ScopeAware+RequiresApproval) but no IAgentToolInterceptor is resolved. " +
                    "Returning PauseForApproval to enforce the approval gate.",
                    input.AgentName, input.ToolName);
                return new DurableToolInterceptorResult
                {
                    Outcome = DurableToolOutcome.PauseForApproval,
                    Message = $"Tool '{input.ToolName}' requires approval. No interceptor resolved — defaulting to approval gate.",
                };
            }

            // Non-required or non-scope-aware: degrade to Proceed
            // so the tool still runs rather than silently blocking the session.
            _logger.LogWarning(
                "RunToolInterceptor dispatched for agent '{AgentName}' tool '{ToolName}' " +
                "but no IAgentToolInterceptor is resolved. Defaulting to Proceed.",
                input.AgentName, input.ToolName);
            return new DurableToolInterceptorResult { Outcome = DurableToolOutcome.Proceed };
        }

        ctx.Heartbeat($"intercepting tool '{input.ToolName}'");

        // Deserialize the state bag snapshot when present. Parsing the workflow ID is only
        // valid when this activity runs inside AgentWorkflow (ID = ta-{agent}-{key} format).
        // For TemporalAIAgent sub-agent calls the workflow ID is the parent's, which may not
        // parse as a TemporalAgentSessionId — and SerializedStateBag is always null on that
        // path anyway. Skip the parse entirely when there is nothing to deserialize.
        AgentSessionStateBag? stateBag = null;
        if (input.SerializedStateBag.HasValue)
        {
            var session = TemporalAgentSession.FromStateBag(
                TemporalAgentSessionId.Parse(ctx.Info.WorkflowId!),
                input.SerializedStateBag);
            stateBag = session.StateBag.Count > 0 ? session.StateBag : null;
        }

        var toolContext = new AgentToolContext
        {
            AgentName = input.AgentName,
            ToolName = input.ToolName,
            Arguments = input.Arguments is null
                ? new Dictionary<string, object?>()
                : new Dictionary<string, object?>(input.Arguments),
            CallId = input.CallId,
            SessionId = ctx.Info.WorkflowId,
            StateBag = stateBag,
            // Feature B: pass through scope-aware fields so the interceptor can consult scope records.
            ScopeAware = input.ScopeAware,
            RequiresApproval = input.RequiresApproval,
        };

        // Snapshot the bag's serialized form before the interceptor runs so we can detect
        // (and propagate) in-place mutations afterwards (X-2 StateBag write-back).
        var stateBagBefore = stateBag is { Count: > 0 } ? stateBag.Serialize().GetRawText() : null;

        DurableToolDecision decision;
        try
        {
            decision = await toolInterceptor
                .BeforeToolCallAsync(toolContext, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex,
                "IAgentToolInterceptor.BeforeToolCallAsync threw for agent '{AgentName}' tool '{ToolName}'. " +
                "Defaulting to Block.",
                input.AgentName, input.ToolName);
            return new DurableToolInterceptorResult
            {
                Outcome = DurableToolOutcome.Block,
                Message = $"Interceptor threw an exception: {ex.Message}",
            };
        }

        var result = DurableToolInterceptorResult.FromDecision(decision);

        // X-2: propagate StateBag mutations the interceptor made in place. Only emit
        // UpdatedStateBag when the serialized bag actually changed. The workflow merges it
        // back into _currentStateBag before tool dispatch (AgentWorkflow).
        if (toolContext.StateBag is { Count: > 0 } mutatedBag)
        {
            var stateBagAfter = mutatedBag.Serialize();
            if (!string.Equals(stateBagBefore, stateBagAfter.GetRawText(), StringComparison.Ordinal))
            {
                result = result.WithUpdatedStateBag(stateBagAfter);
            }
        }

        return result;
    }

    /// <summary>
    /// Loads all always-scope records for an agent and logical store key from the configured
    /// <see cref="TemporalCommunity.Extensions.Agents.Approvals.IApprovalScopeStore"/>.
    /// When no store is configured, returns an empty result.
    /// </summary>
    /// <remarks>
    /// Failure handling is delegated to the workflow: the activity itself throws on store errors,
    /// and the workflow catches <see cref="Temporalio.Activities.ActivityFailureException"/> with
    /// the <c>when (!IsActivityCancellation(ex))</c> filter to apply fail-open semantics.
    /// </remarks>
    [Activity("TemporalCommunity.Extensions.Agents.LoadAlwaysScopes")]
    public async Task<LoadAlwaysScopesResult> LoadAlwaysScopesAsync(LoadAlwaysScopesInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var ct = ActivityExecutionContext.Current.CancellationToken;
        var blueprint = ResolveBlueprint(input.AgentName);

        // Resolve ApprovalScopeStore fresh per call from a scoped service provider.
        using var scope = serviceScopeFactory.CreateScope();
        IApprovalScopeStore? approvalScopeStore = null;
        if (blueprint.Registration.UseApprovalScopes && blueprint.Registration.ApprovalScopesOptions is not null)
        {
            var storeFactory = blueprint.Registration.ApprovalScopesOptions.ApprovalScopeStore
                ?? blueprint.AgentsOptions.ApprovalScopeStore;
            approvalScopeStore = storeFactory?.Invoke(scope.ServiceProvider);
        }

        if (approvalScopeStore is null)
        {
            // No store configured — return empty result gracefully.
            return new LoadAlwaysScopesResult { Scopes = [] };
        }

        var records = await approvalScopeStore
            .LoadAsync(input.AgentName, input.StoreKey, ct)
            .ConfigureAwait(false);

        return new LoadAlwaysScopesResult { Scopes = records ?? [] };
    }

    /// <summary>
    /// Appends an always-scope record to the configured
    /// <see cref="TemporalCommunity.Extensions.Agents.Approvals.IApprovalScopeStore"/>.
    /// Idempotent by <see cref="ApprovalScopeRecord.OriginatingRequestId"/>.
    /// When no store is configured, logs a warning and returns without error.
    /// </summary>
    /// <remarks>
    /// Failure handling is delegated to the workflow: the activity itself throws on store errors,
    /// and the workflow catches <see cref="Temporalio.Activities.ActivityFailureException"/> with
    /// the <c>when (!IsActivityCancellation(ex))</c> filter to apply fail-open semantics.
    /// </remarks>
    [Activity("TemporalCommunity.Extensions.Agents.AppendAlwaysScope")]
    public async Task AppendAlwaysScopeAsync(AppendAlwaysScopeInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var ct = ActivityExecutionContext.Current.CancellationToken;
        var blueprint = ResolveBlueprint(input.AgentName);

        // Resolve ApprovalScopeStore fresh per call from a scoped service provider.
        using var scope = serviceScopeFactory.CreateScope();
        IApprovalScopeStore? approvalScopeStore = null;
        if (blueprint.Registration.UseApprovalScopes && blueprint.Registration.ApprovalScopesOptions is not null)
        {
            var storeFactory = blueprint.Registration.ApprovalScopesOptions.ApprovalScopeStore
                ?? blueprint.AgentsOptions.ApprovalScopeStore;
            approvalScopeStore = storeFactory?.Invoke(scope.ServiceProvider);
        }

        if (approvalScopeStore is null)
        {
            _logger.LogWarning(
                "[{SessionId}] AppendAlwaysScopeAsync: no IApprovalScopeStore is configured for agent " +
                "'{AgentName}'. The always-scope record for tool '{ToolName}' (RequestId: {RequestId}) " +
                "will not be persisted.",
                input.SessionId, input.AgentName, input.ToolName, input.OriginatingRequestId);
            return;
        }

        var record = new ApprovalScopeRecord
        {
            ToolName = input.ToolName,
            Pattern = input.Pattern,
            GrantedAt = input.GrantedAt,
            OriginatingRequestId = input.OriginatingRequestId,
        };

        await approvalScopeStore
            .AppendAsync(input.AgentName, input.StoreKey, record, ct)
            .ConfigureAwait(false);
    }

    [Activity("TemporalCommunity.Extensions.Agents.InvokeAgentTool")]
    public async Task<InvokeAgentToolResult> InvokeAgentToolAsync(InvokeAgentToolInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var ctx = ActivityExecutionContext.Current;
        var ct = ctx.CancellationToken;

        // Tools live on the blueprint (Bucket 1 — stateless AIFunction delegates).
        // No scope is needed for the tool lookup itself; however a scope is opened below for
        // the TemporalAgentContext so any scoped services the tool resolves via the context are
        // properly lifetime-managed.
        var blueprint = ResolveBlueprint(input.AgentName);

        if (!blueprint.Tools.TryGetValue(input.ToolName, out var fn))
        {
            throw new InvalidOperationException(
                $"Tool '{input.ToolName}' is not registered on agent '{input.AgentName}'.");
        }

        ctx.Heartbeat($"invoking tool '{input.ToolName}'");

        using var span = TemporalAgentTelemetry.ActivitySource.StartActivity(
            TemporalAgentTelemetry.AgentToolInvokeSpanName,
            ActivityKind.Internal);
        span?.SetTag(TemporalAgentTelemetry.AgentNameAttribute, input.AgentName);
        span?.SetTag(TemporalAgentTelemetry.AgentToolNameAttribute, input.ToolName);
        if (!string.IsNullOrEmpty(input.CallId))
        {
            span?.SetTag(TemporalAgentTelemetry.AgentToolCallIdAttribute, input.CallId);
        }

        // Open a per-call scope so any scoped services the tool resolves via TemporalAgentContext
        // are properly lifetime-managed and not captured as captive singletons.
        using var scope = serviceScopeFactory.CreateScope();

        // Set up TemporalAgentContext for the tool invocation so tools that call
        // TemporalAgentContext.Current (e.g. for RequestApprovalAsync) work in the
        // per-tool activity path. Mirrors the SetCurrent/clear-in-finally pattern at
        // RunDurableAgentStepAsync line ~296. Without this, HITL tools and any tool
        // that needs the agent context are broken when dispatched as InvokeAgentTool
        // activities (which is the default since v0.3).
        //
        // SessionId parse is wrapped in try/catch: in tests, ActivityEnvironment uses
        // arbitrary workflow IDs (e.g. "test") that don't match the agent session
        // prefix. When parsing fails, we skip context setup — a tool that needs the
        // context will throw the same "No TemporalAgentContext is available" error
        // as before this fix, but tools that don't need it continue to work.
        var contextSetUp = false;
        // X-1: hold the session so we can both (a) seed the tool with the carried StateBag and
        // (b) capture the tool's StateBag mutations for write-back after invocation.
        TemporalAgentSession? session = null;
        try
        {
            ArgumentNullException.ThrowIfNull(ctx.Info.WorkflowId, nameof(ctx.Info.WorkflowId));
            var sessionId = TemporalAgentSessionId.Parse(ctx.Info.WorkflowId);

            // Validate that the parsed agent name matches the tool's registered agent.
            // Scheduled-job workflow IDs (ta-{agent}-scheduled-{runId}) parse successfully
            // but produce a wrong agent name (e.g. "refundagent-scheduled"). Setting a
            // context with a mismatched ID would let tools call RequestApprovalAsync against
            // the wrong workflow; skip context setup in that case.
            if (!sessionId.AgentName.Equals(input.AgentName, StringComparison.OrdinalIgnoreCase))
            {
                // Workflow ID parsed but belongs to a different agent (e.g. a scheduled job).
                // Leave TemporalAgentContext unset — tools that need it will throw on access.
            }
            else
            {
                // X-1: build the session from the carried StateBag (was literal null before),
                // so tools and any AIContextProvider they consult see accumulated state.
                session = TemporalAgentSession.FromStateBag(sessionId, input.SerializedStateBag);
                var temporalContext = new TemporalAgentContext(ctx.TemporalClient, session, scope.ServiceProvider);
                TemporalAgentContext.SetCurrent(temporalContext);
                contextSetUp = true;
            }
        }
        catch (FormatException)
        {
            // Workflow ID isn't a valid agent session ID — likely a test environment.
            // Tools that need TemporalAgentContext.Current will throw on access.
        }

        // Snapshot the bag's serialized form before invocation so we can detect (and write back)
        // tool-driven mutations afterwards (X-1). Only computed on the real agent-session path.
        var stateBagBefore = session?.SerializeStateBag()?.GetRawText();

        try
        {
            _logger.LogAgentToolInvocationStarted(input.AgentName, input.ToolName);

            var arguments = input.Arguments is null
                ? new AIFunctionArguments()
                : new AIFunctionArguments(input.Arguments);

            var result = await fn.InvokeAsync(arguments, ct).ConfigureAwait(false);

            _logger.LogAgentToolInvocationCompleted(input.AgentName, input.ToolName);

            // X-1: capture StateBag write-back. Only emit UpdatedStateBag when the serialized
            // bag actually changed, so the no-mutation case stays null (wire-compatible). The
            // workflow merges this back into _currentStateBag in tool-call index order.
            System.Text.Json.JsonElement? updatedStateBag = null;
            if (session is not null)
            {
                var after = session.SerializeStateBag();
                if (!string.Equals(stateBagBefore, after?.GetRawText(), StringComparison.Ordinal))
                {
                    updatedStateBag = after;
                }
            }

            return new InvokeAgentToolResult
            {
                Result = result,
                CallId = input.CallId,
                UpdatedStateBag = updatedStateBag,
            };
        }
        catch (Exception ex)
        {
            span?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogAgentToolInvocationFailed(input.AgentName, input.ToolName, ex);
            throw;
        }
        finally
        {
            if (contextSetUp)
            {
                TemporalAgentContext.SetCurrent(null);
            }
        }
    }
}
