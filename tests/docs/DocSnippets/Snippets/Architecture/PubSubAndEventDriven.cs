// HARNESS for docs/architecture/MAF/pub-sub-and-event-driven.md § "Registration".
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Extensions.Hosting;
using TemporalCommunity.Extensions.Agents;
using DocSnippets.Harness;

namespace DocSnippets.Architecture;

internal static class PubSubAndEventDriven
{
    internal static void Configure(HostApplicationBuilder builder, IChatClient chatClient)
    {
        // BEGIN SNIPPET docs/architecture/MAF/pub-sub-and-event-driven.md#registration
        builder.Services.AddChatClient(chatClient);

        builder.Services.AddTemporalClient("localhost:7233", "default");

        builder.Services
            .AddHostedTemporalWorker("agents")
            .AddWorkflow<EventDrivenFanOutWorkflow>()
            .AddTemporalAgents(opts =>
            {
                opts.AddDurableAgent("SummarizerAgent", agent =>
                {
                    agent.Instructions = "Summarize the post.";
                    agent.ChatClient   = sp => sp.GetRequiredService<IChatClient>();
                });
                opts.AddDurableAgent("TaggerAgent", agent =>
                {
                    agent.Instructions = "Generate tags for the post.";
                    agent.ChatClient   = sp => sp.GetRequiredService<IChatClient>();
                });
                opts.AddDurableAgent("ModeratorAgent", agent =>
                {
                    agent.Instructions = "Moderate the post for policy violations.";
                    agent.ChatClient   = sp => sp.GetRequiredService<IChatClient>();
                });
            });
        // END SNIPPET docs/architecture/MAF/pub-sub-and-event-driven.md#registration
    }
}
