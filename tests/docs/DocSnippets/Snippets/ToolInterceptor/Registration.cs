// HARNESS for docs/how-to/MAF/tool-interceptor.md § "Registration".
//
// DOC DEFECT (as of this file's commit): both tool registrations pass the factory lambda first.
// The factory overload takes the name first. Corrected below.
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Extensions.Hosting;
using TemporalCommunity.Extensions.Agents;
using DocSnippets.Harness;

namespace DocSnippets.ToolInterceptor;

internal static class Registration
{
    internal static void Configure(HostApplicationBuilder builder)
    {
        // BEGIN SNIPPET docs/how-to/MAF/tool-interceptor.md#registration (lines 283-311)
        builder.Services
            .AddHostedTemporalWorker("agents")
            .AddTemporalAgents(opts =>
            {
                // Worker-level default — applies to every agent that does not register its own.
                opts.DefaultToolInterceptor = sp => new RiskScoringInterceptor(
                    sp.GetRequiredService<RiskService>());

                opts.AddDurableAgent("OrderAgent", agent =>
                {
                    agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();

                    // Per-agent interceptor — overrides the worker default for this agent only.
                    agent.AddToolInterceptor(sp => new OrderPolicyInterceptor(
                        sp.GetRequiredService<OrderPolicyService>()));

                    agent.AddTool("lookup_order", sp => AIFunctionFactory.Create(
                        sp.GetRequiredService<OrderService>().LookupOrder, name: "lookup_order"));

                    agent.AddTool(
                        "cancel_order",
                        sp => AIFunctionFactory.Create(
                            sp.GetRequiredService<OrderService>().CancelOrder, name: "cancel_order"),
                        opts => opts.NoRetry());
                });
            });
        // END SNIPPET docs/how-to/MAF/tool-interceptor.md#registration
    }
}
