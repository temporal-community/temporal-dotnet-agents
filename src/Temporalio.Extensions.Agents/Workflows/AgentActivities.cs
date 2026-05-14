using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Temporalio.Activities;
using Temporalio.Extensions.Agents.HistoryStore;
using Temporalio.Extensions.Agents.Session;
using Temporalio.Extensions.Agents.State;
using Temporalio.Extensions.AI;
using Temporalio.Extensions.AI.Exceptions;
using Temporalio.Extensions.AI.Internal;
using Temporalio.Workflows;

namespace Temporalio.Extensions.Agents.Workflows;

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
    bool SuppressAgentTurnSpan);

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
    [Activity("Temporalio.Extensions.Agents.ReduceHistoryInStore")]
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
            // HistoryReducer signature expects IList<DurableSessionEntry>; materialize prior.
            // Materialize the result as a List<T> which satisfies both IList and IReadOnlyList.
            reduced = reducer(prior.ToList()).ToList();
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
    /// Appends the full turn — request entry + response entry carrying all messages accumulated
    /// across every LLM step and tool call — to the agent's external history store.
    /// Dispatched by <see cref="AgentWorkflow"/> after <c>ExecuteDurableAgentTurnAsync</c>
    /// returns, replacing the former in-activity append that was limited to the final assistant
    /// message and was skipped entirely when the iteration cap was hit.
    /// </summary>
    [Activity("Temporalio.Extensions.Agents.AppendAgentTurn")]
    public async Task AppendAgentTurnAsync(AppendAgentTurnInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var cached = ResolveDurableAgent(input.AgentName);
        if (cached.HistoryStore is null)
        {
            return;
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
    /// <c>Temporalio.Extensions.Agents.InvokeAgentTool</c> activities.
    /// </summary>
    [Activity("Temporalio.Extensions.Agents.RunDurableAgentStep")]
    public async Task<AgentStepResult> RunDurableAgentStepAsync(AgentStepInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var ctx = ActivityExecutionContext.Current;
        var ct = ctx.CancellationToken;

        var cached = ResolveDurableAgent(input.AgentName);

        // Fix 4 (P1-1 + P1-2): when the workflow was started by a proxy-only client, resolve
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
        var tools = cached.Tools.Values.Cast<AITool>().ToList();
        chatOptions.Tools = tools.Count > 0 ? tools : null;
        chatOptions.ResponseFormat = input.Request.ResponseFormat;

        if (!input.Request.EnableToolCalls)
        {
            chatOptions.Tools = null;
        }
        else if (input.Request.EnableToolNames is { Count: > 0 } enabledNames && chatOptions.Tools is not null)
        {
            chatOptions.Tools = [.. chatOptions.Tools.Where(t => enabledNames.Contains(t.Name))];
        }

        // Step 3c.2: LLM call goes through agent.RunStreamingAsync (NOT chatClient directly),
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
            var aggregated = new Microsoft.Agents.AI.AIContext();
            foreach (var provider in cached.ContextProviders)
            {
                var invokingCtx = new Microsoft.Agents.AI.AIContextProvider.InvokingContext(
                    cached.Agent, session, aggregated);
                var providerCtx = await provider.InvokingAsync(invokingCtx, ct).ConfigureAwait(false);
                providerAIContexts!.Add(providerCtx);
            }

            var extraMessages = new List<ChatMessage>();
            foreach (var ctxResult in providerAIContexts!)
            {
                if (ctxResult.Messages is { } extra)
                {
                    foreach (var m in extra)
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

        // Step 3c.3 (2b-enriched OTel): when the user's pipeline installs OpenTelemetryAgent or
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
            await foreach (var update in cached.Agent.RunStreamingAsync(
                    augmentedMessages, session, runOptions, ct).WithCancellation(ct).ConfigureAwait(false))
            {
                collected.Add(update);
                ctx.Heartbeat(update.Text);
            }

            var response = collected.ToAgentResponse();
            var assistantMessage = response.Messages.Count > 0
                ? response.Messages[response.Messages.Count - 1]
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
            ProxyResolvedWorkerConfig? resolvedConfig = input.NeedsWorkerSettingsResolution
                ? new ProxyResolvedWorkerConfig
                {
                    MaxToolCallsPerTurn = resolvedMaxToolCalls ?? cached.Registration.MaxToolCallsPerTurn,
                    UseExternalStoreMode = resolvedExternalStore ?? false,
                    ToolActivityOptions = resolvedToolOpts ?? new Dictionary<string, ActivityOptions>(),
                }
                : null;

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
        var providers = registration.ContextProviderFactories.Count == 0
            ? Array.Empty<AIContextProvider>()
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

        return new CachedDurableAgent(
            agent,
            resolvedTools,
            registration,
            resolvedStore,
            providers,
            agentsOptions,
            suppressAgentTurnSpan);
    }

    /// <summary>Fully-qualified type name of MAF's internal function-invocation decorator.</summary>
    /// <remarks>
    /// Hard-coded because the type is <see langword="internal sealed"/> in
    /// <c>Microsoft.Agents.AI</c> and not accessible via <c>typeof()</c>. The constant is
    /// duplicated from <see cref="Internal.DurableAgentPipelineValidator"/> deliberately —
    /// both call sites share the same wire-format-stable contract with MAF; if MAF ever
    /// renames the type, both constants update in lockstep.
    /// </remarks>
    private const string FunctionInvocationDelegatingAgentFullName =
        "Microsoft.Agents.AI.FunctionInvocationDelegatingAgent";

    /// <summary>
    /// Per-tool activity used by durable agents. Looks up the named agent's local tool registry,
    /// invokes the tool with the supplied arguments, and returns the result.
    /// </summary>
    [Activity("Temporalio.Extensions.Agents.InvokeAgentTool")]
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

        try
        {
            _logger.LogAgentToolInvocationStarted(input.AgentName, input.ToolName);

            var arguments = input.Arguments is null
                ? new AIFunctionArguments()
                : new AIFunctionArguments(input.Arguments);

            var result = await fn.InvokeAsync(arguments, ct).ConfigureAwait(false);

            _logger.LogAgentToolInvocationCompleted(input.AgentName, input.ToolName);

            return new InvokeAgentToolResult
            {
                Result = result,
                CallId = input.CallId,
            };
        }
        catch (Exception ex)
        {
            span?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogAgentToolInvocationFailed(input.AgentName, input.ToolName, ex);
            throw;
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
