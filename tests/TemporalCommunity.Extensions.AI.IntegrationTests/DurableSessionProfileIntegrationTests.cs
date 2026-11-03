using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TemporalCommunity.Extensions.AI.Exceptions;
using Temporalio.Client;
using TemporalCommunity.Extensions.AI.IntegrationTests.Helpers;
using Temporalio.Extensions.Hosting;
using Temporalio.Testing;
using Xunit;

namespace TemporalCommunity.Extensions.AI.IntegrationTests;

/// <summary>
/// Managed-session profile policy and single-active-turn integration tests.
/// A session captures one worker-side profile at creation; every admitted turn uses that profile,
/// while a different request is rejected during an active turn.
/// </summary>
public class DurableSessionProfileIntegrationTests
{
    // ── Pattern 3: reject a new turn while a tool call is in flight ────────

    /// <summary>
    /// Turn 1 uses the captured profile. While it blocks during a tool call, a different
    /// request is rejected; both model steps for Turn 1 use the pinned client.
    /// </summary>
    [Fact]
    public async Task Pattern3_ConcurrentTurns_UsePinnedProfile()
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;

        // The second client is registered but is not an allowed route for this session.
        var client1 = new RecordingChatClient("key-1");
        var client2 = new RecordingChatClient("key-2");

        // The tool gate keeps Turn 1 active while the second request is rejected.
        var toolGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var toolStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var blockingTool = AIFunctionFactory.Create(
            async (string? _ = null) =>
            {
                toolStarted.TrySetResult();
                await toolGate.Task.ConfigureAwait(false);
                return (object?)"tool-done";
            },
            "blocking_race_tool",
            "Blocks until released.");

