using TemporalCommunity.Extensions.Agents;
using Temporalio.Testing;

namespace DocSnippets.DosAndDonts;

// BEGIN SNIPPET docs/how-to/MAF/dos-and-donts.md#configure-a-local-temporal-server-for-agents-integration-tests
internal static class AgentTestEnvironment
{
    internal static async Task<WorkflowEnvironment> StartLocalAsync()
    {
        var environment = await WorkflowEnvironment.StartLocalAsync(new()
        {
            DevServerOptions = new()
            {
                DownloadVersion = "v1.8.0",
                ExtraArgs =
                [
                    "--search-attribute", "AgentName=Keyword",
                    "--search-attribute", "SessionCreatedAt=Datetime",
                    "--search-attribute", "TurnCount=Int",
                ],
            },
        });
        environment.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;
        return environment;
    }
}
// END SNIPPET docs/how-to/MAF/dos-and-donts.md#configure-a-local-temporal-server-for-agents-integration-tests
