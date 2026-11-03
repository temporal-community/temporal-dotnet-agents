using TemporalCommunity.Extensions.AI.Internal;
using Temporalio.Common;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests.Internal;

public sealed class DurableManagedToolRetryPolicyTests
{
    [Fact]
    public void Resolve_UnmarkedToolDefaultsToOneAttempt()
    {
        var policy = DurableManagedToolRetryPolicy.Resolve(null, null, false, "write");

        Assert.Equal(1, policy.MaximumAttempts);
    }

    [Fact]
    public void Resolve_IdempotentToolUsesBoundedDefault()
    {
        var policy = DurableManagedToolRetryPolicy.Resolve(null, null, true, "read");

        Assert.Equal(DefaultRetryPolicy.DefaultMaximumAttempts, policy.MaximumAttempts);
    }

    [Fact]
    public void Resolve_AllowsMultipleAttemptsOnlyForIdempotentTools()
    {
        var configured = new RetryPolicy { MaximumAttempts = 3 };

        var policy = DurableManagedToolRetryPolicy.Resolve(configured, null, true, "write");

        Assert.Same(configured, policy);
        Assert.Throws<InvalidOperationException>(() =>
            DurableManagedToolRetryPolicy.Resolve(configured, null, false, "write"));
    }

    [Fact]
    public void Resolve_RejectsUnboundedIdempotentPolicy()
    {
        Assert.Throws<InvalidOperationException>(() =>
            DurableManagedToolRetryPolicy.Resolve(
                new RetryPolicy { MaximumAttempts = 0 },
                null,
                true,
                "write"));
    }
}
