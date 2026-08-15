using Temporalio.Activities;

namespace TemporalCommunity.Extensions.AI;

internal sealed class DurableToolsetActivities(Internal.DurableToolsetCatalog catalog)
{
    [Activity("TemporalCommunity.Extensions.AI.ResolveDurableToolsets")]
    public Task<Internal.DurableToolsetManifest> ResolveDurableToolsetsAsync(
        Internal.DurableToolsetResolutionRequest request) =>
        Task.FromResult(catalog.Resolve(request));
}
