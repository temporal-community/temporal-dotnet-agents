// HARNESS for docs/how-to/MAF/skills.md § "Quick start".
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Extensions.Hosting;
using TemporalCommunity.Extensions.Agents;

namespace DocSnippets.Skills;

internal static class QuickStart
{
    internal static void Configure(HostApplicationBuilder builder)
    {
        // BEGIN SNIPPET docs/how-to/MAF/skills.md#quick-start (lines 49-70)
        builder.Services.AddTemporalClient("localhost:7233", "default");

        builder.Services
            .AddHostedTemporalWorker("agents")
            .AddTemporalAgents(opts =>
            {
                opts.AddDurableAgent("SupportAgent", agent =>
                {
                    agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
                    agent.Instructions = "You are a helpful support agent. " +
                        "A catalog of skills is available to you. " +
                        "Use load_skill to fetch instructions for a relevant skill before proceeding.";

                    // Use MAF's native file-skill discovery for ./skills.
                    agent.UseSkills(s =>
                    {
                        s.AddSkillsFromDirectory("./skills");
                    });
                });
            });
        // END SNIPPET docs/how-to/MAF/skills.md#quick-start
    }
}
