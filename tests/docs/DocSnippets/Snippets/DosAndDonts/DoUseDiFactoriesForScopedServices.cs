// HARNESS for docs/how-to/MAF/dos-and-donts.md
// § "Do use DI factories on `AddDurableAgent` for agents that need scoped services".
//
// This section already uses the correct factory-first AddTool form. It is compiled here precisely
// because it is the shape the other docs get wrong — a regression here would be silent otherwise.
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using TemporalCommunity.Extensions.Agents;
using DocSnippets.Harness;

namespace DocSnippets.DosAndDonts;

internal static class DoUseDiFactoriesForScopedServices
{
    internal static void Configure(TemporalAgentsOptions opts)
    {
        // BEGIN SNIPPET docs/how-to/MAF/dos-and-donts.md#do-use-di-factories-on-adddurableagent-for-agents-that-need-scoped-services (lines 151-160)
        opts.AddDurableAgent("MyAgent", agent =>
        {
            agent.Instructions = "...";
            agent.ChatClient   = sp => sp.GetRequiredService<IChatClient>();
            agent.AddTool("do_thing", sp => AIFunctionFactory.Create(
                sp.GetRequiredService<IMyService>().DoThingAsync,
                "do_thing"));
        });
        // END SNIPPET docs/how-to/MAF/dos-and-donts.md#do-use-di-factories-on-adddurableagent-for-agents-that-need-scoped-services
    }
}
