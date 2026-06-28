using System.Collections.Concurrent;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Client;
using TemporalCommunity.Extensions.Agents.IntegrationTests.Helpers;
using TemporalCommunity.Extensions.Agents.Session;
using TemporalCommunity.Extensions.Agents.Tests.StepMode;
using TemporalCommunity.Extensions.Agents.Tools;
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
}
