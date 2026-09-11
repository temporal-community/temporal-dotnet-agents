using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using TemporalCommunity.Extensions.Agents.Approvals;
using TemporalCommunity.Extensions.Agents.Scheduling;
using TemporalCommunity.Extensions.Agents.Session;
using TemporalCommunity.Extensions.Agents.State;
using TemporalCommunity.Extensions.Agents.Tools;
using TemporalCommunity.Extensions.Agents.Workflows;
using TemporalCommunity.Extensions.AI.Approvals;
using TemporalCommunity.Extensions.AI.Session;
using TemporalCommunity.Extensions.AI.Tools;
using Temporalio.Workflows;

using AgentsInterceptorInput = TemporalCommunity.Extensions.Agents.Workflows.DurableToolInterceptorInput;
using AgentsInterceptorResult = TemporalCommunity.Extensions.AI.Tools.DurableToolInterceptorResult;
using AgentsToolOutcome = TemporalCommunity.Extensions.AI.Tools.DurableToolOutcome;

namespace TemporalCommunity.Extensions.Agents;

/// <summary>
/// An <see cref="AIAgent"/> for use inside orchestrating Temporal workflows.
/// Drives the durable-agent dispatch loop (<c>RunDurableAgentStep</c> + <c>InvokeAgentTool</c>)
/// directly via <see cref="Workflow.ExecuteActivityAsync{TActivityInstance, TResult}"/>.
/// Maintains conversation history as workflow state (replayed from event history).
/// </summary>
/// <remarks>
/// Use this type only from inside a Temporal workflow (e.g., via
/// <see cref="WorkflowAgents.GetTemporalAgent"/>). For external/host code
/// (API servers, CLIs, console apps), resolve a Temporal agent proxy via
/// <see cref="ServiceCollectionExtensions.GetTemporalAgentProxy"/>.
/// </remarks>
public sealed class TemporalAIAgent : AIAgent
{
    private readonly string _agentName;
    private readonly ActivityOptions _activityOptions;
    // Cached after the first successful worker-settings resolution step so subsequent turns
    // skip the resolution handshake and use the resolved value rather than the hard-coded default.
    private bool _settingsResolved;
    private int _resolvedMaxToolCallsPerTurn = 20;

    // Per-tool activity options resolved on first step (P1-2 fix).
    private IReadOnlyDictionary<string, ActivityOptions>? _toolActivityOptions;

    // Feature L — interceptor config resolved on first step.
    private ActivityOptions? _interceptorActivityOptions;
    private IReadOnlyDictionary<string, ActivityOptions>? _interceptorToolActivityOptions;
    private IReadOnlyList<string>? _interceptorSkippedTools;
    private IReadOnlyList<string>? _requiresApprovalTools;
    // Feature B: scope-aware tool lists captured from first-step resolved config (Task 4.7).
    private IReadOnlyList<string>? _scopeAwareTools;
    private IReadOnlyList<string>? _scopeAwareApprovalTools;

    internal TemporalAIAgent(string agentName, ActivityOptions? activityOptions = null)
    {
        _agentName = agentName;

        // A caller-supplied ActivityOptions is used verbatim — including a null RetryPolicy, which
        // is then the caller's own choice. The library-built default must NOT leave RetryPolicy
        // null: the server reads null as "use the server default", which is MaximumAttempts = 0,
        // i.e. UNLIMITED retries. A sub-agent LLM step that fails deterministically (an exhausted
        // scripted client, a provider error the classifier cannot positively identify) would then
        // retry forever and hang the orchestrating workflow. ResolveForModel applies the same
        // bounded backstop DefaultTemporalAgentClient already applies to the AgentWorkflow path,
        // so the idiomatic sub-agent path is no longer the one unbounded LLM dispatch.
        _activityOptions = activityOptions ?? new ActivityOptions
        {
            StartToCloseTimeout = TimeSpan.FromMinutes(30),
            HeartbeatTimeout = TimeSpan.FromMinutes(5),
            RetryPolicy = TemporalCommunity.Extensions.AI.Internal.DefaultRetryPolicy.ResolveForModel(null),
            Summary = AgentActivities.BuildActivitySummary(_agentName),
        };
    }

