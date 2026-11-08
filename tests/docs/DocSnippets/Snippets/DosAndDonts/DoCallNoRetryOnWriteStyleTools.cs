// HARNESS for docs/how-to/MAF/dos-and-donts.md § "Do rely on the one-attempt default for effectful tools".
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using TemporalCommunity.Extensions.Agents;

namespace DocSnippets.DosAndDonts;

internal static class DoCallNoRetryOnWriteStyleTools
{
    internal static void Configure(TemporalAgentsOptions agents, AIFunction sendEmailTool, AIFunction lookupOrderTool)
    {
        // BEGIN SNIPPET docs/how-to/MAF/dos-and-donts.md#do-rely-on-the-one-attempt-default-for-effectful-tools (lines 544-555)
        agents.Agent(
            "SupportAgent",
            new ChatClientAgentOptions
            {
                ChatOptions = new ChatOptions
                {
                    Tools = [sendEmailTool, lookupOrderTool],
                },
            });
        // END SNIPPET docs/how-to/MAF/dos-and-donts.md#do-rely-on-the-one-attempt-default-for-effectful-tools
    }
}
