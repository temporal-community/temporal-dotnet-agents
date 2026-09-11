// HARNESS for docs/how-to/MAF/usage.md § "Per-Tool Activity Configuration".
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
        // BEGIN SNIPPET docs/how-to/MAF/usage.md#per-tool-activity-configuration (lines 872-893)
        builder.Services.AddTemporalClient("localhost:7233", "default");

        builder.Services
            .AddHostedTemporalWorker("agents")
            .AddTemporalAgents(opts =>
            {
                opts.AddDurableAgent("SupportAgent", agent =>
                {
                    agent.Instructions = "You help customers with support requests.";
                    agent.ChatClient   = sp => sp.GetRequiredService<IChatClient>();

                    // Read tool — no per-tool policy, so it falls through to agent.RetryPolicy,
                    // then opts.DefaultRetryPolicy, then the bounded five-attempt backstop.
                    agent.AddTool(lookupOrderTool);

                    // Write tool — bind NoRetry() to the AIFunction reference. Cannot mistype the name.
                    agent.AddTool(sendEmailTool, opts => opts.NoRetry().WithTimeout(TimeSpan.FromSeconds(30)));

                    agent.MaxToolCallsPerTurn = 10;
                });
            });
        // END SNIPPET docs/how-to/MAF/usage.md#per-tool-activity-configuration
    }
}
