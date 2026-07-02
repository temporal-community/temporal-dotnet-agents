#pragma warning disable MAAI001 // experimental MAF AIContextProvider.InvokingContext/InvokedContext ctors; inventoried in Internal/ExperimentalApiSuppressions.cs
using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Temporalio.Activities;
using TemporalCommunity.Extensions.Agents.Approvals;
using TemporalCommunity.Extensions.Agents.HistoryStore;
using TemporalCommunity.Extensions.Agents.Session;
using TemporalCommunity.Extensions.Agents.State;
using TemporalCommunity.Extensions.Agents.Tools;
using TemporalCommunity.Extensions.AI.Approvals;
using TemporalCommunity.Extensions.AI.Exceptions;
using TemporalCommunity.Extensions.AI.Internal;
using TemporalCommunity.Extensions.AI.Session;
using TemporalCommunity.Extensions.AI.Tools;
using Temporalio.Workflows;

namespace TemporalCommunity.Extensions.Agents.Workflows;

/// <summary>
/// Cached state for a durable agent registered via <c>TemporalAgentsOptions.AddDurableAgent</c>.
/// Composed once at first activity dispatch (lazy) and reused for the lifetime of the worker.
/// </summary>
/// <param name="Agent">
/// The composed agent pipeline — either the bare <c>ChatClientAgent</c> (when no
/// <c>ConfigureAgentPipeline</c> was provided) or the chain of user-supplied
/// <c>DelegatingAIAgent</c> decorators wrapping it.
/// </param>
/// <param name="Tools">Resolved per-agent tool registry keyed by case-insensitive name.</param>
/// <param name="Registration">Source-of-truth registration snapshot from the builder.</param>
/// <param name="HistoryStore">Resolved external history store (per-agent override or worker-level default), or null.</param>
/// <param name="ContextProviders">Resolved AIContextProvider list, invoked explicitly per-turn (not by MAF).</param>
/// <param name="AgentsOptions">Reference to the shared agents-options snapshot.</param>
/// <param name="SuppressAgentTurnSpan">
/// Step 3c.3 (2b-enriched OTel): when <see langword="true"/>, the activity skips emitting its
/// own <c>agent.turn</c> span and instead tags <c>Activity.Current</c> with the
/// Temporal-namespaced correlation ID — deferring the canonical GenAI span to MAF's
/// <c>OpenTelemetryAgent</c> (or MEAI's <c>OpenTelemetryChatClient</c>) if either is present in
/// the pipeline. Computed once at compose time via <c>AgentChainWalker</c> so the per-turn
/// dispatch path does no extra walks.
/// </param>
internal sealed record CachedDurableAgent(
    AIAgent Agent,
    IReadOnlyDictionary<string, AIFunction> Tools,
    DurableAgentRegistration Registration,
    IAgentHistoryStore? HistoryStore,
    IReadOnlyList<AIContextProvider> ContextProviders,
    TemporalAgentsOptions AgentsOptions,
    bool SuppressAgentTurnSpan,
    string? CompactionStrategyKey,
    Compaction.ICompactionStrategy? CompactionStrategy,
    IReadOnlyList<AITool> ToolsAsAITools,
    IDurableToolInterceptor<AgentToolContext>? ToolInterceptor = null,
    IApprovalScopeStore? ApprovalScopeStore = null);

