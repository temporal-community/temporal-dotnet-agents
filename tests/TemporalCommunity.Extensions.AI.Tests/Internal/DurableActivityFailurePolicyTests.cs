using System.Text;
using TemporalCommunity.Extensions.AI.Internal;
using Temporalio.Exceptions;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests.Internal;

public sealed class DurableActivityFailurePolicyTests
{
    private const string Secret = "private prompt, credentials, and provider response";

    [Fact]
    public void CreateBounded_OrdinaryExceptionPreservesSdkRetryIdentityWithoutContent()
    {
        var original = new InvalidOperationException(Secret, new Exception(Secret));
        original.Data["secret"] = Secret;

        var bounded = DurableActivityFailurePolicy.CreateBounded(original, "FallbackFailure");

        Assert.Equal(nameof(InvalidOperationException), bounded.ErrorType);
        Assert.False(bounded.NonRetryable);
        Assert.Null(bounded.NextRetryDelay);
        AssertPrivateContentOmitted(bounded);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CreateBounded_ApplicationFailurePreservesRetryMetadataWithoutContent(bool nonRetryable)
    {
        var original = new ApplicationFailureException(
            Secret,
            new InvalidOperationException(Secret),
            errorType: "ProviderBackoff",
            nonRetryable: nonRetryable,
            details: [Secret],
            nextRetryDelay: TimeSpan.FromMinutes(3));

        var bounded = DurableActivityFailurePolicy.CreateBounded(original, "FallbackFailure");

        Assert.Equal("ProviderBackoff", bounded.ErrorType);
        Assert.Equal(nonRetryable, bounded.NonRetryable);
        Assert.Equal(TimeSpan.FromMinutes(3), bounded.NextRetryDelay);
        AssertPrivateContentOmitted(bounded);
    }

    [Fact]
    public void CreateBounded_ExplicitPermanentClassificationRetainsItsStableType()
    {
        var bounded = DurableActivityFailurePolicy.CreateBounded(
            new InvalidOperationException(Secret),
            "DurableModelResultSerializationFailed",
            nonRetryable: true);

        Assert.Equal("DurableModelResultSerializationFailed", bounded.ErrorType);
        Assert.True(bounded.NonRetryable);
        AssertPrivateContentOmitted(bounded);
    }

    [Fact]
    public void CreateBounded_ExplicitRetryabilityOverrideDoesNotLoseApplicationRetryDelay()
    {
        var bounded = DurableActivityFailurePolicy.CreateBounded(
            new ApplicationFailureException(
                Secret,
                errorType: "ProviderBackoff",
                nonRetryable: true,
                nextRetryDelay: TimeSpan.FromSeconds(7)),
            "FallbackFailure",
            nonRetryable: false);

        Assert.Equal("ProviderBackoff", bounded.ErrorType);
        Assert.False(bounded.NonRetryable);
        Assert.Equal(TimeSpan.FromSeconds(7), bounded.NextRetryDelay);
        AssertPrivateContentOmitted(bounded);
    }

    [Fact]
    public void CreateBounded_DoesNotTruncateClrRetryIdentity()
    {
        var original = new ExceptionWithAStaticClrTypeNameLongerThanTheApplicationFailureContentLimitSoRetryPolicyMatchingMustNotSilentlyChangeWhenFailureDetailsAreSanitized();

        var bounded = DurableActivityFailurePolicy.CreateBounded(original, "FallbackFailure");

        Assert.True(Encoding.UTF8.GetByteCount(original.GetType().Name) > 128);
        Assert.Equal(original.GetType().Name, bounded.ErrorType);
        AssertPrivateContentOmitted(bounded);
    }

    [Fact]
    public void CreateBounded_DynamicApplicationErrorTypeRemainsUtf8Bounded()
    {
        var bounded = DurableActivityFailurePolicy.CreateBounded(
            new ApplicationFailureException(
                Secret,
                errorType: new string('界', 100),
                nextRetryDelay: TimeSpan.FromSeconds(2)),
            "FallbackFailure");

        Assert.Equal(new string('界', 42), bounded.ErrorType);
        Assert.True(Encoding.UTF8.GetByteCount(bounded.ErrorType!) <= 128);
        Assert.Equal(TimeSpan.FromSeconds(2), bounded.NextRetryDelay);
        AssertPrivateContentOmitted(bounded);
    }

    private static void AssertPrivateContentOmitted(ApplicationFailureException bounded)
    {
        Assert.Equal("Durable activity failed; failure details were omitted.", bounded.Message);
        Assert.Null(bounded.InnerException);
        Assert.Null(bounded.StackTrace);
        Assert.Equal(0, bounded.Details.Count);
        Assert.Empty(bounded.Data);
        Assert.DoesNotContain(Secret, bounded.ToString(), StringComparison.Ordinal);
    }

    private sealed class ExceptionWithAStaticClrTypeNameLongerThanTheApplicationFailureContentLimitSoRetryPolicyMatchingMustNotSilentlyChangeWhenFailureDetailsAreSanitized
        : Exception
    {
    }
}
