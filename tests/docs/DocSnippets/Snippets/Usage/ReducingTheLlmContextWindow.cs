// HARNESS for docs/how-to/MAF/usage.md § "Reducing the LLM Context Window".
//
// TWO DOC DEFECTS (as of this file's commit), both found by compiling this block:
//
//  1. `openAiClient.GetChatClient("gpt-4o-mini").AsBuilder()` does not compile. OpenAI's
//     `ChatClient` is not an `IChatClient`; `AsBuilder()` is an extension ON `IChatClient`. The
//     call needs `.AsIChatClient()` first — exactly as every other snippet in the same doc does.
//     Corrected below.
//
//  2. `MessageCountingChatReducer` is annotated `[Experimental("MEAI001")]`, so a reader who copies
//     this block gets error MEAI001 until they suppress it. The doc never says so. The pragma below
//     sits OUTSIDE the snippet markers so the snippet text stays doc-faithful — when the doc grows
//     the suppression note, the pragma moves inside.
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
#pragma warning disable MEAI001 // see note at the top of this file
        // BEGIN SNIPPET docs/how-to/MAF/usage.md#reducing-the-llm-context-window (lines 162-184)
        var chatClient = openAiClient.GetChatClient("gpt-4o-mini")
            .AsIChatClient()
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
        // END SNIPPET docs/how-to/MAF/usage.md#reducing-the-llm-context-window
#pragma warning restore MEAI001
    }
}
