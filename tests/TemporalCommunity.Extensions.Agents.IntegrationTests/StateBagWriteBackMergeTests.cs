using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Client;
using Temporalio.Common;
using TemporalCommunity.Extensions.Agents.IntegrationTests.Helpers;
using TemporalCommunity.Extensions.Agents.Session;
using TemporalCommunity.Extensions.Agents.Tests.StepMode;
using TemporalCommunity.Extensions.Agents.Tools;
using TemporalCommunity.Extensions.Agents.Workflows;
using TemporalCommunity.Extensions.AI;
using TemporalCommunity.Extensions.AI.Tools;
using Temporalio.Testing;
using Temporalio.Worker;
using Xunit;
using Xunit.Abstractions;

namespace TemporalCommunity.Extensions.Agents.IntegrationTests;

/// <summary>
/// X-2 and X-1 — workflow-level merge of tool/interceptor StateBag write-backs.
///
/// <list type="bullet">
/// <item><b>X-2:</b> an interceptor that writes a flat key to its StateBag has it merged into
/// the workflow bag BEFORE tool dispatch — the tool dispatched in the same step observes the
/// interceptor's value.</item>
/// <item><b>X-1:</b> two concurrent tool calls in one step that write the SAME StateBag key are merged
/// in tool-call index order — the later index wins on conflict, deterministically. The next step's
/// context provider observes the later-index value.</item>
/// </list>
/// </summary>
[Trait("Category", "Integration")]
public class StateBagWriteBackMergeTests : IClassFixture<StateBagWriteBackMergeTests.Fixture>
{
    private readonly Fixture _fixture;
    private readonly ITestOutputHelper _output;
    private WorkflowEnvironment Env => _fixture.Environment;

