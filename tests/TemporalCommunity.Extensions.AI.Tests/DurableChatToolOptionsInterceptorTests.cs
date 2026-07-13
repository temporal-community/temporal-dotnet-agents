using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests;

/// <summary>
/// Tests for the Phase 2 interceptor additions to <see cref="DurableChatToolOptions"/>:
/// <see cref="DurableChatToolOptions.SkipInterceptor"/>,
/// <see cref="DurableChatToolOptions.WithInterceptorTimeout"/>,
/// <see cref="DurableChatToolOptions.RequireApproval"/>, and
/// <see cref="DurableChatToolOptions.WithApprovalTimeout"/>.
/// Mirrors <c>DurableToolOptionsInterceptorTests</c> in the Agents project for symmetry.
/// </summary>
public class DurableChatToolOptionsInterceptorTests
{
    [Fact]
    public void SkipInterceptorFlag_DefaultIsFalse()
    {
        var opts = new DurableChatToolOptions();
        Assert.False(opts.SkipInterceptorFlag);
    }

    [Fact]
    public void SkipInterceptor_SetsFlag()
    {
        var opts = new DurableChatToolOptions();
        opts.SkipInterceptor();
        Assert.True(opts.SkipInterceptorFlag);
    }

    [Fact]
    public void SkipInterceptor_ReturnsSameInstance()
    {
        var opts = new DurableChatToolOptions();
        Assert.Same(opts, opts.SkipInterceptor());
    }

    [Fact]
    public void RequireApprovalFlag_DefaultIsFalse()
    {
        var opts = new DurableChatToolOptions();
        Assert.False(opts.RequireApprovalFlag);
    }

    [Fact]
    public void RequireApproval_SetsFlag()
    {
        var opts = new DurableChatToolOptions();
        opts.RequireApproval();
        Assert.True(opts.RequireApprovalFlag);
    }

    [Fact]
    public void RequireApproval_ReturnsSameInstance()
    {
        var opts = new DurableChatToolOptions();
        Assert.Same(opts, opts.RequireApproval());
    }

    [Fact]
    public void InterceptorTimeout_DefaultIsNull()
    {
        var opts = new DurableChatToolOptions();
        Assert.Null(opts.InterceptorTimeout);
    }

    [Fact]
    public void WithInterceptorTimeout_SetsTimeout()
    {
        var opts = new DurableChatToolOptions();
        opts.WithInterceptorTimeout(TimeSpan.FromSeconds(30));
        Assert.Equal(TimeSpan.FromSeconds(30), opts.InterceptorTimeout);
    }

    [Fact]
    public void WithInterceptorTimeout_ReturnsSameInstance()
    {
        var opts = new DurableChatToolOptions();
        Assert.Same(opts, opts.WithInterceptorTimeout(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void WithInterceptorTimeout_Zero_Throws()
    {
        var opts = new DurableChatToolOptions();
        Assert.Throws<ArgumentOutOfRangeException>(() => opts.WithInterceptorTimeout(TimeSpan.Zero));
    }

    [Fact]
    public void WithInterceptorTimeout_Negative_Throws()
    {
        var opts = new DurableChatToolOptions();
        Assert.Throws<ArgumentOutOfRangeException>(() => opts.WithInterceptorTimeout(TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void AllInterceptorOptions_ChainFluently()
    {
        var opts = new DurableChatToolOptions()
            .RequireApproval()
            .WithInterceptorTimeout(TimeSpan.FromMinutes(2));

        Assert.True(opts.RequireApprovalFlag);
        Assert.Equal(TimeSpan.FromMinutes(2), opts.InterceptorTimeout);
    }

    [Fact]
    public void SkipInterceptor_DoesNotAffectRequireApproval()
    {
        var opts = new DurableChatToolOptions()
            .SkipInterceptor()
            .RequireApproval();

        Assert.True(opts.SkipInterceptorFlag);
        Assert.True(opts.RequireApprovalFlag);
    }

    [Fact]
    public void ApprovalTimeout_DefaultIsNull()
    {
        var opts = new DurableChatToolOptions();
        Assert.Null(opts.ApprovalTimeout);
    }

    [Fact]
    public void WithApprovalTimeout_SetsTimeoutAndReturnsSameInstance()
    {
        var opts = new DurableChatToolOptions();
        Assert.Same(opts, opts.WithApprovalTimeout(TimeSpan.FromMinutes(5)));
        Assert.Equal(TimeSpan.FromMinutes(5), opts.ApprovalTimeout);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void WithApprovalTimeout_NonPositive_Throws(int seconds)
    {
        var opts = new DurableChatToolOptions();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            opts.WithApprovalTimeout(TimeSpan.FromSeconds(seconds)));
    }
}
