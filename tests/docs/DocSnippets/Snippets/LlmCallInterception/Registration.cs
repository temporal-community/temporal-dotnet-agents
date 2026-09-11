// HARNESS for docs/how-to/MAF/llm-call-interception.md § "Registration".
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenAI;
using TemporalCommunity.Extensions.Agents;
using DocSnippets.Harness;

// Same collision as in dos-and-donts: the doc's example decorator name shadows MEAI's
// Microsoft.Extensions.AI.AuditingChatClient. Aliased out here so the snippet stays verbatim.
using AuditingChatClient = DocSnippets.Harness.AuditingChatClient;

namespace DocSnippets.LlmCallInterception;

internal static class Registration
{
    internal static void Configure(TemporalAgentsOptions opts, AIFunction weatherTool)
    {
        // BEGIN SNIPPET docs/how-to/MAF/llm-call-interception.md#registration (lines 186-195)
        opts.AddDurableAgent("Assistant", agent =>
        {
            agent.Instructions = "You are a helpful assistant.";
            agent.ChatClient = sp => new AuditingChatClient(
                sp.GetRequiredService<OpenAIClient>().GetChatClient("gpt-4o-mini").AsIChatClient(),
                sp.GetRequiredService<ILogger<AuditingChatClient>>());
            agent.AddTool(weatherTool);
        });
        // END SNIPPET docs/how-to/MAF/llm-call-interception.md#registration
    }
}
