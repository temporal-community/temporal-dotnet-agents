using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TemporalCommunity.Extensions.Agents.IntegrationTests.Helpers;
using TemporalCommunity.Extensions.Tests.Shared;
using Temporalio.Client;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class TemporalAgentRegistrationIntegrationTests
{
    [Fact]
    public async Task Agent_KeyedProxyRunsTwoTurnsAndDispatchesMafTool()
    {
        await using var environment = await TestEnvironmentHelper.StartLocalAsync();
        var tool = AIFunctionFactory.Create(GetWeather, name: "get_weather");
        var toolCall = new FunctionCallContent(
            "weather-call",
            "get_weather",
            new Dictionary<string, object?> { ["city"] = "Seattle" });
        var chatClient = new ScriptedChatClient(
        [
            new ChatResponse(new ChatMessage(ChatRole.Assistant, [toolCall])),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Seattle: cloudy.")),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "The previous forecast was Seattle: cloudy.")),
        ]);
        var taskQueue = $"maf-registration-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(environment.Client);
        builder.Services.AddChatClient(chatClient);
        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddTemporalAgents(agents =>
                agents.Agent(
                    "weather",
                    new ChatClientAgentOptions
                    {
                        ChatOptions = new ChatOptions
                        {
                            Instructions = "Use get_weather for forecasts.",
                            Tools = [tool],
                        },
                    }));

        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            var agent = host.Services.GetRequiredKeyedService<AIAgent>("weather");
            var session = await agent.CreateSessionAsync();
            var workflowId = ((TemporalCommunity.Extensions.Agents.Session.TemporalAgentSession)session)
                .SessionId.WorkflowId;

            var first = await agent.RunAsync("Weather in Seattle?", session);
            var second = await agent.RunAsync("And tomorrow?", session);

            Assert.Equal("Seattle: cloudy.", first.Text);
            Assert.Contains("Seattle: cloudy.", second.Text);
            Assert.Equal(workflowId,
                ((TemporalCommunity.Extensions.Agents.Session.TemporalAgentSession)session)
                    .SessionId.WorkflowId);
            Assert.Equal(3, chatClient.CallCount);
            Assert.Contains(
                chatClient.Calls[2].Messages,
                message => message.Role == ChatRole.Assistant && message.Text == "Seattle: cloudy.");
            Assert.Contains(
                chatClient.Calls[2].Messages,
                message => message.Role == ChatRole.User && message.Text == "Weather in Seattle?");
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private static string GetWeather(string city) => $"{city}: cloudy.";
}
