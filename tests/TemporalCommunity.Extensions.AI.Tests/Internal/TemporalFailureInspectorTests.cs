using TemporalCommunity.Extensions.AI.Exceptions;
using TemporalCommunity.Extensions.AI.Internal;
using Temporalio.Exceptions;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests.Internal;

public class TemporalFailureInspectorTests
{
    [Fact]
    public void FindNonRetryableApplicationFailure_TraversesNestedAggregateWrappers()
    {
        var expected = new ApplicationFailureException(
            "fatal durable configuration",
            errorType: nameof(DurableConfigurationException),
            nonRetryable: true);
        var wrapped = new AggregateException(
            new InvalidOperationException("activity wrapper", expected));

        var actual = TemporalFailureInspector.FindNonRetryableApplicationFailure(
            wrapped,
            nameof(DurableConfigurationException));

        Assert.Same(expected, actual);
    }

    [Fact]
    public void FindNonRetryableApplicationFailure_DoesNotMatchOrdinaryToolFailure()
    {
        var ordinary = new ApplicationFailureException(
            "ordinary user tool failure",
            errorType: "UserToolFailure",
            nonRetryable: true);

        var actual = TemporalFailureInspector.FindNonRetryableApplicationFailure(
            ordinary,
            nameof(DurableConfigurationException));

        Assert.Null(actual);
    }
}
