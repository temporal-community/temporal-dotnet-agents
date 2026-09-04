using FakeItEasy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TemporalCommunity.Extensions.AI.Exceptions;
using TemporalCommunity.Extensions.AI.Internal;
using Temporalio.Client;
using Temporalio.Converters;
using Temporalio.Extensions.Hosting;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests.Internal;

public sealed class DurableAIDataConverterValidatorTests
{
    [Fact]
    public void PostConfigure_AcceptsDurableAIDataConverter()
    {
        var validator = CreateValidator(DurableAIDataConverter.Instance);

        Assert.Null(Record.Exception(() => validator.PostConfigure(null, new TemporalWorkerServiceOptions())));
    }

    [Fact]
    public void PostConfigure_RejectsDefaultConverterThatLosesDurableAIData()
    {
        var validator = CreateValidator(DataConverter.Default);

        var exception = Assert.Throws<DurableConfigurationException>(
            () => validator.PostConfigure(null, new TemporalWorkerServiceOptions()));

        Assert.Contains("DurableAIDataConverter", exception.Message);
    }

    [Fact]
    public void PostConfigure_WithoutDiClient_IsNoOpForWorkerOwnedClientPath()
    {
        var validator = new DurableAIDataConverterValidator(new ServiceCollection().BuildServiceProvider());

        Assert.Null(Record.Exception(() => validator.PostConfigure(null, new TemporalWorkerServiceOptions())));
    }

    [Fact]
    public void PostConfigure_AcceptsDurablePayloadConverterWithCodec()
    {
        var converter = DurableAIDataConverter.CreateDataConverter(
            new DurableAIGzipPayloadCodec(new DurableAIGzipPayloadCodecOptions()));
        var validator = CreateValidator(converter);

        Assert.Null(Record.Exception(() => validator.PostConfigure(null, new TemporalWorkerServiceOptions())));
    }

    [Fact]
    public void Register_WiresConverterValidatorAsPostConfigureOption()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(CreateClient(DurableAIDataConverter.Instance));
        DurableAIRegistrar.Register(
            services,
            builder: null,
            options: new DurableExecutionOptions { TaskQueue = "test" });

        var provider = services.BuildServiceProvider();

        Assert.Contains(
            provider.GetServices<IPostConfigureOptions<TemporalWorkerServiceOptions>>(),
            registration => registration is DurableAIDataConverterValidator);
    }

    private static DurableAIDataConverterValidator CreateValidator(DataConverter converter)
    {
        var services = new ServiceCollection();
        services.AddSingleton(CreateClient(converter));
        return new DurableAIDataConverterValidator(services.BuildServiceProvider());
    }

    private static ITemporalClient CreateClient(DataConverter converter)
    {
        var client = A.Fake<ITemporalClient>();
        A.CallTo(() => client.Options).Returns(new TemporalClientOptions { DataConverter = converter });
        return client;
    }
}
