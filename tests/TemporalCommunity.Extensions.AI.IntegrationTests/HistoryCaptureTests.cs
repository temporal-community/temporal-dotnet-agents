using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Extensions.Hosting;
using Temporalio.Testing;
using TemporalCommunity.Extensions.AI;
using TemporalCommunity.Extensions.AI.IntegrationTests.Helpers;
using TemporalCommunity.Extensions.AI.Session;
using TemporalCommunity.Extensions.AI.Tests.Compat;
using Xunit;

namespace TemporalCommunity.Extensions.AI.IntegrationTests;

/// <summary>
/// Captures workflow history JSON files for use by the replay unit-test suite.
/// These tests run against the embedded Temporal server; the resulting JSON files
/// are checked in under <c>tests/TemporalCommunity.Extensions.AI.Tests/Compat/Histories/</c>
/// and replayed in <c>WorkflowReplayTests</c> — which runs in the <c>just test-unit-all</c>
/// fast lane with no server.
/// </summary>
/// <remarks>
/// <para>
/// These tests are trait-gated out of the normal integration run (<c>just test-integration-ai</c>)
/// so they do not overwrite the checked-in golden JSON on every run. Regenerate the corpus on
/// demand with <c>just capture-histories</c> — run it ONLY when the workflow logic changes
/// (new activity type, new wire string, new CAN trigger condition), then commit the updated JSON.
/// </para>
/// <para>
/// The output directory is resolved relative to the repository root, not the test binary
/// location, so the files land in the correct checked-in path regardless of build output
/// directory.
/// </para>
/// </remarks>
[Trait("Category", "HistoryCapture")]
public class HistoryCaptureTests
{
    // Absolute path to the checked-in Histories directory that lives in the unit-test project.
    // Resolved from the test binary location (bin/Release/net10.0/) climbing up to the repo root.
    private static string HistoriesDir =>
        Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                // AppContext.BaseDirectory = tests/<IntegrationProj>/bin/Release/net10.0/
                // Climb: net10.0/ → bin/ → <IntegrationProj>/ → tests/
                "..", "..", "..",
                "..", // tests/
                "TemporalCommunity.Extensions.AI.Tests",
                "Compat",
                "Histories"));

    // ── Direct middleware metadata compatibility ─────────────────────────

    /// <summary>
    /// Captures the pre-metadata-fix direct-adapter payload. The workflow supplies a factory
    /// key and tag, but the current transport removes those values before scheduling the
    /// activity. Commit 2 must keep this history replayable while changing new payloads.
    /// </summary>
    [Fact]
    public async Task Capture_DirectMiddlewareOptionsV1()
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;

        using var host = BuildDirectMiddlewareHost(env.Client);
        await host.StartAsync();

        var handle = await env.Client.StartWorkflowAsync(
            (DurableChatClientWorkflow workflow) => workflow.RunAsync(
                new DurableChatClientWorkflowInput { IncludeCompatibilityMetadata = true }),
            new WorkflowOptions(
                $"capture-direct-middleware-{Guid.NewGuid():N}",
                DurableChatClientWorkflow.TaskQueue));

        var result = await handle.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal("Response: scheduler probe", result);

        var history = await handle.FetchHistoryAsync();
        await SaveHistoryAsync("direct-middleware-options-v1.json", history);

        await host.StopAsync();
    }

    // ── Pattern 1 simple turn (no tool calls) ─────────────────────────────

    /// <summary>
    /// Captures a history of a simple Pattern-1 chat turn (single user→assistant exchange,
    /// no tool calls, no CAN). This is the baseline replay history.
    /// </summary>
    [Fact]
    public async Task Capture_Pattern1_SimpleTurn()
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;

        var chatClient = new TestChatClient();
        var taskQueue = $"capture-p1-{Guid.NewGuid():N}";

        using var host = BuildSimpleHost(env.Client, chatClient, taskQueue);
        await host.StartAsync();

        var sessionClient = host.Services.GetRequiredService<DurableChatSessionClient>();
        var conversationId = $"cap-p1-{Guid.NewGuid():N}";

        await sessionClient.SendAsync(conversationId, [new ChatMessage(ChatRole.User, "hello")]);

        var workflowId = sessionClient.GetWorkflowId(conversationId);
        var handle = env.Client.GetWorkflowHandle(workflowId);
        var history = await handle.FetchHistoryAsync();

        await SaveHistoryAsync("pattern-1-simple-turn.json", history);

        await host.StopAsync();
    }

    // ── Pattern 3 with a single tool call ─────────────────────────────────

    /// <summary>
    /// Captures a Pattern-3 history with one tool call: the workflow dispatches
    /// <c>GetChatStep</c> → <c>InvokeFunction</c> → <c>GetChatStep</c> (final).
    /// This exercises the durable tool dispatch loop in a single turn.
    /// </summary>
    [Fact]
    public async Task Capture_Pattern3_WithTool()
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;

        var harness = new ScriptedToolHarness();
        var weatherTool = harness.BuildAlwaysSucceeds(
            "get_weather",
            "Returns current weather.",
            _ => "sunny 72F");

        var scripted = ScriptedChatClient.WithToolCallsThenFinal(
            [new FunctionCallContent("call-1", "get_weather", new Dictionary<string, object?> { ["city"] = "Boston" })],
            "The weather in Boston is sunny, 72F.");

        var taskQueue = $"capture-p3-{Guid.NewGuid():N}";
        using var host = BuildPattern3Host(env.Client, taskQueue, scripted,
            builder => builder.AddDurableTools(weatherTool));
        await host.StartAsync();

        var sessionClient = host.Services.GetRequiredService<DurableChatSessionClient>();
        var conversationId = $"cap-p3-{Guid.NewGuid():N}";

        var response = await sessionClient.SendAsync(
            conversationId,
            [new ChatMessage(ChatRole.User, "weather in Boston?")]);

        Assert.NotNull(response);
        Assert.Contains("sunny", response.Text);

        var workflowId = sessionClient.GetWorkflowId(conversationId);
        var handle = env.Client.GetWorkflowHandle(workflowId);
        var history = await handle.FetchHistoryAsync();

        await SaveHistoryAsync("pattern-3-with-tool.json", history);

        await host.StopAsync();
    }

    // ── Typed durable turn ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Captures the public typed workflow surface with a model step, one real tool activity,
    /// explicit state completion, and a final model response.
    /// </summary>
    [Fact]
    public async Task Capture_TypedDurableTurn()
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;
        var scripted = ScriptedChatClient.WithToolCallsThenFinal(
            [new FunctionCallContent(
                "typed-call-1",
                "state_tool",
                new Dictionary<string, object?> { ["value"] = "captured" })],
            "typed turn complete");
        var taskQueue = $"capture-typed-turn-{Guid.NewGuid():N}";
        using var host = BuildTypedTurnHost(env.Client, scripted, taskQueue);
        await host.StartAsync();

        var input = host.Services.GetRequiredService<IDurableChatWorkflowInputFactory>().Create();
        var workflowId = $"capture-typed-turn-{Guid.NewGuid():N}";
        var handle = await env.Client.StartWorkflowAsync(
            (TypedDurableTurnWorkflow workflow) => workflow.RunAsync(input),
            new WorkflowOptions(workflowId, taskQueue));
        var request = new DurableTurnRequest<TypedTurnRequestData, TypedTurnState>
        {
            Messages = [new ChatMessage(ChatRole.User, "capture typed turn")],
            RequestData = new TypedTurnRequestData("capture-operation"),
            InitialTurnState = new TypedTurnState(0, []),
            CorrelationId = "capture-correlation",
        };
        var result = await handle.ExecuteUpdateAsync(
            workflow => workflow.TurnAsync(request),
            new WorkflowUpdateOptions { Id = "capture-typed-update" });

        Assert.Equal(1, result.FinalTurnState!.Revision);
        var history = await handle.FetchHistoryAsync();
        await SaveHistoryAsync("typed-durable-turn-v1.json", history);

        await handle.SignalAsync(workflow => workflow.RequestShutdownAsync());
        await host.StopAsync();
    }

    // ── CAN transition ─────────────────────────────────────────────────────

    /// <summary>
    /// Captures the history of a workflow that crosses a continue-as-new boundary.
    /// Uses <c>MaxEntryCount=4</c> so CAN fires after 2 turns (each turn adds
    /// request + response = 2 entries).
    /// The history fetched is from the NEW run (post-CAN), demonstrating that
    /// replay works across a CAN boundary.
    /// </summary>
    [Fact]
    public async Task Capture_CanTransition()
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;

        var scripted = new ScriptedChatClient(
            Enumerable.Range(1, 20).Select(i => new ChatResponse(
                new ChatMessage(ChatRole.Assistant, $"response {i}"))));

        var taskQueue = $"capture-can-{Guid.NewGuid():N}";
        using var host = BuildCanHost(env.Client, scripted, taskQueue, maxEntryCount: 4);
        await host.StartAsync();

        var sessionClient = host.Services.GetRequiredService<DurableChatSessionClient>();
        var conversationId = $"cap-can-{Guid.NewGuid():N}";
        var workflowId = sessionClient.GetWorkflowId(conversationId);
        var handle = env.Client.GetWorkflowHandle<DurableChatWorkflow>(workflowId);

        // Turn 1
        await sessionClient.SendAsync(conversationId, [new ChatMessage(ChatRole.User, "turn 1")]);
        var initialRunId = (await handle.DescribeAsync()).RunId;

        // Drive until CAN fires (run ID changes). Check BEFORE each dispatch so we capture
        // the exact post-CAN run ID without landing an extra turn on it.
        string? postCanRunId = null;
        for (var i = 2; i <= 10; i++)
        {
            var rid = (await handle.DescribeAsync()).RunId;
            if (rid != initialRunId) { postCanRunId = rid; break; }

            try
            {
                await sessionClient.SendAsync(conversationId,
                    [new ChatMessage(ChatRole.User, $"turn {i}")]);
            }
            catch (Temporalio.Exceptions.WorkflowUpdateFailedException)
            {
                // CAN in flight — expected transient.
            }
        }
        // Catch the case where CAN fired while the last SendAsync was in flight.
        postCanRunId ??= (await handle.DescribeAsync()).RunId is var last && last != initialRunId
            ? last
            : null;

        Assert.True(postCanRunId is not null, "Expected MaxEntryCount-driven CAN to fire within 10 turns.");

        // Pin a handle to the exact post-CAN run so FetchHistoryAsync always queries that run,
        // not the run-less latest-resolving handle (which may resolve to a stale pre-CAN run
        // or a later CAN run in a race).
        var postCanHandle = env.Client.GetWorkflowHandle<DurableChatWorkflow>(workflowId, runId: postCanRunId);

        // Send one more turn so the new run has its own activity history (not just WorkflowStarted).
        // Retry if the new run is still starting up (WorkflowUpdateFailedException is transient here).
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            try
            {
                await sessionClient.SendAsync(conversationId,
                    [new ChatMessage(ChatRole.User, "turn after CAN")]);
                break;
            }
            catch (Temporalio.Exceptions.WorkflowUpdateFailedException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200));
            }
        }

        // Poll until the post-CAN run's history contains at least one ACTIVITY_TASK_COMPLETED —
        // this confirms the post-CAN run has dispatched and finished an activity, giving the
        // replay test a meaningful history to exercise. Use the pinned postCanHandle so we always
        // query the right run regardless of further CAN transitions.
        WorkflowHistory history;
        deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            history = await postCanHandle.FetchHistoryAsync();
            if (history.Events.Any(e => e.ActivityTaskCompletedEventAttributes is not null))
                break;
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException(
                    "Timed out waiting for ACTIVITY_TASK_COMPLETED in post-CAN history.");
            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }

        await SaveHistoryAsync("can-transition.json", history);

        await host.StopAsync();
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static async Task SaveHistoryAsync(string filename, WorkflowHistory history)
    {
        var dir = HistoriesDir;
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, filename);
        await File.WriteAllTextAsync(path, history.ToJson()).ConfigureAwait(false);
    }

    private static IHost BuildSimpleHost(ITemporalClient client, IChatClient chatClient, string taskQueue)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(client);
        builder.Services.AddChatClient(chatClient).Build();
        builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
            new NoopEmbeddingGenerator());

        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddDurableAI(opts =>
            {
                opts.ActivityTimeout = TimeSpan.FromSeconds(30);
                opts.HeartbeatTimeout = TimeSpan.FromSeconds(10);
                opts.SessionTimeToLive = TimeSpan.FromMinutes(5);
            });

        return builder.Build();
    }

    private static IHost BuildDirectMiddlewareHost(ITemporalClient client)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(client);
        builder.Services.AddSingleton<IChatClient>(new TestChatClient());
        // DurableChatClientWorkflow requests the keyed "capture" decorator when it
        // captures the compatibility metadata carried by this history fixture.
        builder.Services.AddKeyedSingleton<IChatClientDecorator>(
            "capture",
            new PassthroughChatClientDecorator());
        builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
            new NoopEmbeddingGenerator());

        builder.Services
            .AddHostedTemporalWorker(DurableChatClientWorkflow.TaskQueue)
            .AddDurableAI(options =>
            {
                options.ActivityTimeout = TimeSpan.FromSeconds(30);
                options.HeartbeatTimeout = TimeSpan.FromSeconds(10);
                options.SessionTimeToLive = TimeSpan.FromMinutes(5);
            })
            .AddWorkflow<DurableChatClientWorkflow>();

        return builder.Build();
    }

    private static IHost BuildPattern3Host(
        ITemporalClient client,
        string taskQueue,
        IChatClient chatClient,
        Action<ITemporalWorkerServiceOptionsBuilder> registerTools)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(client);
        builder.Services.AddChatClient(chatClient).Build();
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

    private static IHost BuildCanHost(
        ITemporalClient client,
        IChatClient chatClient,
        string taskQueue,
        int maxEntryCount)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(client);
        builder.Services.AddChatClient(chatClient).Build();
        builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
            new NoopEmbeddingGenerator());

        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddDurableAI(opts =>
            {
                opts.MaxEntryCount = maxEntryCount;
                opts.ActivityTimeout = TimeSpan.FromSeconds(30);
                opts.HeartbeatTimeout = TimeSpan.FromSeconds(10);
                opts.SessionTimeToLive = TimeSpan.FromMinutes(5);
            });

        return builder.Build();
    }

    private static IHost BuildTypedTurnHost(
        ITemporalClient client,
        IChatClient chatClient,
        string taskQueue)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(client);
        builder.Services.AddChatClient(chatClient).Build();
        builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
            new NoopEmbeddingGenerator());
        var worker = builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddDurableAI(options =>
            {
                options.RegisterDefaultWorkflow = false;
                options.ActivityTimeout = TimeSpan.FromSeconds(30);
            })
            .AddWorkflow<TypedDurableTurnWorkflow>();
        var declaration = AIFunctionFactory.Create(
            (string value) => string.Empty,
            "state_tool",
            "Updates captured typed state.").AsDeclarationOnly();
        worker.AddDurableTool<TypedTurnRequestData, TypedTurnState>(
            declaration,
            (_, context) => new DurableToolActivation<TypedTurnState>
            {
                Function = AIFunctionFactory.Create(
                    (string value) => value,
                    "state_tool",
                    "Updates captured typed state."),
                CompleteState = (_, _) => ValueTask.FromResult(
                    DurableStateUpdate<TypedTurnState>.Replace(
                        new TypedTurnState(
                            (context.TurnState?.Revision ?? 0) + 1,
                            ["captured"]))),
            });
        return builder.Build();
    }

    private sealed class PassthroughChatClientDecorator : IChatClientDecorator
    {
        public IChatClient Decorate(IChatClient inner, ChatOptions? options) => inner;
    }

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
