using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using TemporalCommunity.Extensions.AI.Tools;
using Temporalio.Workflows;

namespace TemporalCommunity.Extensions.Agents.Workflows;

/// <summary>
/// A simple, fire-and-forget Temporal workflow for scheduled or deferred agent runs.
/// Unlike <see cref="AgentWorkflow"/>, this workflow carries no persisted history,
/// no StateBag, no TTL loop, and no <c>[WorkflowUpdate]</c> handlers — it executes
/// the durable-agent dispatch loop in-place and exits.
/// </summary>
/// <remarks>
/// <para>
/// Workflow ID convention: <c>ta-{agentName}-scheduled-{scheduleId}</c>.
/// </para>
/// </remarks>
[Workflow("TemporalCommunity.Extensions.Agents.AgentJobWorkflow")]
internal sealed class AgentJobWorkflow
{
    [WorkflowRun]
    public async Task RunAsync(AgentJobInput input)
    {
        // Feature B — Task 7.1: warn early when scope-aware required tools are present.
        // AgentJobWorkflow has no DurableApprovalMixin so workflow-parked approval is not
        // supported. When the interceptor returns PauseForApproval for a scope-aware required
        // tool (because no matching scope record exists), the decision degrades to Block below
        // in Phase 2. Emitting a LogWarning here at workflow start makes this degradation
        // visible before the tool call rather than silently at block time.
        // Note: SerializedStateBag is always null in AgentJobWorkflow's interceptor input
        // (see DurableToolInterceptorInput construction in Phase 1 below) — scope records
        // from StateBag are never consulted on this path.
        if (input.ScopeAwareApprovalTools is { Count: > 0 } scopeApprovalTools)
        {
            var names = string.Join(", ", scopeApprovalTools);
            Workflow.Logger.LogWarning(
                "Tool(s) '{ToolNames}' are configured with RequireApproval().ScopeAware() but this execution " +
                "context does not support workflow-parked approval. Unapproved calls will be blocked.",
                names);
        }

        var stepActivityOptions = new ActivityOptions
        {
            StartToCloseTimeout = input.ActivityTimeout,
            HeartbeatTimeout = input.HeartbeatTimeout,
            Summary = AgentActivities.BuildActivitySummary(input.AgentName),
            RetryPolicy = input.RetryPolicy,
        };

        var accumulated = new List<ChatMessage>(input.Request.Messages);
        var maxIterations = input.MaxToolCallsPerTurn;

        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            var stepInput = new AgentStepInput
            {
                AgentName = input.AgentName,
                Request = input.Request,
                AccumulatedMessages = accumulated,
                SerializedStateBag = null,
                SessionId = null,
            };

            var stepResult = await Workflow.ExecuteActivityAsync(
                (AgentActivities a) => a.RunDurableAgentStepAsync(stepInput),
                stepActivityOptions).ConfigureAwait(true);

            accumulated.Add(stepResult.AssistantMessage);

            if (stepResult.IsFinal || stepResult.ToolCalls is null || stepResult.ToolCalls.Count == 0)
            {
                return;
            }

            var toolCalls = stepResult.ToolCalls;

            // Feature L: Phase 1 — fan out interceptor activities if an interceptor is configured.
            DurableToolInterceptorResult[]? interceptorResults = null;
            if (input.InterceptorActivityOptions is { } interceptorOpts)
            {
                var interceptorTasks = new List<Task<DurableToolInterceptorResult>>(toolCalls.Count);
                var skippedTools = input.InterceptorSkippedTools;
                var interceptorToolOpts = input.InterceptorToolActivityOptions;

                foreach (var tc in toolCalls)
                {
                    if (DurableToolDecisionPolicy.IsToolSkipped(tc.Name, skippedTools))
                    {
                        interceptorTasks.Add(Task.FromResult(
                            new DurableToolInterceptorResult { Outcome = DurableToolOutcome.Proceed }));
                    }
                    else
                    {
                        var interceptorInput = new DurableToolInterceptorInput
                        {
                            AgentName = input.AgentName,
                            ToolName = tc.Name,
                            Arguments = tc.Arguments is null ? null : new Dictionary<string, object?>(tc.Arguments),
                            CallId = tc.CallId,
                            // SerializedStateBag is always null on this path — AgentJobWorkflow
                            // has no StateBag and scope records are never consulted here.
                            SerializedStateBag = null,
                            // Feature B: populate scope-aware fields so the interceptor can
                            // enforce the approval gate for scope-aware tools (Task 4.7).
                            ScopeAware = input.ScopeAwareTools?.Contains(tc.Name, StringComparer.OrdinalIgnoreCase) == true,
                            RequiresApproval = input.RequiresApprovalTools?.Contains(tc.Name, StringComparer.OrdinalIgnoreCase) == true
                                || input.ScopeAwareApprovalTools?.Contains(tc.Name, StringComparer.OrdinalIgnoreCase) == true,
                        };
                        // See also: AgentWorkflow.ExecuteDurableAgentTurnAsync (MAF path) — parallel typed dispatch
                        interceptorTasks.Add(Workflow.ExecuteActivityAsync(
                            (AgentActivities a) => a.RunToolInterceptorAsync(interceptorInput),
                            DurableToolDecisionPolicy.ResolveInterceptorActivityOptions(tc.Name, interceptorOpts, interceptorToolOpts)));
                    }
                }

                interceptorResults = await Workflow.WhenAllAsync(interceptorTasks).ConfigureAwait(true);
            }

            // Feature L: Phase 2 — process decisions.
            var toolTasks = new List<Task<InvokeAgentToolResult>?>(toolCalls.Count);
            var syntheticResults = new string?[toolCalls.Count];

            for (var i = 0; i < toolCalls.Count; i++)
            {
                var tc = toolCalls[i];
                var interceptorResult = interceptorResults?[i];
                // Determine effective outcome (Rule 2: RequireApproval floor, Block never overridden).
                var outcome = DurableToolDecisionPolicy.GetEffectiveOutcome(
                    interceptorResult?.Outcome, tc.Name, input.RequiresApprovalTools);

                switch (outcome)
                {
                    case DurableToolOutcome.Proceed:
                        var toolOptions = ResolveDurableToolActivityOptions(input, tc.Name);
                        var toolInput = new InvokeAgentToolInput
                        {
                            AgentName = input.AgentName,
                            ToolName = tc.Name,
                            Arguments = DurableToolDecisionPolicy.GetEffectiveArguments(interceptorResult?.ModifiedArguments, (IReadOnlyDictionary<string, object?>?)tc.Arguments),
                            CallId = tc.CallId,
                        };
                        toolTasks.Add(Workflow.ExecuteActivityAsync(
                            (AgentActivities a) => a.InvokeAgentToolAsync(toolInput),
                            toolOptions));
                        break;

                    case DurableToolOutcome.PauseForApproval:
                        // AgentJobWorkflow has no DurableApprovalMixin — degrade to Block.
                        Workflow.Logger.LogWarning(
                            "Interceptor returned PauseForApproval for tool '{ToolName}' on agent '{AgentName}' " +
                            "but AgentJobWorkflow does not support workflow-parked approval. Degrading to Block.",
                            tc.Name, input.AgentName);
                        syntheticResults[i] = $"[Blocked] Tool '{tc.Name}' requires approval but approval is not supported in job workflows.";
                        toolTasks.Add(null);
                        break;

                    case DurableToolOutcome.Skip:
                        syntheticResults[i] = DurableToolDecisionPolicy.SkipMessage(interceptorResult?.Message);
                        toolTasks.Add(null);
                        break;

                    case DurableToolOutcome.Block:
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
                    functionResultContents.Add(new FunctionResultContent(
                        callId: toolCalls[i].CallId,
                        result: toolResults[pendingIdx++].Result));
                }
            }

            accumulated.Add(new ChatMessage(ChatRole.Tool, functionResultContents));
        }
    }

    /// <summary>
    /// Resolves the <see cref="ActivityOptions"/> for a per-tool dispatch. When
    /// <see cref="AgentJobInput.DurableAgentToolActivityOptions"/> contains an entry for
    /// <paramref name="toolName"/>, those options (with their per-tool retry policy and timeouts)
    /// are used; otherwise a default is built from the flat job-level settings.
    /// </summary>
    private static ActivityOptions ResolveDurableToolActivityOptions(AgentJobInput input, string toolName)
    {
        if (input.DurableAgentToolActivityOptions is not null
            && input.DurableAgentToolActivityOptions.TryGetValue(toolName, out var perTool))
        {
            return perTool;
        }

        return new ActivityOptions
        {
            StartToCloseTimeout = input.ActivityTimeout,
            HeartbeatTimeout = input.HeartbeatTimeout,
            Summary = toolName,
            RetryPolicy = input.RetryPolicy,
        };
    }
}
