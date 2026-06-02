using Xunit;

namespace Temporalio.Extensions.Agents.Tests;

/// <summary>
/// Tests for the Feature L additions to <see cref="DurableToolOptions"/>:
/// <see cref="DurableToolOptions.SkipInterceptor"/>, <see cref="DurableToolOptions.WithInterceptorTimeout"/>,
/// and <see cref="DurableToolOptions.RequireApproval"/>.
/// </summary>
public class DurableToolOptionsInterceptorTests
{
    [Fact]
    public void SkipInterceptorFlag_DefaultIsFalse()
    {
        var opts = new DurableToolOptions();
        Assert.False(opts.SkipInterceptorFlag);
    }

    [Fact]
    public void SkipInterceptor_SetsFlag()
    {
        var opts = new DurableToolOptions();
        opts.SkipInterceptor();
        Assert.True(opts.SkipInterceptorFlag);
    }

    [Fact]
    public void SkipInterceptor_ReturnsSameInstance()
    {
        var opts = new DurableToolOptions();
        Assert.Same(opts, opts.SkipInterceptor());
    }

    [Fact]
    public void RequireApprovalFlag_DefaultIsFalse()
    {
        var opts = new DurableToolOptions();
        Assert.False(opts.RequireApprovalFlag);
    }

    [Fact]
    public void RequireApproval_SetsFlag()
    {
        var opts = new DurableToolOptions();
        opts.RequireApproval();
        Assert.True(opts.RequireApprovalFlag);
    }

    [Fact]
    public void RequireApproval_ReturnsSameInstance()
    {
        var opts = new DurableToolOptions();
        Assert.Same(opts, opts.RequireApproval());
    }

    [Fact]
    public void InterceptorTimeout_DefaultIsNull()
    {
        var opts = new DurableToolOptions();
        Assert.Null(opts.InterceptorTimeout);
    }

    [Fact]
    public void WithInterceptorTimeout_SetsTimeout()
    {
        var opts = new DurableToolOptions();
        opts.WithInterceptorTimeout(TimeSpan.FromSeconds(30));
        Assert.Equal(TimeSpan.FromSeconds(30), opts.InterceptorTimeout);
    }

    [Fact]
    public void WithInterceptorTimeout_ReturnsSameInstance()
    {
        var opts = new DurableToolOptions();
        Assert.Same(opts, opts.WithInterceptorTimeout(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void WithInterceptorTimeout_Zero_Throws()
    {
        var opts = new DurableToolOptions();
        Assert.Throws<ArgumentOutOfRangeException>(() => opts.WithInterceptorTimeout(TimeSpan.Zero));
    }

    [Fact]
    public void WithInterceptorTimeout_Negative_Throws()
    {
        var opts = new DurableToolOptions();
        Assert.Throws<ArgumentOutOfRangeException>(() => opts.WithInterceptorTimeout(TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void AllInterceptorOptions_ChainFluently()
    {
        var opts = new DurableToolOptions()
            .RequireApproval()
            .WithInterceptorTimeout(TimeSpan.FromMinutes(2));

        Assert.True(opts.RequireApprovalFlag);
        Assert.Equal(TimeSpan.FromMinutes(2), opts.InterceptorTimeout);
    }
}
