// RefundWorkflow.cs — single-turn driver workflow.
//
// The workflow only needs to send the user's complaint to the agent and return its
// final reply. The durable agent runs as a series of RunDurableAgentStep +
// InvokeAgentTool activities under the hood; the workflow doesn't need to manage
// the tool loop directly.

using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.Agents;
using Temporalio.Workflows;
using static TemporalCommunity.Extensions.Agents.TemporalWorkflowExtensions;

namespace PerToolActivities;

/// <summary>
/// Single-turn workflow that hands a customer complaint to <c>RefundAgent</c> and
/// returns the agent's final response.
/// <para>
/// Because <c>RefundAgent</c> is registered via <c>AddDurableAgent</c>, every LLM
/// call it makes is a separate <c>RunDurableAgentStep</c> activity and every tool
/// call the model issues is a separate <c>InvokeAgentTool</c> activity dispatched
/// in parallel from the durable loop inside <c>AgentWorkflow</c>. This workflow is
/// blissfully ignorant of that machinery — one <c>RunAsync</c> call covers the
/// whole multi-step turn.
/// </para>
/// </summary>
[Workflow("PerToolActivities.RefundWorkflow")]
public sealed class RefundWorkflow
{
    [WorkflowRun]
    public async Task<string> RunAsync(RefundTurnInput input)
    {
        var agent = GetTemporalAgent("RefundAgent");
        var session = await agent.CreateSessionAsync().ConfigureAwait(true);

        var response = await agent.RunAsync(
            [new ChatMessage(ChatRole.User, input.CustomerComplaint)],
            session,
            new TemporalAgentRunOptions
            {
                EnableToolCalls = input.EnableToolCalls,
                EnableToolNames = input.EnableToolNames?.ToList(),
            }).ConfigureAwait(true);

        return response.Text ?? string.Empty;
    }
}

/// <summary>Per-turn input, including the MAF tool-exposure controls demonstrated by this sample.</summary>
public sealed record RefundTurnInput(
    string CustomerComplaint,
    bool EnableToolCalls = true,
    IReadOnlyList<string>? EnableToolNames = null);
