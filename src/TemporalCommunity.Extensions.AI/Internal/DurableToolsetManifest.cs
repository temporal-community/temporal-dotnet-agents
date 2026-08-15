using System.Text.Json;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace TemporalCommunity.Extensions.AI.Internal;

internal sealed record DurableToolsetResolutionRequest
{
    internal const int CurrentVersion = 1;

    public bool UseWorkerDefaults { get; init; }

    public IReadOnlyList<string>? ToolsetIds { get; init; }
}

internal sealed record DurableToolsetManifest
{
    internal const int CurrentVersion = 1;

    public int ManifestVersion { get; init; }

    public required IReadOnlyList<string> ToolsetIds { get; init; }

    public required IReadOnlyList<DurableToolsetManifestMember> Members { get; init; }

    public required string Fingerprint { get; init; }

    internal void Validate()
    {
        if (ManifestVersion != CurrentVersion)
        {
            throw Failure(
                $"Unsupported durable toolset manifest version '{ManifestVersion}'. " +
                $"This worker supports version '{CurrentVersion}'.");
        }

        var expected = DurableToolsetManifestFingerprint.Create(this);
        if (!string.Equals(Fingerprint, expected, StringComparison.Ordinal))
        {
            throw Failure("The durable toolset manifest fingerprint is invalid.");
        }
    }

    internal static ApplicationFailureException Failure(string message) => new(
        message,
        errorType: nameof(Exceptions.DurableConfigurationException),
        nonRetryable: true);
}

internal sealed record DurableToolsetManifestMember
{
    public required string ToolsetId { get; init; }

    public required string ActivationKey { get; init; }

    public required DurableFunctionDeclarationSnapshot Declaration { get; init; }

    public required ActivityOptions ToolActivityOptions { get; init; }

    public bool InterceptorEnabled { get; init; }

    public ActivityOptions? InterceptorActivityOptions { get; init; }

    public bool SkipInterceptor { get; init; }

    public bool RequiresApproval { get; init; }

    public required TimeSpan ApprovalTimeout { get; init; }
}

internal static class DurableToolsetManifestFingerprint
{
    internal static string Create(DurableToolsetManifest manifest)
    {
        var payload = new DurableToolsetManifestFingerprintPayload
        {
            ManifestVersion = manifest.ManifestVersion,
            ToolsetIds = manifest.ToolsetIds,
            Members = manifest.Members,
        };
        var json = JsonSerializer.SerializeToElement(payload, DurableAIJsonUtilities.DefaultOptions);
        return $"tai-toolset-v1:{DurableJsonSchemaFingerprint.Create(json)}";
    }

    private sealed record DurableToolsetManifestFingerprintPayload
    {
        public int ManifestVersion { get; init; }

        public required IReadOnlyList<string> ToolsetIds { get; init; }

        public required IReadOnlyList<DurableToolsetManifestMember> Members { get; init; }
    }
}
