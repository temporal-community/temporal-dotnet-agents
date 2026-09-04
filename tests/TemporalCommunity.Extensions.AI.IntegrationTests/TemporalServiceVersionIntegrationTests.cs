using Temporalio.Api.WorkflowService.V1;
using Xunit;

namespace TemporalCommunity.Extensions.AI.IntegrationTests;

[Trait("Category", "Integration")]
public class TemporalServiceVersionIntegrationTests
{
    [Fact]
    public async Task PinnedDevServer_MeetsMinimumTemporalServiceVersion()
    {
        await using var environment = await TemporalServiceTestEnvironment.StartLocalAsync();
        environment.Client.Options.DataConverter = DurableAIDataConverter.Instance;

        var response = await environment.Client.WorkflowService.GetSystemInfoAsync(
            new GetSystemInfoRequest());
        var version = TemporalServiceTestEnvironment.ParseAndValidateServerVersion(
            response.ServerVersion);

        Assert.True(version >= TemporalServiceTestEnvironment.MinimumTemporalServiceVersion);
        Assert.Equal(1, version.Major);
        Assert.Equal(31, version.Minor);
    }
}
