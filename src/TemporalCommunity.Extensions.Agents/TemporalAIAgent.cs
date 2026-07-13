using System.Runtime.CompilerServices;
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
/// <see cref="TemporalWorkflowExtensions.GetTemporalAgent"/>). For external/host code
/// (API servers, CLIs, console apps), resolve a Temporal agent proxy via
/// <see cref="ServiceCollectionExtensions.GetTemporalAgentProxy"/>.
/// </remarks>
public sealed class TemporalAIAgent : AIAgent
{
    private readonly string _agentName;
    private readonly List<DurableSessionEntry> _history = [];
    private readonly ActivityOptions _activityOptions;
    private int _requestCount;
    // Carried StateBag for context-provider state (e.g. WorkingSetContextProvider) across
    // steps and turns. Threaded into each RunDurableAgentStep activity and refreshed from
    // stepResult.UpdatedStateBag, mirroring AgentWorkflow's _currentStateBag (AgentWorkflow.cs
    // :345 in / :381 out). Without this, sub-agent context providers lose state every step.
    private JsonElement? _currentStateBag;
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
        _activityOptions = activityOptions ?? new ActivityOptions
        {
            StartToCloseTimeout = TimeSpan.FromMinutes(30),
            HeartbeatTimeout = TimeSpan.FromMinutes(5),
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

        _history.Add(AgentSessionRequest.FromRunRequest(request, Workflow.UtcNow));
        _requestCount++;

        var sessionId = session is TemporalAgentSession ts ? ts.SessionId : (TemporalAgentSessionId?)null;

        Workflow.Logger.LogInWorkflowAgentDispatching(_agentName, _requestCount);

        // Drive the durable-agent dispatch loop for sub-agents inside an orchestrating workflow.
        // Mirrors the AgentWorkflow main loop but without continue-as-new / search attributes /
        // history reduction (the orchestrating workflow owns those concerns).
        var accumulated = new List<ChatMessage>();
        foreach (var entry in _history)
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
                SerializedStateBag = _currentStateBag,
                SessionId = sessionId,
                NeedsWorkerSettingsResolution = !_settingsResolved && iteration == 0,
            };

            var stepResult = await Workflow.ExecuteActivityAsync(
                (AgentActivities a) => a.RunDurableAgentStepAsync(stepInput),
                _activityOptions);

            // Persist the step's StateBag mutations so context-provider state (e.g.
            // WorkingSetContextProvider) survives across steps and turns. Mirrors
            // AgentWorkflow.cs:381. Context providers run inside the LLM-step activity and are
            // trusted-tier by design, so their StateBag output is applied unfiltered here —
            // unlike tool/interceptor write-backs below, which are deny-list filtered via
            // StateBagMerge.
            _currentStateBag = stepResult.UpdatedStateBag;

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
                // Note: SerializedStateBag is always null in TemporalAIAgent's interceptor
                // input (constructed below) — scope records from StateBag are never consulted
                // on this path.
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

                _history.Add(AgentSessionResponse.FromAgentResponse(
                    request.CorrelationId!, response, Workflow.UtcNow));

                return response;
            }

            var toolCalls = stepResult.ToolCalls;

            // Feature L: Phase 1 — fan out interceptor activities if configured.
            AgentsInterceptorResult[]? interceptorResults = null;
            if (_interceptorActivityOptions is { } interceptorOpts)
            {
                var interceptorTasks = new List<Task<AgentsInterceptorResult>>(toolCalls.Count);
                foreach (var tc in toolCalls)
                {
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
                            // SerializedStateBag is always null on this path — TemporalAIAgent
                            // has no StateBag and scope records from StateBag are never consulted.
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

            // Feature L: Phase 2 — process decisions. PauseForApproval degrades to Block
            // since TemporalAIAgent has no DurableApprovalMixin.
            var toolTasks = new List<Task<InvokeAgentToolResult>?>(toolCalls.Count);
            var syntheticResults = new string?[toolCalls.Count];

            for (var i = 0; i < toolCalls.Count; i++)
            {
                var tc = toolCalls[i];
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
                            // providers / scope-aware tools see it (was implicitly null before).
                            SerializedStateBag = _currentStateBag,
                        };
                        // Use per-tool ActivityOptions when resolved (honours NoRetry(), WithTimeout(), etc.)
                        // falling back to the shared _activityOptions (P1-2 fix).
                        var toolDispatchOpts = _toolActivityOptions is not null
                            && _toolActivityOptions.TryGetValue(tc.Name, out var perToolOpts)
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
            _currentStateBag = StateBagMerge.Merge(
                _currentStateBag,
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
        _history.Add(AgentSessionResponse.FromAgentResponse(
            request.CorrelationId!, iterCapResponse, Workflow.UtcNow));

        return iterCapResponse;
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Streaming is not supported; return the full response as a single update.
        var response = await RunCoreAsync(messages, session, options, cancellationToken);
        foreach (var update in response.ToAgentResponseUpdates())
        {
            yield return update;
        }
    }
}
