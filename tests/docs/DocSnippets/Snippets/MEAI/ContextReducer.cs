using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TemporalCommunity.Extensions.AI;
using Temporalio.Client;
using Temporalio.Extensions.Hosting;

namespace DocSnippets.MEAI;

internal static class ContextReducer
{
    internal static void Configure(
        HostApplicationBuilder clientBuilder,
        HostApplicationBuilder workerBuilder)
    {
        // BEGIN SNIPPET docs/how-to/MEAI/usage.md#context-only-chat-reducers
        clientBuilder.Services.AddTemporalClient("localhost:7233", "default");
        clientBuilder.Services.AddDurableAIClient(
            "durable-chat",
            options => options.DefaultContextReducerKey = "recent-context");

#pragma warning disable MEAI001
        workerBuilder.Services.AddKeyedSingleton<IChatReducer>(
            "recent-context",
            new MessageCountingChatReducer(20));
#pragma warning restore MEAI001

        workerBuilder.Services
            .AddHostedTemporalWorker("localhost:7233", "default", "durable-chat")
            .AddDurableAIWorker();
        // END SNIPPET docs/how-to/MEAI/usage.md#context-only-chat-reducers
    }
}
