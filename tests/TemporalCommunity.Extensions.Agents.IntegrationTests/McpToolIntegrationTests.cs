using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TemporalCommunity.Extensions.Agents.Session;
using TemporalCommunity.Extensions.Tests.Shared;
using Temporalio.Client;
using Temporalio.Extensions.Hosting;
using Temporalio.Testing;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class McpToolIntegrationTests
{
    [Fact]
    public async Task McpTool_RunsThroughMafAsSeparateToolActivity()
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;
        await using var mcp = await InProcessMcpServer.CreateAsync();
        var pinned = InProcessMcpServer.CreatePinnedTools(mcp.Client);

        var scripted = ScriptedChatClient.WithToolCallsThenFinal(
            [new FunctionCallContent(
                "mcp-call-1",
                "lookup_inventory",
                new Dictionary<string, object?> { ["sku"] = "SKU-123" })],
            "Inventory checked.");

        var taskQueue = $"mcp-maf-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(env.Client);
        builder.Services.AddSingleton<IChatClient>(scripted);
        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddTemporalAgents(options =>
            {
                options.EnableSearchAttributes = false;
                options.AddDurableAgent("McpAgent", agent =>
                {
                    agent.ChatClient = services => services.GetRequiredService<IChatClient>();
                    agent.AddTool(pinned["lookup_inventory"]);
                    agent.AddTool(
                        pinned["delete_inventory"],
                        policy => policy.NoRetry().RequireApproval());
                });
            });

        using var host = builder.Build();
        await host.StartAsync();

        var proxy = host.Services.GetTemporalAgentProxy("McpAgent");
        var session = await proxy.CreateSessionAsync();
        var result = await proxy.RunAsync("Check SKU-123", session);

        Assert.Equal("Inventory checked.", result.Text);
        Assert.Equal(
            ["lookup_inventory", "delete_inventory"],
            scripted.Calls[0].Options!.Tools!.Select(tool => tool.Name).ToArray());

        var temporalSession = Assert.IsType<TemporalAgentSession>(session);
        var handle = env.Client.GetWorkflowHandle(temporalSession.SessionId.WorkflowId);
        Assert.Equal(2, await WorkflowHistoryAssertions.CountActivityScheduledAsync(
            handle,
            "TemporalCommunity.Extensions.Agents.RunDurableAgentStep"));
        Assert.Equal(1, await WorkflowHistoryAssertions.CountActivityScheduledAsync(
            handle,
            "TemporalCommunity.Extensions.Agents.InvokeAgentTool"));

        await host.Services.GetRequiredService<ITemporalAgentClient>()
            .ShutdownAsync(temporalSession.SessionId);
        await host.StopAsync();
    }
}
