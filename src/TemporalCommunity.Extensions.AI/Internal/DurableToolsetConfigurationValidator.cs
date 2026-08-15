using Microsoft.Extensions.Options;
using TemporalCommunity.Extensions.AI.Exceptions;
using Temporalio.Exceptions;
using Temporalio.Extensions.Hosting;

namespace TemporalCommunity.Extensions.AI.Internal;

/// <summary>
/// Validates the stock workflow's worker-owned default toolset selection when worker options are
/// finalized. Explicit custom-workflow selections remain activity-time inputs and are validated
/// by <see cref="DurableToolsetCatalog"/> when resolved.
/// </summary>
internal sealed class DurableToolsetConfigurationValidator(
    DurableToolsetCatalog catalog)
    : IPostConfigureOptions<TemporalWorkerServiceOptions>
{
    public void PostConfigure(string? name, TemporalWorkerServiceOptions options)
    {
        try
        {
            _ = catalog.Resolve(new DurableToolsetResolutionRequest
            {
                UseWorkerDefaults = true,
            });
        }
        catch (ApplicationFailureException exception)
        {
            throw new DurableConfigurationException(
                "The worker's default durable toolset configuration is invalid. " +
                "Correct DefaultToolsetIds and the selected toolset registrations before " +
                "starting the worker.",
                exception);
        }
    }
}