        // The pinned client handles both model steps for Turn 1.
        client1.Enqueue(new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("call-race", "blocking_race_tool")])));
        client1.Enqueue(new ChatResponse(new ChatMessage(ChatRole.Assistant, "turn-1-final-answer")));
        client1.Enqueue(new ChatResponse(new ChatMessage(ChatRole.Assistant, "turn-2-answer")));

        var taskQueue = $"crit3-p3-{Guid.NewGuid():N}";
        using var host = BuildHostWithKeyedClients(
            env.Client,
            taskQueue,
            ("key-1", client1),
            ("key-2", client2),
            workerBuilder => workerBuilder.AddDurableTool(blockingTool, o => o.NoRetry()));
        await host.StartAsync();

        var sessionClient = host.Services.GetRequiredService<DurableChatSessionClient>();
        var conversationId = $"race-p3-{Guid.NewGuid():N}";

        // Start Turn 1 in the background; it will block during the tool fan-out.
        var turn1Options = new ChatOptions().WithChatClientKey("key-1");
        var turn1Task = Task.Run(async () =>
            await sessionClient.SendAsync(
                conversationId,
                [new ChatMessage(ChatRole.User, "turn-1")],
                turn1Options));

        // Wait until the tool activity has started (Turn 1 is now inside the tool fan-out).
        await toolStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // A different request cannot start while Turn 1 is active, even on the pinned route.
        var turn2Options = new ChatOptions().WithChatClientKey("key-1");
        await Assert.ThrowsAsync<DurableChatRequestException>(() =>
            sessionClient.SendAsync(
                conversationId,
                [new ChatMessage(ChatRole.User, "turn-2")],
                turn2Options));

        // Release the tool gate so Turn 1 can proceed to its second LLM step.
        toolGate.TrySetResult();

        // The admitted turn completes successfully; a conflicting active turn was rejected.
        var response1 = await turn1Task.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.NotNull(response1);

        Assert.Equal(2, client1.CallCount);
        Assert.Equal(0, client2.CallCount);

        await host.StopAsync();
    }

    // ── Pattern 1: a second turn is rejected while a model call is active ──

    /// <summary>
    /// Turn 0 blocks at the model call. A different request is rejected until it completes,
    /// and the admitted turn uses the session's pinned client.
    /// </summary>
    [Fact]
    public async Task Pattern1_ConcurrentTurns_UsePinnedProfile()
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;

        // Other registered profiles are not used by this session.
        var client0 = new RecordingChatClient("key-0");
        var client1 = new RecordingChatClient("key-1");
        var client2 = new RecordingChatClient("key-2");

        // Gate: Turn 0 blocks inside its LLM call until the test releases it.
        var client0Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client0Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        client0.Enqueue(new ChatResponse(new ChatMessage(ChatRole.Assistant, "turn-0-answer")));
        client0.BlockOnNextCall(client0Gate, client0Started);

        var taskQueue = $"crit3-p1-{Guid.NewGuid():N}";
        using var host = BuildHostWithKeyedClients(
            env.Client,
            taskQueue,
            ("key-0", client0),
            ("key-1", client1),
            ("key-2", client2),
            workerBuilder => { /* no tools: Pattern 1 path (no durable tools registered) */ });
        await host.StartAsync();

        var sessionClient = host.Services.GetRequiredService<DurableChatSessionClient>();
        var conversationId = $"race-p1-{Guid.NewGuid():N}";

        // Start Turn 0 — it will block inside the LLM call.
        var turn0Options = new ChatOptions().WithChatClientKey("key-0");
        var turn0Task = Task.Run(async () =>
            await sessionClient.SendAsync(
                conversationId,
                [new ChatMessage(ChatRole.User, "turn-0")],
                turn0Options));

        // Wait until Turn 0's activity has started (LLM call in progress).
        await client0Started.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // A different request while Turn 0 is blocking is rejected, not queued.
        var turn1Options = new ChatOptions().WithChatClientKey("key-0");
        await Assert.ThrowsAsync<DurableChatRequestException>(() =>
            sessionClient.SendAsync(
                conversationId,
                [new ChatMessage(ChatRole.User, "turn-1")],
                turn1Options));

        // Release Turn 0's LLM call.
        client0Gate.TrySetResult();

        // The one admitted turn completes successfully.
        await turn0Task.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(1, client0.CallCount);
        Assert.Equal(0, client1.CallCount);
        Assert.Equal(0, client2.CallCount);

        await host.StopAsync();
    }

    // ── Test-host plumbing ──────────────────────────────────────────────────

    /// <summary>
    /// Builds a worker host with multiple keyed <see cref="IChatClient"/> registrations.
    /// The first registration is the session's pinned profile. Pattern 1 vs Pattern 3 is determined
    /// by whether <paramref name="registerTools"/> registers any durable tools
    /// (Pattern 3 activates when the tool registry is non-empty).
    ///
    /// <para>
    /// Unlike the main <see cref="DurableToolDispatchIntegrationTests.BuildHost"/>,
    /// this helper registers named keyed clients — the unkeyed slot is deliberately
    /// left empty so all resolution must go through the profile captured in workflow input.
    /// </para>
    /// </summary>
    private static IHost BuildHostWithKeyedClients(
        ITemporalClient client,
        string taskQueue,
        Action<ITemporalWorkerServiceOptionsBuilder> registerTools,
        params (string Key, IChatClient Client)[] keyedClients)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(client);

        foreach (var (key, chatClient) in keyedClients)
        {
            builder.Services.AddKeyedSingleton<IChatClient>(key, chatClient);
        }

        // Register a fallback unkeyed client that throws so any unintended unkeyed
        // resolution surfaces loudly rather than silently returning wrong responses.
        builder.Services.AddSingleton<IChatClient>(new ThrowingChatClient());


        var workerBuilder = builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddDurableAI(opts =>
            {
                opts.ActivityTimeout = TimeSpan.FromSeconds(60);
                opts.HeartbeatTimeout = TimeSpan.FromSeconds(15);
                opts.SessionTimeToLive = TimeSpan.FromMinutes(5);
                opts.DefaultChatClientKey = keyedClients[0].Key;
            });

        registerTools(workerBuilder);

        return builder.Build();
    }

    // Overload accepting array syntax with Action last (C# params limitation workaround).
    private static IHost BuildHostWithKeyedClients(
        ITemporalClient client,
        string taskQueue,
        (string Key, IChatClient Client) pair1,
        (string Key, IChatClient Client) pair2,
        Action<ITemporalWorkerServiceOptionsBuilder> registerTools) =>
        BuildHostWithKeyedClients(client, taskQueue, registerTools, pair1, pair2);

    private static IHost BuildHostWithKeyedClients(
        ITemporalClient client,
        string taskQueue,
        (string Key, IChatClient Client) pair1,
        (string Key, IChatClient Client) pair2,
        (string Key, IChatClient Client) pair3,
        Action<ITemporalWorkerServiceOptionsBuilder> registerTools) =>
        BuildHostWithKeyedClients(client, taskQueue, registerTools, pair1, pair2, pair3);

    // ── Chat client helpers ─────────────────────────────────────────────────

    /// <summary>
    /// An <see cref="IChatClient"/> that:
    /// <list type="bullet">
    ///   <item>Records every call made to it (call count, per-instance)</item>
    ///   <item>Serves responses from a queue (same dequeue-on-call contract as
    ///     <see cref="ScriptedChatClient"/>)</item>
    ///   <item>Supports an optional one-shot blocking gate for a specific call
    ///     (used to hold <c>_isProcessing == true</c> while queuing concurrent turns)</item>
    /// </list>
    /// </summary>
    private sealed class RecordingChatClient(string label) : IChatClient
    {
        private readonly object _gate = new();
        private readonly Queue<ChatResponse> _scripted = new();
        private int _callCount;
        private TaskCompletionSource? _blockGate;
        private TaskCompletionSource? _startedSignal;

        public string Label { get; } = label;
        public int CallCount => Volatile.Read(ref _callCount);

        public ChatClientMetadata Metadata { get; } = new($"recording-{label}");

        /// <summary>
        /// Enqueue a scripted response. The client dequeues one response per call.
        /// Throws if the queue is empty when a call arrives.
        /// </summary>
        public void Enqueue(ChatResponse response)
        {
            ArgumentNullException.ThrowIfNull(response);
            lock (_gate)
            {
                _scripted.Enqueue(response);
            }
        }

        /// <summary>
        /// Installs a one-shot blocking gate on the NEXT streaming call.
        /// The call will signal <paramref name="startedSignal"/> when it begins
        /// streaming, then wait for <paramref name="releaseGate"/> before yielding
        /// any response updates.
        /// </summary>
        public void BlockOnNextCall(TaskCompletionSource releaseGate, TaskCompletionSource startedSignal)
        {
            lock (_gate)
            {
                _blockGate = releaseGate;
                _startedSignal = startedSignal;
            }
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (_scripted.Count == 0)
                    throw new InvalidOperationException(
                        $"RecordingChatClient[{Label}] ran out of scripted responses.");
                var response = _scripted.Dequeue();
                Interlocked.Increment(ref _callCount);
                return Task.FromResult(response);
            }
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ChatResponse response;
            TaskCompletionSource? gate;
            TaskCompletionSource? started;

            lock (_gate)
            {
                if (_scripted.Count == 0)
                    throw new InvalidOperationException(
                        $"RecordingChatClient[{Label}] ran out of scripted responses.");
                response = _scripted.Dequeue();
                Interlocked.Increment(ref _callCount);
                gate = _blockGate;
                started = _startedSignal;
                _blockGate = null;
                _startedSignal = null;
            }

            if (gate is not null)
            {
                // Signal that this streaming call has started, then wait for release.
                started?.TrySetResult();
                await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (var update in response.ToChatResponseUpdates())
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    /// <summary>
    /// Fallback unkeyed <see cref="IChatClient"/> that always throws. Any call to this
    /// client means the test failed to route through the expected keyed registration.
    /// </summary>
    private sealed class ThrowingChatClient : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new("throwing-fallback");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "ThrowingChatClient was called — test is routing through the unkeyed " +
                "fallback instead of the expected keyed client. Check WithChatClientKey usage.");

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            throw new InvalidOperationException(
                "ThrowingChatClient was called — test is routing through the unkeyed " +
                "fallback instead of the expected keyed client. Check WithChatClientKey usage.");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

}
