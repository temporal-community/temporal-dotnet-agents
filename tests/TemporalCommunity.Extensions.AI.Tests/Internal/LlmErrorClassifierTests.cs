using System.Net;
using TemporalCommunity.Extensions.AI.Internal;
using Temporalio.Exceptions;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests.Internal;

/// <summary>
/// Unit tests for <see cref="LlmErrorClassifier"/> — the HTTP-status-bucket classifier that
/// decides whether an LLM-call exception should fail fast (non-retryable) or be retried.
/// </summary>
public class LlmErrorClassifierTests
{
    // ── Non-retryable statuses (fail fast) ────────────────────────────────────

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(422)]
    public void HttpRequestException_NonRetryableStatus_IsNonRetryable(int status)
    {
        var ex = new HttpRequestException("boom", inner: null, statusCode: (HttpStatusCode)status);
        Assert.True(LlmErrorClassifier.IsNonRetryable(ex));
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(422)]
    public void ClientResultException_NonRetryableStatus_IsNonRetryable(int status)
    {
        var ex = new global::System.ClientModel.ClientResultException(status);
        Assert.True(LlmErrorClassifier.IsNonRetryable(ex));
    }

    // ── Retryable statuses (let RetryPolicy handle) ───────────────────────────

    [Theory]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public void HttpRequestException_RetryableStatus_IsRetryable(int status)
    {
        var ex = new HttpRequestException("boom", inner: null, statusCode: (HttpStatusCode)status);
        Assert.False(LlmErrorClassifier.IsNonRetryable(ex));
    }

    [Theory]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public void ClientResultException_RetryableStatus_IsRetryable(int status)
    {
        var ex = new global::System.ClientModel.ClientResultException(status);
        Assert.False(LlmErrorClassifier.IsNonRetryable(ex));
    }

    // ── Default-retryable posture for unknown / statusless errors ─────────────

    [Fact]
    public void UnknownException_IsRetryable()
    {
        Assert.False(LlmErrorClassifier.IsNonRetryable(new InvalidOperationException("scripted client exhausted")));
    }

    [Fact]
    public void HttpRequestException_NoStatus_IsRetryable()
    {
        // A raw connection failure carries no StatusCode — default to retryable (transient).
        Assert.False(LlmErrorClassifier.IsNonRetryable(new HttpRequestException("connection refused")));
    }

    [Fact]
    public void NullException_IsRetryable()
    {
        Assert.False(LlmErrorClassifier.IsNonRetryable(null));
    }

    [Fact]
    public void UnknownHttpStatus_NotInEitherBucket_IsRetryable()
    {
        // 418 is neither in the non-retryable set nor a known transient — default retryable.
        var ex = new HttpRequestException("teapot", inner: null, statusCode: (HttpStatusCode)418);
        Assert.False(LlmErrorClassifier.IsNonRetryable(ex));
    }

    // ── Wrapped / inner exceptions ────────────────────────────────────────────

    [Fact]
    public void InnerException_NonRetryableStatus_IsUnwrapped()
    {
        var inner = new HttpRequestException("bad request", inner: null, statusCode: HttpStatusCode.BadRequest);
        var wrapped = new InvalidOperationException("wrapper", inner);
        Assert.True(LlmErrorClassifier.IsNonRetryable(wrapped));
    }

    [Fact]
    public void AggregateException_WithNonRetryableInner_IsUnwrapped()
    {
        var inner = new HttpRequestException("unauthorized", inner: null, statusCode: HttpStatusCode.Unauthorized);
        var agg = new AggregateException(new Exception("noise"), inner);
        Assert.True(LlmErrorClassifier.IsNonRetryable(agg));
    }

    [Fact]
    public void AggregateException_AllRetryableInners_IsRetryable()
    {
        var agg = new AggregateException(
            new HttpRequestException("throttled", inner: null, statusCode: HttpStatusCode.TooManyRequests),
            new TimeoutException("slow"));
        Assert.False(LlmErrorClassifier.IsNonRetryable(agg));
    }

    [Fact]
    public void DeeplyNestedNonRetryable_IsUnwrapped()
    {
        var leaf = new global::System.ClientModel.ClientResultException(403);
        var mid = new InvalidOperationException("mid", leaf);
        var top = new AggregateException(new Exception("top"), mid);
        Assert.True(LlmErrorClassifier.IsNonRetryable(top));
    }

    [Fact]
    public void CreateNonRetryableFailure_DeterministicStatus_ReturnsTypedTemporalFailure()
    {
        var providerError = new HttpRequestException(
            "unauthorized", inner: null, statusCode: HttpStatusCode.Unauthorized);

        var failure = LlmFailurePolicy.CreateNonRetryableFailure(providerError);

        var typedFailure = Assert.IsType<ApplicationFailureException>(failure);
        Assert.Equal(DurableChatActivities.LlmNonRetryableErrorType, typedFailure.ErrorType);
        Assert.Same(providerError, typedFailure.InnerException);
    }

    [Fact]
    public void CreateNonRetryableFailure_TransientStatus_ReturnsNull()
    {
        var providerError = new HttpRequestException(
            "throttled", inner: null, statusCode: HttpStatusCode.TooManyRequests);

        Assert.Null(LlmFailurePolicy.CreateNonRetryableFailure(providerError));
    }

}
