using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Client;
using TemporalCommunity.Extensions.AI.IntegrationTests.Helpers;
using Temporalio.Extensions.Hosting;
using Temporalio.Testing;
using Xunit;

namespace TemporalCommunity.Extensions.AI.IntegrationTests;

/// <summary>
/// Managed-session profile policy and concurrent queued-turn integration tests.
/// A session captures one worker-side profile at creation; every admitted turn uses that profile,
/// including turns queued while another turn is active.
/// </summary>
public class DurableSessionProfileIntegrationTests
{
    // ── Pattern 3: queued turn while a tool call is in flight ───────────────

    /// <summary>
    /// Two turns use the same captured profile. Turn 1 blocks during a tool call while Turn 2
    /// queues; both model steps for Turn 1 and the later Turn 2 call use the pinned client.
    /// </summary>
    [Fact]
    public async Task Pattern3_ConcurrentTurns_UsePinnedProfile()
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;

        // The second client is registered but is not an allowed route for this session.
        var client1 = new RecordingChatClient("key-1");
        var client2 = new RecordingChatClient("key-2");

        // The tool gate keeps Turn 1 active while Turn 2 is admitted and queued.
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

        // The pinned client handles both model steps for Turn 1 and the queued Turn 2.
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

        // Queue Turn 2 while Turn 1 is blocked, using the session's pinned route.
        var turn2Options = new ChatOptions().WithChatClientKey("key-1");
        var turn2Task = Task.Run(async () =>
            await sessionClient.SendAsync(
                conversationId,
                [new ChatMessage(ChatRole.User, "turn-2")],
                turn2Options));

        // Wait until Turn 2's update has been accepted while Turn 1 remains active.
        var workflowHandle = env.Client.GetWorkflowHandle(sessionClient.GetWorkflowId(conversationId));
        await PollUntilUpdateCountAsync(workflowHandle, minAcceptedCount: 2, TimeSpan.FromSeconds(30));

        // Release the tool gate so Turn 1 can proceed to its second LLM step.
        toolGate.TrySetResult();

        // Both turns must complete successfully.
        var response1 = await turn1Task.WaitAsync(TimeSpan.FromSeconds(60));
        var response2 = await turn2Task.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.NotNull(response1);
        Assert.NotNull(response2);

        Assert.Equal(3, client1.CallCount);
        Assert.Equal(0, client2.CallCount);

        await host.StopAsync();
    }

    // ── Pattern 1: multiple turns queued behind a model call ────────────────

    /// <summary>
    /// Three turns use one profile. Turn 0 blocks at the model call while Turns 1 and 2 queue;
    /// all are then dispatched to the session's pinned client.
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
        client0.Enqueue(new ChatResponse(new ChatMessage(ChatRole.Assistant, "turn-1-answer")));
        client0.Enqueue(new ChatResponse(new ChatMessage(ChatRole.Assistant, "turn-2-answer")));

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

        // Queue Turn 1 and Turn 2 while Turn 0 is blocking, using its pinned profile.
        var turn1Options = new ChatOptions().WithChatClientKey("key-0");
        var turn1Task = Task.Run(async () =>
            await sessionClient.SendAsync(
                conversationId,
                [new ChatMessage(ChatRole.User, "turn-1")],
                turn1Options));

        var turn2Options = new ChatOptions().WithChatClientKey("key-0");
        var turn2Task = Task.Run(async () =>
            await sessionClient.SendAsync(
                conversationId,
                [new ChatMessage(ChatRole.User, "turn-2")],
                turn2Options));

        // Wait until Turn 1 and Turn 2 have both been accepted by the workflow.
        var workflowHandleP1 = env.Client.GetWorkflowHandle(sessionClient.GetWorkflowId(conversationId));
        await PollUntilUpdateCountAsync(workflowHandleP1, minAcceptedCount: 3, TimeSpan.FromSeconds(30));

        // Release Turn 0's LLM call.
        client0Gate.TrySetResult();

        // All three turns must complete successfully.
        await turn0Task.WaitAsync(TimeSpan.FromSeconds(60));
        var response1 = await turn1Task.WaitAsync(TimeSpan.FromSeconds(60));
        var response2 = await turn2Task.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.NotNull(response1);
        Assert.NotNull(response2);

        Assert.Equal(3, client0.CallCount);
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

    // ── Deterministic coordination helpers ────────────────────────────────────

    /// <summary>
    /// Polls <paramref name="handle"/>'s history until at least
    /// <paramref name="minAcceptedCount"/> <c>WorkflowExecutionUpdateAccepted</c> events
    /// have been recorded, or <paramref name="timeout"/> elapses.
    ///
    /// <para>
    /// Each <c>SendAsync</c> call issues a <c>WorkflowExecutionUpdate</c> (via
    /// <c>ExecuteUpdateAsync</c>). Once the workflow accepts an update it appends a
    /// <c>WorkflowExecutionUpdateAccepted</c> event to the history. Polling for N such
    /// events is a deterministic alternative to a fixed-duration sleep: it confirms
    /// the queued turns have been accepted and their metadata written before releasing
    /// the gate that advances the in-flight turn.
    /// </para>
    /// </summary>
    private static async Task PollUntilUpdateCountAsync(
        WorkflowHandle handle,
        int minAcceptedCount,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var count = 0;
            await foreach (var ev in handle.FetchHistoryEventsAsync().ConfigureAwait(false))
            {
                if (ev.WorkflowExecutionUpdateAcceptedEventAttributes is not null)
                    count++;
            }
            if (count >= minAcceptedCount)
                return;
            await Task.Delay(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
        }
        throw new TimeoutException(
            $"Expected at least {minAcceptedCount} WorkflowExecutionUpdateAccepted events " +
            $"within {timeout}, but did not observe them.");
    }

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
