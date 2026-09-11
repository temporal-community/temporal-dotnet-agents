// HARNESS for docs/how-to/MAF/dos-and-donts.md § "Do call `opts.NoRetry()` on write-style tools".
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using TemporalCommunity.Extensions.Agents;

namespace DocSnippets.DosAndDonts;

internal static class DoCallNoRetryOnWriteStyleTools
{
    internal static void Configure(TemporalAgentsOptions opts, AIFunction sendEmailTool, AIFunction lookupOrderTool)
    {
        // BEGIN SNIPPET docs/how-to/MAF/dos-and-donts.md#do-call-optsnoretry-on-write-style-tools (lines 472-483)
        opts.AddDurableAgent("SupportAgent", agent =>
        {
            agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();

            // Write tool — non-idempotent, must not double-fire on retry.
            agent.AddTool(sendEmailTool, opts => opts.NoRetry().WithTimeout(TimeSpan.FromSeconds(30)));

            // Read tool — idempotent, inherits worker default retry policy.
            agent.AddTool(lookupOrderTool);
        });
        // END SNIPPET docs/how-to/MAF/dos-and-donts.md#do-call-optsnoretry-on-write-style-tools
    }
}