    /// <inheritdoc/>
    public override string? Name => _agentName;

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
    {
        var sessionId = TemporalAgentSessionId.WithDeterministicKey(_agentName, Workflow.NewGuid());
        return new ValueTask<AgentSession>(new TemporalAgentSession(sessionId));
    }

    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
    {
        if (session is not TemporalAgentSession temporalSession)
        {
            throw new InvalidOperationException(
                $"Expected a {nameof(TemporalAgentSession)} but got '{session.GetType().Name}'.");
        }

        return new ValueTask<JsonElement>(temporalSession.Serialize(jsonSerializerOptions));
    }

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement serializedState,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
    {
        return new ValueTask<AgentSession>(TemporalAgentSession.Deserialize(serializedState, jsonSerializerOptions));
    }

    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (!Workflow.InWorkflow) throw new InvalidOperationException("TemporalAIAgent must be used inside a Temporal workflow. Use TemporalAIAgentProxy for external-context invocation.");

        session ??= await CreateSessionAsync(cancellationToken).ConfigureAwait(true);

        // The session — not this agent — owns the conversation's StateBag, so that one agent
        // instance can drive several sessions without their state colliding. A foreign
        // AgentSession has nowhere to hold that state, so reject it here instead of silently
        // dropping every mutation. SerializeSessionCoreAsync already rejects the same case.
        if (session is not TemporalAgentSession temporalSession)
        {
            throw new InvalidOperationException(
                $"Expected a {nameof(TemporalAgentSession)} but got '{session.GetType().Name}'. " +
                $"Create the session with {nameof(CreateSessionAsync)} on this agent.");
        }

        // SECURITY: a session now carries its conversation history, so running one agent's session
        // on another agent would flatten that whole transcript into this agent's prompt and into
        // this agent's Temporal event history — a cross-agent (and, in a multi-tenant host,
        // cross-tenant) disclosure. TemporalAIAgentProxy has always checked this; before session
        // ownership a mismatched session carried only an ID and a StateBag, so the check mattered
        // less here. It matters now. Matching is by agent name, so two agent instances resolved
        // from the same registration remain interchangeable.
        if (!string.Equals(temporalSession.SessionId.AgentName, _agentName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The provided session belongs to agent '{temporalSession.SessionId.AgentName}', not agent '{_agentName}'. " +
                "Create the session from the same agent that will run it.");
        }

