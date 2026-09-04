using Temporalio.Testing;

namespace TemporalCommunity.Extensions.AI.IntegrationTests;

/// <summary>
/// AI integration tests register the embedded environment client directly with hosted workers.
/// Give that manual client the same converter contract production callers must configure.
/// </summary>
internal static class TemporalServiceTestEnvironment
{
    internal static readonly Version MinimumTemporalServiceVersion =
        TemporalCommunity.Extensions.Tests.Shared.TemporalServiceTestEnvironment.MinimumTemporalServiceVersion;

    internal static async Task<WorkflowEnvironment> StartLocalAsync(params string[] extraArgs)
    {
        var environment = await TemporalCommunity.Extensions.Tests.Shared.TemporalServiceTestEnvironment
            .StartLocalAsync(extraArgs);
        environment.Client.Options.DataConverter = DurableAIDataConverter.Instance;
        return environment;
    }

    internal static async Task<WorkflowEnvironment> StartTimeSkippingAsync()
    {
        var environment = await TemporalCommunity.Extensions.Tests.Shared.TemporalServiceTestEnvironment
            .StartTimeSkippingAsync();
        environment.Client.Options.DataConverter = DurableAIDataConverter.Instance;
        return environment;
    }

    internal static Version ParseAndValidateServerVersion(string? serverVersion) =>
        TemporalCommunity.Extensions.Tests.Shared.TemporalServiceTestEnvironment
            .ParseAndValidateServerVersion(serverVersion);
}
