using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Client;
using Temporalio.Extensions.AI.IntegrationTests.Helpers;
using Temporalio.Extensions.Hosting;
using Temporalio.Testing;
using Xunit;

namespace Temporalio.Extensions.AI.IntegrationTests;

/// <summary>
/// CRIT-3 regression tests: the instance-field bridge between <c>ChatAsync</c> and
/// <c>ExecuteTurnAsync</c> is unsafe under concurrent queued turns.
///
/// <para>
/// Root cause: <c>DurableChatWorkflow._lastClientKey</c> and <c>_lastConversationId</c>
/// are instance fields that <c>ChatAsync</c> writes before the base session loop
/// serializes turn execution. When a prior turn holds <c>_isProcessing == true</c>,
/// subsequent concurrent <c>ChatAsync</c> calls run, overwrite those fields, then
/// suspend at <c>WaitConditionAsync</c>. The suspended turn resumes with corrupted
/// per-turn metadata — most visibly, Turn N dispatches its LLM activity with Turn M's
/// <see cref="IChatClient"/> key.
/// </para>
///
/// <para>
/// Fix (<see cref="DurableChatWorkflow"/>): remove the instance-field bridge and replace
/// with a per-turn dictionary keyed by <c>DurableSessionRequest</c> object reference
/// (reference equality).
/// </para>
///
/// <para>
/// Two test shapes — one per execution mode:
/// <list type="bullet">
///   <item><b>Pattern 3</b>: 2 turns. Turn 1 yields between tool-loop iterations
///     (tool call blocks); Turn 2 queues during that yield. Assert no cross-routing.</item>
///   <item><b>Pattern 1</b>: 3 turns. Turn 0 blocks at the LLM step itself
///     (gate in the chat client); Turns 1 and 2 both queue and write their
///     metadata before Turn 0 releases. Assert Turns 1 and 2 each get their
///     own client, not each other's.</item>
/// </list>
/// </para>
/// </summary>
public class DurablePerTurnMetadataRaceIntegrationTests
{
    // ── CRIT-3 Pattern 3: 2-turn shape ─────────────────────────────────────