        // Reject a second run over the same live session before any activity is scheduled — two
        // interleaved runs would append to one history and merge into one StateBag with no
        // defined ordering. Distinct sessions on this same agent remain free to run in parallel.
        temporalSession.EnterRun();
        try
        {
            return await RunTurnAsync(messages, temporalSession, options).ConfigureAwait(true);
        }
        finally
        {
            temporalSession.ExitRun();
        }
    }

    /// <remarks>
    /// Takes no <see cref="CancellationToken"/> deliberately. The turn body never used the caller's
    /// token: every activity dispatched here relies on <c>Workflow.CancellationToken</c>, which is
    /// the documented pattern for workflow code. A caller-supplied child token would therefore be
    /// ignored — that is pre-existing behaviour, not something this extraction introduced. Honouring
    /// one would mean threading it into each <c>ExecuteActivityAsync</c> call, not adding a
    /// parameter here.
    /// </remarks>
    private async Task<AgentResponse> RunTurnAsync(
        IEnumerable<ChatMessage> messages,
        TemporalAgentSession temporalSession,
        AgentRunOptions? options)
    {
        IList<string>? enableToolNames = null;
        bool enableToolCalls = true;
        string? callerCorrelationId = null;
        ChatResponseFormat? responseFormat = null;

        if (options is TemporalAgentRunOptions temporalOptions)
        {
            enableToolCalls = temporalOptions.EnableToolCalls;
            enableToolNames = temporalOptions.EnableToolNames;
            callerCorrelationId = temporalOptions.CorrelationId;
        }
        else if (options is ChatClientAgentRunOptions chatOptions)
        {
            responseFormat = chatOptions.ChatOptions?.ResponseFormat;
        }

        if (options?.ResponseFormat is { } format)
        {
            responseFormat = format;
        }

        var request = new RunRequest([.. messages], responseFormat, enableToolCalls, enableToolNames)
        {
            OrchestrationId = Workflow.Info.WorkflowId,
            CorrelationId = string.IsNullOrEmpty(callerCorrelationId)
                ? Workflow.NewGuid().ToString("N")
                : callerCorrelationId,
        };

        temporalSession.AppendHistoryEntry(AgentSessionRequest.FromRunRequest(request, Workflow.UtcNow));

        var sessionId = temporalSession.SessionId;

        Workflow.Logger.LogInWorkflowAgentDispatching(_agentName, temporalSession.RunCount);

        // Drive the durable-agent dispatch loop for sub-agents inside an orchestrating workflow.
        // Mirrors the AgentWorkflow main loop but without continue-as-new / search attributes /
        // history reduction (the orchestrating workflow owns those concerns).
        var accumulated = new List<ChatMessage>();
        foreach (var entry in temporalSession.History)
        {
            foreach (var m in entry.Messages)
                accumulated.Add(m);
        }

        var allTurnMessages = new List<ChatMessage>();
        UsageDetails? totalUsage = null;
        var maxIterations = _resolvedMaxToolCallsPerTurn;

        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            var stepInput = new AgentStepInput
            {
                AgentName = _agentName,
                Request = request,
                AccumulatedMessages = accumulated,
                SerializedStateBag = temporalSession.SerializeStateBag(),
                SessionId = sessionId,
                NeedsWorkerSettingsResolution = !_settingsResolved && iteration == 0,
            };

            var stepResult = await Workflow.ExecuteActivityAsync(
                (AgentActivities a) => a.RunDurableAgentStepAsync(stepInput),
                _activityOptions);

            // Persist the step's StateBag mutations on the session so context-provider state
            // (e.g. WorkingSetContextProvider) survives across steps, turns, and continue-as-new.
            // Context providers run inside the LLM-step activity and are trusted-tier by design,
            // so their output is overlaid unfiltered — unlike tool/interceptor write-backs below,
            // which are deny-list filtered. Overlay rather than replace: a hash-gated step returns
            // a null or partial bag, and replacing would wipe keys the workflow thread wrote
            // between activities.
            temporalSession.OverlayTrustedStateBag(stepResult.UpdatedStateBag);

            if (stepResult.ResolvedWorkerConfig is not null)
            {
                _settingsResolved = true;
                _resolvedMaxToolCallsPerTurn = stepResult.ResolvedWorkerConfig.MaxToolCallsPerTurn;
                maxIterations = _resolvedMaxToolCallsPerTurn;
            }

            // Capture worker-side config from the first resolution step.
            if (iteration == 0 && stepResult.ResolvedWorkerConfig is { } resolvedConfig)
            {
                _toolActivityOptions = resolvedConfig.ToolActivityOptions;      // per-tool InvokeAgentTool options (P1-2 fix)
                _interceptorActivityOptions = resolvedConfig.InterceptorActivityOptions;
                _interceptorToolActivityOptions = resolvedConfig.InterceptorToolActivityOptions;
                _interceptorSkippedTools = resolvedConfig.InterceptorSkippedTools;
                _requiresApprovalTools = resolvedConfig.RequiresApprovalTools;
                // Feature B (Task 4.7): capture scope-aware tool lists.
                _scopeAwareTools = resolvedConfig.ScopeAwareTools;
                _scopeAwareApprovalTools = resolvedConfig.ScopeAwareApprovalTools;

                // Feature B — Task 7.2: warn when scope-aware required tools are present.
                // TemporalAIAgent has no DurableApprovalMixin so workflow-parked approval is
                // not supported. When the interceptor returns PauseForApproval for a
                // scope-aware required tool (because no matching scope record exists), the
                // decision degrades to Block below. Emitting a LogWarning here after the first
                // step's ResolvedWorkerConfig arrives makes this degradation visible before
                // the tool call rather than silently at block time.
                // Note: TemporalAIAgent's interceptor input still passes a null SerializedStateBag
                // (constructed below), so scope records in StateBag are not consulted on this path
                // even though the session now has a StateBag to consult.
                if (resolvedConfig.ScopeAwareApprovalTools is { Count: > 0 } scopeApprovalTools)
                {
                    var names = string.Join(", ", scopeApprovalTools);
                    Workflow.Logger.LogWarning(
                        "Tool(s) '{ToolNames}' are configured with RequireApproval().ScopeAware() but this execution " +
                        "context does not support workflow-parked approval. Unapproved calls will be blocked.",
                        names);
                }
            }

            if (stepResult.Usage is not null)
            {
                totalUsage ??= new UsageDetails();
                totalUsage.InputTokenCount = (totalUsage.InputTokenCount ?? 0) + (stepResult.Usage.InputTokenCount ?? 0);
                totalUsage.OutputTokenCount = (totalUsage.OutputTokenCount ?? 0) + (stepResult.Usage.OutputTokenCount ?? 0);
                totalUsage.TotalTokenCount = (totalUsage.TotalTokenCount ?? 0) + (stepResult.Usage.TotalTokenCount ?? 0);
            }

            accumulated.Add(stepResult.AssistantMessage);
            allTurnMessages.Add(stepResult.AssistantMessage);

            if (stepResult.IsFinal || stepResult.ToolCalls is null || stepResult.ToolCalls.Count == 0)
            {
                var response = new AgentResponse
                {
                    Messages = allTurnMessages,
                    Usage = totalUsage,
                    CreatedAt = Workflow.UtcNow,
                };

                temporalSession.AppendHistoryEntry(AgentSessionResponse.FromAgentResponse(
                    request.CorrelationId!, response, Workflow.UtcNow));

                return response;
            }

            var toolCalls = stepResult.ToolCalls;

            var registeredToolNames = _toolActivityOptions?.Keys.ToArray() ?? [];
            var enabledToolNamesForDispatch = request.EnableToolNames is { } requestedNames
                ? requestedNames.ToArray()
                : null;
            var enabledToolCalls = new bool[toolCalls.Count];
            for (var i = 0; i < toolCalls.Count; i++)
            {
                enabledToolCalls[i] = AgentRunToolSelectionPolicy.IsCallEnabled(
                    toolCalls[i].Name,
                    registeredToolNames,
                    request.EnableToolCalls,
                    enabledToolNamesForDispatch);
                if (!enabledToolCalls[i])
                {
                    Workflow.Logger.LogRunToolCallBlocked(
                        _agentName,
                        Workflow.Info.WorkflowId,
                        request.CorrelationId ?? string.Empty,
                        iteration + 1,
                        toolCalls[i].Name);
                }
            }

            // Feature L: Phase 1 — fan out interceptor activities if configured.
            AgentsInterceptorResult[]? interceptorResults = null;
            if (_interceptorActivityOptions is { } interceptorOpts)
            {
                var interceptorTasks = new List<Task<AgentsInterceptorResult>>(toolCalls.Count);
                for (var i = 0; i < toolCalls.Count; i++)
                {
                    var tc = toolCalls[i];
                    if (!enabledToolCalls[i])
                    {
                        interceptorTasks.Add(Task.FromResult(
                            new AgentsInterceptorResult { Outcome = AgentsToolOutcome.Proceed }));
                        continue;
                    }

                    if (DurableToolDecisionPolicy.IsToolSkipped(tc.Name, _interceptorSkippedTools))
                    {
                        interceptorTasks.Add(Task.FromResult(
                            new AgentsInterceptorResult { Outcome = AgentsToolOutcome.Proceed }));
                    }
                    else
                    {
                        var interceptorInput = new AgentsInterceptorInput
                        {
                            AgentName = _agentName,
                            ToolName = tc.Name,
                            Arguments = tc.Arguments is null ? null : new Dictionary<string, object?>(tc.Arguments),
                            CallId = tc.CallId,
                            // Still null here, but NOT because there is no StateBag to send — as of
                            // session ownership there is one (see toolDispatchStateBag below, and
                            // AgentWorkflow, which does pass its bag to interceptors). Passing it
                            // would let a scope-aware interceptor find a matching approval-scope
                            // record and return Proceed where it currently returns
                            // PauseForApproval, which degrades to Block on this path. That is a
                            // change to approval semantics and needs its own design and tests, so
                            // it is deliberately left alone here rather than altered in passing.
                            SerializedStateBag = null,
                            // Feature B (Task 4.7): populate scope-aware fields.
                            ScopeAware = _scopeAwareTools?.Contains(tc.Name, StringComparer.OrdinalIgnoreCase) == true,
                            RequiresApproval = _requiresApprovalTools?.Contains(tc.Name, StringComparer.OrdinalIgnoreCase) == true
                                || _scopeAwareApprovalTools?.Contains(tc.Name, StringComparer.OrdinalIgnoreCase) == true,
                        };
                        // See also: AgentWorkflow.ExecuteDurableAgentTurnAsync (MAF path) — parallel typed dispatch
                        interceptorTasks.Add(Workflow.ExecuteActivityAsync(
                            (AgentActivities a) => a.RunToolInterceptorAsync(interceptorInput),
                            DurableToolDecisionPolicy.ResolveInterceptorActivityOptions(tc.Name, interceptorOpts, _interceptorToolActivityOptions)));
                    }
                }
                interceptorResults = await Workflow.WhenAllAsync(interceptorTasks).ConfigureAwait(true);
            }

            // Snapshot the session bag once for this iteration's tool fan-out: every tool in the
            // turn must observe the same pre-fan-out state, and re-serializing per tool would both
            // cost more and risk handing different tools different views.
            var toolDispatchStateBag = temporalSession.SerializeStateBag();

            // Feature L: Phase 2 — process decisions. PauseForApproval degrades to Block
            // since TemporalAIAgent has no DurableApprovalMixin.
            var toolTasks = new List<Task<InvokeAgentToolResult>?>(toolCalls.Count);
            var syntheticResults = new string?[toolCalls.Count];

            for (var i = 0; i < toolCalls.Count; i++)
            {
                var tc = toolCalls[i];
                if (!enabledToolCalls[i])
                {
                    syntheticResults[i] = AgentRunToolSelectionPolicy.CreateBlockedResult(tc.Name);
                    toolTasks.Add(null);
                    continue;
                }

                var interceptorResult = interceptorResults?[i];
                // Determine effective outcome (Rule 2: RequireApproval floor, Block never overridden).
                var outcome = DurableToolDecisionPolicy.GetEffectiveOutcome(
                    interceptorResult?.Outcome, tc.Name, _requiresApprovalTools);

                switch (outcome)
                {
                    case AgentsToolOutcome.Proceed:
                        var toolInput = new InvokeAgentToolInput
                        {
                            AgentName = _agentName,
                            ToolName = tc.Name,
                            Arguments = DurableToolDecisionPolicy.GetEffectiveArguments(interceptorResult?.ModifiedArguments, (IReadOnlyDictionary<string, object?>?)tc.Arguments),
                            CallId = tc.CallId,
                            // X-1: seed the tool with accumulated session state so context
                            // providers / scope-aware tools see it.
                            //
                            // KNOWN LIMITATION (pre-existing, not introduced by session ownership):
                            // this bag is currently ignored on the sub-agent path. InvokeAgentToolInput
                            // carries no SessionId, so InvokeAgentToolAsync derives the session from
                            // ActivityExecutionContext.Info.WorkflowId — which here is the ORCHESTRATING
                            // workflow's ID, not a "ta-{agent}-{key}" session ID. The parse fails, no
                            // TemporalAgentContext is established, and the tool's StateBag write-back
                            // comes back null. So toolStateBagWriteBacks below is always all-null on
                            // this path and the merge is a no-op. RunDurableAgentStepAsync does not
                            // have this problem because AgentStepInput does carry SessionId and it is
                            // preferred over the workflow ID; that is why context-provider StateBag
                            // threading works for sub-agents while tool write-backs do not.
                            // Closing this means adding SessionId to InvokeAgentToolInput and
                            // preferring it — a wire addition with approval-routing implications, so
                            // it needs its own design rather than a change in passing.
                            SerializedStateBag = toolDispatchStateBag,
                        };
                        // Use per-tool ActivityOptions when resolved (honours NoRetry(), WithTimeout(), etc.)
                        // falling back to the shared _activityOptions (P1-2 fix).
                        //
                        // The fallback is unreachable on this branch and is kept only as a
                        // structural guard. Reaching here requires enabledToolCalls[i], and
                        // AgentRunToolSelectionPolicy.IsCallEnabled only returns true when the tool
                        // name is present in registeredToolNames — which is exactly
                        // _toolActivityOptions.Keys. TryGetToolValue then compares with the same
                        // OrdinalIgnoreCase semantics, so a name that passed the gate always has an
                        // entry. A null _toolActivityOptions yields an empty registeredToolNames,
                        // which disables every call before dispatch. Every entry the resolution step
                        // produces already carries a bounded policy (see
                        // DefaultTemporalAgentClient.BuildDurableAgentToolActivityOptions, which
                        // runs each one through DefaultRetryPolicy.ResolveForTool), so tool dispatch
                        // was never the unbounded path — only the LLM step above was. The fallback
                        // now inherits the bounded model policy rather than a null one, so even a
                        // future change that does reach it cannot retry forever.
                        var toolDispatchOpts = DurableToolDecisionPolicy.TryGetToolValue(
                            _toolActivityOptions,
                            tc.Name,
                            out var perToolOpts)
                                ? perToolOpts
                                : _activityOptions;
                        toolTasks.Add(Workflow.ExecuteActivityAsync(
                            (AgentActivities a) => a.InvokeAgentToolAsync(toolInput),
                            toolDispatchOpts));
                        break;

                    case AgentsToolOutcome.PauseForApproval:
                        // TemporalAIAgent is a sub-agent inside an orchestrating workflow and
                        // has no DurableApprovalMixin — degrade to Block with a warning.
                        Workflow.Logger.LogWarning(
                            "Interceptor returned PauseForApproval for tool '{ToolName}' on agent '{AgentName}' " +
                            "but TemporalAIAgent does not support workflow-parked approval. Degrading to Block.",
                            tc.Name, _agentName);
                        syntheticResults[i] = $"[Blocked] Tool '{tc.Name}' requires approval but approval is not supported in sub-agent context.";
                        toolTasks.Add(null);
                        break;

                    case AgentsToolOutcome.Skip:
                        syntheticResults[i] = DurableToolDecisionPolicy.SkipMessage(interceptorResult?.Message);
                        toolTasks.Add(null);
                        break;

                    case AgentsToolOutcome.Block:
                    default:
                        syntheticResults[i] = DurableToolDecisionPolicy.BlockMessage(interceptorResult?.Message);
                        toolTasks.Add(null);
                        break;
                }
            }

            // Phase 3: await approved tasks.
            var pendingTasks = toolTasks.Where(t => t is not null).Cast<Task<InvokeAgentToolResult>>().ToList();
            InvokeAgentToolResult[]? toolResults = pendingTasks.Count > 0
                ? await Workflow.WhenAllAsync(pendingTasks).ConfigureAwait(true)
                : null;

            var functionResultContents = new List<AIContent>(toolCalls.Count);
            // X-1: collect tool StateBag write-backs by tool-call index for a deterministic
            // index-order merge (later index wins). toolResults is in ascending tool-call-index
            // order (pendingTasks was built by iterating toolTasks in index order).
            var toolStateBagWriteBacks = new JsonElement?[toolCalls.Count];
            var pendingIdx = 0;
            for (var i = 0; i < toolCalls.Count; i++)
            {
                if (syntheticResults[i] is { } synthetic)
                {
                    functionResultContents.Add(new FunctionResultContent(
                        callId: toolCalls[i].CallId,
                        result: synthetic));
                }
                else if (toolResults is not null && pendingIdx < toolResults.Length)
                {
                    var toolResult = toolResults[pendingIdx++];
                    toolStateBagWriteBacks[i] = toolResult.UpdatedStateBag;
                    functionResultContents.Add(new FunctionResultContent(
                        callId: toolCalls[i].CallId,
                        result: toolResult.Result));
                }
            }

            // X-1: merge tool StateBag mutations back so the next RunDurableAgentStep sees them.
            // Post-result; does not re-run tools (.NoRetry() semantics unaffected).
            // SECURITY: the merge applies the reserved approval-scope deny-list
            // (StateBagMerge.ApprovalScopesReservedPrefix). TemporalAIAgent has no approval-scope
            // store plumbing, so there is no custom always-scopes store key to pass — the prefix
            // deny-list (covering the session key and default always key) is sufficient here.
            temporalSession.MergeToolStateBagWriteBacks(
                toolStateBagWriteBacks,
                alwaysScopesStoreKey: null,
                Workflow.Logger);

            var toolResultMessage = new ChatMessage(ChatRole.Tool, functionResultContents);
            accumulated.Add(toolResultMessage);
            allTurnMessages.Add(toolResultMessage);
        }

        var iterCapResponse = new AgentResponse
        {
            Messages = allTurnMessages,
            Usage = totalUsage,
            CreatedAt = Workflow.UtcNow,
        };
        temporalSession.AppendHistoryEntry(AgentSessionResponse.FromAgentResponse(
            request.CorrelationId!, iterCapResponse, Workflow.UtcNow));

        return iterCapResponse;
    }

    protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Streaming is not supported for Temporal workflow agents.");
    }
}
