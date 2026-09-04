using FakeItEasy;
using Microsoft.Extensions.DependencyInjection;
using TemporalCommunity.Extensions.Agents.Internal;
using TemporalCommunity.Extensions.AI;
using TemporalCommunity.Extensions.Agents.State;
using TemporalCommunity.Extensions.AI.Exceptions;
using TemporalCommunity.Extensions.AI.Session;
using Temporalio.Client;
using Temporalio.Converters;
using Temporalio.Extensions.Hosting;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.Tests.Internal;

public class TemporalAgentDataConverterValidatorTests
{
    [Fact]
    public void PostConfigure_AcceptsTemporalAgentDataConverter()
    {
        var validator = CreateValidator(TemporalAgentDataConverter.Instance);

        Assert.Null(Record.Exception(() => validator.PostConfigure(null, new TemporalWorkerServiceOptions())));
    }

    [Fact]
    public void PostConfigure_RejectsIncompatibleConverter()
    {
        var validator = CreateValidator(DataConverter.Default);

        var exception = Assert.Throws<DurableConfigurationException>(
            () => validator.PostConfigure(null, new TemporalWorkerServiceOptions()));

        Assert.Contains("TemporalAgentDataConverter", exception.Message);
    }

    [Fact]
    public void PostConfigure_RejectsNonDefaultConverterThatStillLosesAgentEntries()
    {
        var incompatibleConverter = new DataConverter(
            DataConverter.Default.PayloadConverter,
            new DefaultFailureConverter());
        var validator = CreateValidator(incompatibleConverter);

        Assert.Throws<DurableConfigurationException>(
            () => validator.PostConfigure(null, new TemporalWorkerServiceOptions()));
    }

    [Fact]
    public void PostConfigure_RejectsDurableAIConverterWithoutAgentRegistration()
    {
        var validator = CreateValidator(DurableAIDataConverter.Instance);

        Assert.Throws<DurableConfigurationException>(
            () => validator.PostConfigure(null, new TemporalWorkerServiceOptions()));
    }

    [Fact]
    public void PostConfigure_AcceptsAgentPayloadConverterWithCodec()
    {
        var converter = new DataConverter(
            TemporalAgentDataConverter.Instance.PayloadConverter,
            new DefaultFailureConverter(),
            new DurableAIGzipPayloadCodec(new DurableAIGzipPayloadCodecOptions()));
        var validator = CreateValidator(converter);

        Assert.Null(Record.Exception(() => validator.PostConfigure(null, new TemporalWorkerServiceOptions())));
    }

    [Fact]
    public async Task CreateDataConverter_WithCodec_RoundTripsEncodedAgentSessionEntry()
    {
        var codec = new DurableAIGzipPayloadCodec(new DurableAIGzipPayloadCodecOptions
        {
            MinimumPayloadSizeBytes = 1,
        });
        var converter = TemporalAgentDataConverter.CreateDataConverter(codec);
        DurableSessionEntry entry = new AgentSessionRequest
        {
            CorrelationId = "codec-agent-entry",
            CreatedAt = DateTimeOffset.UnixEpoch,
            Messages = [],
            OrchestrationId = "codec-agent",
        };

        var payload = converter.PayloadConverter.ToPayload(entry);
        var encoded = Assert.Single(await codec.EncodeAsync([payload]));
        var decoded = Assert.Single(await codec.DecodeAsync([encoded]));
        var restored = converter.PayloadConverter.ToValue(decoded, typeof(DurableSessionEntry));

        var request = Assert.IsType<AgentSessionRequest>(restored);
        Assert.Equal("codec-agent", request.OrchestrationId);
        Assert.Same(codec, converter.PayloadCodec);
    }

    private static TemporalAgentDataConverterValidator CreateValidator(DataConverter converter)
    {
        var client = A.Fake<ITemporalClient>();
        A.CallTo(() => client.Options).Returns(new TemporalClientOptions { DataConverter = converter });

        var services = new ServiceCollection();
        services.AddSingleton(client);
        return new TemporalAgentDataConverterValidator(services.BuildServiceProvider());
    }
}
