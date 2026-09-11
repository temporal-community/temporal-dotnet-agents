// HARNESS for docs/how-to/MAF/dos-and-donts.md
// § "Do decorate the registered `IChatClient` when you need per-LLM-call visibility".
//
// The block opens with a bare `agent.ChatClient = ...` statement that has no enclosing lambda in
// the doc, so the harness supplies `agent` as a parameter alongside `opts`.
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenAI;
using TemporalCommunity.Extensions.Agents;
using DocSnippets.Harness;

// The doc names its example decorator `LoggingChatClient`, which collides with MEAI's own
// Microsoft.Extensions.AI.LoggingChatClient. The alias resolves the ambiguity out here so the
// snippet below can stay verbatim. (Worth a doc rename someday — see README.md.)
using LoggingChatClient = DocSnippets.Harness.LoggingChatClient;

namespace DocSnippets.DosAndDonts;

internal static class DoDecorateTheRegisteredChatClient
{
    internal static void Configure(DurableAgentBuilder agent, TemporalAgentsOptions opts)
    {
        // BEGIN SNIPPET docs/how-to/MAF/dos-and-donts.md#do-decorate-the-registered-ichatclient-when-you-need-per-llm-call-visibility (lines 335-344)
        agent.ChatClient = sp => new LoggingChatClient(
            sp.GetRequiredService<OpenAIClient>().GetChatClient("gpt-4o-mini").AsIChatClient(),
            sp.GetRequiredService<ILogger<LoggingChatClient>>());

        opts.AddDurableAgent("Assistant", agent =>
        {
            agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
        });
        // END SNIPPET docs/how-to/MAF/dos-and-donts.md#do-decorate-the-registered-ichatclient-when-you-need-per-llm-call-visibility
    }
}
