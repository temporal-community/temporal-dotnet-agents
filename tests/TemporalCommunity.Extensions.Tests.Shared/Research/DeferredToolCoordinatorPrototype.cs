namespace TemporalCommunity.Extensions.Tests.Shared.Research;

/// <summary>
/// Test-only prototype used to evaluate generalized deferred tool input. This type is compiled
/// into test infrastructure and is never included in either shipping package.
/// </summary>
public sealed class DeferredToolCoordinatorPrototype
{
    private const int DefaultResolutionHistoryLimit = 16;
    private readonly Dictionary<string, DeferredToolRequestPrototype> _pending = new(StringComparer.Ordinal);
    private readonly List<DeferredToolResolutionPrototype> _resolutions = [];

    public DeferredToolCoordinatorPrototype(int maximumPending = 1)
    {
        if (maximumPending <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPending));
        }

        MaximumPending = maximumPending;
    }

    public int MaximumPending { get; }

    public DeferredToolTransitionPrototype Begin(
        DeferredToolRequestPrototype request,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateIdentifier(request.InvocationId, nameof(request.InvocationId));
        ValidateIdentifier(request.InputKind, nameof(request.InputKind));
        ExpireDue(now);

        if (_pending.TryGetValue(request.InvocationId, out var pending))
        {
            return pending == request
                ? DeferredToolTransitionPrototype.AlreadyPending
                : DeferredToolTransitionPrototype.Conflict;
        }

        var prior = FindResolution(request.InvocationId);
        if (prior is not null)
        {
            return prior.Request == request
                ? DeferredToolTransitionPrototype.AlreadyResolved
                : DeferredToolTransitionPrototype.Conflict;
        }

        if (request.ExpiresAt <= now)
        {
            Remember(new DeferredToolResolutionPrototype(
                request,
                DeferredToolResolutionKindPrototype.TimedOut,
                Completion: null));
            return DeferredToolTransitionPrototype.Expired;
        }

        if (_pending.Count >= MaximumPending)
        {
            return DeferredToolTransitionPrototype.CapacityExceeded;
        }

        _pending.Add(request.InvocationId, request);
        return DeferredToolTransitionPrototype.Accepted;
    }

    public DeferredToolTransitionPrototype Submit(
        DeferredToolCompletionPrototype completion,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(completion);
        ValidateIdentifier(completion.InvocationId, nameof(completion.InvocationId));
        ExpireDue(now);

        var prior = FindResolution(completion.InvocationId);
        if (prior is not null)
        {
            return prior.Kind switch
            {
                DeferredToolResolutionKindPrototype.Completed when prior.Completion == completion =>
                    DeferredToolTransitionPrototype.AlreadyResolved,
                DeferredToolResolutionKindPrototype.Completed => DeferredToolTransitionPrototype.Conflict,
                DeferredToolResolutionKindPrototype.TimedOut => DeferredToolTransitionPrototype.Expired,
                DeferredToolResolutionKindPrototype.Cancelled => DeferredToolTransitionPrototype.Cancelled,
                _ => throw new InvalidOperationException("Unknown research resolution kind."),
            };
        }

        if (!_pending.Remove(completion.InvocationId, out var request))
        {
            return DeferredToolTransitionPrototype.NotPending;
        }

        Remember(new DeferredToolResolutionPrototype(
            request,
            DeferredToolResolutionKindPrototype.Completed,
            completion));
        return DeferredToolTransitionPrototype.Accepted;
    }

    public DeferredToolTransitionPrototype Cancel(string invocationId, DateTimeOffset now)
    {
        ValidateIdentifier(invocationId, nameof(invocationId));
        ExpireDue(now);

        var prior = FindResolution(invocationId);
        if (prior is not null)
        {
            return prior.Kind == DeferredToolResolutionKindPrototype.Cancelled
                ? DeferredToolTransitionPrototype.AlreadyResolved
                : DeferredToolTransitionPrototype.Conflict;
        }

        if (!_pending.Remove(invocationId, out var request))
        {
            return DeferredToolTransitionPrototype.NotPending;
        }

        Remember(new DeferredToolResolutionPrototype(
            request,
            DeferredToolResolutionKindPrototype.Cancelled,
            Completion: null));
        return DeferredToolTransitionPrototype.Accepted;
    }

    public DeferredToolExecutionPrototype ExecuteResolved(
        string invocationId,
        Func<DeferredToolCompletionPrototype, bool> authorize,
        Action<DeferredToolCompletionPrototype> effect)
    {
        ValidateIdentifier(invocationId, nameof(invocationId));
        ArgumentNullException.ThrowIfNull(authorize);
        ArgumentNullException.ThrowIfNull(effect);

        var completion = FindResolution(invocationId)?.Completion;
        if (completion is null)
        {
            return DeferredToolExecutionPrototype.NotReady;
        }

        if (!authorize(completion))
        {
            return DeferredToolExecutionPrototype.AuthorizationDenied;
        }

        effect(completion);
        return DeferredToolExecutionPrototype.Executed;
    }

    public DeferredToolCoordinatorSnapshotPrototype Capture()
    {
        return new DeferredToolCoordinatorSnapshotPrototype(
            MaximumPending,
            [.. _pending.Values],
            [.. _resolutions]);
    }

    public static DeferredToolCoordinatorPrototype Restore(
        DeferredToolCoordinatorSnapshotPrototype snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var coordinator = new DeferredToolCoordinatorPrototype(snapshot.MaximumPending);
        if (snapshot.Pending.Count > snapshot.MaximumPending)
        {
            throw new InvalidOperationException("The deferred-tool snapshot exceeds its pending-input capacity.");
        }

        foreach (var request in snapshot.Pending)
        {
            if (!coordinator._pending.TryAdd(request.InvocationId, request))
            {
                throw new InvalidOperationException("The deferred-tool snapshot contains duplicate pending IDs.");
            }
        }

        foreach (var resolution in snapshot.Resolutions.TakeLast(DefaultResolutionHistoryLimit))
        {
            if (coordinator._pending.ContainsKey(resolution.Request.InvocationId)
                || coordinator.FindResolution(resolution.Request.InvocationId) is not null)
            {
                throw new InvalidOperationException("The deferred-tool snapshot contains conflicting IDs.");
            }

            coordinator._resolutions.Add(resolution);
        }

        return coordinator;
    }

    private void ExpireDue(DateTimeOffset now)
    {
        foreach (var request in _pending.Values.Where(request => request.ExpiresAt <= now).ToArray())
        {
            _pending.Remove(request.InvocationId);
            Remember(new DeferredToolResolutionPrototype(
                request,
                DeferredToolResolutionKindPrototype.TimedOut,
                Completion: null));
        }
    }

    private DeferredToolResolutionPrototype? FindResolution(string invocationId) =>
        _resolutions.LastOrDefault(resolution =>
            string.Equals(resolution.Request.InvocationId, invocationId, StringComparison.Ordinal));

    private void Remember(DeferredToolResolutionPrototype resolution)
    {
        _resolutions.RemoveAll(existing =>
            string.Equals(existing.Request.InvocationId, resolution.Request.InvocationId, StringComparison.Ordinal));
        _resolutions.Add(resolution);
        if (_resolutions.Count > DefaultResolutionHistoryLimit)
        {
            _resolutions.RemoveRange(0, _resolutions.Count - DefaultResolutionHistoryLimit);
        }
    }

    private static void ValidateIdentifier(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty identifier is required.", parameterName);
        }
    }
}

public sealed record DeferredToolRequestPrototype(
    string InvocationId,
    string InputKind,
    DateTimeOffset ExpiresAt);

public sealed record DeferredToolCompletionPrototype(string InvocationId, string Payload);

public sealed record DeferredToolResolutionPrototype(
    DeferredToolRequestPrototype Request,
    DeferredToolResolutionKindPrototype Kind,
    DeferredToolCompletionPrototype? Completion);

public sealed record DeferredToolCoordinatorSnapshotPrototype(
    int MaximumPending,
    IReadOnlyList<DeferredToolRequestPrototype> Pending,
    IReadOnlyList<DeferredToolResolutionPrototype> Resolutions);

public enum DeferredToolTransitionPrototype
{
    Accepted,
    AlreadyPending,
    AlreadyResolved,
    Conflict,
    CapacityExceeded,
    NotPending,
    Expired,
    Cancelled,
}

public enum DeferredToolResolutionKindPrototype
{
    Completed,
    TimedOut,
    Cancelled,
}

public enum DeferredToolExecutionPrototype
{
    NotReady,
    AuthorizationDenied,
    Executed,
}
