// HARNESS for docs/how-to/MAF/usage.md § "Reducing the LLM Context Window".
//
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenAI;
using Temporalio.Extensions.Hosting;
using TemporalCommunity.Extensions.Agents;

namespace DocSnippets.Usage;

internal static class ReducingTheLlmContextWindow
{
    internal static void Configure(HostApplicationBuilder builder, OpenAIClient openAiClient)
    {
        // BEGIN SNIPPET docs/how-to/MAF/usage.md#reducing-the-llm-context-window
#pragma warning disable MEAI001
        builder.Services.AddKeyedSingleton<IChatReducer>(
            "recent-context",
            new MessageCountingChatReducer(20));
        builder.Services.AddTemporalClient("localhost:7233", "default");

        builder.Services
            .AddHostedTemporalWorker("agents")
            .AddTemporalAgents(opts =>
            {
                opts.DefaultContextReducerKey = "recent-context";
                opts.AddDurableAgent("MyAgent", agent =>
                {
                    agent.Instructions = "You are a helpful assistant.";
                    agent.ChatClient   = sp => sp.GetRequiredService<IChatClient>();
                    agent.MaxModelContextTokens = 8192;
                    agent.ModelOutputTokenReserve = 1024;
                });
            });
#pragma warning restore MEAI001
        // END SNIPPET docs/how-to/MAF/usage.md#reducing-the-llm-context-window
    }
}
