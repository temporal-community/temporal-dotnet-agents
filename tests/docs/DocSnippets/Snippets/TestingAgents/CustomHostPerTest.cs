// HARNESS for docs/how-to/MAF/testing-agents.md § "Custom Host Per Test".
//
// `fixture` and `myChatClient` come in as parameters — the doc shows the body of a test method on a
// class that already has both. Everything inside the markers is verbatim, including the [Fact]
// attribute, which is inert here: no test SDK is referenced and nothing in this project executes.
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Client;
using Temporalio.Extensions.Hosting;
using TemporalCommunity.Extensions.Agents;
using Xunit;
using DocSnippets.Harness;

namespace DocSnippets.TestingAgents;

internal static class CustomHostPerTest
{
    internal static Task RunAsync(IntegrationTestFixture fixture, IChatClient myChatClient) =>
        AgentFactory_ResolvesServiceDependencies_AtActivityTime(fixture, myChatClient);

    // BEGIN SNIPPET docs/how-to/MAF/testing-agents.md#custom-host-per-test (lines 506-535)
    [Fact]
    public static async Task AgentFactory_ResolvesServiceDependencies_AtActivityTime(
        IntegrationTestFixture fixture,
        IChatClient myChatClient)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(fixture.Environment.Client);
        builder.Services.AddSingleton<IMyCustomService, MyCustomService>();

        builder.Services.AddChatClient(myChatClient);

        builder.Services
            .AddHostedTemporalWorker("custom-queue-" + Guid.NewGuid())
            .AddTemporalAgents(opts =>
            {
                opts.AddDurableAgent("FactoryAgent", agent =>
                {
                    agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
                    agent.AddTool("do_thing", sp => AIFunctionFactory.Create(
                        sp.GetRequiredService<IMyCustomService>().DoThing,
                        "do_thing"));
                });
            });

        using var host = builder.Build();
        await host.StartAsync();

        var proxy = host.Services.GetTemporalAgentProxy("FactoryAgent");
        // ... test with the custom agent
    }
    // END SNIPPET docs/how-to/MAF/testing-agents.md#custom-host-per-test
}
