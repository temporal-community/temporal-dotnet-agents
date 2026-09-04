using Temporalio.Testing;
using TemporalCommunity.Extensions.Tests.Shared;

namespace TemporalCommunity.Extensions.Agents.IntegrationTests.Helpers;

/// <summary>
/// Shared helper for creating <see cref="WorkflowEnvironment"/> instances
/// with the custom search attributes required by <c>AgentWorkflow</c>.
/// </summary>
internal static class TestEnvironmentHelper
{
    /// <summary>
    /// The <c>--search-attribute</c> args that register the custom search attributes
    /// used by <see cref="Workflows.AgentWorkflow"/> (AgentName, SessionCreatedAt, TurnCount).
    /// </summary>
    internal static readonly string[] SearchAttributeArgs =
    [
        "--search-attribute", "AgentName=Keyword",
        "--search-attribute", "SessionCreatedAt=Datetime",
        "--search-attribute", "TurnCount=Int",
    ];

    /// <summary>
    /// Starts a local Temporal test environment with the required search attributes registered.
    /// </summary>
    internal static async Task<WorkflowEnvironment> StartLocalAsync(params string[] extraArgs)
    {
        var allArgs = new List<string>(SearchAttributeArgs);
        allArgs.AddRange(extraArgs);

        var environment = await TemporalServiceTestEnvironment.StartLocalAsync([.. allArgs]);
        environment.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;
        return environment;
    }
}
