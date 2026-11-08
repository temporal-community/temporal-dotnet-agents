// Compile-checked activity invocation example from docs/how-to/MAF/usage.md.
using DocSnippets.Harness;
using TemporalCommunity.Extensions.Agents;
using Temporalio.Workflows;

namespace DocSnippets.Usage;

internal static class WorkflowSubagent
{
    // BEGIN SNIPPET docs/how-to/MAF/usage.md#invoking-agents-from-a-workflow
    [Workflow]
    public sealed class ResearchWorkflow
    {
        [WorkflowRun]
        public async Task<string> RunAsync(string topic)
        {
            var sessionId = WorkflowAgents.NewAgentSessionId("ResearcherAgent");
            var requestId = Workflow.NewGuid().ToString("N");
            return await Workflow.ExecuteActivityAsync(
                (AgentInvocationActivities activities) => activities.RunAsync(
                    "ResearcherAgent", sessionId.WorkflowId, requestId, $"Research: {topic}"),
                new ActivityOptions { StartToCloseTimeout = TimeSpan.FromMinutes(35) });
        }
    }
    // END SNIPPET docs/how-to/MAF/usage.md#invoking-agents-from-a-workflow
}
