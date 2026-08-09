using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests;

public class TemporalServiceVersionTests
{
    [Theory]
    [InlineData("1.31.0", 1, 31, 0)]
    [InlineData("1.31.2", 1, 31, 2)]
    [InlineData("1.31.2-custom", 1, 31, 2)]
    [InlineData("1.32.0", 1, 32, 0)]
    [InlineData("2.0.0", 2, 0, 0)]
    public void ParseAndValidateServerVersion_AcceptsSupportedVersions(
        string value,
        int major,
        int minor,
        int build)
    {
        var actual = TemporalServiceTestEnvironment.ParseAndValidateServerVersion(value);

        Assert.Equal(new Version(major, minor, build), actual);
    }

    [Theory]
    [InlineData("1.30.99")]
    [InlineData("0.31.0")]
    [InlineData("")]
    [InlineData("not-a-version")]
    [InlineData(null)]
    public void ParseAndValidateServerVersion_RejectsUnsupportedOrMalformedVersions(string? value)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            TemporalServiceTestEnvironment.ParseAndValidateServerVersion(value));

        Assert.Contains("Temporal Service 1.31.0 or newer is required", exception.Message);
        Assert.Contains(value ?? "(missing)", exception.Message);
    }
}