    public StateBagWriteBackMergeTests(Fixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    // ── X-2: interceptor write-back merged before tool dispatch ───────────────

    [Fact]
    public async Task InterceptorWriteBack_NonReservedKey_VisibleToToolInSameStep()
    {
        const string toolName = "reader_tool";
        var scripted = new ScriptedChatClient(
        [
            // Step 1: one tool call. The provider seeds the StateBag (so the interceptor's bag is
            // non-null), the interceptor adds "interceptor.value", then the tool reads it back.
            new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("c1", toolName, new Dictionary<string, object?>())])),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done.")),
        ]);

        var toolReads = new ConcurrentQueue<string>();

        var tool = AIFunctionFactory.Create(
            () =>
            {
                var bag = TemporalAgentContext.Current.CurrentSession.StateBag;
                var seen = bag.TryGetValue<string>("interceptor.value", out var v,
                    System.Text.Json.JsonSerializerOptions.Default) ? v : "<absent>";
                toolReads.Enqueue(seen ?? "<null>");
                return seen ?? "<null>";
            },
            new AIFunctionFactoryOptions { Name = toolName });

        var taskQueue = $"x2-interceptor-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(Env.Client);
        builder.Services.AddSingleton<IChatClient>(scripted);
        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddTemporalAgents(opts =>
            {
                opts.AddDurableAgent("X2Agent", agent =>
                {
                    agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
                    agent.AddTool(tool);
                    // Seed the StateBag so the interceptor receives a non-null bag to mutate.
                    agent.AddContextProvider(new SeedingProvider("seed.key", "seed"));
                    agent.AddToolInterceptor(_ => new WritingInterceptor("interceptor.value", "from-interceptor"));
                });
            });

        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("X2Agent");
            var session = (TemporalAgentSession)await proxy.CreateSessionAsync();
            await proxy.RunAsync("go", session);

            Assert.True(toolReads.TryDequeue(out var observed));
            _output.WriteLine($"Tool observed interceptor value: {observed}");
            Assert.Equal("from-interceptor", observed);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    // ── X-1: concurrent tool write-backs merge in index order, later wins ────────

    [Fact]
    public async Task ConcurrentToolWriteBacks_LaterIndexWins_OnConflict()
    {
        const string toolName = "writer_tool";
        var scripted = new ScriptedChatClient(
        [
            // Step 1: TWO tool calls in the same step. index 0 writes "A", index 1 writes "B"
            // to the SAME key. The terminal provider callback reads the merged value.
            new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [
                new FunctionCallContent("c0", toolName, new Dictionary<string, object?> { ["val"] = "A" }),
                new FunctionCallContent("c1", toolName, new Dictionary<string, object?> { ["val"] = "B" }),
            ])),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done.")),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Second turn complete.")),
        ]);

        var provider = new ConflictKeyReadingProvider("conflict.key");

        // Tool writes its "val" argument to the shared conflict key in its StateBag write-back.
        var tool = AIFunctionFactory.Create(
            ([System.ComponentModel.Description("value")] string val) =>
            {
                var bag = TemporalAgentContext.Current.CurrentSession.StateBag;
                bag.SetValue("conflict.key", val, System.Text.Json.JsonSerializerOptions.Default);
                return $"wrote {val}";
            },
            new AIFunctionFactoryOptions { Name = toolName });

        var taskQueue = $"x1-conflict-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(Env.Client);
        builder.Services.AddSingleton<IChatClient>(scripted);
        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddTemporalAgents(opts =>
            {
                opts.AddDurableAgent("X1Agent", agent =>
                {
                    agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
                    agent.AddTool(tool);
                    agent.AddContextProvider(provider);
                });
            });

        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("X1Agent");
            var session = (TemporalAgentSession)await proxy.CreateSessionAsync();
            await proxy.RunAsync("go", session);

            // Tool-call index 1 ("B") must win over index 0 ("A") regardless of completion
            // order — deterministic. The terminal callback sees the workflow-committed value.
            _output.WriteLine($"Terminal provider callback observed conflict.key = {provider.LastObservedValue}");
            Assert.Equal("B", provider.LastObservedValue);

            await proxy.RunAsync("second turn", session);
            Assert.True(provider.NextTurnObservedCompletionState,
                "Successful InvokedAsync StateBag changes must be committed to the next turn.");
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task FailedTurn_ContextProviderStateBagWrite_DoesNotLeakIntoNextTurn()
    {
        const string toolName = "failing_tool";
        const string failedTurnKey = "failed-turn.provider-state";
        var scripted = new ScriptedChatClient(
        [
            new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("fail-1", toolName, new Dictionary<string, object?>())])),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Recovered.")),
        ]);
        var provider = new FailedTurnWritingProvider(failedTurnKey);
        var tool = AIFunctionFactory.Create(
            FailTool,
            new AIFunctionFactoryOptions { Name = toolName });

        var taskQueue = $"failed-turn-statebag-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(Env.Client);
        builder.Services.AddSingleton<IChatClient>(scripted);
        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddTemporalAgents(opts =>
            {
                opts.AddDurableAgent("FailedTurnStateAgent", agent =>
                {
                    agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
                    agent.AddContextProvider(provider);
                    agent.AddTool(tool, toolOptions => toolOptions.NoRetry());
                });
            });

        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("FailedTurnStateAgent");
            var session = (TemporalAgentSession)await proxy.CreateSessionAsync();

            await Assert.ThrowsAnyAsync<Exception>(() => proxy.RunAsync("fail", session));

            var response = await proxy.RunAsync("recover", session);

            Assert.Equal("Recovered.", response.Messages[^1].Text);
            Assert.False(provider.SecondTurnObservedFailedState,
                "StateBag changes from a failed turn must not be visible to the next turn.");
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task FailedFireAndForgetTurn_StateBagWrite_DoesNotLeakIntoNextUpdate()
    {
        const string toolName = "fire_and_forget_failing_tool";
        const string failedTurnKey = "fire-and-forget.failed-state";
        var scripted = new ScriptedChatClient(
        [
            new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("fire-and-forget-fail", toolName,
                    new Dictionary<string, object?>())])),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Recovered after background failure.")),
        ]);
        var provider = new FailedTurnWritingProvider(failedTurnKey);
        var toolInvoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tool = AIFunctionFactory.Create(
            () =>
            {
                toolInvoked.TrySetResult();
                return FailTool();
            },
            new AIFunctionFactoryOptions { Name = toolName });

        var taskQueue = $"failed-fire-and-forget-statebag-{Guid.NewGuid():N}";
        const string failedRequestId = "failed-fire-and-forget-statebag";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(Env.Client);
        builder.Services.AddSingleton<IChatClient>(scripted);
        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddTemporalAgents(opts =>
            {
                opts.AddDurableAgent("FailedFireAndForgetStateAgent", agent =>
                {
                    agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
                    agent.AddContextProvider(provider);
                    agent.AddTool(tool, toolOptions => toolOptions.NoRetry());
                });
            });

        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("FailedFireAndForgetStateAgent");
            var session = (TemporalAgentSession)await proxy.CreateSessionAsync();

            var fireAndForgetResponse = await proxy.RunAsync(
                "fail in the background",
                session,
                new TemporalAgentRunOptions
                {
                    IsFireAndForget = true,
                    RequestId = failedRequestId,
                });
            Assert.Empty(fireAndForgetResponse.Messages);

            // The admission validator rejects a different request while this turn is active.
            await toolInvoked.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var workflow = Env.Client.GetWorkflowHandle<AgentWorkflow>(session.SessionId.WorkflowId);
            DurableChatTerminalOutcome? terminalOutcome = null;
            var terminalDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < terminalDeadline && terminalOutcome is null)
            {
                terminalOutcome = await workflow.QueryAsync<AgentWorkflow, DurableChatTerminalOutcome?>(
                    wf => wf.GetTerminalTurnOutcome(failedRequestId));
                if (terminalOutcome is null)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(50));
                }
            }

            Assert.NotNull(terminalOutcome);
            Assert.NotNull(terminalOutcome.FailureErrorType);
            var response = await proxy.RunAsync("recover", session)
                .WaitAsync(TimeSpan.FromSeconds(15));

            Assert.Equal("Recovered after background failure.", response.Messages[^1].Text);
            Assert.False(provider.SecondTurnObservedFailedState,
                "StateBag changes from a failed fire-and-forget turn must not reach the next Update.");
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public Task FailedTurn_DoesNotRestoreStateFromBeforePrecedingSuccessfulTurn() =>
        RunFailedTurnScenarioAsync(captureHistory: false);

    [Fact]
    [Trait("Category", "HistoryCapture")]
    public Task Capture_FailedTurnStateBagRollbackHistory() =>
        RunFailedTurnScenarioAsync(captureHistory: true);

    private async Task RunFailedTurnScenarioAsync(bool captureHistory)
    {
        const string toolName = "queued_failing_tool";
        const string committedKey = "concurrent-turn.committed";
        const string failedTurnKey = "concurrent-turn.failed";
        var scripted = new ScriptedChatClient(
        [
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "First turn completed.")),
            new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("fail-queued", toolName, new Dictionary<string, object?>())])),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Observed restored state.")),
        ]);
        var blockingClient = new FirstCallBlockingChatClient(scripted);
        var provider = new QueuedTurnStateProvider(committedKey, failedTurnKey);
        var tool = AIFunctionFactory.Create(
            FailTool,
            new AIFunctionFactoryOptions { Name = toolName });

        var taskQueue = $"failed-turn-statebag-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(Env.Client);
        builder.Services.AddSingleton<IChatClient>(blockingClient);
        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddTemporalAgents(opts =>
            {
                opts.AddDurableAgent("QueuedFailedTurnStateAgent", agent =>
                {
                    agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
                    agent.AddContextProvider(provider);
                    agent.AddTool(tool, toolOptions => toolOptions.NoRetry());
                });
            });

        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("QueuedFailedTurnStateAgent");
            var session = (TemporalAgentSession)await proxy.CreateSessionAsync();

            // Hold the first model activity until its context provider has written the value that
            // will be committed, then let that turn finish before starting the failing turn.
            var firstTurn = proxy.RunAsync("commit", session);
            await blockingClient.FirstCallStarted.WaitAsync(TimeSpan.FromSeconds(10));
            blockingClient.ReleaseFirstCall();

            var firstResponse = await firstTurn;
            Assert.Equal("First turn completed.", firstResponse.Messages[^1].Text);
            var workflowHandle = Env.Client.GetWorkflowHandle<AgentWorkflow>(
                session.SessionId.WorkflowId);
            await Assert.ThrowsAnyAsync<Exception>(() => proxy.RunAsync("fail", session));

            var finalResponse = await proxy.RunAsync("observe", session);

            Assert.Equal("Observed restored state.", finalResponse.Messages[^1].Text);
            Assert.True(provider.ThirdTurnObservedCommittedState,
                "The successful preceding turn's committed StateBag value must survive rollback of a queued turn.");
            Assert.False(provider.ThirdTurnObservedFailedState,
                "The queued failed turn's StateBag value must be rolled back.");

            var history = await workflowHandle.FetchHistoryAsync();
            var replayOptions = new WorkflowReplayerOptions
            {
                DataConverter = TemporalAgentDataConverter.Instance,
            };
            replayOptions.AddWorkflow(AgentWorkflowWorkerFactory.CreateDefinition(
                host.Services.GetRequiredService<TemporalAgentsOptions>(),
                taskQueue));
            var replay = await new WorkflowReplayer(replayOptions)
                .ReplayWorkflowAsync(history, throwOnReplayFailure: false);
            Assert.Null(replay.ReplayFailure);

            if (captureHistory)
            {
                await SaveAgentHistoryAsync("failed-turn-statebag-rollback.json", history);
            }
        }
        finally
        {
            blockingClient.ReleaseFirstCall();
            await host.StopAsync();
        }
    }

    private static async Task SaveAgentHistoryAsync(string filename, WorkflowHistory history)
    {
        var directory = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..", "..", "..", "..",
                "TemporalCommunity.Extensions.Agents.Tests",
                "Compat",
                "Histories"));
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, filename), history.ToJson());
    }

    public sealed class Fixture : IAsyncLifetime
    {
        public WorkflowEnvironment Environment { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            Environment = await TestEnvironmentHelper.StartLocalAsync();
            Environment.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;
        }

        public Task DisposeAsync() => Environment.ShutdownAsync();
    }

    /// <summary>Context provider that seeds a fixed key once before the logical turn.</summary>
    private sealed class SeedingProvider(string key, string value) : AIContextProvider
    {
        protected override ValueTask<AIContext> ProvideAIContextAsync(
            InvokingContext context, CancellationToken cancellationToken = default)
        {
            if (context.Session is TemporalAgentSession s)
                s.StateBag.SetValue(key, value, System.Text.Json.JsonSerializerOptions.Default);
            return new ValueTask<AIContext>(new AIContext());
        }
    }

    /// <summary>Context provider that records a key from the terminal workflow-owned StateBag.</summary>
    private sealed class ConflictKeyReadingProvider(string key) : AIContextProvider
    {
        public string? LastObservedValue { get; private set; }

        public bool NextTurnObservedCompletionState { get; private set; }

        protected override ValueTask<AIContext> ProvideAIContextAsync(
            InvokingContext context, CancellationToken cancellationToken = default)
        {
            if (context.Session is TemporalAgentSession session
                && Volatile.Read(ref _seenTerminalCallback) != 0
                && session.StateBag.TryGetValue<string>(
                    "provider.completion",
                    out _,
                    System.Text.Json.JsonSerializerOptions.Default))
            {
                NextTurnObservedCompletionState = true;
            }

            return new(new AIContext());
        }

        private int _seenTerminalCallback;

        protected override ValueTask InvokedCoreAsync(
            InvokedContext context, CancellationToken cancellationToken = default)
        {
            if (context.Session is TemporalAgentSession session
                && session.StateBag.TryGetValue<string>(key, out var v,
                    System.Text.Json.JsonSerializerOptions.Default))
            {
                LastObservedValue = v;
            }

            if (context.InvokeException is null && context.Session is TemporalAgentSession successfulSession)
            {
                successfulSession.StateBag.SetValue(
                    "provider.completion",
                    "committed",
                    System.Text.Json.JsonSerializerOptions.Default);
                Interlocked.Exchange(ref _seenTerminalCallback, 1);
            }

            return default;
        }
    }

    /// <summary>Interceptor that proceeds, writing one flat key to its StateBag.</summary>
    private sealed class WritingInterceptor(string key, string value) : IAgentToolInterceptor
    {
        public Task<DurableToolDecision> BeforeToolCallAsync(
            AgentToolContext context, CancellationToken cancellationToken = default)
        {
            context.StateBag?.SetValue(key, value, System.Text.Json.JsonSerializerOptions.Default);
            return Task.FromResult(DurableToolDecision.Proceed());
        }
    }

    private sealed class FailedTurnWritingProvider(string key) : AIContextProvider
    {
        private int _callCount;

        public bool SecondTurnObservedFailedState { get; private set; }

        protected override ValueTask<AIContext> ProvideAIContextAsync(
            InvokingContext context, CancellationToken cancellationToken = default)
        {
            if (context.Session is not TemporalAgentSession session)
                return new ValueTask<AIContext>(new AIContext());

            var call = Interlocked.Increment(ref _callCount);
            if (call == 2)
            {
                SecondTurnObservedFailedState = session.StateBag.TryGetValue<string>(
                    key,
                    out _,
                    System.Text.Json.JsonSerializerOptions.Default);
            }

            return new ValueTask<AIContext>(new AIContext());
        }

        protected override ValueTask InvokedCoreAsync(
            InvokedContext context,
            CancellationToken cancellationToken = default)
        {
            if (context.InvokeException is not null
                && context.Session is TemporalAgentSession session
                && Volatile.Read(ref _callCount) == 1)
            {
                session.StateBag.SetValue(
                    key,
                    "must-be-rolled-back",
                    System.Text.Json.JsonSerializerOptions.Default);
            }

            return default;
        }
    }

    private sealed class QueuedTurnStateProvider(string committedKey, string failedTurnKey) : AIContextProvider
    {
        private int _callCount;

        public bool ThirdTurnObservedCommittedState { get; private set; }

        public bool ThirdTurnObservedFailedState { get; private set; }

        protected override ValueTask<AIContext> ProvideAIContextAsync(
            InvokingContext context, CancellationToken cancellationToken = default)
        {
            if (context.Session is not TemporalAgentSession session)
                return new ValueTask<AIContext>(new AIContext());

            switch (Interlocked.Increment(ref _callCount))
            {
                case 1:
                    session.StateBag.SetValue(
                        committedKey,
                        "committed",
                        System.Text.Json.JsonSerializerOptions.Default);
                    break;
                case 2:
                    session.StateBag.SetValue(
                        failedTurnKey,
                        "must-be-rolled-back",
                        System.Text.Json.JsonSerializerOptions.Default);
                    break;
                case 3:
                    ThirdTurnObservedCommittedState = session.StateBag.TryGetValue<string>(
                        committedKey,
                        out var committed,
                        System.Text.Json.JsonSerializerOptions.Default)
                        && committed == "committed";
                    ThirdTurnObservedFailedState = session.StateBag.TryGetValue<string>(
                        failedTurnKey,
                        out _,
                        System.Text.Json.JsonSerializerOptions.Default);
                    break;
            }

            return new ValueTask<AIContext>(new AIContext());
        }
    }

    private sealed class FirstCallBlockingChatClient(IChatClient innerClient)
        : DelegatingChatClient(innerClient)
    {
        private readonly TaskCompletionSource _firstCallStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseFirstCall =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;

        public Task FirstCallStarted => _firstCallStarted.Task;

        public void ReleaseFirstCall() => _releaseFirstCall.TrySetResult();

        public override async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            await WaitForFirstCallReleaseAsync(cancellationToken);
            return await base.GetResponseAsync(messages, options, cancellationToken);
        }

        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await WaitForFirstCallReleaseAsync(cancellationToken);
            await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
            {
                yield return update;
            }
        }

        private async Task WaitForFirstCallReleaseAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _callCount) != 1)
                return;

            _firstCallStarted.TrySetResult();
            await _releaseFirstCall.Task.WaitAsync(cancellationToken);
        }
    }

    private static string FailTool() => throw new InvalidOperationException("Expected tool failure.");
}
