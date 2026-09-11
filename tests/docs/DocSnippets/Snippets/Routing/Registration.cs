// HARNESS for docs/how-to/MAF/routing.md § "Registration".
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Temporalio.Extensions.Hosting;
using TemporalCommunity.Extensions.Agents;
using DocSnippets.Harness;

namespace DocSnippets.Routing;

internal static class Registration
{
    internal static void Configure(IServiceCollection services, IChatClient chatClient)
    {
        // BEGIN SNIPPET docs/how-to/MAF/routing.md#registration (lines 51-83)
        services.AddChatClient(chatClient);
        services.AddTemporalClient("localhost:7233", "default");

        services.AddHostedTemporalWorker("agents")
            .AddTemporalAgents(opts =>
            {
                opts.AddDurableAgent("Classifier", agent =>
                {
                    agent.Instructions = "Classify the user's question into one of: ORDERS, TECH_SUPPORT, GENERAL.";
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
                    agent.Description  = "Handles technical issues and troubleshooting.";
                    agent.Instructions = "You are a technical support specialist...";
                    agent.ChatClient   = sp => sp.GetRequiredService<IChatClient>();
                });
                opts.AddDurableAgent("GeneralAgent", agent =>
                {
                    agent.Description  = "Handles questions that don't fit other specialists.";
                    agent.Instructions = "You are a general assistant.";
                    agent.ChatClient   = sp => sp.GetRequiredService<IChatClient>();
                });
            })
            .AddWorkflow<CustomerServiceWorkflow>();
        // END SNIPPET docs/how-to/MAF/routing.md#registration
    }
}
