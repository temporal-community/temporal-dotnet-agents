// HARNESS for docs/how-to/MAF/routing.md § "Step 1: Register agents with descriptions".
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Temporalio.Extensions.Hosting;
using TemporalCommunity.Extensions.Agents;
using DocSnippets.Harness;

namespace DocSnippets.Routing;

internal static class RegisterAgentsWithDescriptions
{
    internal static void Configure(IServiceCollection services, IChatClient chatClient)
    {
        // BEGIN SNIPPET docs/how-to/MAF/routing.md#step-1-register-agents-with-descriptions (lines 161-195)
        services.AddChatClient(chatClient);
        services.AddTemporalClient("localhost:7233", "default");

        services.AddHostedTemporalWorker("agents")
            .AddTemporalAgents(opts =>
            {
                // Not a routable specialist — no Description set, so it is excluded from GetAgentDescriptors().
                opts.AddDurableAgent("Classifier", agent =>
                {
                    agent.Instructions = "Classify the user's question.";
                    agent.ChatClient   = sp => sp.GetRequiredService<IChatClient>();
                });
                opts.AddDurableAgent("OrdersAgent", agent =>
                {
                    agent.Description  = "Handles order tracking, returns, and shipping.";
                    agent.Instructions = "You are an orders specialist...";
                    agent.ChatClient   = sp => sp.GetRequiredService<IChatClient>();
                });
                opts.AddDurableAgent("TechSupportAgent", agent =>
                {
                    agent.Description  = "Handles technical issues, app crashes, and troubleshooting.";
                    agent.Instructions = "You are a technical support specialist...";
                    agent.ChatClient   = sp => sp.GetRequiredService<IChatClient>();
                });
                opts.AddDurableAgent("GeneralAgent", agent =>
                {
                    agent.Description  = "Handles general questions and fallback routing.";
                    agent.Instructions = "You are a general assistant.";
                    agent.ChatClient   = sp => sp.GetRequiredService<IChatClient>();
                });
            })
            .AddWorkflow<DynamicRoutingWorkflow>()
            .AddSingletonActivities<RoutingActivities>();
        // END SNIPPET docs/how-to/MAF/routing.md#step-1-register-agents-with-descriptions
    }
}
