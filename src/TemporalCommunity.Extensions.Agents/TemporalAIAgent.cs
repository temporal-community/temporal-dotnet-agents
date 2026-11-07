using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using TemporalCommunity.Extensions.Agents.Scheduling;
using TemporalCommunity.Extensions.Agents.Session;
using TemporalCommunity.Extensions.Agents.State;
using TemporalCommunity.Extensions.Agents.Tools;
using TemporalCommunity.Extensions.Agents.Workflows;
using TemporalCommunity.Extensions.AI.Approvals;
using TemporalCommunity.Extensions.AI.Session;
using TemporalCommunity.Extensions.AI.Tools;
using Temporalio.Common;
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

    /// <summary>
    /// Runs this agent without leaving Temporal's workflow task scheduler.
    /// </summary>
    /// <remarks>
    /// This hides the corresponding <see cref="AIAgent.RunAsync(IEnumerable{ChatMessage}, AgentSession?, AgentRunOptions?, CancellationToken)"/>
    /// overload because the base implementation uses <c>ConfigureAwait(false)</c>, which is not
    /// safe for Temporal workflow continuations.
    /// </remarks>
    public new Task<AgentResponse> RunAsync(
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default) =>
        RunWorkflowAsync([], session, options, cancellationToken);

    /// <inheritdoc cref="AIAgent.RunAsync(string, AgentSession?, AgentRunOptions?, CancellationToken)"/>
    public new Task<AgentResponse> RunAsync(
        string message,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return RunWorkflowAsync([new ChatMessage(ChatRole.User, message)], session, options, cancellationToken);
    }

    /// <inheritdoc cref="AIAgent.RunAsync(ChatMessage, AgentSession?, AgentRunOptions?, CancellationToken)"/>
    public new Task<AgentResponse> RunAsync(
        ChatMessage message,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        return RunWorkflowAsync([message], session, options, cancellationToken);
    }

    /// <inheritdoc cref="AIAgent.RunAsync(IEnumerable{ChatMessage}, AgentSession?, AgentRunOptions?, CancellationToken)"/>
    public new Task<AgentResponse> RunAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var requestMessages = messages as IReadOnlyCollection<ChatMessage> ?? messages.ToList();
        return RunWorkflowAsync(requestMessages, session, options, cancellationToken);
    }

    private Task<AgentResponse> RunWorkflowAsync(
        IReadOnlyCollection<ChatMessage> requestMessages,
        AgentSession? session,
        AgentRunOptions? options,
        CancellationToken cancellationToken)
    {
        if (!Workflow.InWorkflow)
        {
            throw new InvalidOperationException(
                "TemporalAIAgent must be used inside a Temporal workflow. " +
                "Use TemporalAIAgentProxy for external-context invocation.");
        }

        CurrentRunContext = new AgentRunContext(this, session, requestMessages, options);
        return RunCoreAsync(requestMessages, session, options, cancellationToken);
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
        string? callerRequestId = null;
        ChatResponseFormat? responseFormat = null;

        if (options is TemporalAgentRunOptions temporalOptions)
        {
            enableToolCalls = temporalOptions.EnableToolCalls;
            enableToolNames = temporalOptions.EnableToolNames;
            callerCorrelationId = temporalOptions.CorrelationId;
            callerRequestId = temporalOptions.RequestId;
        }
        else if (options is ChatClientAgentRunOptions chatOptions)
        {
            responseFormat = chatOptions.ChatOptions?.ResponseFormat;
        }

        if (options?.ResponseFormat is { } format)
        {
            responseFormat = format;
        }

        if (callerRequestId is not null && string.IsNullOrWhiteSpace(callerRequestId))
        {
            throw new ArgumentException("Request ID cannot be empty or whitespace.", nameof(options));
        }

        var request = new RunRequest([.. messages], responseFormat, enableToolCalls, enableToolNames)
        {
            OrchestrationId = Workflow.Info.WorkflowId,
            CorrelationId = string.IsNullOrEmpty(callerCorrelationId)
                ? Workflow.NewGuid().ToString("N")
                : callerCorrelationId,
            RequestId = callerRequestId ?? Workflow.NewGuid().ToString("N"),
        };

        temporalSession.AppendHistoryEntry(AgentSessionRequest.FromRunRequest(request, Workflow.UtcNow));

        var sessionId = temporalSession.SessionId;

        Workflow.Logger.LogInWorkflowAgentDispatching(_agentName, temporalSession.RunCount);

        var initializedMemory = await Workflow.ExecuteActivityAsync(
            (AgentActivities activities) => activities.InitializeChatHistoryMemoryAsync(
                new InitializeChatHistoryMemoryInput
                {
                    AgentName = _agentName,
                    SessionId = sessionId.WorkflowId,
                    SerializedStateBag = temporalSession.SerializeStateBag(),
                }),
            new ActivityOptions
            {
                StartToCloseTimeout = TimeSpan.FromSeconds(30),
                RetryPolicy = new RetryPolicy
                {
                    InitialInterval = TimeSpan.FromSeconds(1),
                    MaximumAttempts = 2,
                },
                Summary = AgentActivities.BuildActivitySummary(_agentName),
            });
        temporalSession.ApplyTrustedStateBagDelta(initializedMemory.StateBagDelta);

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
                MemoryScope = initializedMemory.Scope,
                NeedsWorkerSettingsResolution = !_settingsResolved && iteration == 0,
            };

            TemporalCommunity.Extensions.AI.Internal.DurableTransportPayloadGuard.EnsureWithinLimit(
                Workflow.PayloadConverter,
                stepInput,
                stepInput.MaximumTransportPayloadSizeBytes,
                stepInput.PayloadCodecExpansionAllowanceBytes,
                "model-step activity input");
            var stepResult = await Workflow.ExecuteActivityAsync(
                (AgentActivities a) => a.RunDurableAgentStepAsync(stepInput),
                _activityOptions);

            // Persist the step's StateBag mutations on the session so context-provider state
            // survives across steps, turns, and continue-as-new.
            temporalSession.ApplyTrustedStateBagDelta(stepResult.StateBagDelta);

            if (stepResult.ResolvedWorkerConfig is not null)
            {
                _settingsResolved = true;
                _resolvedMaxToolCallsPerTurn = stepResult.ResolvedWorkerConfig.MaxToolCallsPerTurn;
                maxIterations = _resolvedMaxToolCallsPerTurn;
            }
            if (stepResult.ApprovalRequiredToolNames is { Count: > 0 } markedTools)
            {
                _requiresApprovalTools = (_requiresApprovalTools ?? [])
                    .Concat(markedTools)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }

            // Capture worker-side config from the first resolution step.
            if (iteration == 0 && stepResult.ResolvedWorkerConfig is { } resolvedConfig)
            {
                _toolActivityOptions = resolvedConfig.ToolActivityOptions;      // per-tool InvokeAgentTool options (P1-2 fix)
                _interceptorActivityOptions = resolvedConfig.InterceptorActivityOptions;
                _interceptorToolActivityOptions = resolvedConfig.InterceptorToolActivityOptions;
                _interceptorSkippedTools = resolvedConfig.InterceptorSkippedTools;
                _requiresApprovalTools = resolvedConfig.RequiresApprovalTools;
            }

            if (stepResult.Usage is not null)
            {
                totalUsage ??= new UsageDetails();
                totalUsage.InputTokenCount = (totalUsage.InputTokenCount ?? 0) + (stepResult.Usage.InputTokenCount ?? 0);
                totalUsage.OutputTokenCount = (totalUsage.OutputTokenCount ?? 0) + (stepResult.Usage.OutputTokenCount ?? 0);
                totalUsage.TotalTokenCount = (totalUsage.TotalTokenCount ?? 0) + (stepResult.Usage.TotalTokenCount ?? 0);
            }

            accumulated.AddRange(stepResult.AssistantMessages);
            allTurnMessages.AddRange(stepResult.AssistantMessages);

            if (stepResult.IsFinal || stepResult.ToolCalls is null || stepResult.ToolCalls.Count == 0)
            {
                var response = new AgentResponse
                {
                    Messages = allTurnMessages,
                    Usage = totalUsage,
                    CreatedAt = Workflow.UtcNow,
                };

                temporalSession.AppendHistoryEntry(AgentSessionResponse.FromAgentResponse(
                    request.CorrelationId!, response, Workflow.UtcNow, request.RequestId));

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
                            // Workflow-local interceptors currently receive no session snapshot.
                            // Preserve this path while the child-agent state protocol is unresolved.
                            SerializedStateBag = null,
                            RequiresApproval = _requiresApprovalTools?.Contains(tc.Name, StringComparer.OrdinalIgnoreCase) == true,
                        };
                        // See also: AgentWorkflow.ExecuteDurableAgentTurnAsync (MAF path) — parallel interceptor dispatch
                        interceptorTasks.Add(Workflow.ExecuteActivityAsync(
                            (AgentActivities a) => a.RunToolInterceptorAsync(interceptorInput),
                            DurableToolDecisionPolicy.ResolveInterceptorActivityOptions(tc.Name, interceptorOpts, _interceptorToolActivityOptions)));
                    }
                }
                interceptorResults = await Workflow.WhenAllAsync(interceptorTasks).ConfigureAwait(true);
            }

            // Feature L: Phase 2 — process decisions. PauseForApproval degrades to Block
            // since TemporalAIAgent has no DurableApprovalMixin.
            var toolResults = new InvokeAgentToolResult?[toolCalls.Count];
            var syntheticResults = new string?[toolCalls.Count];

            for (var i = 0; i < toolCalls.Count; i++)
            {
                var tc = toolCalls[i];
                if (!enabledToolCalls[i])
                {
                    syntheticResults[i] = AgentRunToolSelectionPolicy.CreateBlockedResult(tc.Name);
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
                            SessionId = sessionId.WorkflowId,
                            MemoryScope = stepResult.PreparedTools?
                                .FirstOrDefault(tool => string.Equals(
                                    tool.Name,
                                    tc.Name,
                                    StringComparison.OrdinalIgnoreCase))?.MemoryScope,
                            ToolName = tc.Name,
                            Arguments = DurableToolDecisionPolicy.GetEffectiveArguments(interceptorResult?.ModifiedArguments, (IReadOnlyDictionary<string, object?>?)tc.Arguments),
                            CallId = tc.CallId,
                            PreparedTool = stepResult.PreparedTools?
                                .FirstOrDefault(tool => string.Equals(
                                    tool.Name,
                                    tc.Name,
                                    StringComparison.OrdinalIgnoreCase)),
                            SerializedStateBag = temporalSession.SerializeStateBag(),
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
                                : new ActivityOptions
                                {
                                    StartToCloseTimeout = _activityOptions.StartToCloseTimeout,
                                    HeartbeatTimeout = _activityOptions.HeartbeatTimeout,
                                    Summary = _activityOptions.Summary,
                                    RetryPolicy = TemporalCommunity.Extensions.AI.Internal.DefaultRetryPolicy
                                        .ResolveForAgentTool(null),
                                };
                        TemporalCommunity.Extensions.AI.Internal.DurableTransportPayloadGuard.EnsureWithinLimit(
                            Workflow.PayloadConverter,
                            toolInput,
                            toolInput.MaximumTransportPayloadSizeBytes,
                            toolInput.PayloadCodecExpansionAllowanceBytes,
                            "tool-activity input");
                        toolResults[i] = await Workflow.ExecuteActivityAsync(
                            (AgentActivities a) => a.InvokeAgentToolAsync(toolInput),
                            toolDispatchOpts).ConfigureAwait(true);
                        temporalSession.ApplyToolStateBagDeltas(
                            [toolResults[i]!.StateBagDelta]);
                        break;

                    case AgentsToolOutcome.PauseForApproval:
                        // TemporalAIAgent is a sub-agent inside an orchestrating workflow and
                        // has no DurableApprovalMixin — degrade to Block with a warning.
                        Workflow.Logger.LogWarning(
                            "Interceptor returned PauseForApproval for tool '{ToolName}' on agent '{AgentName}' " +
                            "but TemporalAIAgent does not support workflow-parked approval. Degrading to Block.",
                            tc.Name, _agentName);
                        syntheticResults[i] = $"[Blocked] Tool '{tc.Name}' requires approval but approval is not supported in sub-agent context.";
                        break;

                    case AgentsToolOutcome.Skip:
                        syntheticResults[i] = DurableToolDecisionPolicy.SkipMessage(interceptorResult?.Message);
                        break;

                    case AgentsToolOutcome.Block:
                    default:
                        syntheticResults[i] = DurableToolDecisionPolicy.BlockMessage(interceptorResult?.Message);
                        break;
                }
            }

            var functionResultContents = new List<AIContent>(toolCalls.Count);
            for (var i = 0; i < toolCalls.Count; i++)
            {
                if (syntheticResults[i] is { } synthetic)
                {
                    functionResultContents.Add(new FunctionResultContent(
                        callId: toolCalls[i].CallId,
                        result: synthetic));
                }
                else if (toolResults[i] is { } toolResult)
                {
                    functionResultContents.Add(new FunctionResultContent(
                        callId: toolCalls[i].CallId,
                        result: toolResult.Result));
                }
            }

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
            request.CorrelationId!, iterCapResponse, Workflow.UtcNow, request.RequestId));

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