    /// <summary>
    /// Pattern 3 test: two turns sent to the same session, each with a different
    /// <c>WithChatClientKey</c>. Turn 1 has a tool call that blocks long enough for
    /// Turn 2 to enter <c>ChatAsync</c> and overwrite the instance fields.
    ///
    /// After the fix, Turn 1's SECOND LLM step (post tool result) must still use
    /// <c>key-1-client</c>, not <c>key-2-client</c>.
    /// </summary>
    [Fact]
    public async Task Pattern3_ConcurrentTurns_ClientKeyNotCrossRouted()
    {
        await using var env = await WorkflowEnvironment.StartLocalAsync();

        // Two independently recording clients, one per key.
        var client1 = new RecordingChatClient("key-1");
        var client2 = new RecordingChatClient("key-2");

        // The tool gate: blocks Turn 1 between its first and second GetChatStep
        // activities, giving Turn 2 enough time to write _lastClientKey.
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

        // Turn 1 script: first LLM step returns a tool call; second returns the final answer.
        // Turn 2 script: single LLM step returning a direct answer (no tool calls).
        // Both clients share a scripted response queue — key-1 needs 2 responses, key-2 needs 1.
        client1.Enqueue(new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("call-race", "blocking_race_tool")])));
        client1.Enqueue(new ChatResponse(new ChatMessage(ChatRole.Assistant, "turn-1-final-answer")));
        client2.Enqueue(new ChatResponse(new ChatMessage(ChatRole.Assistant, "turn-2-answer")));

        var taskQueue = $"crit3-p3-{Guid.NewGuid():N}";
        using var host = BuildHostWithKeyedClients(
            env.Client,
            taskQueue,
            ("key-1", client1),
            ("key-2", client2),
            workerBuilder => workerBuilder.AddDurableTools(blockingTool, o => o.NoRetry()));
        await host.StartAsync();

        var sessionClient = host.Services.GetRequiredService<DurableChatSessionClient>();
        var conversationId = $"race-p3-{Guid.NewGuid():N}";

        // Start Turn 1 in the background; it will block during the tool fan-out.
        var turn1Options = new ChatOptions().WithChatClientKey("key-1");
        var turn1Task = Task.Run(async () =>
            await sessionClient.ChatAsync(
                conversationId,
                [new ChatMessage(ChatRole.User, "turn-1")],
                turn1Options));

        // Wait until the tool activity has started (Turn 1 is now inside the tool fan-out).
        await toolStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Send Turn 2 while Turn 1 is blocked. Turn 2's ChatAsync will write _lastClientKey
        // to "key-2" and then suspend at WaitConditionAsync (because _isProcessing == true).
        var turn2Options = new ChatOptions().WithChatClientKey("key-2");
        var turn2Task = Task.Run(async () =>
            await sessionClient.ChatAsync(
                conversationId,
                [new ChatMessage(ChatRole.User, "turn-2")],
                turn2Options));

        // Give Turn 2's ChatAsync time to reach WaitConditionAsync and overwrite the fields.
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        // Release the tool gate so Turn 1 can proceed to its second LLM step.
        toolGate.TrySetResult();

        // Both turns must complete successfully.
        var response1 = await turn1Task.WaitAsync(TimeSpan.FromSeconds(60));
        var response2 = await turn2Task.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.NotNull(response1);
        Assert.NotNull(response2);

        // key-1-client must have been called exactly twice: once before the tool call
        // and once after the tool result was fed back (both GetChatStep activities for Turn 1).
        Assert.Equal(2, client1.CallCount);

        // key-2-client must have been called exactly once: Turn 2's single LLM step.
        Assert.Equal(1, client2.CallCount);

        await host.StopAsync();
    }

    // ── CRIT-3 Pattern 1: 3-turn shape ─────────────────────────────────────

    /// <summary>
    /// Pattern 1 test: three turns queued against the same session. Turn 0 blocks
    /// at the LLM call (keeping <c>_isProcessing == true</c>); Turns 1 and 2 both
    /// enter <c>ChatAsync</c>, each writing their <c>_lastClientKey</c>, and suspend.
    ///
    /// The write order is Turn 1 (writes "key-1") then Turn 2 (writes "key-2").
    /// When Turn 0 finishes and Turn 1 resumes, the unfixed code reads <c>_lastClientKey = "key-2"</c>
    /// (the last writer's value). After the fix, Turn 1 reads its own metadata.
    ///
    /// Pattern 1 requires 3 concurrent turns to create the overwrite window:
    /// Turn 0 must be in-flight so both Turn 1 and Turn 2 can queue and write before
    /// Turn 1 executes. A 2-turn scenario cannot create this window because Turn 1
    /// would wait at <c>WaitConditionAsync</c> before Turn 2 has written anything.
    /// </summary>
    [Fact]
    public async Task Pattern1_ConcurrentTurns_ClientKeyNotCrossRouted()
    {
        await using var env = await WorkflowEnvironment.StartLocalAsync();

        // Three independently recording clients.
        var client0 = new RecordingChatClient("key-0");
        var client1 = new RecordingChatClient("key-1");
        var client2 = new RecordingChatClient("key-2");

        // Gate: Turn 0 blocks inside its LLM call until the test releases it,
        // keeping _isProcessing == true while Turns 1 and 2 enter ChatAsync.
        var client0Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client0Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        client0.BlockOnNextCall(client0Gate, client0Started);
        client1.Enqueue(new ChatResponse(new ChatMessage(ChatRole.Assistant, "turn-1-answer")));
        client2.Enqueue(new ChatResponse(new ChatMessage(ChatRole.Assistant, "turn-2-answer")));

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
            await sessionClient.ChatAsync(
                conversationId,
                [new ChatMessage(ChatRole.User, "turn-0")],
                turn0Options));

        // Wait until Turn 0's activity has started (LLM call in progress).
        await client0Started.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Queue Turn 1 and Turn 2 while Turn 0 is blocking. Both ChatAsync calls
        // will run on the Temporal task scheduler (FIFO), write their keys in order
        // (key-1 then key-2), then suspend at WaitConditionAsync.
        var turn1Options = new ChatOptions().WithChatClientKey("key-1");
        var turn1Task = Task.Run(async () =>
            await sessionClient.ChatAsync(
                conversationId,
                [new ChatMessage(ChatRole.User, "turn-1")],
                turn1Options));

        var turn2Options = new ChatOptions().WithChatClientKey("key-2");
        var turn2Task = Task.Run(async () =>
            await sessionClient.ChatAsync(
                conversationId,
                [new ChatMessage(ChatRole.User, "turn-2")],
                turn2Options));

        // Allow time for both ChatAsync calls to arrive at the server and queue.
        await Task.Delay(TimeSpan.FromMilliseconds(700));

        // Release Turn 0's LLM call.
        client0Gate.TrySetResult();

        // All three turns must complete successfully.
        await turn0Task.WaitAsync(TimeSpan.FromSeconds(60));
        var response1 = await turn1Task.WaitAsync(TimeSpan.FromSeconds(60));
        var response2 = await turn2Task.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.NotNull(response1);
        Assert.NotNull(response2);

        // Each keyed client must have been called exactly once, for its own turn.
        Assert.Equal(1, client0.CallCount);
        Assert.Equal(1, client1.CallCount);
        Assert.Equal(1, client2.CallCount);

        await host.StopAsync();
    }

    // ── Test-host plumbing ──────────────────────────────────────────────────

    /// <summary>
    /// Builds a Pattern 1 or Pattern 3 worker host with multiple keyed
    /// <see cref="IChatClient"/> registrations. Pattern 1 vs Pattern 3 is determined
    /// by whether <paramref name="registerTools"/> registers any durable tools
    /// (Pattern 3 activates when the tool registry is non-empty).
    ///
    /// <para>
    /// Unlike the main <see cref="DurableToolDispatchIntegrationTests.BuildHost"/>,
    /// this helper registers named keyed clients — the unkeyed slot is deliberately
    /// left empty so all resolution must go through the per-call key.
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

        builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
            new NoopEmbeddingGenerator());

        var workerBuilder = builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddDurableAI(opts =>
            {
                opts.ActivityTimeout = TimeSpan.FromSeconds(60);
                opts.HeartbeatTimeout = TimeSpan.FromSeconds(15);
                opts.SessionTimeToLive = TimeSpan.FromMinutes(5);
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

    /// <summary>
    /// Stub <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> to satisfy
    /// <see cref="DurableEmbeddingActivities"/> constructor injection without exercising it.
    /// </summary>
    private sealed class NoopEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public EmbeddingGeneratorMetadata Metadata { get; } = new("noop", null, null, 1);

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(
                values.Select(_ => new Embedding<float>(new[] { 0f })).ToList()));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
