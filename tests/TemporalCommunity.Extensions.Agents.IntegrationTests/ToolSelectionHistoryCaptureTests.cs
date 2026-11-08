using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TemporalCommunity.Extensions.Agents.Session;
using TemporalCommunity.Extensions.Agents.IntegrationTests.Helpers;
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
    public async Task Capture_AgentWorkflow_FirstUpdateWithStart()
    {
        await using var environment = await TestEnvironmentHelper.StartLocalAsync();
        environment.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        var taskQueue = $"first-update-with-start-capture-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(environment.Client);
        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddTemporalAgents(options => options.AddDurableAgent(
                "FirstTurnReplayAgent",
                agent => agent.ChatClient = _ => new EchoChatClient()));

        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("FirstTurnReplayAgent");
            var session = (TemporalAgentSession)await proxy.CreateSessionAsync();
            var response = await proxy.RunAsync("Capture first Update-With-Start.", session);

            Assert.Equal("Echo [1]: Capture first Update-With-Start.", response.Messages[^1].Text);
            var handle = environment.Client.GetWorkflowHandle(session.SessionId.WorkflowId);
            await SaveHistoryAsync(
                "first-update-with-start-agent-workflow.json",
                await handle.FetchHistoryAsync());
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public Task Capture_AgentWorkflow_BlockedToolCall() =>
        CaptureAsync();

    private static async Task CaptureAsync()
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
        worker.AddTemporalAgents(options => options.AddDurableAgent("ReplayAgent", agent =>
        {
            agent.ChatClient = services => services.GetRequiredService<IChatClient>();
            agent.AddTool(recorder.Build());
        }));

        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("ReplayAgent");
            var session = (TemporalAgentSession)await proxy.CreateSessionAsync();
            await proxy.RunAsync(
                "Attempt the replay tool.",
                session,
                new TemporalAgentRunOptions { EnableToolCalls = false });
            var handle = environment.Client.GetWorkflowHandle(session.SessionId.WorkflowId);

            Assert.Equal(0, recorder.CallCount);
            var history = await handle.FetchHistoryAsync();
            await SaveHistoryAsync("tool-selection-agent-workflow.json", history);
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

}
