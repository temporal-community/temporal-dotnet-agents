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

        var selectedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ToolsetIds)
        {
            if (string.IsNullOrWhiteSpace(id) || !selectedIds.Add(id))
            {
                throw Failure("The durable toolset manifest contains an invalid toolset selection.");
            }
        }

        var activationKeys = new HashSet<string>(StringComparer.Ordinal);
        var functionNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in Members)
        {
            member.Validate(selectedIds);
            if (!activationKeys.Add(member.ActivationKey)
                || !functionNames.Add(member.Declaration.Name))
            {
                throw Failure("The durable toolset manifest contains an ambiguous member.");
            }
        }

        var expected = DurableToolsetManifestFingerprint.Create(this);
        if (!string.Equals(Fingerprint, expected, StringComparison.Ordinal))
        {
            throw Failure("The durable toolset manifest fingerprint is invalid.");
        }
    }

    internal DurableToolsetManifest Narrow(IReadOnlyList<string>? selectedToolsetIds)
    {
        Validate();
        if (selectedToolsetIds is null)
        {
            return this;
        }

        var requested = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in selectedToolsetIds)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw Failure("A durable turn toolset selection contains an empty ID.");
            }

            if (!requested.Add(id))
            {
                throw Failure($"Durable toolset '{id}' was selected more than once for one turn.");
            }

            if (!ToolsetIds.Contains(id, StringComparer.Ordinal))
            {
                throw Failure(
                    $"Durable toolset '{id}' is outside the workflow's recorded baseline.");
            }
        }

        // A caller can select a subset, but cannot reorder the recorded authority baseline.
        var orderedIds = ToolsetIds.Where(requested.Contains).ToArray();
        var narrowed = new DurableToolsetManifest
        {
            ManifestVersion = ManifestVersion,
            ToolsetIds = orderedIds,
            Members = Members.Where(member => requested.Contains(member.ToolsetId)).ToArray(),
            Fingerprint = string.Empty,
        };
        narrowed = narrowed with
        {
            Fingerprint = DurableToolsetManifestFingerprint.Create(narrowed),
        };
        narrowed.Validate();
        return narrowed;
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

    public required string MemberIdentityFingerprint { get; init; }

    public required DurableFunctionDeclarationSnapshot Declaration { get; init; }

    public required ActivityOptions ToolActivityOptions { get; init; }

    public bool InterceptorEnabled { get; init; }

    public ActivityOptions? InterceptorActivityOptions { get; init; }

    public bool SkipInterceptor { get; init; }

    public bool RequiresApproval { get; init; }

    public required TimeSpan ApprovalTimeout { get; init; }

    internal void Validate(HashSet<string> selectedToolsetIds)
    {
        Declaration.Validate();
        if (string.IsNullOrWhiteSpace(ToolsetId)
            || !selectedToolsetIds.Contains(ToolsetId)
            || string.IsNullOrWhiteSpace(ActivationKey)
            || !string.Equals(
                MemberIdentityFingerprint,
                DurableToolsetMemberIdentityFingerprint.Create(
                    ToolsetId,
                    ActivationKey,
                    Declaration),
                StringComparison.Ordinal)
            || !ToolActivityOptions.StartToCloseTimeout.HasValue
            || ToolActivityOptions.StartToCloseTimeout.Value <= TimeSpan.Zero
            || !ToolActivityOptions.HeartbeatTimeout.HasValue
            || ToolActivityOptions.HeartbeatTimeout.Value <= TimeSpan.Zero
            || ApprovalTimeout <= TimeSpan.Zero
            || (InterceptorEnabled && InterceptorActivityOptions is null)
            || (!InterceptorEnabled && InterceptorActivityOptions is not null))
        {
            throw DurableToolsetManifest.Failure(
                "The durable toolset manifest contains an invalid member or policy.");
        }
    }
}

internal static class DurableToolsetMemberIdentityFingerprint
{
    internal static string Create(
        string toolsetId,
        string activationKey,
        DurableFunctionDeclarationSnapshot declaration)
    {
        var value = JsonSerializer.SerializeToElement(new
        {
            ToolsetId = toolsetId,
            ActivationKey = activationKey,
            Declaration = declaration,
        }, DurableAIJsonUtilities.DefaultOptions);
        return $"tai-tool-member-v1:{DurableJsonSchemaFingerprint.Create(value)}";
    }
}

internal static class DurableToolsetAuthorityBindingFingerprint
{
    internal static string Create(string manifestFingerprint, string memberIdentityFingerprint)
    {
        var value = JsonSerializer.SerializeToElement(new
        {
            ManifestFingerprint = manifestFingerprint,
            MemberIdentityFingerprint = memberIdentityFingerprint,
        }, DurableAIJsonUtilities.DefaultOptions);
        return $"tai-tool-binding-v1:{DurableJsonSchemaFingerprint.Create(value)}";
    }
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
