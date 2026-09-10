using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TemporalCommunity.Extensions.AI.IntegrationTests.Helpers;
using Temporalio.Client;
using Temporalio.Converters;
using Temporalio.Extensions.Hosting;
using Temporalio.Testing;
using Xunit;

namespace TemporalCommunity.Extensions.AI.IntegrationTests;

/// <summary>
/// Shared embedded Temporal service for the plugin-coexistence tests. The tests build several
/// hosts against one server; starting a server per test would dominate the suite runtime.
/// </summary>
public sealed class AiPluginCoexistenceFixture : IAsyncLifetime
{
    public WorkflowEnvironment Environment { get; private set; } = null!;

    public string TargetHost =>
        Environment.Client.Connection.Options.TargetHost
        ?? throw new InvalidOperationException("Test server target host is unavailable.");

    public string Namespace => Environment.Client.Options.Namespace;

    public async Task InitializeAsync() =>
        Environment = await TemporalServiceTestEnvironment.StartLocalAsync();

    public async Task DisposeAsync() => await Environment.ShutdownAsync();
}

/// <summary>
/// Proves that <c>AddDurableAI</c> composes with plugins the consumer registers through
/// Temporal's own surface — <see cref="Temporalio.Worker.TemporalWorkerOptions.Plugins"/> and
/// <see cref="TemporalClientConnectOptions.Plugins"/> — rather than through any package-owned
/// plugin-registration API.
/// <para>
/// Every test here asserts on callback <em>execution</em> (or on an effect only a callback could
/// produce). Asserting that a plugin is present in an options collection would pass even if the
/// SDK never invoked it.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class PluginCoexistenceIntegrationTests : IClassFixture<AiPluginCoexistenceFixture>
{
    private static readonly TimeSpan CallbackTimeout = TimeSpan.FromSeconds(30);

    private readonly AiPluginCoexistenceFixture fixture;

    public PluginCoexistenceIntegrationTests(AiPluginCoexistenceFixture fixture) =>
        this.fixture = fixture;

    // ---------------------------------------------------------------------------------------
    // Case 3a: a worker plugin registered BEFORE AddDurableAI survives and its callbacks run.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task WorkerPluginRegisteredBeforeAddDurableAI_ExecutesOnRealWorkerStart()
    {
        var log = new PluginCallLog();
        var consumerPlugin = new RecordingWorkerPlugin("before", log);
        var taskQueue = $"ai-plugin-before-{Guid.NewGuid():N}";

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<IChatClient>(new TestChatClient());

        var worker = builder.Services.AddHostedTemporalWorker(
            fixture.TargetHost, fixture.Namespace, taskQueue);

        // Consumer-native worker plugin registration, BEFORE canonical registration.
        worker.ConfigureOptions(options => options.Plugins = [consumerPlugin]);
        worker.AddDurableAI(options => options.RegisterDefaultWorkflow = false);

        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            await AssertCallbackExecutedAsync(
                consumerPlugin.RunWorkerEntered, "Consumer worker plugin RunWorkerAsync", log);

            Assert.Equal(1, consumerPlugin.ConfigureWorkerCalls);
            Assert.Equal(1, consumerPlugin.RunWorkerCalls);

            // Canonical registration also survived the coexistence.
            Assert.NotNull(host.Services.GetRequiredService<DurableExecutionOptions>());
        }
        finally
        {
            await host.StopAsync();
        }
    }

    // ---------------------------------------------------------------------------------------
    // Case 3b: a worker plugin registered AFTER AddDurableAI survives and its callbacks run.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task WorkerPluginRegisteredAfterAddDurableAI_ExecutesOnRealWorkerStart()
    {
        var log = new PluginCallLog();
        var consumerPlugin = new RecordingWorkerPlugin("after", log);
        var taskQueue = $"ai-plugin-after-{Guid.NewGuid():N}";

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<IChatClient>(new TestChatClient());

        var worker = builder.Services.AddHostedTemporalWorker(
            fixture.TargetHost, fixture.Namespace, taskQueue);

        worker.AddDurableAI(options => options.RegisterDefaultWorkflow = false);

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
            Assert.NotNull(host.Services.GetRequiredService<DurableExecutionOptions>());
        }
        finally
        {
            await host.StopAsync();
        }
    }

    // ---------------------------------------------------------------------------------------
    // Case 4: consumer client plugins and the internal converter plugin all execute, in a
    // deterministic order. The converter plugin is internal, so its execution is observed
    // through the converter a later probe sees.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task ConsumerClientPlugins_AndInternalConverterPlugin_ExecuteInDeterministicOrder()
    {
        var log = new PluginCallLog();
        var firstPlugin = new RecordingClientPlugin("first", log);
        var lastPlugin = new RecordingClientPlugin("last", log);
        var taskQueue = $"ai-client-plugin-order-{Guid.NewGuid():N}";

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<IChatClient>(new TestChatClient());

        var worker = builder.Services.AddHostedTemporalWorker(
            fixture.TargetHost, fixture.Namespace, taskQueue);

        // IConfigureOptions runs before every IPostConfigureOptions, so this lands first.
        worker.ConfigureOptions(options => options.ClientOptions!.Plugins = [firstPlugin]);

        worker.AddDurableAI(options => options.RegisterDefaultWorkflow = false);

        // Registered after AddDurableAI, so this post-configure runs after the package's and
        // appends behind the internal converter plugin.
        builder.Services.PostConfigureAll<TemporalWorkerServiceOptions>(options =>
        {
            if (options.ClientOptions is null)
            {
                return;
            }

            var plugins = options.ClientOptions.Plugins?.ToList() ?? [];
            plugins.Add(lastPlugin);
            options.ClientOptions.Plugins = plugins;
        });

        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            await AssertCallbackExecutedAsync(
                lastPlugin.ConfigureClientCalled, "Trailing consumer client plugin ConfigureClient", log);

            // Both consumer plugins executed both callbacks.
            Assert.Equal(1, firstPlugin.ConnectCalls);
            Assert.Equal(1, firstPlugin.ConfigureClientCalls);
            Assert.Equal(1, lastPlugin.ConnectCalls);
            Assert.Equal(1, lastPlugin.ConfigureClientCalls);

            // The internal converter plugin ran between them: the earlier probe still saw the
            // SDK default, the later probe saw the converter the internal plugin installed.
            Assert.Same(DataConverter.Default, firstPlugin.ConverterSeenByConfigureClient);
            Assert.Same(DurableAIDataConverter.Instance, lastPlugin.ConverterSeenByConfigureClient);

            // Deterministic ordering, not just "both ran".
            AssertOrdered(log, "first:Connect:enter", "last:Connect:enter");
            AssertOrdered(log, "first:ConfigureClient", "last:ConfigureClient");
        }
        finally
        {
            await host.StopAsync();
        }
    }

    // ---------------------------------------------------------------------------------------
    // Case 5: canonical registration appends; it never replaces an existing plugin collection.
    // Options-shape assertion — the execution-level counterpart is the ordering test above,
    // which also fails if the consumer plugin is dropped.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public void AddDurableAI_NeverReplacesExistingClientPluginCollection()
    {
        var log = new PluginCallLog();
        var consumerPlugin = new RecordingClientPlugin("consumer", log);
        var taskQueue = $"ai-no-replace-{Guid.NewGuid():N}";

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IChatClient>(new TestChatClient());

        services
            .AddHostedTemporalWorker(fixture.TargetHost, fixture.Namespace, taskQueue)
            .ConfigureOptions(options => options.ClientOptions!.Plugins = [consumerPlugin])
            .AddDurableAI(options => options.RegisterDefaultWorkflow = false);

        using var provider = services.BuildServiceProvider();
        var options = ResolveWorkerOptions(provider, taskQueue);

        var pluginNames = options.ClientOptions!.Plugins!.Select(plugin => plugin.Name).ToList();
        Assert.Contains("consumer", pluginNames);
        Assert.Contains(DurableAIDataConverterPlugin.PluginName, pluginNames);
        Assert.Equal(2, pluginNames.Count);
    }

    // ---------------------------------------------------------------------------------------
    // Case 6a: the internal converter plugin is added at most once when worker options are
    // rebuilt against a consumer-owned (shared) client-options instance.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public void InternalConverterPlugin_AddedOnce_WhenWorkerOptionsAreRebuilt()
    {
        var taskQueue = $"ai-dedupe-rebuild-{Guid.NewGuid():N}";

        // A consumer-owned instance assigned into the options — unlike the 3-arg
        // AddHostedTemporalWorker overload, this same object is reused on every options build.
        var sharedClientOptions = new TemporalClientConnectOptions(fixture.TargetHost)
        {
            Namespace = fixture.Namespace,
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IChatClient>(new TestChatClient());

        services
            .AddHostedTemporalWorker(taskQueue)
            .ConfigureOptions(options => options.ClientOptions = sharedClientOptions)
            .AddDurableAI(options => options.RegisterDefaultWorkflow = false);

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
                plugin => plugin.Name == DurableAIDataConverterPlugin.PluginName));
    }

    // ---------------------------------------------------------------------------------------
    // Case 6b: two hosted workers sharing one client-options instance still get exactly one
    // converter plugin.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public void InternalConverterPlugin_AddedOnce_AcrossTwoWorkersSharingClientOptions()
    {
        var queueA = $"ai-dedupe-a-{Guid.NewGuid():N}";
        var queueB = $"ai-dedupe-b-{Guid.NewGuid():N}";

        var sharedClientOptions = new TemporalClientConnectOptions(fixture.TargetHost)
        {
            Namespace = fixture.Namespace,
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IChatClient>(new TestChatClient());

        services
            .AddHostedTemporalWorker(queueA)
            .ConfigureOptions(options => options.ClientOptions = sharedClientOptions)
            .AddDurableAI(options => options.RegisterDefaultWorkflow = false);

        services
            .AddHostedTemporalWorker(queueB)
            .ConfigureOptions(options => options.ClientOptions = sharedClientOptions)
            .AddDurableAI(options => options.RegisterDefaultWorkflow = false);

        using var provider = services.BuildServiceProvider();
        var optionsA = ResolveWorkerOptions(provider, queueA);
        var optionsB = ResolveWorkerOptions(provider, queueB);
        Assert.Same(sharedClientOptions, optionsA.ClientOptions);
        Assert.Same(sharedClientOptions, optionsB.ClientOptions);

        Assert.Equal(
            1,
            sharedClientOptions.Plugins!.Count(
                plugin => plugin.Name == DurableAIDataConverterPlugin.PluginName));
    }

    // ---------------------------------------------------------------------------------------
    // Case 7: a consumer-set data converter is not overwritten by the internal plugin.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task ConsumerDataConverter_IsNotOverwrittenByInternalConverterPlugin()
    {
        var log = new PluginCallLog();
        var probe = new RecordingClientPlugin("probe", log);
        var customConverter = DataConverter.Default with { PayloadCodec = new PassthroughPayloadCodec() };
        var taskQueue = $"ai-custom-converter-{Guid.NewGuid():N}";

        Assert.NotEqual(DataConverter.Default, customConverter);

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<IChatClient>(new TestChatClient());

        var worker = builder.Services.AddHostedTemporalWorker(
            fixture.TargetHost, fixture.Namespace, taskQueue);

        worker.ConfigureOptions(options => options.ClientOptions!.DataConverter = customConverter);
        worker.AddDurableAI(options => options.RegisterDefaultWorkflow = false);

        // Probe placed behind the internal converter plugin so it observes the final decision.
        builder.Services.PostConfigureAll<TemporalWorkerServiceOptions>(options =>
        {
            if (options.ClientOptions is null)
            {
                return;
            }

            var plugins = options.ClientOptions.Plugins?.ToList() ?? [];
            plugins.Add(probe);
            options.ClientOptions.Plugins = plugins;
        });

        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            await AssertCallbackExecutedAsync(
                probe.ConfigureClientCalled, "Probe client plugin ConfigureClient", log);

            Assert.Same(customConverter, probe.ConverterSeenByConfigureClient);
            Assert.NotSame(DurableAIDataConverter.Instance, probe.ConverterSeenByConfigureClient);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    // ---------------------------------------------------------------------------------------
    // Case 9: consumer plugin multiplicity is the consumer's business. Two distinct instances
    // that share a Name must both survive and both execute — no name-based dedup.
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
        var taskQueue = $"ai-plugin-multiplicity-{Guid.NewGuid():N}";

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<IChatClient>(new TestChatClient());

        var worker = builder.Services.AddHostedTemporalWorker(
            fixture.TargetHost, fixture.Namespace, taskQueue);

        worker.ConfigureOptions(options =>
        {
            options.Plugins = [workerPluginOne, workerPluginTwo];
            options.ClientOptions!.Plugins = [clientPluginOne, clientPluginTwo];
        });

        worker.AddDurableAI(options => options.RegisterDefaultWorkflow = false);

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

    /// <summary>
    /// Resolves the named <see cref="TemporalWorkerServiceOptions"/> for a task queue.
    /// The SDK derives the options name from the task queue plus an optional deployment
    /// version; with no version the name is the task queue itself. The TaskQueue assertion
    /// fails loudly if that derivation ever stops holding (a wrong name yields blank options).
    /// </summary>
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
