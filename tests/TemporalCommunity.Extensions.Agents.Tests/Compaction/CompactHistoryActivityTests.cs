#pragma warning disable TA002 // compaction surface is experimental

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using TemporalCommunity.Extensions.AI;
using TemporalCommunity.Extensions.AI.Session;
using TemporalCommunity.Extensions.Agents.Compaction;
using TemporalCommunity.Extensions.Agents.State;
using TemporalCommunity.Extensions.Agents.Tests.HistoryStore;
using TemporalCommunity.Extensions.Agents.Workflows;
using Temporalio.Testing;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.Tests.Compaction;

/// <summary>
/// Step 6d activity-level tests. Exercises the end-to-end compaction flow without spinning
/// up a Temporal server: ComposeDurableAgent → strategy resolution → CompactHistory dispatch
/// → strategy.CompactAsync → store.AppendAsync.
/// </summary>
public class CompactHistoryActivityTests
{
    [Fact]
    public async Task CompactHistory_WithTruncationStrategy_AppendsMarkerToStore()
    {
        var store = new FakeAgentHistoryStore();
        var (activities, _, _) = BuildHarness(store, configure: opts =>
        {
            opts.AddDurableAgent("Agent1", b =>
            {
                b.ChatClient = _ => new StubChat();
                b.CompactionStrategyKey = TruncationCompactionStrategy.Key;
                b.HistoryStore = _ => store;
            });
        });

        // Seed 15 source entries.
        var sessionId = "session-1";
        var seeded = new List<DurableSessionEntry>();
        for (int i = 0; i < 15; i++)
        {
            seeded.Add(new AgentSessionRequest
            {
                CorrelationId = $"entry-{i}",
                CreatedAt = DateTimeOffset.UtcNow,
                Messages = Array.Empty<ChatMessage>(),
            });
        }
        store.Seed(sessionId, seeded);

        var input = new CompactHistoryInput
        {
            AgentName = "Agent1",
            SessionId = sessionId,
            TargetMessageIds = new[] { "entry-0", "entry-1", "entry-2" },
            MarkerCorrelationId = "marker-deterministic-1",
        };

        var env = new ActivityEnvironment();
        await env.RunAsync(() => activities.CompactHistoryAsync(input));

        // Marker was appended.
        Assert.Equal(1, store.AppendCount);
        var snapshot = store.Snapshot(sessionId);
        var marker = Assert.IsType<CompactionMarkerEntry>(snapshot[^1]);
        Assert.Equal("marker-deterministic-1", marker.CorrelationId);
        Assert.Equal(TruncationCompactionStrategy.Key, marker.Strategy);
        Assert.Equal(new[] { "entry-0", "entry-1", "entry-2" }, marker.CompactedMessageIds);
    }

    [Fact]
    public async Task CompactHistory_WithSummarizationStrategy_AppendsSummaryMarker()
    {
        var store = new FakeAgentHistoryStore();
        var chat = new StubChat(reply: "Rolled up the conversation.");
        var (activities, _, _) = BuildHarness(store, configure: opts =>
        {
            opts.AddDurableAgent("Agent2", b =>
            {
                b.ChatClient = _ => chat;
                b.CompactionStrategyKey = SummarizationCompactionStrategy.Key;
                b.HistoryStore = _ => store;
            });
        });

        var sessionId = "session-2";
        store.Seed(sessionId, new DurableSessionEntry[]
        {
            new AgentSessionRequest
            {
                CorrelationId = "old-1",
                CreatedAt = DateTimeOffset.UtcNow,
                Messages = new[] { new ChatMessage(ChatRole.User, "Older user message") },
            },
            new AgentSessionResponse
            {
                CorrelationId = "old-2",
                CreatedAt = DateTimeOffset.UtcNow,
                Messages = new[] { new ChatMessage(ChatRole.Assistant, "Older agent reply") },
            },
        });

        var input = new CompactHistoryInput
        {
            AgentName = "Agent2",
            SessionId = sessionId,
            TargetMessageIds = new[] { "old-1", "old-2" },
            MarkerCorrelationId = "marker-sum-1",
        };

        var env = new ActivityEnvironment();
        await env.RunAsync(() => activities.CompactHistoryAsync(input));

        var snapshot = store.Snapshot(sessionId);
        var marker = Assert.IsType<CompactionMarkerEntry>(snapshot[^1]);
        Assert.Equal(SummarizationCompactionStrategy.Key, marker.Strategy);
        Assert.Single(marker.Messages);
        Assert.Equal("Rolled up the conversation.", marker.Messages[0].Text);
    }

