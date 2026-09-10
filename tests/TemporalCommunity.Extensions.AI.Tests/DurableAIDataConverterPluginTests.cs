using FakeItEasy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Temporalio.Client;
using Temporalio.Converters;
using Temporalio.Extensions.Hosting;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests;

/// <summary>
/// Covers the internal, automatic <see cref="DurableAIDataConverterPlugin"/> and the
/// options configurators that <c>AddDurableAI</c> registers to install it.
/// </summary>
/// <remarks>
/// The package no longer exposes any plugin-registration wrappers. Consumers that need to
/// add their own plugins use Temporal's own surface —
/// <see cref="Temporalio.Worker.TemporalWorkerOptions.Plugins"/> and
/// <see cref="TemporalClientConnectOptions.Plugins"/> — so the only plugin behaviour this
/// package owns is the converter plugin exercised below.
/// </remarks>
public class DurableAIDataConverterPluginTests
{
    // ── Helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// Fires all registered configurators for <see cref="TemporalWorkerServiceOptions"/> on a
    /// fresh instance. Named configurators are fired with their own registered name (obtained via
    /// reflection) so the action runs regardless of the internal options-name format used by the
    /// Hosting library. Post-configurators run afterwards, matching the options pipeline order.
    /// </summary>
    private static TemporalWorkerServiceOptions BuildWorkerServiceOptions(IServiceProvider provider)
    {
        var opts = new TemporalWorkerServiceOptions();
        foreach (var svc in provider.GetServices<IConfigureOptions<TemporalWorkerServiceOptions>>())
        {
            if (svc is IConfigureNamedOptions<TemporalWorkerServiceOptions> named)
            {
                var name = svc.GetType().GetProperty("Name")?.GetValue(svc) as string;
                named.Configure(name, opts);
            }
            else
            {
                svc.Configure(opts);
            }
        }
        foreach (var svc in provider.GetServices<IPostConfigureOptions<TemporalWorkerServiceOptions>>())
        {
            svc.PostConfigure(string.Empty, opts);
        }
        return opts;
    }

    private static ITemporalClient CreateDurableClient()
    {
        var client = A.Fake<ITemporalClient>();
        A.CallTo(() => client.Options).Returns(new TemporalClientOptions
        {
            DataConverter = DurableAIDataConverter.Instance,
        });
        return client;
    }

    // ── DurableAIDataConverterPlugin.ConfigureClient ─────────────────────

    [Fact]
    public void DurableAIDataConverterPlugin_DefaultConverter_SetsInstance()
    {
        var plugin = new DurableAIDataConverterPlugin();
        var options = new TemporalClientOptions();

        Assert.Equal(DataConverter.Default, options.DataConverter);

        plugin.ConfigureClient(options);

        Assert.Same(DurableAIDataConverter.Instance, options.DataConverter);
    }

    [Fact]
    public void DurableAIDataConverterPlugin_CustomConverter_IsPreserved()
    {
        var plugin = new DurableAIDataConverterPlugin();
        var customConverter = new DataConverter(new DefaultPayloadConverter(), new DefaultFailureConverter());
        var options = new TemporalClientOptions { DataConverter = customConverter };

        plugin.ConfigureClient(options);

        Assert.Same(customConverter, options.DataConverter);
    }

    [Fact]
    public void DurableAIDataConverterPlugin_ConfigureClient_IsIdempotent_WhenCalledTwice()
    {
        // The plugin is installed automatically by AddDurableAI's configurators, and the
        // options pipeline can fire those more than once. A second application must be a
        // no-op — a future refactor cannot silently regress this.
        var plugin = new DurableAIDataConverterPlugin();
        var options = new TemporalClientOptions();

        Assert.Equal(DataConverter.Default, options.DataConverter);

        plugin.ConfigureClient(options);
        var afterFirst = options.DataConverter;
        Assert.Same(DurableAIDataConverter.Instance, afterFirst);

        plugin.ConfigureClient(options);

        // Second call must leave the converter unchanged — not replaced, not nulled,
        // not swapped for a new instance.
        Assert.Same(DurableAIDataConverter.Instance, options.DataConverter);
        Assert.Same(afterFirst, options.DataConverter);
    }

