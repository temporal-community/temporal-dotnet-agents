using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Temporalio.Extensions.AI;
using Temporalio.Workflows;

namespace Temporalio.Extensions.Agents.Workflows;

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
/// <para>
/// <b>External history store is not supported for scheduled or deferred runs.</b>
/// Even when the agent is configured with an <c>IAgentHistoryStore</c>, turns executed
/// by this workflow are <em>not</em> written to the store. The workflow does not dispatch
/// <c>AppendAgentTurn</c> activities, so the external store sees no record of these runs.
/// Use <see cref="AgentWorkflow"/> (via <c>TemporalAIAgentProxy</c> or
/// <c>DefaultTemporalAgentClient</c>) for workloads that require durable history storage.
/// </para>
/// </remarks>
[Workflow("Temporalio.Extensions.Agents.AgentJobWorkflow")]
internal sealed class AgentJobWorkflow
{
    [WorkflowRun]
    public async Task RunAsync(AgentJobInput input)
    {
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
                IsFirstStep = iteration == 0,
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
                    var isSkipped = skippedTools is not null
                        && skippedTools.Contains(tc.Name, StringComparer.OrdinalIgnoreCase);

                    if (isSkipped)
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
                            SerializedStateBag = null,
                        };
                        var baseOpts = interceptorToolOpts is not null
                            && interceptorToolOpts.TryGetValue(tc.Name, out var toolSpecific)
                                ? toolSpecific
                                : interceptorOpts;
                        interceptorTasks.Add(Workflow.ExecuteActivityAsync(
                            (AgentActivities a) => a.RunToolInterceptorAsync(interceptorInput),
                            new ActivityOptions
                            {
                                StartToCloseTimeout = baseOpts.StartToCloseTimeout,
                                HeartbeatTimeout = baseOpts.HeartbeatTimeout,
                                RetryPolicy = baseOpts.RetryPolicy,
                                Summary = $"intercept:{tc.Name}",
                            }));
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
                var outcome = interceptorResult?.Outcome ?? DurableToolOutcome.Proceed;

                // Rule 2: RequireApproval is an absolute floor. Block is strictly stricter than
                // approval and is honoured as-is; every other outcome (Proceed, Skip,
                // PauseForApproval) is overridden to PauseForApproval so the approval gate
                // cannot be bypassed by an interceptor returning Skip (BLOCK-3 fix).
                var toolRequiresApproval = input.RequiresApprovalTools is not null
                    && input.RequiresApprovalTools.Contains(tc.Name, StringComparer.OrdinalIgnoreCase);
                if (toolRequiresApproval && outcome != DurableToolOutcome.Block)
                {
                    outcome = DurableToolOutcome.PauseForApproval;
                }

                switch (outcome)
                {
                    case DurableToolOutcome.Proceed:
                        var effectiveArgs = interceptorResult?.ModifiedArguments is { } mArgs
                            ? mArgs
                            : (tc.Arguments is null ? null : new Dictionary<string, object?>(tc.Arguments));
                        var toolOptions = ResolveDurableToolActivityOptions(input, tc.Name);
                        var toolInput = new InvokeAgentToolInput
                        {
                            AgentName = input.AgentName,
                            ToolName = tc.Name,
                            Arguments = effectiveArgs,
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
                        syntheticResults[i] = interceptorResult?.Message ?? string.Empty;
                        toolTasks.Add(null);
                        break;

                    case DurableToolOutcome.Block:
                    default:
                        syntheticResults[i] = $"[Blocked] {interceptorResult?.Message ?? "Tool execution was blocked."}";
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
