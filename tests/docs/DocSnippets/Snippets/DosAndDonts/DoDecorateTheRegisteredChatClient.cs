// HARNESS for docs/how-to/MAF/dos-and-donts.md
// § "Do decorate the registered `IChatClient` when you need per-LLM-call visibility".
//
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenAI;
using TemporalCommunity.Extensions.Agents;
using DocSnippets.Harness;

// The doc names its example decorator `AuditingChatClient`, which collides with MEAI's own
// Microsoft.Extensions.AI.AuditingChatClient. The alias resolves the ambiguity out here so the
// snippet below can stay verbatim. (Worth a doc rename someday — see README.md.)
using AuditingChatClient = DocSnippets.Harness.AuditingChatClient;

namespace DocSnippets.DosAndDonts;

internal static class DoDecorateTheRegisteredChatClient
{
    internal static void Configure(TemporalAgentsOptions opts)
    {
        // BEGIN SNIPPET docs/how-to/MAF/dos-and-donts.md#do-decorate-the-registered-ichatclient-when-you-need-per-llm-call-visibility
        opts.AddDurableAgent("Assistant", agent =>
        {
            agent.ChatClient = sp => new AuditingChatClient(
                sp.GetRequiredService<OpenAIClient>().GetChatClient("gpt-4o-mini").AsIChatClient(),
                sp.GetRequiredService<ILogger<AuditingChatClient>>());
        });
        // END SNIPPET docs/how-to/MAF/dos-and-donts.md#do-decorate-the-registered-ichatclient-when-you-need-per-llm-call-visibility
    }
}
