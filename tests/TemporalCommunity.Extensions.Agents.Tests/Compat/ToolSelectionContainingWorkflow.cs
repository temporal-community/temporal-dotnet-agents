using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Temporalio.Workflows;
using static TemporalCommunity.Extensions.Agents.TemporalWorkflowExtensions;

namespace TemporalCommunity.Extensions.Agents.Tests.Compat;

/// <summary>
/// Stable containing workflow used to capture and replay TemporalAIAgent tool-selection commands.
/// </summary>
[Workflow("TemporalCommunity.Extensions.Agents.Tests.ToolSelectionContainingWorkflow")]
internal sealed class ToolSelectionContainingWorkflow
{
    [WorkflowRun]
    public async Task RunAsync()
    {
        var agent = GetTemporalAgent("ReplayAgent");
        var session = await agent.CreateSessionAsync().ConfigureAwait(true);
        await agent.RunAsync(
            [new ChatMessage(ChatRole.User, "Attempt the replay tool.")],
            session,
            new TemporalAgentRunOptions { EnableToolCalls = false }).ConfigureAwait(true);
    }
}
