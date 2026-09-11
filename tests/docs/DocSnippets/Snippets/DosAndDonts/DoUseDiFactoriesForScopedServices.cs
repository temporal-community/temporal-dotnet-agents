// HARNESS for docs/how-to/MAF/dos-and-donts.md
// § "Do use DI factories on `AddDurableAgent` instead of `BuildServiceProvider()`" and
// § "Don't assume the builder's factory slots share a lifetime".
//
// This section already uses the correct factory-first AddTool form. It is compiled here precisely
// because it is the shape the other docs get wrong — a regression here would be silent otherwise.
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using TemporalCommunity.Extensions.Agents;
using TemporalCommunity.Extensions.Agents.Session;
using DocSnippets.Harness;

namespace DocSnippets.DosAndDonts;

internal static class DoUseDiFactoriesForScopedServices
{
    internal static void Configure(TemporalAgentsOptions opts)
    {
        // BEGIN SNIPPET docs/how-to/MAF/dos-and-donts.md#do-use-di-factories-on-adddurableagent-instead-of-buildserviceprovider (lines 151-161)
        opts.AddDurableAgent("MyAgent", agent =>
        {
            agent.Instructions = "...";
            agent.ChatClient   = sp => sp.GetRequiredService<IChatClient>();
            agent.AddTool("do_thing", sp => AIFunctionFactory.Create(
                sp.GetRequiredService<IMyService>().DoThingAsync,
                name: "do_thing"));
        });
        // END SNIPPET docs/how-to/MAF/dos-and-donts.md#do-use-di-factories-on-adddurableagent-instead-of-buildserviceprovider
    }

    // The doc's companion warning: resolve a scoped service inside the tool BODY, never in the
    // factory, because the factory result is cached for the worker's lifetime.
    internal static void ScopedResolutionInsideToolBody(DurableAgentBuilder agent)
    {
        // BEGIN SNIPPET docs/how-to/MAF/dos-and-donts.md#dont-assume-the-builders-factory-slots-share-a-lifetime (lines 178-188)
        agent.AddTool("do_thing", _ => AIFunctionFactory.Create(
            async (string id) =>
            {
                // Scoped to this one tool invocation, not to the worker.
                var db = TemporalAgentContext.Current.GetService<MyDbContext>()
                    ?? throw new InvalidOperationException("MyDbContext is not registered.");
                return await db.LookupAsync(id);
            },
            name: "do_thing"));
        // END SNIPPET docs/how-to/MAF/dos-and-donts.md#dont-assume-the-builders-factory-slots-share-a-lifetime
    }
}
