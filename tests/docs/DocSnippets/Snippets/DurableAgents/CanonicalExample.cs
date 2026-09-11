// HARNESS for docs/how-to/MAF/durable-agents.md § "Canonical example".
//
// Already uses the correct factory-first AddTool form (name first, `name:` on the inner
// AIFunctionFactory.Create). Compiled here to keep it that way.
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenAI;
using Temporalio.Extensions.Hosting;
using TemporalCommunity.Extensions.Agents;
using DocSnippets.Harness;

namespace DocSnippets.DurableAgents;

internal static class CanonicalExample
{
    internal static void Configure(
        HostApplicationBuilder builder,
        OpenAIClient openAiClient,
        string model,
        string taskQueue)
    {
        // BEGIN SNIPPET docs/how-to/MAF/durable-agents.md#canonical-example (lines 21-60)
        builder.Services.AddSingleton<OrderService>();
        builder.Services.AddSingleton<RefundService>();
        builder.Services.AddSingleton<EmailService>();

        builder.Services.AddChatClient(openAiClient.GetChatClient(model).AsIChatClient());
        builder.Services.AddTemporalClient("localhost:7233", "default");

        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddTemporalAgents(opts =>
            {
                opts.AddDurableAgent("RefundAgent", agent =>
                {
                    agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
                    agent.Instructions = "You are a refund specialist...";
                    agent.MaxToolCallsPerTurn = 10;  // caps the per-turn LLM↔tool loop; default 20 — see usage.md

                    // Read tool — retries on transient failure, bounded at five attempts by default.
                    agent.AddTool("lookup_order", sp => AIFunctionFactory.Create(
                        sp.GetRequiredService<OrderService>().LookupOrder,
                        name: "lookup_order"));

                    // Write tools — never retry, never re-fire on activity-level retry.
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
                });
            });
        // END SNIPPET docs/how-to/MAF/durable-agents.md#canonical-example
    }
}
