using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TemporalCommunity.Extensions.Agents.Scheduling;
using TemporalCommunity.Extensions.Agents.Session;
using TemporalCommunity.Extensions.Agents.IntegrationTests.Helpers;
using TemporalCommunity.Extensions.Agents.Tests.Compat;
using TemporalCommunity.Extensions.Agents.Tests.StepMode;
using TemporalCommunity.Extensions.Agents.Workflows;
using TemporalCommunity.Extensions.Tests.Shared;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Extensions.Hosting;
using Temporalio.Testing;
using Temporalio.Workflows;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.IntegrationTests;

[Trait("Category", "HistoryCapture")]
public sealed class ToolSelectionHistoryCaptureTests
{
    [Fact]
    public Task Capture_AgentWorkflow_BlockedToolCall() =>
        CaptureAsync(CaptureKind.AgentWorkflow, "tool-selection-agent-workflow.json");

    [Fact]
    public Task Capture_AgentJobWorkflow_BlockedToolCall() =>
        CaptureAsync(CaptureKind.AgentJobWorkflow, "tool-selection-agent-job-workflow.json");

    [Fact]
    public Task Capture_TemporalAIAgentContainingWorkflow_BlockedToolCall() =>
        CaptureAsync(CaptureKind.TemporalAIAgent, "tool-selection-temporal-ai-agent.json");

    private static async Task CaptureAsync(CaptureKind kind, string filename)
    {
        await using var environment = await TestEnvironmentHelper.StartLocalAsync();
        environment.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        var recorder = new RecordingTool { Name = "replay_tool" };
        var scripted = ScriptedChatClient.WithToolCallsThenFinal(
            [new FunctionCallContent("replay-call", recorder.Name,
                new Dictionary<string, object?> { ["input"] = "data" })],
            "Blocked replay call handled.");
        var taskQueue = $"tool-selection-capture-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(environment.Client);
        builder.Services.AddSingleton<IChatClient>(scripted);
        var worker = builder.Services.AddHostedTemporalWorker(taskQueue);
        if (kind == CaptureKind.TemporalAIAgent)
            worker.AddWorkflow<ToolSelectionContainingWorkflow>();
        worker.AddTemporalAgents(options => options.AddDurableAgent("ReplayAgent", agent =>
        {
            agent.ChatClient = services => services.GetRequiredService<IChatClient>();
            agent.AddTool(recorder.Build());
        }));

        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            WorkflowHandle handle;
            if (kind == CaptureKind.AgentWorkflow)
            {
                var proxy = host.Services.GetTemporalAgentProxy("ReplayAgent");
                var session = (TemporalAgentSession)await proxy.CreateSessionAsync();
                await proxy.RunAsync(
                    "Attempt the replay tool.",
                    session,
                    new TemporalAgentRunOptions { EnableToolCalls = false });
                handle = environment.Client.GetWorkflowHandle(session.SessionId.WorkflowId);
            }
            else if (kind == CaptureKind.AgentJobWorkflow)
            {
                var request = new RunRequest("Attempt the replay tool.", enableToolCalls: false);
                var input = DefaultTemporalAgentClient.BuildAgentJobInput(
                    "ReplayAgent",
                    request,
                    host.Services.GetRequiredService<TemporalAgentsOptions>(),
                    taskQueue);
                handle = await environment.Client.StartWorkflowAsync(
                    (AgentJobWorkflow workflow) => workflow.RunAsync(input),
                    new WorkflowOptions($"ta-replayagent-scheduled-{Guid.NewGuid():N}", taskQueue));
                await handle.GetResultAsync();
            }
            else
            {
                handle = await environment.Client.StartWorkflowAsync(
                    (ToolSelectionContainingWorkflow workflow) => workflow.RunAsync(),
                    new WorkflowOptions($"tool-selection-subagent-{Guid.NewGuid():N}", taskQueue));
                await handle.GetResultAsync();
            }

            Assert.Equal(0, recorder.CallCount);
            var history = await handle.FetchHistoryAsync();
            await SaveHistoryAsync(filename, history);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private static async Task SaveHistoryAsync(string filename, WorkflowHistory history)
    {
        var directory = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "TemporalCommunity.Extensions.Agents.Tests",
            "Compat",
            "Histories"));
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, filename), history.ToJson());
    }

    private enum CaptureKind
    {
        AgentWorkflow,
        AgentJobWorkflow,
        TemporalAIAgent,
    }
}