    [Fact]
    public async Task CompactHistory_WhenAgentHasNoStrategy_Throws()
    {
        var store = new FakeAgentHistoryStore();
        var (activities, _, _) = BuildHarness(store, configure: opts =>
        {
            opts.AddDurableAgent("NoCompaction", b =>
            {
                b.ChatClient = _ => new StubChat();
                b.HistoryStore = _ => store;
                // No CompactionStrategyKey.
            });
        });

        var input = new CompactHistoryInput
        {
            AgentName = "NoCompaction",
            SessionId = "session",
            TargetMessageIds = new[] { "x" },
            MarkerCorrelationId = "marker-x",
        };

        var env = new ActivityEnvironment();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            env.RunAsync(() => activities.CompactHistoryAsync(input)));
    }

    [Fact]
    public async Task CompactHistory_WhenAgentHasNoStore_Throws()
    {
        // Compaction requires an external history store — markers have nowhere to live
        // without one. Verify the activity fails loudly.
        var (activities, _, _) = BuildHarness(historyStore: null, configure: opts =>
        {
            opts.AddDurableAgent("Storeless", b =>
            {
                b.ChatClient = _ => new StubChat();
                b.CompactionStrategyKey = TruncationCompactionStrategy.Key;
                // No HistoryStore.
            });
        });

        var input = new CompactHistoryInput
        {
            AgentName = "Storeless",
            SessionId = "session",
            TargetMessageIds = new[] { "x" },
            MarkerCorrelationId = "marker-y",
        };

        var env = new ActivityEnvironment();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            env.RunAsync(() => activities.CompactHistoryAsync(input)));
    }

    [Fact]
    public void Compose_WithUnregisteredStrategyKey_FailsFast()
    {
        // Q12-adjacent: an agent configured with a strategy key that has no matching
        // ICompactionStrategy registration fails loudly at compose time rather than at the
        // first compaction dispatch (which might be days later).
        var store = new FakeAgentHistoryStore();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            BuildHarness(store, configure: opts =>
            {
                opts.AddDurableAgent("BogusAgent", b =>
                {
                    b.ChatClient = _ => new StubChat();
                    b.CompactionStrategyKey = "nonexistent-strategy";
                    b.HistoryStore = _ => store;
                });
            }, dispatchOnce: true));

        Assert.Contains("nonexistent-strategy", ex.Message);
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    private static (AgentActivities Activities, IServiceProvider Sp, TemporalAgentsOptions Options)
        BuildHarness(
            FakeAgentHistoryStore? historyStore,
            Action<TemporalAgentsOptions> configure,
            bool dispatchOnce = false)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new DurableExecutionOptions { TaskQueue = "test" });
        var options = new TemporalAgentsOptions();
        configure(options);

        // Pre-register built-in strategies + the options singleton — mimics what
        // TemporalAgentsRegistrar.Register does.
        TemporalAgentsRegistrar.Register(services, builder: null, options);

        var sp = services.BuildServiceProvider();
        var activities = new AgentActivities(sp);

        if (dispatchOnce)
        {
            // Force a compose by dispatching CompactHistory — surfaces the
            // unregistered-strategy error eagerly.
            var firstAgent = options.GetRegisteredAgentNames().First();
            var input = new CompactHistoryInput
            {
                AgentName = firstAgent,
                SessionId = "trigger-compose",
                TargetMessageIds = new[] { "x" },
                MarkerCorrelationId = "m",
            };
            new ActivityEnvironment().RunAsync(() => activities.CompactHistoryAsync(input)).GetAwaiter().GetResult();
        }

        return (activities, sp, options);
    }

    private sealed class StubChat : IChatClient
    {
        private readonly string _reply;
        public StubChat(string? reply = null) { _reply = reply ?? "ok"; }

        public void Dispose() { }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, _reply)]));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            AsyncEnumerable.Empty<ChatResponseUpdate>();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }
}
