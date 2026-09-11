// HARNESS for docs/how-to/MAF/usage.md § "Reducing the LLM Context Window".
//
// Both defects this harness originally caught are now fixed in the doc:
//
//  1. `openAiClient.GetChatClient(...).AsBuilder()` did not compile — OpenAI's `ChatClient` is not
//     an `IChatClient`, and `AsBuilder()` is an extension ON `IChatClient`. The doc now calls
//     `.AsIChatClient()` first.
//
//  2. `MessageCountingChatReducer` is `[Experimental("MEAI001")]`. The doc now says so, so the
//     pragma sits INSIDE the snippet markers — it is part of what a reader copies.
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
        // BEGIN SNIPPET docs/how-to/MAF/usage.md#reducing-the-llm-context-window (lines 166-188)
#pragma warning disable MEAI001
        var chatClient = openAiClient.GetChatClient("gpt-4o-mini")
            .AsIChatClient()                                     // OpenAI's ChatClient is not an IChatClient
            .AsBuilder()
            .UseChatReducer(new MessageCountingChatReducer(20))   // 20-message window to the LLM
            .Build();
        //  Note: do NOT call .UseFunctionInvocation() — the durable-agent path composes
        //  the chat pipeline internally and tools are dispatched as separate Temporal activities.
        //  This is enforced: the activity fails non-retryably before the model is called.

        builder.Services.AddChatClient(chatClient);
        builder.Services.AddTemporalClient("localhost:7233", "default");

        builder.Services
            .AddHostedTemporalWorker("agents")
            .AddTemporalAgents(opts =>
            {
                opts.AddDurableAgent("MyAgent", agent =>
                {
                    agent.Instructions = "You are a helpful assistant.";
                    agent.ChatClient   = sp => sp.GetRequiredService<IChatClient>();
                });
            });
#pragma warning restore MEAI001
        // END SNIPPET docs/how-to/MAF/usage.md#reducing-the-llm-context-window
    }
}
