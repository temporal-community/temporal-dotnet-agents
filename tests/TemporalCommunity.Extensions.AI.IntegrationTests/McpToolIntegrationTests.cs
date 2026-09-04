using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using TemporalCommunity.Extensions.Tests.Shared;
using Temporalio.Client;
using Temporalio.Extensions.Hosting;
using Temporalio.Testing;
using Xunit;

namespace TemporalCommunity.Extensions.AI.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class McpToolIntegrationTests
{
    [Fact]
    public async Task PinnedMcpTool_IsModelVisibleAndRunsAsSeparateActivity()
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;
        await using var mcp = await InProcessMcpServer.CreateAsync();
        var pinned = InProcessMcpServer.CreatePinnedTools(mcp.Client);

        var scripted = ScriptedChatClient.WithToolCallsThenFinal(
            [new FunctionCallContent(
                "mcp-call-1",
                "lookup_inventory",
                new Dictionary<string, object?> { ["sku"] = "SKU-123" })],
            "Inventory checked.");

        var taskQueue = $"mcp-meai-{Guid.NewGuid():N}";
        using var host = BuildHost(env.Client, taskQueue, scripted, pinned);
        await host.StartAsync();

        var sessions = host.Services.GetRequiredService<IDurableChatSessionClient>();
        var conversationId = $"mcp-{Guid.NewGuid():N}";
        var response = await sessions.SendAsync(
            conversationId,
            [new ChatMessage(ChatRole.User, "Check SKU-123")]);

        Assert.Equal("Inventory checked.", response.Text);
        Assert.Equal(
            ["lookup_inventory", "delete_inventory"],
            scripted.Calls[0].Options!.Tools!.Select(tool => tool.Name).ToArray());
        Assert.DoesNotContain(scripted.Calls[0].Options!.Tools!, tool => tool.Name == "server_admin");

        var handle = env.Client.GetWorkflowHandle(sessions.GetWorkflowId(conversationId));
        Assert.Equal(2, await WorkflowHistoryAssertions.CountActivityScheduledAsync(
            handle,
            "TemporalCommunity.Extensions.AI.GetChatStep"));
        Assert.Equal(1, await WorkflowHistoryAssertions.CountActivityScheduledAsync(
            handle,
            "TemporalCommunity.Extensions.AI.InvokeFunction"));

        await host.StopAsync();
    }

    [Fact]
    public async Task DynamicDiscovery_ReturnsServerCatalog_WhilePinnedCatalogRemainsAllowlist()
    {
        await using var mcp = await InProcessMcpServer.CreateAsync();

        var discovered = await mcp.Client.ListToolsAsync();
        var pinned = InProcessMcpServer.CreatePinnedTools(mcp.Client);

        Assert.Equal(
            ["delete_inventory", "lookup_inventory", "server_admin"],
            discovered.Select(tool => tool.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(
            ["delete_inventory", "lookup_inventory"],
            pinned.Keys.Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task StalePinnedDefinition_FailsClearlyAtInvocation()
    {
        await using var mcp = await InProcessMcpServer.CreateAsync();
        var source = InProcessMcpServer.PinnedDefinitions[0];
        var staleDefinition = new Tool
        {
            Name = "renamed_inventory_tool",
            Description = source.Description,
            InputSchema = source.InputSchema,
            OutputSchema = source.OutputSchema,
        };
        var stale = new McpClientTool(mcp.Client, staleDefinition);

        var exception = await Assert.ThrowsAnyAsync<Exception>(
            () => stale.InvokeAsync(new AIFunctionArguments()).AsTask());

        Assert.Contains("renamed_inventory_tool", exception.ToString(), StringComparison.Ordinal);
    }

    private static IHost BuildHost(
        ITemporalClient client,
        string taskQueue,
        ScriptedChatClient scripted,
        IReadOnlyDictionary<string, McpClientTool> tools)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(client);
        builder.Services.AddChatClient(scripted);

        var worker = builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddDurableAI(options => options.DefaultToolsetIds = ["inventory-v1"]);

        worker.AddDurableToolset("inventory-v1", set => set
            .Add(tools["lookup_inventory"])
            .Add(tools["delete_inventory"], options => options.NoRetry().RequireApproval()));

        return builder.Build();
    }
}
