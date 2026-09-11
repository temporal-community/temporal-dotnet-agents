// HARNESS for docs/how-to/MAF/usage.md § "Worker-hosted example".
//
// DOC DEFECT (as of this file's commit): all three tool registrations in the doc pass the factory
// lambda as the FIRST argument —
//     agent.AddTool(sp => AIFunctionFactory.Create(..., "lookup_order"));
// — which binds to no overload. The factory overload is
// AddTool(string name, Func<IServiceProvider, AIFunction> factory, Action<DurableToolOptions>?).
// Corrected below; the doc-side fix is tracked in README.md next to this project.
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenAI;
using Temporalio.Extensions.Hosting;
using TemporalCommunity.Extensions.Agents;
using DocSnippets.Harness;

namespace DocSnippets.Usage;

internal static class WorkerHostedExample
{
    internal static void Configure(
        HostApplicationBuilder builder,
        OpenAIClient openAiClient,
        string model,
        string taskQueue)
    {
        // BEGIN SNIPPET docs/how-to/MAF/usage.md#worker-hosted-example (lines 36-74)
        builder.Services.AddSingleton<OrderService>();
        builder.Services.AddSingleton<RefundService>();
        builder.Services.AddSingleton<EmailService>();
        builder.Services.AddChatClient(openAiClient.GetChatClient(model).AsIChatClient()).Build();
        builder.Services.AddTemporalClient("localhost:7233", "default");

        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddTemporalAgents(opts =>
            {
                opts.AddDurableAgent("RefundAgent", agent =>
                {
                    agent.Description = "Issues refunds and notifies the customer.";
                    agent.Instructions = "You are a refund specialist.";
                    agent.ChatClient   = sp => sp.GetRequiredService<IChatClient>();

                    agent.AddTool("lookup_order", sp => AIFunctionFactory.Create(
                        sp.GetRequiredService<OrderService>().LookupOrder,
                        name: "lookup_order"));

                    // Write tools must opt out of retry — non-idempotent re-execution is the foot-gun.
                    agent.AddTool(
                        "apply_refund",
                        sp => AIFunctionFactory.Create(
                            sp.GetRequiredService<RefundService>().ApplyRefund,
                            name: "apply_refund"),
                        opts => opts.NoRetry());

                    agent.AddTool(
                        "send_email",
                        sp => AIFunctionFactory.Create(
                            sp.GetRequiredService<EmailService>().SendEmail,
                            name: "send_email"),
                        opts => opts.NoRetry());

                    agent.MaxToolCallsPerTurn = 10;
                });
            })
            .AddWorkflow<RefundWorkflow>();
        // END SNIPPET docs/how-to/MAF/usage.md#worker-hosted-example
    }
}
