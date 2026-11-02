using Temporalio.Testing;

namespace TemporalCommunity.Extensions.AI.IntegrationTests.Helpers;

/// <summary>Fresh server per test, configured before creating any client handle or worker.</summary>
public static class DurableChatTestEnvironment
{
    public static async Task<WorkflowEnvironment> StartLocalAsync()
    {
        var environment = await TemporalServiceTestEnvironment.StartLocalAsync();
        environment.Client.Options.DataConverter = DurableAIDataConverter.Instance;
        return environment;
    }

    public static async Task<WorkflowEnvironment> StartTimeSkippingAsync()
    {
        var environment = await TemporalServiceTestEnvironment.StartTimeSkippingAsync();
        environment.Client.Options.DataConverter = DurableAIDataConverter.Instance;
        return environment;
    }
}
