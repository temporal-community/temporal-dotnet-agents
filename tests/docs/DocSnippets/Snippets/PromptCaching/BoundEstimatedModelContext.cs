using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TemporalCommunity.Extensions.Agents;
using Temporalio.Extensions.Hosting;

namespace DocSnippets.PromptCaching;

internal static class BoundEstimatedModelContext
{
    internal static void Configure(HostApplicationBuilder builder, IChatClient chatClient)
    {
        builder.Services.AddChatClient(chatClient);

        // BEGIN SNIPPET docs/how-to/MAF/prompt-caching.md#6-bound-the-estimated-model-context
        builder.Services
            .AddHostedTemporalWorker("agents")
            .AddTemporalAgents(opts =>
            {
                opts.AddDurableAgent("Assistant", agent =>
                {
                    agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
                    agent.MaxModelContextTokens = 8192;
                    agent.ModelOutputTokenReserve = 1024;
                });
            });
        // END SNIPPET docs/how-to/MAF/prompt-caching.md#6-bound-the-estimated-model-context
    }
}
