// HARNESS for docs/how-to/MAF/mcp-tools.md § "Choose your catalog: discovery or pinned definitions".
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using TemporalCommunity.Extensions.Agents;

namespace DocSnippets.McpTools;

internal static class ChooseYourCatalog
{
    private static IReadOnlyList<Tool> LoadCheckedInDefinitions() => [];

    internal static void ConfigureDiscovered(DurableAgentBuilder agent, IList<McpClientTool> discovered)
    {
        agent.AddTools(discovered);   // every returned tool, each with the default policy
    }

    internal static void Configure(DurableAgentBuilder agent, McpClient mcp)
    {
        // BEGIN SNIPPET docs/how-to/MAF/mcp-tools.md#choose-your-catalog-discovery-or-pinned-definitions (lines 69-74)
        IReadOnlyList<Tool> reviewed = LoadCheckedInDefinitions();

        foreach (var definition in reviewed)
        {
            agent.AddTool(new McpClientTool(mcp, definition));
        }
        // END SNIPPET docs/how-to/MAF/mcp-tools.md#choose-your-catalog-discovery-or-pinned-definitions
    }
}