    [Fact]
    public void DurableAIDataConverterPlugin_ConfigureClient_DoesNotOverrideUserConverter()
    {
        // If the user has already set a non-default converter, ConfigureClient must
        // never override it — neither on the first call nor on a repeated call.
        var plugin = new DurableAIDataConverterPlugin();
        var userConverter = new DataConverter(new DefaultPayloadConverter(), new DefaultFailureConverter());
        var options = new TemporalClientOptions { DataConverter = userConverter };

        plugin.ConfigureClient(options);
        Assert.Same(userConverter, options.DataConverter);

        plugin.ConfigureClient(options);
        Assert.Same(userConverter, options.DataConverter);
    }

    // ── AddDurableAI configurator registration ────────────────────────────

    [Fact]
    public void AddDurableAI_RegistersClientOptionsConfigurator()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(A.Fake<ITemporalClient>());
        services.AddHostedTemporalWorker("my-queue").AddDurableAI();

        Assert.Contains(services, sd =>
            sd.ServiceType == typeof(IConfigureOptions<TemporalClientConnectOptions>) &&
            sd.ImplementationType == typeof(DurableAIClientOptionsConfigurator));
    }

    [Fact]
    public void AddDurableAI_RegistersWorkerClientConfigurator()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(A.Fake<ITemporalClient>());
        services.AddHostedTemporalWorker("my-queue").AddDurableAI();

        Assert.Contains(services, sd =>
            sd.ServiceType == typeof(IPostConfigureOptions<TemporalWorkerServiceOptions>) &&
            sd.ImplementationType == typeof(DurableAIWorkerClientConfigurator));
    }

    [Fact]
    public void AddDurableAI_CalledTwice_ConfiguratorRegisteredOnce()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(A.Fake<ITemporalClient>());
        var builder = services.AddHostedTemporalWorker("my-queue");
        builder.AddDurableAI();
        builder.AddDurableAI();

        var clientConfiguratorCount = services.Count(sd =>
            sd.ServiceType == typeof(IConfigureOptions<TemporalClientConnectOptions>) &&
            sd.ImplementationType == typeof(DurableAIClientOptionsConfigurator));

        var workerConfiguratorCount = services.Count(sd =>
            sd.ServiceType == typeof(IPostConfigureOptions<TemporalWorkerServiceOptions>) &&
            sd.ImplementationType == typeof(DurableAIWorkerClientConfigurator));

        Assert.Equal(1, clientConfiguratorCount);
        Assert.Equal(1, workerConfiguratorCount);
    }

    [Fact]
    public void AddDurableAI_WithoutITemporalClient_ThrowsInvalidOperationException()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // Intentionally omit ITemporalClient registration to verify the fail-fast guard.
        var builder = services.AddHostedTemporalWorker("my-queue");

        var ex = Assert.Throws<InvalidOperationException>(() => builder.AddDurableAI());

        Assert.Contains("No ITemporalClient registered in DI", ex.Message);
        Assert.Contains("AddDurableAI", ex.Message);
    }

    // ── DataConverter dedupe in PostConfigure ────────────────────────────

    [Fact]
    public void DurableAIWorkerClientConfigurator_DoesNotPushDuplicate()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(CreateDurableClient());
        services.AddHostedTemporalWorker("localhost:7233", "default", "my-queue")
            .AddDurableAI();

        var opts = BuildWorkerServiceOptions(services.BuildServiceProvider());

        // Fire the IPostConfigureOptions a second time on the same options to
        // simulate a double-application path. The dedupe must hold.
        var provider = services.BuildServiceProvider();
        foreach (var svc in provider.GetServices<IPostConfigureOptions<TemporalWorkerServiceOptions>>())
        {
            svc.PostConfigure(string.Empty, opts);
        }

        Assert.NotNull(opts.ClientOptions);
        var converterPluginCount = opts.ClientOptions!.Plugins?
            .Count(p => string.Equals(p.Name, DurableAIDataConverterPlugin.PluginName, StringComparison.Ordinal)) ?? 0;
        Assert.Equal(1, converterPluginCount);
    }
}
