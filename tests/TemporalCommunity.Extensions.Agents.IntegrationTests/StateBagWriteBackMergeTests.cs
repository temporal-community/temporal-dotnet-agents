using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Client;
using TemporalCommunity.Extensions.Agents.IntegrationTests.Helpers;
using TemporalCommunity.Extensions.Agents.Scheduling;
using TemporalCommunity.Extensions.Agents.Session;
using TemporalCommunity.Extensions.Agents.Tests.StepMode;
using TemporalCommunity.Extensions.Agents.Tools;
using TemporalCommunity.Extensions.Agents.Workflows;
using TemporalCommunity.Extensions.AI;
using TemporalCommunity.Extensions.AI.Tools;
using Temporalio.Testing;
using Xunit;
using Xunit.Abstractions;

namespace TemporalCommunity.Extensions.Agents.IntegrationTests;

/// <summary>
/// X-2 and X-1 — workflow-level merge of tool/interceptor StateBag write-backs.
///
/// <list type="bullet">
/// <item><b>X-2:</b> an interceptor that writes a non-reserved key to its StateBag has it merged into
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

    // ── X-2: interceptor non-reserved write-back merged before tool dispatch ─────

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
            // to the SAME key. Step 2: final (the provider on step 2 reads the merged value).
            new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [
                new FunctionCallContent("c0", toolName, new Dictionary<string, object?> { ["val"] = "A" }),
                new FunctionCallContent("c1", toolName, new Dictionary<string, object?> { ["val"] = "B" }),
            ])),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done.")),
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

            // The provider on step 2 reads the merged conflict.key. Tool-call index 1 ("B")
            // must win over index 0 ("A") regardless of completion order — deterministic.
            var observedOnStep2 = provider.LastObservedValue;
            _output.WriteLine($"Step-2 provider observed conflict.key = {observedOnStep2}");
            Assert.Equal("B", observedOnStep2);
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
    public async Task QueuedFailedTurn_DoesNotRestoreStateFromBeforePrecedingSuccessfulTurn()
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

        var taskQueue = $"queued-failed-turn-statebag-{Guid.NewGuid():N}";
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

            // Hold turn A inside its model activity after its provider has written the value that
            // will be committed. Queue turn B and wait until Temporal accepts it; its handler has
            // then run synchronously to the serialized turn gate. This reproduces the old stale-
            // snapshot window without timing delays.
            var firstTurn = proxy.RunAsync("commit", session);
            await blockingClient.FirstCallStarted.WaitAsync(TimeSpan.FromSeconds(10));

            var workflowHandle = Env.Client.GetWorkflowHandle<AgentWorkflow>(
                session.SessionId.WorkflowId);
            var queuedTurn = await workflowHandle.StartUpdateAsync<AgentWorkflow, AgentResponse>(
                wf => wf.RunAgentAsync(new RunRequest("fail")
                {
                    CorrelationId = "queued-failed-turn",
                }),
                new WorkflowUpdateStartOptions(WorkflowUpdateStage.Accepted));

            blockingClient.ReleaseFirstCall();

            var firstResponse = await firstTurn;
            Assert.Equal("First turn completed.", firstResponse.Messages[^1].Text);
            await Assert.ThrowsAnyAsync<Exception>(() => queuedTurn.GetResultAsync());

            var finalResponse = await proxy.RunAsync("observe", session);

            Assert.Equal("Observed restored state.", finalResponse.Messages[^1].Text);
            Assert.True(provider.ThirdTurnObservedCommittedState,
                "The successful preceding turn's committed StateBag value must survive rollback of a queued turn.");
            Assert.False(provider.ThirdTurnObservedFailedState,
                "The queued failed turn's StateBag value must be rolled back.");
        }
        finally
        {
            blockingClient.ReleaseFirstCall();
            await host.StopAsync();
        }
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

    /// <summary>Context provider that seeds a fixed key on every LLM call (so downstream bags are non-null).</summary>
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

    /// <summary>Context provider that records the value it reads for a key on each LLM call.</summary>
    private sealed class ConflictKeyReadingProvider(string key) : AIContextProvider
    {
        public string? LastObservedValue { get; private set; }

        protected override ValueTask<AIContext> ProvideAIContextAsync(
            InvokingContext context, CancellationToken cancellationToken = default)
        {
            if (context.Session is TemporalAgentSession s
                && s.StateBag.TryGetValue<string>(key, out var v,
                    System.Text.Json.JsonSerializerOptions.Default))
            {
                LastObservedValue = v;
            }
            return new ValueTask<AIContext>(new AIContext());
        }
    }

    /// <summary>Interceptor that proceeds, writing one non-reserved key to its StateBag.</summary>
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
            if (call == 1)
            {
                session.StateBag.SetValue(
                    key,
                    "must-be-rolled-back",
                    System.Text.Json.JsonSerializerOptions.Default);
            }
            else if (call == 2)
            {
                SecondTurnObservedFailedState = session.StateBag.TryGetValue<string>(
                    key,
                    out _,
                    System.Text.Json.JsonSerializerOptions.Default);
            }

            return new ValueTask<AIContext>(new AIContext());
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
