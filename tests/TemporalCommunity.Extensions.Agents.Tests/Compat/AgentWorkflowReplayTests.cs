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
    public async Task FailedTurnStateBagRollback_ReplaysWithoutError()
    {
        var options = new WorkflowReplayerOptions
        {
            DataConverter = TemporalAgentDataConverter.Instance,
        };
        options.AddWorkflow<AgentWorkflow>();
        var replayer = new WorkflowReplayer(options);
        var history = LoadHistory("failed-turn-statebag-rollback.json");

        var result = await replayer.ReplayWorkflowAsync(
            history,
            throwOnReplayFailure: false);

        Assert.Null(result.ReplayFailure);
    }

    [Theory]
    [InlineData("first-update-with-start-agent-workflow.json", typeof(AgentWorkflow))]
    [InlineData("tool-selection-agent-workflow.json", typeof(AgentWorkflow))]
    [InlineData("tool-selection-agent-job-workflow.json", typeof(AgentJobWorkflow))]
    public async Task CorrectedToolSelectionHistory_ReplaysWithoutError(
        string filename,
        Type workflowType)
    {
        var options = new WorkflowReplayerOptions
        {
            DataConverter = TemporalAgentDataConverter.Instance,
        };
        options.AddWorkflow(workflowType);
        var replayer = new WorkflowReplayer(options);

        var result = await replayer.ReplayWorkflowAsync(
            LoadHistory(filename),
            throwOnReplayFailure: false);

        Assert.Null(result.ReplayFailure);
    }

    private static WorkflowHistory LoadHistory(string filename)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Compat", "Histories", filename);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"History file not found: {path}. Run 'just capture-histories' to create it.",
                path);
        }

        return WorkflowHistory.FromJson(
            Path.GetFileNameWithoutExtension(filename),
            File.ReadAllText(path));
    }
}
