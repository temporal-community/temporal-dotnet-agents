// Compile-checked workflow example from docs/how-to/MAF/usage.md.
using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.Agents;
using Temporalio.Workflows;

namespace DocSnippets.Usage;

internal static class WorkflowSubagent
{
    // BEGIN SNIPPET docs/how-to/MAF/usage.md#agent-orchestration-inside-workflows
    [Workflow]
    public class ResearchWorkflow
    {
        [WorkflowRun]
        public async Task<string> RunAsync(string topic)
        {
            // Get a TemporalAIAgent — runs inference via activity, history tracked in workflow state
            var researcher = WorkflowAgents.GetTemporalAgent("ResearcherAgent");
            var session = await researcher.CreateSessionAsync();

            var outline = await researcher.RunAsync($"Create an outline about: {topic}", session);

            var writer = WorkflowAgents.GetTemporalAgent("WriterAgent");
            var writerSession = await writer.CreateSessionAsync();

            var draft = await writer.RunAsync(
                $"Write a short article based on this outline:\n{outline.Messages[0].Text}",
                writerSession);

            return draft.Messages[0].Text ?? string.Empty;
        }
    }
    // END SNIPPET docs/how-to/MAF/usage.md#agent-orchestration-inside-workflows
}
