// HARNESS for docs/how-to/MAF/testing-agents.md § "Custom Host Per Test".
//
// The documentation uses application-owned greeting service/client doubles; these minimal stand-ins
// exist only to compile the excerpt. The snippet is not discovered or executed.
namespace DocSnippets.TestingAgents
{
using DocSnippets.Harness;

internal interface IGreetingService
{
    string GetGreeting();
}

internal sealed class GreetingService(string greeting) : IGreetingService
{
    public string GetGreeting() => greeting;
}

internal sealed class GreetingChatClient(IGreetingService service) : HarnessChatClient
{
    public IGreetingService Service { get; } = service;
}
}

namespace DocSnippets.TestingAgents
{
// BEGIN SNIPPET docs/how-to/MAF/testing-agents.md#custom-host-per-test (lines 558-611)
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TemporalCommunity.Extensions.Agents;
using Temporalio.Client;
using Temporalio.Extensions.Hosting;
using Xunit;

public sealed class CustomHostPerTest(IntegrationTestFixture fixture)
    : IClassFixture<IntegrationTestFixture>
{
    [Fact]
    public async Task AgentFactory_ResolvesServiceDependencies_AtActivityTime()
    {
        var taskQueue = $"factory-di-{Guid.NewGuid():N}";
        var greeting = "Greetings from DI!";

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(fixture.Client);
        builder.Services.AddSingleton<IGreetingService>(new GreetingService(greeting));

        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddTemporalAgents(options =>
            {
                options.EnableSearchAttributes = false;
                options.AddDurableAgent("DIAgent", agent =>
                {
                    agent.ChatClient = sp =>
                    {
                        var service = sp.GetRequiredService<IGreetingService>();
                        return new GreetingChatClient(service);
                    };
                });
            });

        using var host = builder.Build();
        await host.StartAsync();

        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("DIAgent");
            var session = await proxy.CreateSessionAsync();
            var response = await proxy.RunAsync("Hello", session);

            Assert.NotNull(response);
            Assert.NotEmpty(response.Messages);
            Assert.Contains(greeting, response.Messages[0].Text);
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
// END SNIPPET docs/how-to/MAF/testing-agents.md#custom-host-per-test
}