/// <summary>
/// Temporal activities that perform the actual AI inference for agent sessions.
/// All AI inference must run inside an activity to preserve workflow determinism.
/// </summary>
internal sealed class AgentActivities(
    IServiceProvider services,
    ILoggerFactory? loggerFactory = null)
{
    private readonly ILogger _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<AgentActivities>();

    // Per-durable-agent cache. Composed lazily at first dispatch and reused for the lifetime of
    // the worker. Concurrent first-dispatches for the same agent compose at most once.
    private readonly ConcurrentDictionary<string, CachedDurableAgent> _durableAgentCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Builds the activity summary value (visible in the Temporal Web UI activity list).
    /// Uses the agent name when available; returns null otherwise so the SDK omits the field.
    /// </summary>
    internal static string? BuildActivitySummary(string? agentName) =>
        string.IsNullOrWhiteSpace(agentName) ? null : agentName;

    /// <summary>
    /// Reduces an externally stored session's history to its most recent <c>MaxEntryCount</c>
    /// entries via <see cref="IAgentHistoryStore.ReplaceAsync"/>. Dispatched by the workflow at
    /// continue-as-new time when the agent is using an external history store.
    /// </summary>
    [Activity("TemporalCommunity.Extensions.Agents.ReduceHistoryInStore")]
    public async Task ReduceHistoryInStoreAsync(ReduceHistoryInStoreInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Resolve the agent's history store via the cache (lazy compose). The activity context's
        // workflow ID is the session ID; the agent name is carried on the input so the cache
        // entry resolves correctly even though the activity is dispatched without an agent-name
        // argument by some legacy callers — for v0.3 every dispatch comes from
        // ExecuteDurableAgentTurnAsync which has access to the agent name on AgentWorkflowInput.
        var cached = ResolveDurableAgent(input.AgentName);
        if (cached.HistoryStore is null)
        {
            throw new InvalidOperationException(
                $"ReduceHistoryInStoreAsync was dispatched but no IAgentHistoryStore is configured " +
                $"for agent '{input.AgentName}'.");
        }

        var ct = ActivityExecutionContext.Current.CancellationToken;
        // Reducer runs against the post-compact projection (Q5α) so it operates on the
        // view the LLM actually saw. Compaction markers (Step 5+) are pre-collapsed by the
        // store; the reducer never sees a marker, only the rolled-up summary or filtered
        // entries the strategy chose.
        var prior = await cached.HistoryStore
            .LoadAsync(input.SessionId, applyCompaction: true, ct)
            .ConfigureAwait(false);

        // Resolve effective reducer: per-agent first, then worker default.
        var reducer = cached.Registration.HistoryReducer
                   ?? cached.AgentsOptions.DefaultHistoryReducer;

        IReadOnlyList<DurableSessionEntry> reduced;
        if (reducer is not null)
        {
            // prior.ToList() gives the reducer a fresh mutable copy (reducer contract: may mutate input).
            // Avoid a second ToList() on the result when the reducer already returns IReadOnlyList<T>.
            var reducerInput = prior.ToList();
            var reducerResult = reducer(reducerInput);
            reduced = reducerResult as IReadOnlyList<DurableSessionEntry> ?? reducerResult.ToList();
        }
        else
        {
            if (prior.Count <= input.MaxEntryCount)
            {
                return;
            }

            reduced = prior.Skip(prior.Count - input.MaxEntryCount).ToList();
        }

        await cached.HistoryStore.ReplaceAsync(input.SessionId, reduced, ct).ConfigureAwait(false);
    }

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
    /// Appends the full turn — request entry + response entry carrying all messages accumulated
    /// across every LLM step and tool call — to the agent's external history store.
    /// Dispatched by <see cref="AgentWorkflow"/> after <c>ExecuteDurableAgentTurnAsync</c>
    /// returns, replacing the former in-activity append that was limited to the final assistant
    /// message and was skipped entirely when the iteration cap was hit.
    /// </summary>
    [Activity("TemporalCommunity.Extensions.Agents.AppendAgentTurn")]
    public async Task AppendAgentTurnAsync(AppendAgentTurnInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var cached = ResolveDurableAgent(input.AgentName);
        if (cached.HistoryStore is null)
        {
            throw new InvalidOperationException(
                $"AppendAgentTurn dispatched for agent '{input.AgentName}' but no IAgentHistoryStore is configured. " +
                "This indicates a mismatch between UseExternalStoreMode on the workflow input and the worker's resolved registration.");
        }

        var ct = ActivityExecutionContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;

        var requestEntry = AgentSessionRequest.FromRunRequest(input.Request, now);
        var responseEntry = AgentSessionResponse.FromAgentResponse(
            input.Request.CorrelationId ?? string.Empty,
            input.TurnResponse,
            now);

        await cached.HistoryStore.AppendAsync(
            input.SessionId,
            [requestEntry, responseEntry],
            ct).ConfigureAwait(false);
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

        var cached = ResolveDurableAgent(input.AgentName);

        // When the workflow was started by a proxy-only client, resolve
        // and return worker-side settings so the workflow can patch its input on the first turn.
        bool? resolvedExternalStore = null;
        Dictionary<string, ActivityOptions>? resolvedToolOpts = null;
        if (input.NeedsWorkerSettingsResolution)
        {
            resolvedExternalStore = cached.HistoryStore is not null
                                 || cached.AgentsOptions.HistoryStore is not null;

            var effectiveActivityTimeout = cached.Registration.ActivityTimeout
                ?? cached.AgentsOptions.DefaultActivityTimeout;
            var effectiveHeartbeatTimeout = cached.Registration.HeartbeatTimeout
                ?? cached.AgentsOptions.DefaultHeartbeatTimeout;
            var effectiveRetryPolicy = cached.Registration.RetryPolicy
                ?? cached.AgentsOptions.DefaultRetryPolicy;

            resolvedToolOpts = DefaultTemporalAgentClient.BuildDurableAgentToolActivityOptions(
                cached.Registration,
                effectiveActivityTimeout,
                effectiveHeartbeatTimeout,
                effectiveRetryPolicy);
        }
        var sessionId = input.SessionId ?? TemporalAgentSessionId.Parse(ctx.Info.WorkflowId!);

        // Restore the StateBag so AIContextProvider state survives across step iterations.
        var session = TemporalAgentSession.FromStateBag(sessionId, input.SerializedStateBag);

        IReadOnlyList<ChatMessage> messagesForLlm = input.AccumulatedMessages;
        if (cached.HistoryStore is not null && input.IsFirstStep)
        {
            // Inference-time load — feed the LLM the post-compact view. Compaction markers
            // are collapsed in place; the LLM sees rolled-up summaries instead of the full
            // pre-compact run. The audit-canonical raw history is reachable separately via
            // applyCompaction: false (used by the erasure helper added in Step 5c).
            var prior = await cached.HistoryStore
                .LoadAsync(sessionId.WorkflowId, applyCompaction: true, ct)
                .ConfigureAwait(false);
            if (prior.Count > 0)
            {
                var priorMessageCount = 0;
                foreach (var entry in prior)
                {
                    priorMessageCount += entry.Messages.Count;
                }

                var combined = new List<ChatMessage>(priorMessageCount + input.AccumulatedMessages.Count);
                foreach (var entry in prior)
                {
                    foreach (var m in entry.Messages)
                    {
                        combined.Add(m);
                    }
                }
                combined.AddRange(input.AccumulatedMessages);
                messagesForLlm = combined;
            }
        }

        var registration = cached.Registration;
        var chatOptions = registration.ChatOptions?.Clone() ?? new ChatOptions();
        chatOptions.Instructions = registration.Instructions;
        // Spread [..] makes a per-call copy so downstream mutation (EnableToolNames filter below)
        // cannot corrupt the cached IReadOnlyList.
        chatOptions.Tools = cached.ToolsAsAITools.Count > 0 ? [.. cached.ToolsAsAITools] : null;
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
        // around the call. The pipeline was composed once at ComposeDurableAgent time and
        // cached on cached.Agent — it may be the bare ChatClientAgent (no user decorators) or
        // a chain of wrappers terminating in one. Either way the chain handles the call.

        var augmentedMessages = messagesForLlm;
        var providerAIContexts = cached.ContextProviders.Count == 0
            ? null
            : new List<Microsoft.Agents.AI.AIContext>(cached.ContextProviders.Count);
        if (cached.ContextProviders.Count > 0)
        {
            // Pre-populate with the accumulated conversation history so providers that need
            // to scan prior messages (e.g. WorkingSetContextProvider) can read them from
            // context.AIContext.Messages. Each provider's output is still appended to this
            // aggregated context for subsequent providers in the chain.
            var aggregated = new Microsoft.Agents.AI.AIContext
            {
                Messages = messagesForLlm,
            };
            foreach (var provider in cached.ContextProviders)
            {
                var invokingCtx = new Microsoft.Agents.AI.AIContextProvider.InvokingContext(
                    cached.Agent, session, aggregated);
                var providerCtx = await provider.InvokingAsync(invokingCtx, ct).ConfigureAwait(false);
                providerAIContexts!.Add(providerCtx);
            }

            // Materialize each context's Messages list once. AIContext.Messages is typed as
            // IEnumerable<ChatMessage> — calling Count() and then iterating again would
            // double-enumerate any lazy or one-shot provider (F3 fix).
            var materializedMessages = providerAIContexts!
                .Select(c => c.Messages?.ToList())
                .ToList();
            var extraCapacity = materializedMessages.Sum(m => m?.Count ?? 0);
            var extraMessages = new List<ChatMessage>(extraCapacity);
            foreach (var extra in materializedMessages)
            {
                if (extra is { } msgs)
                {
                    foreach (var m in msgs)
                    {
                        extraMessages.Add(m);
                    }
                }
            }

            if (extraMessages.Count > 0)
            {
                var combined = new List<ChatMessage>(extraMessages.Count + messagesForLlm.Count);
                combined.AddRange(extraMessages);
                combined.AddRange(messagesForLlm);
                augmentedMessages = combined;
            }
        }

        var temporalContext = new TemporalAgentContext(ctx.TemporalClient, session, services);
        TemporalAgentContext.SetCurrent(temporalContext);

        // When the user's pipeline installs OpenTelemetryAgent or
        // OpenTelemetryChatClient, suppress our own agent.turn span to avoid duplicate gen_ai.*
        // attributes (downstream cost-aggregation queries would double-count tokens). Instead
        // tag Activity.Current — which will be MAF's invoke_agent span when present, or the
        // Temporal SDK's RunActivity span otherwise — with the Temporal-namespaced correlation
        // ID so the canonical GenAI semconv data (from MAF) carries our additive context too.
        // The `using var` keeps disposal correct: when suppressed, span is null and the using
        // statement is a no-op; when emitted, the span is disposed at method exit.
        using var span = cached.SuppressAgentTurnSpan
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
            await foreach (var update in cached.Agent.RunStreamingAsync(
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

            if (cached.ContextProviders.Count > 0)
            {
                var invokedCtx = new Microsoft.Agents.AI.AIContextProvider.InvokedContext(
                    cached.Agent,
                    session,
                    requestMessages: augmentedMessages,
                    responseMessages: response.Messages);
                foreach (var provider in cached.ContextProviders)
                {
                    await provider.InvokedAsync(invokedCtx, ct).ConfigureAwait(false);
                }
            }

            var serializedStateBag = session.SerializeStateBag();
            var isFinal = toolCalls.Count == 0;


            int? resolvedMaxToolCalls = input.NeedsWorkerSettingsResolution
                ? cached.Registration.MaxToolCallsPerTurn
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
                foreach (var toolReg in cached.Registration.Tools)
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
                if (cached.Registration.UseApprovalScopes && cached.AgentsOptions.DefaultToolInterceptor is not null)
                {
                    throw new InvalidOperationException(
                        "UseApprovalScopes() cannot be combined with TemporalAgentsOptions.DefaultToolInterceptor. " +
                        "This release does not compose approval scopes with worker-default tool interceptors. " +
                        "Remove DefaultToolInterceptor from TemporalAgentsOptions or do not call UseApprovalScopes() on this agent.");
                }

                // Feature B: startup validation for scope-aware required tools at proxy-start resolution.
                foreach (var toolReg in cached.Registration.Tools)
                {
                    var toolOpts = toolReg.Options;
                    if (toolOpts.RequireApprovalFlag && toolOpts.ScopeAwareFlag && !cached.Registration.UseApprovalScopes)
                    {
                        throw new InvalidOperationException(
                            $"Tool '{toolReg.Name}' has ScopeAware() set but approval scopes are not enabled on agent '{cached.Registration.Name}'. " +
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

                if (cached.ToolInterceptor is not null)
                {
                    var effectiveTimeout = cached.Registration.ActivityTimeout
                        ?? cached.AgentsOptions.DefaultActivityTimeout;
                    var effectiveHeartbeat = cached.Registration.HeartbeatTimeout
                        ?? cached.AgentsOptions.DefaultHeartbeatTimeout;
                    var effectiveRetry = cached.Registration.RetryPolicy
                        ?? cached.AgentsOptions.DefaultRetryPolicy;

                    interceptorActivityOpts = new ActivityOptions
                    {
                        StartToCloseTimeout = effectiveTimeout,
                        HeartbeatTimeout = effectiveHeartbeat,
                        RetryPolicy = effectiveRetry,
                        // Summary is set per-tool at dispatch time: $"intercept:{toolName}"
                    };

                    foreach (var toolReg in cached.Registration.Tools)
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
                var useApprovalScopes = cached.Registration.UseApprovalScopes;
                bool useApprovalScopeStoreMode = false;
                string? alwaysScopesStoreKey = null;
                bool applyAlwaysScopesAtSessionStart = false;
                int maxAlwaysScopeCacheRecords = 0;
                int maxAlwaysScopeCacheBytes = 0;
                TimeSpan approvalScopeActivityTimeout = TimeSpan.Zero;
                int approvalScopeActivityMaximumAttempts = 0;

                if (useApprovalScopes)
                {
                    var scopeOpts = cached.Registration.ApprovalScopesOptions!;

                    // Options validation (positive bounds) — same as direct-start path.
                    if (scopeOpts.MaxAlwaysScopeCacheRecords <= 0)
                        throw new InvalidOperationException($"ApprovalScopesOptions.MaxAlwaysScopeCacheRecords for agent '{cached.Registration.Name}' must be a positive integer.");
                    if (scopeOpts.MaxAlwaysScopeCacheBytes <= 0)
                        throw new InvalidOperationException($"ApprovalScopesOptions.MaxAlwaysScopeCacheBytes for agent '{cached.Registration.Name}' must be a positive integer.");
                    if (scopeOpts.ApprovalScopeActivityMaximumAttempts <= 0)
                        throw new InvalidOperationException($"ApprovalScopesOptions.ApprovalScopeActivityMaximumAttempts for agent '{cached.Registration.Name}' must be a positive integer.");
                    if (scopeOpts.ApprovalScopeActivityTimeout <= TimeSpan.Zero)
                        throw new InvalidOperationException($"ApprovalScopesOptions.ApprovalScopeActivityTimeout for agent '{cached.Registration.Name}' must be greater than TimeSpan.Zero.");

                    useApprovalScopeStoreMode = scopeOpts.ApprovalScopeStore is not null
                                             || cached.AgentsOptions.ApprovalScopeStore is not null;
                    alwaysScopesStoreKey = scopeOpts.AlwaysScopesStoreKey;
                    applyAlwaysScopesAtSessionStart = scopeOpts.ApplyAlwaysScopesAtSessionStart;
                    maxAlwaysScopeCacheRecords = scopeOpts.MaxAlwaysScopeCacheRecords;
                    maxAlwaysScopeCacheBytes = scopeOpts.MaxAlwaysScopeCacheBytes;
                    approvalScopeActivityTimeout = scopeOpts.ApprovalScopeActivityTimeout;
                    approvalScopeActivityMaximumAttempts = scopeOpts.ApprovalScopeActivityMaximumAttempts;
                }

                resolvedConfig = new ProxyResolvedWorkerConfig
                {
                    MaxToolCallsPerTurn = resolvedMaxToolCalls ?? cached.Registration.MaxToolCallsPerTurn,
                    UseExternalStoreMode = resolvedExternalStore ?? false,
                    ToolActivityOptions = resolvedToolOpts ?? new Dictionary<string, ActivityOptions>(),
                    CompactionStrategyKey = cached.CompactionStrategyKey,
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

            // Step 6d: evaluate the compaction trigger (Q2 = B). Compaction is opt-in; if no
            // strategy was configured, or if no external store backs the agent, skip
            // evaluation entirely. The trigger inspects the audit canonical view of the
            // store; populated targets flow up to the workflow which will dispatch
            // CompactHistory after AppendAgentTurn writes the current turn.
            bool compactionNeeded = false;
            IReadOnlyList<string>? compactionTargets = null;
            if (cached.CompactionStrategy is not null && cached.HistoryStore is not null && isFinal)
            {
                // Only evaluate at end-of-turn (isFinal) — tool-call iterations are
                // mid-turn and not the right boundary for compaction.
                var auditView = await cached.HistoryStore
                    .LoadAsync(sessionId.WorkflowId, applyCompaction: false, ct)
                    .ConfigureAwait(false);
                var targets = cached.CompactionStrategy.EvaluateTrigger(auditView);
                if (targets is { Count: > 0 })
                {
                    compactionNeeded = true;
                    compactionTargets = targets;
                }
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
                CompactionNeeded = compactionNeeded,
                CompactionTargetMessageIds = compactionTargets,
            };
        }
        catch (Exception ex)
        {
            span?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogAgentActivityFailed(input.AgentName, sessionId.WorkflowId, ex);

            if (cached.ContextProviders.Count > 0)
            {
                var invokedCtx = new Microsoft.Agents.AI.AIContextProvider.InvokedContext(
                    cached.Agent, session, requestMessages: augmentedMessages, invokeException: ex);
                foreach (var provider in cached.ContextProviders)
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

            throw;
        }
        finally
        {
            TemporalAgentContext.SetCurrent(null);
        }
    }

    /// <summary>
    /// Resolves (and lazily composes) a durable agent's cached state.
    /// </summary>
    /// <exception cref="AgentNotRegisteredException">
    /// Thrown when no durable agent with this name is registered.
    /// </exception>
    internal CachedDurableAgent ResolveDurableAgent(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return _durableAgentCache.GetOrAdd(name, static (n, ctx) =>
        {
            var (self, providerServices) = ctx;
            return self.ComposeDurableAgent(n, providerServices);
        }, (this, services));
    }

    private CachedDurableAgent ComposeDurableAgent(string name, IServiceProvider providerServices)
    {
        var agentsOptions = providerServices.GetService<TemporalAgentsOptions>()
            ?? throw new InvalidOperationException(
                "TemporalAgentsOptions is not registered in DI. Call AddTemporalAgents on the worker " +
                "builder before invoking the durable-agent dispatch path.");

        if (!agentsOptions.DurableAgentRegistrations.TryGetValue(name, out var registration))
        {
            throw new AgentNotRegisteredException(name);
        }

        var userClient = registration.ChatClient(providerServices);
        AIContextProvider[] providers = registration.ContextProviderFactories.Count == 0
            ? []
            : registration.ContextProviderFactories.Select(f => f(providerServices)).ToArray();

        IChatClient chatClient = userClient;

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

        var chatOptions = registration.ChatOptions?.Clone() ?? new ChatOptions();
        chatOptions.Instructions = registration.Instructions;
        chatOptions.Tools = toolList.Count > 0 ? toolList.Cast<AITool>().ToList() : null;

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

        // Compose the user's ConfigureAgentPipeline around the ChatClientAgent. Per-agent
        // callback wins; worker-level DefaultConfigureAgentPipeline is the fallback. If neither
        // is set, the cached agent IS the bare ChatClientAgent (no decorators).
        var configurePipeline = registration.ConfigureAgentPipeline
            ?? agentsOptions.DefaultConfigureAgentPipeline;

        AIAgent agent;
        if (configurePipeline is null)
        {
            agent = chatClientAgent;
        }
        else
        {
            var agentBuilder = new AIAgentBuilder(chatClientAgent);
            configurePipeline.Invoke(agentBuilder);
            agent = agentBuilder.Build(providerServices);
        }

        // Step 3c.3: B-check (runtime fallback to startup C-check). Walk the composed agent
        // chain for FunctionInvocationDelegatingAgent (matched by Type.FullName because the
        // type is internal sealed in Microsoft.Agents.AI). This catches misconfigurations the
        // C-check at IPostConfigureOptions time couldn't reach — e.g., factory-deferred DI
        // patterns where the chat-client factory isn't resolvable at host build time, or
        // worker-only paths that bypass the IPostConfigureOptions hook entirely. Same exception
        // shape, same OffendingType field as the C-check.
        foreach (var link in AgentChainWalker.WalkAIAgent(agent))
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
        // Computed once here; the per-turn dispatch path just reads the cached bool.
        var hasOTelAgent = AgentChainWalker.Contains<OpenTelemetryAgent>(agent);
        var hasOTelChatClient = AgentChainWalker.Contains<OpenTelemetryChatClient>(chatClient);
        var suppressAgentTurnSpan = hasOTelAgent || hasOTelChatClient;

        // Per-agent factory wins; worker-level factory is the fallback.
        var storeFactory = registration.HistoryStore ?? agentsOptions.HistoryStore;
        var resolvedStore = storeFactory?.Invoke(providerServices);

        // Step 6d: resolve the effective compaction-strategy key (per-agent →
        // worker default → null) and pre-resolve the keyed strategy instance from DI.
        // Compaction is opt-in; null strategy means "no compaction" and the per-turn path
        // will short-circuit without consulting the strategy.
        var effectiveCompactionKey =
            registration.CompactionStrategyKey
            ?? agentsOptions.DefaultCompactionStrategy;
        Compaction.ICompactionStrategy? resolvedStrategy = null;
        if (!string.IsNullOrEmpty(effectiveCompactionKey))
        {
            resolvedStrategy = providerServices
                .GetKeyedService<Compaction.ICompactionStrategy>(effectiveCompactionKey);
            if (resolvedStrategy is null)
            {
                throw new InvalidOperationException(
                    $"Agent '{registration.Name}' is configured with compaction strategy " +
                    $"'{effectiveCompactionKey}', but no ICompactionStrategy is registered " +
                    $"under that keyed-DI name. Built-in keys (truncation, sliding-window, " +
                    $"summarization) are pre-registered by AddTemporalAgents.");
            }
        }

        IReadOnlyList<AITool> toolsAsAITools = [.. resolvedTools.Values.Cast<AITool>()];

        // Resolve tool interceptor: per-agent factory wins over worker-level default (H1 rule).
        var interceptorFactory = registration.ToolInterceptorFactory
            ?? agentsOptions.DefaultToolInterceptor;
        var resolvedInterceptor = interceptorFactory?.Invoke(providerServices);

        // Feature B: resolve approval-scope store only when this agent has opted into
        // approval scopes. A worker-default store must not introduce construction side effects
        // for agents that did not call UseApprovalScopes().
        IApprovalScopeStore? resolvedApprovalScopeStore = null;
        if (registration.UseApprovalScopes && registration.ApprovalScopesOptions is not null)
        {
            var approvalScopeStoreFactory = registration.ApprovalScopesOptions.ApprovalScopeStore
                ?? agentsOptions.ApprovalScopeStore;
            resolvedApprovalScopeStore = approvalScopeStoreFactory?.Invoke(providerServices);
        }

        return new CachedDurableAgent(
            agent,
            resolvedTools,
            registration,
            resolvedStore,
            providers,
            agentsOptions,
            suppressAgentTurnSpan,
            effectiveCompactionKey,
            resolvedStrategy,
            toolsAsAITools,
            resolvedInterceptor,
            resolvedApprovalScopeStore);
    }

    /// <summary>Fully-qualified type name of MAF's internal function-invocation decorator.</summary>
    private const string FunctionInvocationDelegatingAgentFullName =
        Internal.AgentInternalConstants.FunctionInvocationDelegatingAgentFullName;

    /// <summary>
    /// Performs an in-session history compaction. Dispatched by the workflow when the
    /// activity-side trigger evaluator (Q2 = B) flags <c>CompactionNeeded</c> on a step
    /// result. Loads the audit canonical view, invokes the configured strategy, appends the
    /// resulting marker to the store.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Per Q12, the workflow pre-mints <see cref="CompactHistoryInput.MarkerCorrelationId"/>
    /// via <see cref="Workflow.NewGuid"/> so activity retries reproduce the same marker ID;
    /// the strategy uses this ID verbatim. Without that, retries would double-write markers
    /// and corrupt the projection contract.
    /// </para>
    /// <para>
    /// LLM-using strategies (summarization) invoke the resolved <see cref="IChatClient"/>
    /// inline within their <c>CompactAsync</c> body — this activity hosts the LLM call,
    /// preserving Q6's "one LLM call = one activity" invariant.
    /// </para>
    /// </remarks>
    [Activity("TemporalCommunity.Extensions.Agents.CompactHistory")]
    public async Task CompactHistoryAsync(CompactHistoryInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var ctx = ActivityExecutionContext.Current;
        var ct = ctx.CancellationToken;

        var cached = ResolveDurableAgent(input.AgentName);
        if (cached.CompactionStrategy is null)
        {
            throw new InvalidOperationException(
                $"CompactHistory was dispatched for agent '{input.AgentName}' but no " +
                $"ICompactionStrategy was resolved at compose time. This indicates a workflow " +
                $"dispatching compaction for an agent that does not have UseCompaction configured.");
        }
        if (cached.HistoryStore is null)
        {
            throw new InvalidOperationException(
                $"CompactHistory was dispatched for agent '{input.AgentName}' but no " +
                $"IAgentHistoryStore is configured. Compaction without external history " +
                $"storage is not supported — the marker has nowhere to live.");
        }

        ctx.Heartbeat($"compacting {input.TargetMessageIds.Count} entries for agent '{input.AgentName}'");

        // Resolve a chat client for strategies that need one (summarization). Truncation and
        // sliding-window ignore the client.
        var chatClient = cached.Registration.ChatClient(services);

        var rawHistory = await cached.HistoryStore
            .LoadAsync(input.SessionId, applyCompaction: false, ct)
            .ConfigureAwait(false);

        var context = new Compaction.CompactionContext
        {
            RawEntries = rawHistory,
            TargetMessageIds = input.TargetMessageIds,
            AgentName = input.AgentName,
            SessionId = input.SessionId,
            MarkerCorrelationId = input.MarkerCorrelationId,
            ChatClient = chatClient,
        };

        var result = await cached.CompactionStrategy
            .CompactAsync(context, ct)
            .ConfigureAwait(false);

        await cached.HistoryStore
            .AppendAsync(input.SessionId, [result.Marker], ct)
            .ConfigureAwait(false);
    }

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

        var cached = ResolveDurableAgent(input.AgentName);

        if (cached.ToolInterceptor is null)
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
            decision = await cached.ToolInterceptor
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
        // UpdatedStateBag when the serialized bag actually changed, so the no-mutation
        // case stays null (wire-compatible with old histories). The workflow merges this
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
        var cached = ResolveDurableAgent(input.AgentName);

        if (cached.ApprovalScopeStore is null)
        {
            // No store configured — return empty result gracefully.
            return new LoadAlwaysScopesResult { Scopes = [] };
        }

        var records = await cached.ApprovalScopeStore
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
        var cached = ResolveDurableAgent(input.AgentName);

        if (cached.ApprovalScopeStore is null)
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

        await cached.ApprovalScopeStore
            .AppendAsync(input.AgentName, input.StoreKey, record, ct)
            .ConfigureAwait(false);
    }

    [Activity("TemporalCommunity.Extensions.Agents.InvokeAgentTool")]
    public async Task<InvokeAgentToolResult> InvokeAgentToolAsync(InvokeAgentToolInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var ctx = ActivityExecutionContext.Current;
        var ct = ctx.CancellationToken;

        var cached = ResolveDurableAgent(input.AgentName);

        if (!cached.Tools.TryGetValue(input.ToolName, out var fn))
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
                var temporalContext = new TemporalAgentContext(ctx.TemporalClient, session, services);
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

/// <summary>
/// Input for <see cref="AgentActivities.ReduceHistoryInStoreAsync"/>.
/// </summary>
internal sealed class ReduceHistoryInStoreInput
{
    /// <summary>The agent name (used to resolve the per-agent <see cref="IAgentHistoryStore"/>).</summary>
    public required string AgentName { get; init; }

    /// <summary>The session ID (agent workflow ID) whose external history should be reduced.</summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// Maximum number of entries to retain in the store after reduction.
    /// </summary>
    public required int MaxEntryCount { get; init; }
}

/// <summary>
/// Input for <see cref="AgentActivities.CompactHistoryAsync"/>.
/// </summary>
internal sealed class CompactHistoryInput
{
    /// <summary>The agent name (resolves the cached compaction strategy + history store).</summary>
    public required string AgentName { get; init; }

    /// <summary>The session ID (agent workflow ID).</summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// Source-entry correlation IDs to compact — handed verbatim to
    /// <see cref="Compaction.CompactionContext.TargetMessageIds"/>.
    /// </summary>
    public required IReadOnlyList<string> TargetMessageIds { get; init; }

    /// <summary>
    /// Pre-minted marker correlation ID. Workflow generates this via
    /// <see cref="Temporalio.Workflows.Workflow.NewGuid"/> so activity retries are idempotent
    /// (same ID across retries → AppendAsync is at-least-once-safe without duplicate
    /// markers — assuming the store de-duplicates on CorrelationId, which is the
    /// implementor contract).
    /// </summary>
    public required string MarkerCorrelationId { get; init; }
}
