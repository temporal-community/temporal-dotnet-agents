using Temporalio.Testing;

namespace Temporalio.Extensions.Agents.IntegrationTests.Helpers;

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
    internal static Task<WorkflowEnvironment> StartLocalAsync(params string[] extraArgs)
    {
        var allArgs = new List<string>(SearchAttributeArgs);
        allArgs.AddRange(extraArgs);

        return WorkflowEnvironment.StartLocalAsync(new()
        {
            DevServerOptions = new()
            {
                ExtraArgs = [.. allArgs],
            },
        });
    }
}
