using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Temporalio.Activities;

namespace TemporalCommunity.Extensions.AI;

internal sealed class DurableToolsetActivities(
    Internal.DurableToolsetCatalog catalog,
    ILoggerFactory? loggerFactory = null)
{
    private readonly ILogger _logger = (loggerFactory ?? NullLoggerFactory.Instance)
        .CreateLogger<DurableToolsetActivities>();

    [Activity("TemporalCommunity.Extensions.AI.ResolveDurableToolsets")]
    public Task<Internal.DurableToolsetManifest> ResolveDurableToolsetsAsync(
        Internal.DurableToolsetResolutionRequest request)
    {
        using var span = DurableChatTelemetry.ActivitySource.StartActivity(
            DurableChatTelemetry.ToolsetResolveSpanName,
            ActivityKind.Internal);
        _logger.LogToolsetResolverStarted();
        try
        {
            var manifest = catalog.Resolve(request);
            var tags = new TagList { { "outcome", "success" } };
            DurableChatTelemetry.ToolsetResolverAttempts.Add(1, tags);
            DurableChatTelemetry.ToolsetResolverSelectedToolsets.Record(manifest.ToolsetIds.Count);
            DurableChatTelemetry.ToolsetResolverSelectedFunctions.Record(manifest.Members.Count);
            span?.SetTag("temporal.ai.toolset.manifest.version", manifest.ManifestVersion);
            span?.SetTag("temporal.ai.toolset.selected_toolsets", manifest.ToolsetIds.Count);
            span?.SetTag("temporal.ai.toolset.selected_functions", manifest.Members.Count);
            span?.SetTag("outcome", "success");
            _logger.LogToolsetResolverCompleted(manifest.ToolsetIds.Count, manifest.Members.Count);
            return Task.FromResult(manifest);
        }
        catch (Exception exception)
        {
            var reason = Internal.DurableToolsetValidation.GetReason(exception);
            var attemptTags = new TagList { { "outcome", "failure" } };
            var rejectionTags = new TagList { { "reason", reason } };
            DurableChatTelemetry.ToolsetResolverAttempts.Add(1, attemptTags);
            DurableChatTelemetry.ToolsetValidationRejections.Add(1, rejectionTags);
            span?.SetTag("outcome", "failure");
            span?.SetTag("reason", reason);
            span?.SetStatus(ActivityStatusCode.Error);
            _logger.LogToolsetResolverFailed(exception, reason);
            throw;
        }
    }
}
