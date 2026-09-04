using TemporalCommunity.Extensions.Agents.Workflows;
using Xunit;

using System.Text;
using Microsoft.Agents.AI;

namespace TemporalCommunity.Extensions.Agents.Tests;

/// <summary>
/// Tests that the <c>StateBagSizeWarnThresholdBytes</c> constant shipped in
/// <see cref="AgentWorkflow"/> is correctly defined and matches the specified default.
/// </summary>
public class StateBagSizeGuardTests
{
    [Fact]
    public void StateBagSizeWarnThreshold_Is64KB()
    {
        Assert.Equal(64 * 1024, AgentWorkflow.StateBagSizeWarnThresholdBytes);
    }

    [Fact]
    public void GetDurableSerializedUtf8ByteCount_EmptyStateBag_IsZero()
    {
        var stateBag = new AgentSessionStateBag();

        Assert.Equal(0, stateBag.GetDurableSerializedUtf8ByteCount());
    }

    [Fact]
    public void GetDurableSerializedUtf8ByteCount_NullStateBag_ThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => AgentSessionStateBagExtensions.GetDurableSerializedUtf8ByteCount(null!));

        Assert.Equal("stateBag", exception.ParamName);
    }

    [Fact]
    public void GetDurableSerializedUtf8ByteCount_MatchesSerializedUtf8Payload()
    {
        var stateBag = new AgentSessionStateBag();
        stateBag.SetValue("weather", "Seattle ☔", System.Text.Json.JsonSerializerOptions.Default);

        var expected = Encoding.UTF8.GetByteCount(stateBag.Serialize().GetRawText());

        Assert.Equal(expected, stateBag.GetDurableSerializedUtf8ByteCount());
    }
}
