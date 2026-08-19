using Temporalio.Common;
using Temporalio.Workflows;

namespace TemporalCommunity.Extensions.AI.Internal;

internal sealed class DurableToolsetCatalog
{
    private readonly IReadOnlyList<DurableToolsetRegistration> registrations;
    private readonly DurableExecutionOptions options;

    internal DurableToolsetCatalog(
        IEnumerable<DurableToolsetRegistration> registrations,
        DurableExecutionOptions options)
    {
        this.registrations = registrations.ToArray();
        this.options = options;
    }

    internal DurableToolsetManifest Resolve(DurableToolsetResolutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ResolutionVersion != DurableToolsetResolutionRequest.CurrentVersion)
        {
            throw DurableToolsetManifest.Failure(
                $"Unsupported durable toolset resolution version '{request.ResolutionVersion}'.",
                DurableToolsetValidationReasons.InvalidManifestVersion);
        }

        if (request.UseWorkerDefaults && request.ToolsetIds is not null)
        {
            throw DurableToolsetManifest.Failure(
                "A durable toolset resolution request cannot combine worker defaults with explicit IDs.",
                DurableToolsetValidationReasons.AuthorityMismatch);
        }

        var selected = request.UseWorkerDefaults
            ? ResolveDefaults()
            : ResolveExplicit(request.ToolsetIds ?? []);
        var toolsetIds = selected.Select(registration => registration.Id).ToArray();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var members = new List<DurableToolsetManifestMember>();

        foreach (var toolset in selected)
        {
            foreach (var member in toolset.Members)
            {
                if (!names.Add(member.Declaration.Name))
                {
                    throw DurableToolsetManifest.Failure(
                        $"Selected durable toolsets contain more than one function named " +
                        $"'{member.Declaration.Name}'. Function names use exact ordinal comparison.",
                        DurableToolsetValidationReasons.NameCollision);
                }

                members.Add(CreateManifestMember(toolset.Id, member));
            }
        }

        var manifest = new DurableToolsetManifest
        {
            ManifestVersion = DurableToolsetManifest.CurrentVersion,
            ToolsetIds = toolsetIds,
            Members = members.ToArray(),
            Fingerprint = string.Empty,
        };
        manifest = manifest with
        {
            Fingerprint = DurableToolsetManifestFingerprint.Create(manifest),
        };
        manifest.Validate();
        return manifest;
    }

    private DurableToolsetRegistration[] ResolveExplicit(IReadOnlyList<string> requestedIds)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var selected = new List<DurableToolsetRegistration>(requestedIds.Count);
        foreach (var id in requestedIds)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw DurableToolsetManifest.Failure(
                    "A durable toolset resolution request contains an empty toolset ID.",
                    DurableToolsetValidationReasons.InvalidDeclaration);
            }

            if (!seen.Add(id))
            {
                throw DurableToolsetManifest.Failure(
                    $"Durable toolset '{id}' was selected more than once.",
                    DurableToolsetValidationReasons.DuplicateSelection);
            }

            var registration = registrations.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, id, StringComparison.Ordinal));
            if (registration is null)
            {
                throw DurableToolsetManifest.Failure(
                    $"Durable toolset '{id}' is not registered on this worker.",
                    DurableToolsetValidationReasons.UnknownToolset);
            }

            selected.Add(registration);
        }

        return selected.ToArray();
    }

    private DurableToolsetRegistration[] ResolveDefaults()
    {
        if (options.DefaultToolsetIds is null)
        {
            return registrations.Where(registration => registration.IsImplicitDefault).ToArray();
        }

        if (registrations.Any(registration => registration.IsImplicitDefault))
        {
            throw DurableToolsetManifest.Failure(
                "Explicit DefaultToolsetIds cannot be combined with the implicit AddDurableTools " +
                "toolset. Register every selected default through AddDurableToolset.",
                DurableToolsetValidationReasons.AuthorityMismatch);
        }

        return ResolveExplicit(options.DefaultToolsetIds);
    }

    private DurableToolsetManifestMember CreateManifestMember(
        string toolsetId,
        DurableToolsetMemberRegistration member)
    {
        var retryPolicy = DefaultRetryPolicy.ResolveForTool(
            member.Options.RetryPolicy ?? options.RetryPolicy);
        var interceptorEnabled = options.DefaultToolInterceptor is not null;
        return new DurableToolsetManifestMember
        {
            ToolsetId = toolsetId,
            ActivationKey = member.ActivationKey,
            MemberIdentityFingerprint = DurableToolsetMemberIdentityFingerprint.Create(
                toolsetId,
                member.ActivationKey,
                member.Declaration),
            Declaration = member.Declaration,
            ToolActivityOptions = new ActivityOptions
            {
                StartToCloseTimeout = member.Options.StartToCloseTimeout ?? options.ActivityTimeout,
                HeartbeatTimeout = member.Options.HeartbeatTimeout ?? options.HeartbeatTimeout,
                RetryPolicy = Clone(retryPolicy),
                Summary = member.Declaration.Name,
            },
            InterceptorEnabled = interceptorEnabled,
            InterceptorActivityOptions = interceptorEnabled
                ? new ActivityOptions
                {
                    StartToCloseTimeout = member.Options.InterceptorTimeout ?? options.ActivityTimeout,
                    HeartbeatTimeout = options.HeartbeatTimeout,
                    RetryPolicy = Clone(DefaultRetryPolicy.ResolveForTool(options.RetryPolicy)),
                    Summary = member.Declaration.Name,
                }
                : null,
            SkipInterceptor = member.Options.SkipInterceptorFlag,
            RequiresApproval = member.Options.RequireApprovalFlag,
            ApprovalTimeout = member.Options.ApprovalTimeout ?? options.ApprovalTimeout,
        };
    }

    private static RetryPolicy Clone(RetryPolicy policy) => new()
    {
        InitialInterval = policy.InitialInterval,
        BackoffCoefficient = policy.BackoffCoefficient,
        MaximumInterval = policy.MaximumInterval,
        MaximumAttempts = policy.MaximumAttempts,
        NonRetryableErrorTypes = policy.NonRetryableErrorTypes?.ToArray(),
    };
}

internal sealed class DurableToolsetActivationCatalog
{
    private readonly IReadOnlyDictionary<string, DurableToolsetActivation> members;

    internal DurableToolsetActivationCatalog(IEnumerable<DurableToolsetRegistration> registrations)
    {
        var result = new Dictionary<string, DurableToolsetActivation>(StringComparer.Ordinal);
        foreach (var registration in registrations)
        {
            foreach (var member in registration.Members)
            {
                result.Add(
                    member.ActivationKey,
                    new DurableToolsetActivation(registration.Id, member));
            }
        }

        members = result;
    }

    internal bool TryGetValue(string activationKey, out DurableToolsetActivation member) =>
        members.TryGetValue(activationKey, out member!);
}

internal sealed record DurableToolsetActivation(
    string ToolsetId,
    DurableToolsetMemberRegistration Member);
