// HARNESS for docs/how-to/MAF/usage.md § "Tool Activity Ordering and Retry Policy".
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Extensions.Hosting;
using TemporalCommunity.Extensions.Agents;

namespace DocSnippets.Usage;

internal static class PerToolActivityConfiguration
{
    internal static void Configure(
        HostApplicationBuilder builder,
        AIFunction lookupOrderTool,
        AIFunction sendEmailTool)
    {
        // BEGIN SNIPPET docs/how-to/MAF/usage.md#tool-activity-ordering-and-retry-policy (lines 1090-1115)
        builder.Services.AddTemporalClient("localhost:7233", "default");

        builder.Services
            .AddHostedTemporalWorker("agents")
            .AddTemporalAgents(agents =>
                agents.Agent(
                    "SupportAgent",
                    new ChatClientAgentOptions
                    {
                        ChatOptions = new ChatOptions
                        {
                            Instructions = "You help customers with support requests.",
                            Tools = [lookupOrderTool, sendEmailTool],
                        },
                },
                _ => { }));
        // END SNIPPET docs/how-to/MAF/usage.md#tool-activity-ordering-and-retry-policy
    }
}
