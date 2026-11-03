using TemporalCommunity.Extensions.AI.Exceptions;
using TemporalCommunity.Extensions.AI.Internal;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests.Internal;

public class DurableTransportPayloadGuardTests
{
    [Fact]
    public void EnsureWithinLimit_UsesConfiguredPayloadConverterAndCodecAllowance()
    {
        var converter = DurableAIDataConverter.Instance.PayloadConverter;
        var value = new string('x', 128);
        var serializedSize = converter.ToPayload(value).CalculateSize();

        DurableTransportPayloadGuard.EnsureWithinLimit(
            converter,
            value,
            maximumPayloadSizeBytes: serializedSize + 16,
            payloadCodecExpansionAllowanceBytes: 16,
            boundary: "test payload");

        var exception = Assert.Throws<DurableTransportPayloadTooLargeException>(() =>
            DurableTransportPayloadGuard.EnsureWithinLimit(
                converter,
                value,
                maximumPayloadSizeBytes: serializedSize + 15,
                payloadCodecExpansionAllowanceBytes: 16,
                boundary: "test payload"));

        Assert.Equal("test payload", exception.Boundary);
        Assert.Equal(serializedSize, exception.SerializedSizeBytes);
        Assert.Equal(serializedSize + 15, exception.MaximumPayloadSizeBytes);
        Assert.Equal(16, exception.PayloadCodecExpansionAllowanceBytes);
    }

    [Fact]
    public void EnsureWithinLimit_RejectsInvalidBudgets()
    {
        var converter = DurableAIDataConverter.Instance.PayloadConverter;

        Assert.Throws<InvalidOperationException>(() =>
            DurableTransportPayloadGuard.EnsureWithinLimit(
                converter,
                "value",
                maximumPayloadSizeBytes: 0,
                payloadCodecExpansionAllowanceBytes: 0,
                boundary: "test payload"));
        Assert.Throws<InvalidOperationException>(() =>
            DurableTransportPayloadGuard.EnsureWithinLimit(
                converter,
                "value",
                maximumPayloadSizeBytes: 10,
                payloadCodecExpansionAllowanceBytes: 10,
                boundary: "test payload"));
    }
}
