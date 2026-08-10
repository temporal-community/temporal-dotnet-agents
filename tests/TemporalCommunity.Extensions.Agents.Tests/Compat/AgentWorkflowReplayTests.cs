using TemporalCommunity.Extensions.Agents.Workflows;
using Temporalio.Common;
using Temporalio.Worker;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.Tests.Compat;

/// <summary>
/// Replays checked-in <see cref="AgentWorkflow"/> histories without a Temporal server.
/// </summary>
public class AgentWorkflowReplayTests
{
    [Fact]
    public async Task QueuedStateBagRollback_ReplaysWithoutError()
    {
        var options = new WorkflowReplayerOptions
        {
            DataConverter = TemporalAgentDataConverter.Instance,
        };
        options.AddWorkflow<AgentWorkflow>();
        var replayer = new WorkflowReplayer(options);
        var history = LoadHistory("queued-statebag-rollback.json");

        var result = await replayer.ReplayWorkflowAsync(
            history,
            throwOnReplayFailure: false);

        Assert.Null(result.ReplayFailure);
    }

    private static WorkflowHistory LoadHistory(string filename)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Compat", "Histories", filename);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"History file not found: {path}. Run 'just capture-agent-histories' to create it.",
                path);
        }

        return WorkflowHistory.FromJson(
            Path.GetFileNameWithoutExtension(filename),
            File.ReadAllText(path));
    }
}
