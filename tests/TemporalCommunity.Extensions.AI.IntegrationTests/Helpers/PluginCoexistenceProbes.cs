using System.Collections.Concurrent;
using Temporalio.Api.Common.V1;
using Temporalio.Client;
using Temporalio.Converters;
using Temporalio.Worker;

namespace TemporalCommunity.Extensions.AI.IntegrationTests.Helpers;

/// <summary>
/// Ordered, thread-safe record of plugin callback invocations. Plugin callbacks fire on
/// worker/client startup threads, so ordering assertions need a concurrency-safe sink.
/// </summary>
internal sealed class PluginCallLog
{
    private readonly ConcurrentQueue<string> entries = new();

    public void Record(string entry) => entries.Enqueue(entry);

    public IReadOnlyList<string> Entries => [.. entries];

    /// <summary>Index of the first occurrence of <paramref name="entry"/>, or -1.</summary>
    public int IndexOf(string entry)
    {
        var snapshot = Entries;
        for (var i = 0; i < snapshot.Count; i++)
        {
            if (string.Equals(snapshot[i], entry, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    public override string ToString() => string.Join(" | ", Entries);
}

/// <summary>
/// A consumer-style <see cref="ITemporalWorkerPlugin"/> that proves its callbacks actually ran.
/// <para>
/// <see cref="Label"/> is separate from <see cref="Name"/> on purpose: the multiplicity tests
/// register two distinct instances that deliberately share a <see cref="Name"/>, and the log
/// still has to tell them apart.
/// </para>
/// </summary>
internal sealed class RecordingWorkerPlugin : ITemporalWorkerPlugin
{
    private readonly PluginCallLog log;
    private readonly TaskCompletionSource runWorkerEntered =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int configureWorkerCalls;
    private int runWorkerCalls;

    public RecordingWorkerPlugin(string label, PluginCallLog log)
        : this(label, label, log)
    {
    }

    public RecordingWorkerPlugin(string label, string name, PluginCallLog log)
    {
        Label = label;
        Name = name;
        this.log = log;
    }

    /// <summary>Log-only identity. Unique per instance even when <see cref="Name"/> is shared.</summary>
    public string Label { get; }

    /// <inheritdoc />
    public string Name { get; }

    public int ConfigureWorkerCalls => Volatile.Read(ref configureWorkerCalls);

    public int RunWorkerCalls => Volatile.Read(ref runWorkerCalls);

    /// <summary>Completes the first time <see cref="RunWorkerAsync{TResult}"/> is entered.</summary>
    public Task RunWorkerEntered => runWorkerEntered.Task;

    /// <inheritdoc />
    public void ConfigureWorker(TemporalWorkerOptions options)
    {
        Interlocked.Increment(ref configureWorkerCalls);
        log.Record($"{Label}:ConfigureWorker");
    }

    /// <inheritdoc />
    public Task<TResult> RunWorkerAsync<TResult>(
        TemporalWorker worker,
        Func<TemporalWorker, CancellationToken, Task<TResult>> continuation,
        CancellationToken stoppingToken)
    {
        Interlocked.Increment(ref runWorkerCalls);
        log.Record($"{Label}:RunWorker");
        runWorkerEntered.TrySetResult();
        return continuation(worker, stoppingToken);
    }

    /// <inheritdoc />
    public void ConfigureReplayer(WorkflowReplayerOptions options) =>
        log.Record($"{Label}:ConfigureReplayer");

    /// <inheritdoc />
    public Task<IEnumerable<WorkflowReplayResult>> ReplayWorkflowsAsync(
        WorkflowReplayer replayer,
        Func<WorkflowReplayer, CancellationToken, Task<IEnumerable<WorkflowReplayResult>>> continuation,
        CancellationToken cancellationToken) =>
        continuation(replayer, cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<WorkflowReplayResult> ReplayWorkflowsAsync(
        WorkflowReplayer replayer,
        Func<WorkflowReplayer, IAsyncEnumerable<WorkflowReplayResult>> continuation,
        CancellationToken cancellationToken) =>
        continuation(replayer);
}

/// <summary>
/// A consumer-style <see cref="ITemporalClientPlugin"/> that proves its callbacks ran and
/// captures the <see cref="DataConverter"/> visible at its position in the plugin chain.
/// <para>
/// The captured converter is the load-bearing observation: a probe ordered after the internal
/// converter plugin sees the converter that plugin installed, which is direct evidence the
/// internal plugin executed — not merely that it was present in a list.
/// </para>
/// </summary>
internal sealed class RecordingClientPlugin : ITemporalClientPlugin
{
    private readonly PluginCallLog log;
    private readonly TaskCompletionSource configureClientCalled =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly TaskCompletionSource connectCalled =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int configureClientCalls;
    private int connectCalls;
    private DataConverter? converterSeenByConfigureClient;

    public RecordingClientPlugin(string label, PluginCallLog log)
        : this(label, label, log)
    {
    }

    public RecordingClientPlugin(string label, string name, PluginCallLog log)
    {
        Label = label;
        Name = name;
        this.log = log;
    }

    /// <summary>Log-only identity. Unique per instance even when <see cref="Name"/> is shared.</summary>
    public string Label { get; }

    /// <inheritdoc />
    public string Name { get; }

    public int ConfigureClientCalls => Volatile.Read(ref configureClientCalls);

    public int ConnectCalls => Volatile.Read(ref connectCalls);

    /// <summary>Completes the first time <see cref="ConfigureClient"/> is invoked.</summary>
    public Task ConfigureClientCalled => configureClientCalled.Task;

    /// <summary>
    /// Completes the first time <see cref="ConnectAsync"/> is entered. A counter alone races:
    /// the client is lazy on the AddTemporalClient topology, so the connection can still be in
    /// flight when the host has finished starting.
    /// </summary>
    public Task ConnectCalled => connectCalled.Task;

    /// <summary>The converter this plugin observed when its turn in the chain came up.</summary>
    public DataConverter? ConverterSeenByConfigureClient =>
        Volatile.Read(ref converterSeenByConfigureClient);

    /// <inheritdoc />
    public void ConfigureClient(TemporalClientOptions options)
    {
        Volatile.Write(ref converterSeenByConfigureClient, options.DataConverter);
        Interlocked.Increment(ref configureClientCalls);
        log.Record($"{Label}:ConfigureClient");
        configureClientCalled.TrySetResult();
    }

    /// <inheritdoc />
    public async Task<TemporalConnection> ConnectAsync(
        TemporalClientConnectOptions options,
        Func<TemporalClientConnectOptions, Task<TemporalConnection>> continuation)
    {
        Interlocked.Increment(ref connectCalls);
        connectCalled.TrySetResult();
        log.Record($"{Label}:Connect:enter");
        var connection = await continuation(options).ConfigureAwait(false);
        log.Record($"{Label}:Connect:exit");
        return connection;
    }
}

/// <summary>
/// A no-op codec used only to build a <see cref="DataConverter"/> that is provably not
/// <see cref="DataConverter.Default"/> (records compare by value, so the codec is what makes
/// the instance distinct).
/// </summary>
internal sealed class PassthroughPayloadCodec : IPayloadCodec
{
    public Task<IReadOnlyCollection<Payload>> EncodeAsync(IReadOnlyCollection<Payload> payloads) =>
        Task.FromResult(payloads);

    public Task<IReadOnlyCollection<Payload>> DecodeAsync(IReadOnlyCollection<Payload> payloads) =>
        Task.FromResult(payloads);
}
