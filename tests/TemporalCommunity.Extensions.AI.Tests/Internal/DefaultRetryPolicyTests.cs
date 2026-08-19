using Temporalio.Common;
using TemporalCommunity.Extensions.AI.Internal;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests.Internal;

public class DefaultRetryPolicyTests
{
    [Fact]
    public void ResolveForModel_WhenUnset_UsesBoundedInteractiveDefault()
    {
        var actual = DefaultRetryPolicy.ResolveForModel(null);

        Assert.Equal(DefaultRetryPolicy.DefaultMaximumAttempts, actual.MaximumAttempts);
        Assert.Equal(
            TimeSpan.FromSeconds(DefaultRetryPolicy.DefaultModelMaximumIntervalSeconds),
            actual.MaximumInterval);
    }

    [Fact]
    public void ResolveForTool_WhenUnset_UsesBoundedRecoveryDefault()
    {
        var actual = DefaultRetryPolicy.ResolveForTool(null);

        Assert.Equal(DefaultRetryPolicy.DefaultMaximumAttempts, actual.MaximumAttempts);
        Assert.Equal(
            TimeSpan.FromSeconds(DefaultRetryPolicy.DefaultToolMaximumIntervalSeconds),
            actual.MaximumInterval);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    public void Resolvers_WhenConfigured_PreserveExactPolicy(int maximumAttempts)
    {
        var configured = new RetryPolicy
        {
            InitialInterval = TimeSpan.FromSeconds(3),
            BackoffCoefficient = 1.5F,
            MaximumInterval = TimeSpan.FromMinutes(4),
            MaximumAttempts = maximumAttempts,
            NonRetryableErrorTypes = ["Permanent"],
        };

        Assert.Same(configured, DefaultRetryPolicy.ResolveForModel(configured));
        Assert.Same(configured, DefaultRetryPolicy.ResolveForTool(configured));
    }
}
