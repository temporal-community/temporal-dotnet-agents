using TemporalCommunity.Extensions.Tests.Shared.Research;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests.Research;

public sealed class DeferredToolCoordinatorPrototypeTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DefaultCapacity_AllowsOnePendingRequestAndRejectsTheNext()
    {
        var coordinator = new DeferredToolCoordinatorPrototype();

        Assert.Equal(DeferredToolTransitionPrototype.Accepted, coordinator.Begin(Request("one"), Now));
        Assert.Equal(
            DeferredToolTransitionPrototype.CapacityExceeded,
            coordinator.Begin(Request("two"), Now));
        Assert.Equal("one", Assert.Single(coordinator.Capture().Pending).InvocationId);
    }

    [Fact]
    public void Begin_DuplicateIsIdempotentButChangedDefinitionConflicts()
    {
        var coordinator = new DeferredToolCoordinatorPrototype();
        var request = Request("one");

        Assert.Equal(DeferredToolTransitionPrototype.Accepted, coordinator.Begin(request, Now));
        Assert.Equal(DeferredToolTransitionPrototype.AlreadyPending, coordinator.Begin(request, Now));
        Assert.Equal(
            DeferredToolTransitionPrototype.Conflict,
            coordinator.Begin(request with { InputKind = "different" }, Now));
    }

    [Fact]
    public void Submit_DuplicateIsIdempotentButDifferentPayloadConflicts()
    {
        var coordinator = new DeferredToolCoordinatorPrototype();
        var completion = new DeferredToolCompletionPrototype("one", "payload-a");
        coordinator.Begin(Request("one"), Now);

        Assert.Equal(DeferredToolTransitionPrototype.Accepted, coordinator.Submit(completion, Now));
        Assert.Equal(DeferredToolTransitionPrototype.AlreadyResolved, coordinator.Submit(completion, Now));
        Assert.Equal(
            DeferredToolTransitionPrototype.Conflict,
            coordinator.Submit(completion with { Payload = "payload-b" }, Now));
    }

    [Theory]
    [InlineData(true, DeferredToolTransitionPrototype.Expired)]
    [InlineData(false, DeferredToolTransitionPrototype.Cancelled)]
    public void TerminalRequest_CannotBeRevivedByLateCompletion(
        bool expire,
        DeferredToolTransitionPrototype expected)
    {
        var coordinator = new DeferredToolCoordinatorPrototype();
        coordinator.Begin(Request("one"), Now);
        if (expire)
        {
            coordinator.Submit(
                new DeferredToolCompletionPrototype("unknown", "ignored"),
                Now.AddMinutes(6));
        }
        else
        {
            coordinator.Cancel("one", Now);
        }

        Assert.Equal(
            expected,
            coordinator.Submit(new DeferredToolCompletionPrototype("one", "late"), Now.AddMinutes(6)));
    }

    [Fact]
    public void CaptureAndRestore_PreserveCapacityPendingInputAndBoundedResolutions()
    {
        var coordinator = new DeferredToolCoordinatorPrototype();
        coordinator.Begin(Request("one"), Now);
        var restored = DeferredToolCoordinatorPrototype.Restore(coordinator.Capture());

        Assert.Equal(
            DeferredToolTransitionPrototype.CapacityExceeded,
            restored.Begin(Request("two"), Now));
        Assert.Equal(
            DeferredToolTransitionPrototype.Accepted,
            restored.Submit(new DeferredToolCompletionPrototype("one", "ready"), Now));
        Assert.Equal(DeferredToolTransitionPrototype.Accepted, restored.Begin(Request("two"), Now));
        Assert.Equal("two", Assert.Single(restored.Capture().Pending).InvocationId);
        Assert.Equal("one", Assert.Single(restored.Capture().Resolutions).Request.InvocationId);
    }

    [Fact]
    public void Restore_RejectsSnapshotWhosePendingCountExceedsItsCap()
    {
        var snapshot = new DeferredToolCoordinatorSnapshotPrototype(
            MaximumPending: 1,
            Pending: [Request("one"), Request("two")],
            Resolutions: []);

        var exception = Assert.Throws<InvalidOperationException>(
            () => DeferredToolCoordinatorPrototype.Restore(snapshot));

        Assert.Contains("exceeds", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExecuteResolved_RechecksCurrentAuthorizationBeforeEveryEffectAttempt()
    {
        var coordinator = new DeferredToolCoordinatorPrototype();
        coordinator.Begin(Request("one"), Now);
        coordinator.Submit(new DeferredToolCompletionPrototype("one", "ready"), Now);
        var authorizationChecks = 0;
        var effects = 0;
        var allowed = false;

        bool Authorize(DeferredToolCompletionPrototype _)
        {
            authorizationChecks++;
            return allowed;
        }

        Assert.Equal(
            DeferredToolExecutionPrototype.AuthorizationDenied,
            coordinator.ExecuteResolved("one", Authorize, _ => effects++));
        allowed = true;
        Assert.Equal(
            DeferredToolExecutionPrototype.Executed,
            coordinator.ExecuteResolved("one", Authorize, _ => effects++));
        Assert.Equal(2, authorizationChecks);
        Assert.Equal(1, effects);
    }

    private static DeferredToolRequestPrototype Request(string id) =>
        new(id, "operator-input", Now.AddMinutes(5));
}
