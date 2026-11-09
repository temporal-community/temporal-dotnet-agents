using System.Text.Json;
using FakeItEasy;
using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.Agents.Workflows;
using Temporalio.Common;
using Temporalio.Converters;
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
        var history = LoadHistory("failed-turn-statebag-rollback.json");
        await AddWorkerWorkflowAsync(options, history, "queued_failing_tool");
        var replayer = new WorkflowReplayer(options);

        var result = await replayer.ReplayWorkflowAsync(
            history,
            throwOnReplayFailure: false);

        Assert.Null(result.ReplayFailure);
    }

    [Theory]
    [InlineData("first-update-with-start-agent-workflow.json")]
    [InlineData("tool-selection-agent-workflow.json")]
    public async Task CorrectedToolSelectionHistory_ReplaysWithoutError(string filename)
    {
        var options = new WorkflowReplayerOptions
        {
            DataConverter = TemporalAgentDataConverter.Instance,
        };
        var history = LoadHistory(filename);
        await AddWorkerWorkflowAsync(
            options,
            history,
            filename == "tool-selection-agent-workflow.json" ? "replay_tool" : null);
        var replayer = new WorkflowReplayer(options);

        var result = await replayer.ReplayWorkflowAsync(
            history,
            throwOnReplayFailure: false);

        Assert.Null(result.ReplayFailure);
    }

    private static async Task AddWorkerWorkflowAsync(
        WorkflowReplayerOptions options,
        WorkflowHistory history,
        string? toolName)
    {
        var started = Assert.Single(
            history.Events,
            e => e.WorkflowExecutionStartedEventAttributes is not null);
        var startAttributes = started.WorkflowExecutionStartedEventAttributes!;
        var input = await TemporalAgentDataConverter.Instance.ToValueAsync<AgentWorkflowInput>(
            Assert.Single(startAttributes.Input.Payloads_));
        var workerOptions = new TemporalAgentsOptions();
        workerOptions.AddDurableAgent(input.AgentName, agent =>
        {
            agent.ChatClient = _ => A.Fake<IChatClient>();
            if (toolName is not null)
            {
                agent.AddTool(
                    AIFunctionFactory.Create(() => "replay", toolName),
                    toolOptions => toolOptions.NoRetry());
            }
        });

        options.AddWorkflow(AgentWorkflowWorkerFactory.CreateDefinition(
            workerOptions,
            startAttributes.TaskQueue.Name));
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

        var historyJson = File.ReadAllText(path);
        using var document = JsonDocument.Parse(historyJson);
        var workflowId = document.RootElement
            .GetProperty("events")[0]
            .GetProperty("workflowExecutionStartedEventAttributes")
            .GetProperty("workflowId")
            .GetString();
        if (string.IsNullOrWhiteSpace(workflowId))
        {
            throw new InvalidDataException($"History file has no workflow ID: {path}");
        }

        return WorkflowHistory.FromJson(workflowId, historyJson);
    }
}
