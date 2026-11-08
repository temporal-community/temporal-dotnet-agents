// HARNESS — compile the public MAF chat-history memory registration example.
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.VectorData;
using TemporalCommunity.Extensions.Agents;
using Temporalio.Extensions.Hosting;

namespace DocSnippets.Memory;

internal static class EpisodicMemory
{
    internal static void Configure(IServiceCollection services)
    {
        services.AddTemporalClient("localhost:7233", "default");
        services.AddHostedTemporalWorker("agents")
            .AddTemporalAgents(agents =>
            {
                // BEGIN SNIPPET docs/how-to/MAF/memory.md#episodic-memory
                agents.Agent(
                    "SupportAgent",
                    new ChatClientAgentOptions { ChatOptions = new ChatOptions { Instructions = "..." } },
                    temporal => temporal.UseChatHistoryMemory(memory =>
                        memory.UseVectorStore(
                            services => services.GetRequiredService<VectorStore>(),
                            collectionName: "support-memory",
                            vectorDimensions: 1536)));
                // END SNIPPET docs/how-to/MAF/memory.md#episodic-memory
            });
    }
}
