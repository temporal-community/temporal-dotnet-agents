using TemporalCommunity.Extensions.AI.Approvals;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests;

/// <summary>
/// The auto-denial reason text is shown to operators and fed back to the model, so the window it
/// names has to match the window that actually elapsed.
/// </summary>
/// <remarks>
/// A fixed "{TotalHours:F0} hours" rendering reported every sub-hour window as "0 hours". These
/// pin each unit boundary and the singular/plural transition, rather than relying on the single
/// six-second case an integration test happens to exercise.
/// </remarks>
public class ApprovalTimeoutDurationTests
{
    [Theory]
    // Seconds, including the singular and the zero case.
    [InlineData(0, "0 seconds")]
    [InlineData(1, "1 second")]
    [InlineData(6, "6 seconds")]
    [InlineData(59, "59 seconds")]
    // Crossing into minutes.
    [InlineData(60, "1 minute")]
    [InlineData(90, "1.5 minutes")]
    [InlineData(15 * 60, "15 minutes")]
    [InlineData(59 * 60, "59 minutes")]
    // Crossing into hours.
    [InlineData(60 * 60, "1 hour")]
    [InlineData(23 * 60 * 60, "23 hours")]
    // Crossing into days.
    [InlineData(24 * 60 * 60, "1 day")]
    [InlineData(36 * 60 * 60, "1.5 days")]
    [InlineData(7 * 24 * 60 * 60, "7 days")]
    public void DescribeDuration_RendersAtTheScaleOfTheWindow(int totalSeconds, string expected) =>
        Assert.Equal(expected, DurableApprovalMixin.DescribeDuration(TimeSpan.FromSeconds(totalSeconds)));

    /// <summary>The shipped default approval window must not render as "0 hours" or similar.</summary>
    [Fact]
    public void DescribeDuration_ShippedDefault_ReadsAsSevenDays() =>
        Assert.Equal("7 days", DurableApprovalMixin.DescribeDuration(TimeSpan.FromDays(7)));
}
