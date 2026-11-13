// HARNESS for docs/library-combinations.md § "Replace a hand-rolled chat workflow".
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Extensions.Hosting;
using TemporalCommunity.Extensions.AI;

namespace DocSnippets.LibraryCombinations;

internal static class AdoptingManagedSession
{
    internal static async Task ConfigureAsync(
        HostApplicationBuilder builder,
        IChatClient innerChatClient)
    {
        // BEGIN SNIPPET docs/library-combinations.md#replace-a-hand-rolled-chat-workflow
        // Application-owned: HostApplicationBuilder builder and innerChatClient.
        builder.Services.AddChatClient(innerChatClient);
        builder.Services.AddTemporalClient("localhost:7233", "default");
        builder.Services.AddHostedTemporalWorker("durable-chat").AddDurableAI();

        using var host = builder.Build();
        await host.StartAsync();
        var sessionClient = host.Services.GetRequiredService<DurableChatSessionClient>();
        var response = await sessionClient.SendAsync(
            "customer-42",
            [new ChatMessage(ChatRole.User, "Summarize my latest order.")]);
        // END SNIPPET docs/library-combinations.md#replace-a-hand-rolled-chat-workflow
    }
}
