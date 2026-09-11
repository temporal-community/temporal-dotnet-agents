// HARNESS for docs/how-to/MAF/usage.md § "Same-Process Registration".
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Extensions.Hosting;
using TemporalCommunity.Extensions.Agents;

namespace DocSnippets.Usage;

internal static class SameProcessRegistration
{
    internal static async Task ConfigureAsync(HostApplicationBuilder builder, IHost host)
    {
        // BEGIN SNIPPET docs/how-to/MAF/usage.md#same-process-registration (lines 411-435)
        // Worker and caller in the same process
        builder.Services.AddTemporalClient("localhost:7233", "default");

        builder.Services
            .AddHostedTemporalWorker("agents")
            .AddTemporalAgents(opts =>
            {
                opts.AddDurableAgent("SupportAgent", agent =>
                {
                    agent.Instructions = "You help customers with support requests.";
                    agent.ChatClient   = sp => sp.GetRequiredService<IChatClient>();
                });
            });

        // ...

        // Resolve and use the proxy — return type is AIAgent
        AIAgent agentProxy = host.Services.GetTemporalAgentProxy("SupportAgent");

        var session = await agentProxy.CreateSessionAsync();
        AgentResponse response = await agentProxy.RunAsync("My order hasn't arrived.", session);

        Console.WriteLine(response.Messages[0].Text);
        // END SNIPPET docs/how-to/MAF/usage.md#same-process-registration
    }
}
