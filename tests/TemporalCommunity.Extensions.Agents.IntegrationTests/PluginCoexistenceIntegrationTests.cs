using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TemporalCommunity.Extensions.AI;
using TemporalCommunity.Extensions.Agents.IntegrationTests.Helpers;
using Temporalio.Client;
using Temporalio.Converters;
using Temporalio.Extensions.Hosting;
using Temporalio.Testing;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.IntegrationTests;

/// <summary>
/// Shared embedded Temporal service for the MAF plugin-coexistence tests. Started through
/// <see cref="TestEnvironmentHelper"/> so the agent search attributes are pre-registered.
/// </summary>
public sealed class AgentsPluginCoexistenceFixture : IAsyncLifetime
{
    public WorkflowEnvironment Environment { get; private set; } = null!;

    public string TargetHost =>
        Environment.Client.Connection.Options.TargetHost
        ?? throw new InvalidOperationException("Test server target host is unavailable.");

    public string Namespace => Environment.Client.Options.Namespace;

    public async Task InitializeAsync() =>
        Environment = await TestEnvironmentHelper.StartLocalAsync();

    public async Task DisposeAsync() => await Environment.ShutdownAsync();
}

/// <summary>
/// Proves that <c>AddTemporalAgents</c> — alone and alongside <c>AddDurableAI</c> — composes with
/// plugins the consumer registers through Temporal's own surface
/// (<see cref="Temporalio.Worker.TemporalWorkerOptions.Plugins"/> and
/// <see cref="TemporalClientConnectOptions.Plugins"/>), rather than through any package-owned
/// plugin-registration API.
/// <para>
/// Assertions target callback <em>execution</em>, or an effect only a callback could produce.
/// The internal converter plugins are observed indirectly: a probe plugin ordered behind them
/// reports the converter they installed.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class PluginCoexistenceIntegrationTests : IClassFixture<AgentsPluginCoexistenceFixture>
{
    private const string AgentName = "PluginCoexistenceAgent";

    private static readonly TimeSpan CallbackTimeout = TimeSpan.FromSeconds(30);

    private readonly AgentsPluginCoexistenceFixture fixture;

    public PluginCoexistenceIntegrationTests(AgentsPluginCoexistenceFixture fixture) =>
        this.fixture = fixture;

    // ---------------------------------------------------------------------------------------
    // Case 1: a worker plugin registered BEFORE AddTemporalAgents survives and its callbacks run.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task WorkerPluginRegisteredBeforeAddTemporalAgents_ExecutesOnRealWorkerStart()
    {
        var log = new PluginCallLog();
        var consumerPlugin = new RecordingWorkerPlugin("before", log);
        var taskQueue = $"maf-plugin-before-{Guid.NewGuid():N}";

        var builder = CreateHostBuilder();
        var worker = AddWorker(builder, taskQueue);

        // Consumer-native worker plugin registration, BEFORE canonical registration.
        worker.ConfigureOptions(options => options.Plugins = [consumerPlugin]);
        worker.AddTemporalAgents(ConfigureAgents);

        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            await AssertCallbackExecutedAsync(
                consumerPlugin.RunWorkerEntered, "Consumer worker plugin RunWorkerAsync", log);

            Assert.Equal(1, consumerPlugin.ConfigureWorkerCalls);
            Assert.Equal(1, consumerPlugin.RunWorkerCalls);

            // Canonical registration also survived the coexistence.
            Assert.NotNull(host.Services.GetRequiredService<ITemporalAgentClient>());
        }
        finally
        {
            await host.StopAsync();
        }
    }

    // ---------------------------------------------------------------------------------------
    // Case 2: a worker plugin registered AFTER AddTemporalAgents survives and its callbacks run.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task WorkerPluginRegisteredAfterAddTemporalAgents_ExecutesOnRealWorkerStart()
    {
        var log = new PluginCallLog();
        var consumerPlugin = new RecordingWorkerPlugin("after", log);
        var taskQueue = $"maf-plugin-after-{Guid.NewGuid():N}";

        var builder = CreateHostBuilder();
        var worker = AddWorker(builder, taskQueue);

        worker.AddTemporalAgents(ConfigureAgents);

        // Consumer-native worker plugin registration, AFTER canonical registration.
        worker.ConfigureOptions(options => options.Plugins = [consumerPlugin]);

        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            await AssertCallbackExecutedAsync(
                consumerPlugin.RunWorkerEntered, "Consumer worker plugin RunWorkerAsync", log);

            Assert.Equal(1, consumerPlugin.ConfigureWorkerCalls);
            Assert.Equal(1, consumerPlugin.RunWorkerCalls);
            Assert.NotNull(host.Services.GetRequiredService<ITemporalAgentClient>());
        }
        finally
        {
            await host.StopAsync();
        }
    }

    // ---------------------------------------------------------------------------------------
    // Case 4: consumer client plugins and the internal MAF converter plugin all execute, in a
    // deterministic order.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task ConsumerClientPlugins_AndInternalAgentConverterPlugin_ExecuteInDeterministicOrder()
    {
        var log = new PluginCallLog();
        var firstPlugin = new RecordingClientPlugin("first", log);
        var lastPlugin = new RecordingClientPlugin("last", log);
        var taskQueue = $"maf-client-plugin-order-{Guid.NewGuid():N}";

        var builder = CreateHostBuilder();
        var worker = AddWorker(builder, taskQueue);

        // IConfigureOptions runs before every IPostConfigureOptions, so this lands first.
        worker.ConfigureOptions(options => options.ClientOptions!.Plugins = [firstPlugin]);
        worker.AddTemporalAgents(ConfigureAgents);

        // Registered after AddTemporalAgents, so this post-configure appends behind the
        // internal converter plugin.
        AppendClientPluginAfterCanonicalRegistration(builder.Services, lastPlugin);

        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            await AssertCallbackExecutedAsync(
                lastPlugin.ConfigureClientCalled, "Trailing consumer client plugin ConfigureClient", log);

            Assert.Equal(1, firstPlugin.ConnectCalls);
            Assert.Equal(1, firstPlugin.ConfigureClientCalls);
            Assert.Equal(1, lastPlugin.ConnectCalls);
            Assert.Equal(1, lastPlugin.ConfigureClientCalls);

            // The internal converter plugin ran between the two probes.
            Assert.Same(DataConverter.Default, firstPlugin.ConverterSeenByConfigureClient);
            Assert.Same(TemporalAgentDataConverter.Instance, lastPlugin.ConverterSeenByConfigureClient);

            AssertOrdered(log, "first:Connect:enter", "last:Connect:enter");
            AssertOrdered(log, "first:ConfigureClient", "last:ConfigureClient");
        }
        finally
        {
            await host.StopAsync();
        }
    }

    // ---------------------------------------------------------------------------------------
    // Case 8: registering BOTH libraries on one worker selects the MAF converter (the intended
    // superset) in either registration order, and drops no consumer plugin.
    // ---------------------------------------------------------------------------------------
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BothLibraries_SelectMafConverter_WithoutDroppingConsumerPlugins(bool agentsFirst)
    {
        var log = new PluginCallLog();
        var firstPlugin = new RecordingClientPlugin("first", log);
        var lastPlugin = new RecordingClientPlugin("last", log);
        var workerPlugin = new RecordingWorkerPlugin("worker", log);
        var taskQueue = $"maf-meai-combined-{(agentsFirst ? "af" : "mf")}-{Guid.NewGuid():N}";

        var builder = CreateHostBuilder();
        var worker = AddWorker(builder, taskQueue);

        worker.ConfigureOptions(options =>
        {
            options.Plugins = [workerPlugin];
            options.ClientOptions!.Plugins = [firstPlugin];
        });

        if (agentsFirst)
        {
            worker.AddTemporalAgents(ConfigureAgents);
            worker.AddDurableAI(options => options.RegisterDefaultWorkflow = false);
        }
        else
        {
            worker.AddDurableAI(options => options.RegisterDefaultWorkflow = false);
            worker.AddTemporalAgents(ConfigureAgents);
        }

        AppendClientPluginAfterCanonicalRegistration(builder.Services, lastPlugin);

        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            await AssertCallbackExecutedAsync(
                lastPlugin.ConfigureClientCalled, "Trailing consumer client plugin ConfigureClient", log);
            await AssertCallbackExecutedAsync(
                workerPlugin.RunWorkerEntered, "Consumer worker plugin RunWorkerAsync", log);

            // The MAF converter is the superset and must win regardless of which library's
            // converter plugin ran first.
            Assert.Same(TemporalAgentDataConverter.Instance, lastPlugin.ConverterSeenByConfigureClient);
            Assert.NotSame(DurableAIDataConverter.Instance, lastPlugin.ConverterSeenByConfigureClient);

            // Neither library dropped the consumer's plugins.
            Assert.Equal(1, firstPlugin.ConnectCalls);
            Assert.Equal(1, firstPlugin.ConfigureClientCalls);
            Assert.Equal(1, lastPlugin.ConnectCalls);
            Assert.Equal(1, workerPlugin.ConfigureWorkerCalls);
            Assert.Equal(1, workerPlugin.RunWorkerCalls);

            // Both libraries are actually registered on this worker.
            Assert.NotNull(host.Services.GetRequiredService<ITemporalAgentClient>());
            Assert.NotNull(host.Services.GetRequiredService<DurableExecutionOptions>());
        }
        finally
        {
            await host.StopAsync();
        }
    }

    // ---------------------------------------------------------------------------------------
    // Case 5: canonical registration appends; it never replaces an existing plugin collection.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public void AddTemporalAgents_NeverReplacesExistingClientPluginCollection()
    {
        var log = new PluginCallLog();
        var consumerPlugin = new RecordingClientPlugin("consumer", log);
        var taskQueue = $"maf-no-replace-{Guid.NewGuid():N}";

        var services = CreateServices();
        services
            .AddHostedTemporalWorker(fixture.TargetHost, fixture.Namespace, taskQueue)
            .ConfigureOptions(options => options.ClientOptions!.Plugins = [consumerPlugin])
            .AddTemporalAgents(ConfigureAgents);

        using var provider = services.BuildServiceProvider();
        var options = ResolveWorkerOptions(provider, taskQueue);

        var pluginNames = options.ClientOptions!.Plugins!.Select(plugin => plugin.Name).ToList();
        Assert.Contains("consumer", pluginNames);
        Assert.Contains(TemporalAgentDataConverterPlugin.PluginName, pluginNames);
        Assert.Equal(2, pluginNames.Count);
    }

    // ---------------------------------------------------------------------------------------
    // Case 6: the internal MAF converter plugin is added at most once when worker options are
    // rebuilt against a consumer-owned (shared) client-options instance.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public void InternalAgentConverterPlugin_AddedOnce_WhenWorkerOptionsAreRebuilt()
    {
        var taskQueue = $"maf-dedupe-rebuild-{Guid.NewGuid():N}";

        // A consumer-owned instance assigned into the options — unlike the 3-arg
        // AddHostedTemporalWorker overload, this same object is reused on every options build.
        var sharedClientOptions = new TemporalClientConnectOptions(fixture.TargetHost)
        {
            Namespace = fixture.Namespace,
        };

        var services = CreateServices();
        services
            .AddHostedTemporalWorker(taskQueue)
            .ConfigureOptions(options => options.ClientOptions = sharedClientOptions)
            .AddTemporalAgents(ConfigureAgents);

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IOptionsFactory<TemporalWorkerServiceOptions>>();

        // Two builds of the same named options — what IOptionsMonitor does on a config reload.
        var first = factory.Create(taskQueue);
        var second = factory.Create(taskQueue);
        Assert.Equal(taskQueue, first.TaskQueue);
        Assert.Equal(taskQueue, second.TaskQueue);

        Assert.Equal(
            1,
            sharedClientOptions.Plugins!.Count(
                plugin => plugin.Name == TemporalAgentDataConverterPlugin.PluginName));
    }

    // ---------------------------------------------------------------------------------------
    // Case 7: a consumer-set data converter is not overwritten by the internal MAF plugin.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task ConsumerDataConverter_IsNotOverwrittenByAgentConverterPlugin()
    {
        var log = new PluginCallLog();
        var probe = new RecordingClientPlugin("probe", log);
        var customConverter = DataConverter.Default with { PayloadCodec = new PassthroughPayloadCodec() };
        var taskQueue = $"maf-custom-converter-{Guid.NewGuid():N}";

        Assert.NotEqual(DataConverter.Default, customConverter);

        var builder = CreateHostBuilder();
        var worker = AddWorker(builder, taskQueue);

        worker.ConfigureOptions(options => options.ClientOptions!.DataConverter = customConverter);
        worker.AddTemporalAgents(ConfigureAgents);
        AppendClientPluginAfterCanonicalRegistration(builder.Services, probe);

        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            await AssertCallbackExecutedAsync(
                probe.ConfigureClientCalled, "Probe client plugin ConfigureClient", log);

            Assert.Same(customConverter, probe.ConverterSeenByConfigureClient);
            Assert.NotSame(TemporalAgentDataConverter.Instance, probe.ConverterSeenByConfigureClient);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    // ---------------------------------------------------------------------------------------
    // Case 9: consumer plugin multiplicity is the consumer's business — two distinct instances
    // sharing a Name must both survive and both execute.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task ConsumerPluginsSharingAName_AreNotDeduplicated()
    {
        const string SharedName = "consumer.duplicate";
        var log = new PluginCallLog();
        var workerPluginOne = new RecordingWorkerPlugin("worker-one", SharedName, log);
        var workerPluginTwo = new RecordingWorkerPlugin("worker-two", SharedName, log);
        var clientPluginOne = new RecordingClientPlugin("client-one", SharedName, log);
        var clientPluginTwo = new RecordingClientPlugin("client-two", SharedName, log);
        var taskQueue = $"maf-plugin-multiplicity-{Guid.NewGuid():N}";

        var builder = CreateHostBuilder();
        var worker = AddWorker(builder, taskQueue);

        worker.ConfigureOptions(options =>
        {
            options.Plugins = [workerPluginOne, workerPluginTwo];
            options.ClientOptions!.Plugins = [clientPluginOne, clientPluginTwo];
        });
        worker.AddTemporalAgents(ConfigureAgents);

        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            await AssertCallbackExecutedAsync(
                workerPluginOne.RunWorkerEntered, "First same-named worker plugin RunWorkerAsync", log);
            await AssertCallbackExecutedAsync(
                workerPluginTwo.RunWorkerEntered, "Second same-named worker plugin RunWorkerAsync", log);

            Assert.Equal(1, workerPluginOne.RunWorkerCalls);
            Assert.Equal(1, workerPluginTwo.RunWorkerCalls);
            Assert.Equal(1, clientPluginOne.ConfigureClientCalls);
            Assert.Equal(1, clientPluginTwo.ConfigureClientCalls);
            Assert.Equal(1, clientPluginOne.ConnectCalls);
            Assert.Equal(1, clientPluginTwo.ConnectCalls);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private static void ConfigureAgents(TemporalAgentsOptions options) =>
        options.AddDurableAgent(AgentName, agent =>
        {
            agent.Instructions = "You are a helpful agent.";
            agent.ChatClient = _ => new EchoChatClient();
        });


    // ---------------------------------------------------------------------------------------
    // The DOCUMENTED canonical topology: AddTemporalClient(...) owns the client, the worker
    // takes only a task queue. Every other case here uses the three-argument worker, where the
    // worker owns its client — a different code path that these assertions do not reach.
    // ---------------------------------------------------------------------------------------
    [Theory]
    [InlineData(true)]   // consumer plugin registered BEFORE canonical registration
    [InlineData(false)]  // ... and after
    public async Task ClientPluginOnAddTemporalClient_ExecutesAndSurvivesAddTemporalAgents(bool pluginFirst)
    {
        var log = new PluginCallLog();
        var consumerPlugin = new RecordingClientPlugin($"maf-di-client-{pluginFirst}", log);
        var taskQueue = $"maf-di-client-{Guid.NewGuid():N}";

        // Deliberately NOT CreateHostBuilder(): that pre-registers ITemporalClient from the
        // fixture, so AddTemporalClient's TryAddSingleton would be a no-op and the plugin would
        // never see a client it owns. This topology only exists when AddTemporalClient builds it.
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<IChatClient>(new EchoChatClient());

        // AddTemporalAgents requires ITemporalClient to already be in DI and says so, so the
        // client registration itself cannot move. What varies is whether the consumer appends
        // its plugin to those options before or after canonical registration.
        var clientOptions = builder.Services.AddTemporalClient(fixture.TargetHost, fixture.Namespace);

        void AddConsumerPlugin() =>
            clientOptions.Configure(options =>
            {
                var plugins = options.Plugins?.ToList() ?? [];
                plugins.Add(consumerPlugin);
                options.Plugins = plugins;
            });

        if (pluginFirst)
        {
            AddConsumerPlugin();
        }

        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddTemporalAgents(ConfigureAgents);

        if (!pluginFirst)
        {
            AddConsumerPlugin();
        }

        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            await AssertCallbackExecutedAsync(
                consumerPlugin.ConfigureClientCalled, "Consumer client plugin ConfigureClient", log);

            Assert.Equal(1, consumerPlugin.ConfigureClientCalls);

            // The MAF converter must still be in force. On this topology the library applies it
            // through IConfigureOptions<TemporalClientConnectOptions> rather than as a plugin, so
            // a regression would not show up in the plugin list at all.
            Assert.NotNull(consumerPlugin.ConverterSeenByConfigureClient);
            Assert.Same(
                TemporalAgentDataConverter.Instance.PayloadConverter,
                consumerPlugin.ConverterSeenByConfigureClient!.PayloadConverter);

            // ConnectAsync does NOT fire here, even after a real RPC: AddTemporalClient builds
            // the client with TemporalClient.CreateLazy, which bypasses the plugin connect chain.
            // Confirmed upstream SDK behaviour — the same probe against a bare AddTemporalClient
            // with no library registration reports ConfigureClient=1, ConnectAsync=0. Pinned so
            // that if the SDK starts invoking it, this fails and the documented caveat is revisited.
            await host.Services.GetRequiredService<ITemporalClient>()
                .Connection.WorkflowService.GetSystemInfoAsync(
                    new Temporalio.Api.WorkflowService.V1.GetSystemInfoRequest());

            Assert.Equal(0, consumerPlugin.ConnectCalls);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private HostApplicationBuilder CreateHostBuilder()
    {
        var builder = Host.CreateApplicationBuilder();
        RegisterSharedServices(builder.Services);
        return builder;
    }

    private IServiceCollection CreateServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        RegisterSharedServices(services);
        return services;
    }

    /// <summary>
    /// AddTemporalAgents requires an <see cref="ITemporalClient"/> in DI. The worker itself still
    /// creates its own client from ClientOptions (the 3-arg AddHostedTemporalWorker overload),
    /// which is the path the converter plugin configures.
    /// </summary>
    private void RegisterSharedServices(IServiceCollection services)
    {
        services.AddSingleton<ITemporalClient>(fixture.Environment.Client);
        services.AddSingleton<IChatClient>(new EchoChatClient());
    }

    private ITemporalWorkerServiceOptionsBuilder AddWorker(
        HostApplicationBuilder builder, string taskQueue) =>
        builder.Services.AddHostedTemporalWorker(
            fixture.TargetHost, fixture.Namespace, taskQueue);

    private static void AppendClientPluginAfterCanonicalRegistration(
        IServiceCollection services, ITemporalClientPlugin plugin) =>
        services.PostConfigureAll<TemporalWorkerServiceOptions>(options =>
        {
            if (options.ClientOptions is null)
            {
                return;
            }

            var plugins = options.ClientOptions.Plugins?.ToList() ?? [];
            plugins.Add(plugin);
            options.ClientOptions.Plugins = plugins;
        });

    private static TemporalWorkerServiceOptions ResolveWorkerOptions(
        IServiceProvider provider, string taskQueue)
    {
        var options = provider
            .GetRequiredService<IOptionsMonitor<TemporalWorkerServiceOptions>>()
            .Get(taskQueue);
        Assert.Equal(taskQueue, options.TaskQueue);
        return options;
    }

    /// <summary>
    /// Awaits a plugin-callback signal and fails with the ordered call log if it never fires.
    /// A bare <see cref="TimeoutException"/> here would not say which callback was missing.
    /// </summary>
    private static async Task AssertCallbackExecutedAsync(
        Task callbackSignal, string description, PluginCallLog log)
    {
        try
        {
            await callbackSignal.WaitAsync(CallbackTimeout);
        }
        catch (TimeoutException)
        {
            Assert.Fail(
                $"{description} did not execute within {CallbackTimeout.TotalSeconds:0}s. " +
                $"Plugin call log: [{log}]");
        }
    }

    private static void AssertOrdered(PluginCallLog log, string earlier, string later)
    {
        var earlierIndex = log.IndexOf(earlier);
        var laterIndex = log.IndexOf(later);
        Assert.True(
            earlierIndex >= 0,
            $"Expected '{earlier}' in the plugin call log. Log: {log}");
        Assert.True(
            laterIndex >= 0,
            $"Expected '{later}' in the plugin call log. Log: {log}");
        Assert.True(
            earlierIndex < laterIndex,
            $"Expected '{earlier}' before '{later}'. Log: {log}");
    }
}
