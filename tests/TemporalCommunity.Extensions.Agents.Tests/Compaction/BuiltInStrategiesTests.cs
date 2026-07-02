#pragma warning disable TA002 // strategies are experimental but referenced by name in tests

using System.Runtime.CompilerServices;
using FakeItEasy;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Temporalio.Client;
using TemporalCommunity.Extensions.AI;
using TemporalCommunity.Extensions.AI.Session;
using TemporalCommunity.Extensions.Agents.Compaction;
using TemporalCommunity.Extensions.Agents.State;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.Tests.Compaction;

/// <summary>
/// Step 6c tests: pin behavior of the 3 built-in strategies + the DI pre-registration
/// contract.
/// </summary>
public class BuiltInStrategiesTests
{
    // =========================================================================
    // TruncationCompactionStrategy
    // =========================================================================

    [Fact]
    public void Truncation_BelowThreshold_DoesNotFire()
    {
        var strategy = new TruncationCompactionStrategy(triggerEntryCount: 10, keepRecentCount: 4);
        var history = MakeHistory(8);
        Assert.Null(strategy.EvaluateTrigger(history));
    }

    [Fact]
    public void Truncation_AboveThreshold_TargetsAllBeyondRecentWindow()
    {
        // 15 entries, threshold 10, keep recent 4 → compact 11 oldest.
        var strategy = new TruncationCompactionStrategy(triggerEntryCount: 10, keepRecentCount: 4);
        var history = MakeHistory(15);

        var targets = strategy.EvaluateTrigger(history);

        Assert.NotNull(targets);
        Assert.Equal(11, targets!.Count);
        Assert.Equal("entry-0", targets[0]);
        Assert.Equal("entry-10", targets[10]);
    }

    [Fact]
    public void Truncation_SkipsPreExistingMarkers_FromTargets()
    {
        // History: 3 markers + 12 sources → 15 total, threshold 10, keep 4 → compact range
        // is index 0..10. Of those 11 entries, 3 are markers and should NOT appear in the
        // target list (re-compacting a marker is undefined).
        var strategy = new TruncationCompactionStrategy(triggerEntryCount: 10, keepRecentCount: 4);
        var history = new List<DurableSessionEntry>();
        for (int i = 0; i < 15; i++)
        {
            if (i % 5 == 0)
            {
                history.Add(MakeMarker($"marker-{i}", Array.Empty<string>()));
            }
            else
            {
                history.Add(MakeSource($"entry-{i}"));
            }
        }

        var targets = strategy.EvaluateTrigger(history);

        Assert.NotNull(targets);
        Assert.DoesNotContain(targets, id => id.StartsWith("marker-"));
    }

    [Fact]
    public async Task Truncation_ProducesMarkerWithSuppliedCorrelationId_NoSummary()
    {
        var strategy = new TruncationCompactionStrategy();
        var ctx = MakeContext(
            rawEntries: MakeHistory(5),
            targetIds: new[] { "entry-0", "entry-1" },
            markerCorrelationId: "marker-fixed-id");

        var result = await strategy.CompactAsync(ctx);

        Assert.Equal("marker-fixed-id", result.Marker.CorrelationId);
        Assert.Equal(TruncationCompactionStrategy.Key, result.Marker.Strategy);
        Assert.Equal(string.Empty, result.Marker.ModelId);
        Assert.Empty(result.Marker.Messages);
    }

    [Fact]
    public void Truncation_InvalidCtorArgs_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TruncationCompactionStrategy(0, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TruncationCompactionStrategy(10, 0));
        Assert.Throws<ArgumentException>(() => new TruncationCompactionStrategy(10, 10));
        Assert.Throws<ArgumentException>(() => new TruncationCompactionStrategy(10, 15));
    }

    // =========================================================================
    // SlidingWindowCompactionStrategy
    // =========================================================================

    [Fact]
    public void SlidingWindow_AtCapacity_DoesNotFire()
    {
        var strategy = new SlidingWindowCompactionStrategy(windowSize: 5);
        Assert.Null(strategy.EvaluateTrigger(MakeHistory(5)));
    }

    [Fact]
    public void SlidingWindow_OneOver_FiresOnSingleEntry()
    {
        var strategy = new SlidingWindowCompactionStrategy(windowSize: 5);
        var targets = strategy.EvaluateTrigger(MakeHistory(6));

        Assert.NotNull(targets);
        Assert.Single(targets!);
        Assert.Equal("entry-0", targets[0]);
    }

    [Fact]
    public async Task SlidingWindow_ProducesEmptySummaryMarker()
    {
        var strategy = new SlidingWindowCompactionStrategy();
        var ctx = MakeContext(
            rawEntries: MakeHistory(2),
            targetIds: new[] { "entry-0" },
            markerCorrelationId: "marker-sw-1");

        var result = await strategy.CompactAsync(ctx);

        Assert.Equal(SlidingWindowCompactionStrategy.Key, result.Marker.Strategy);
        Assert.Empty(result.Marker.Messages);
        Assert.Equal(new[] { "entry-0" }, result.Marker.CompactedMessageIds);
    }

    // =========================================================================
    // SummarizationCompactionStrategy
    // =========================================================================

    [Fact]
    public async Task Summarization_InvokesChatClient_BuildsMarkerWithSummary()
    {
        var chat = new StubChat(reply: "TL;DR — user asked about widgets; agent responded.");
        var strategy = new SummarizationCompactionStrategy();
        var ctx = MakeContext(
            rawEntries: MakeHistory(3),
            targetIds: new[] { "entry-0", "entry-1" },
            markerCorrelationId: "marker-sum-1",
            chatClient: chat);

        var result = await strategy.CompactAsync(ctx);

        Assert.Equal(1, chat.CallCount);
        Assert.Equal(SummarizationCompactionStrategy.Key, result.Marker.Strategy);
        Assert.Single(result.Marker.Messages);
        Assert.Contains("TL;DR", result.Marker.Messages[0].Text);
    }

    [Fact]
    public async Task Summarization_PromptIncludesSystemMessageAndTargetMessages()
    {
        // The strategy's prompt must include the system instruction + the messages from the
        // target entries (only). Non-target entries (those outside the keep-recent window)
        // must NOT leak into the prompt.
        var chat = new StubChat(reply: "ok");
        var strategy = new SummarizationCompactionStrategy();

        var source0 = MakeSource("entry-0", "First user turn.");
        var source1 = MakeSource("entry-1", "Second user turn.");
        var source2 = MakeSource("entry-2", "Recent turn — should NOT be in prompt.");

        var ctx = MakeContext(
            rawEntries: new DurableSessionEntry[] { source0, source1, source2 },
            targetIds: new[] { "entry-0", "entry-1" },
            markerCorrelationId: "m",
            chatClient: chat);

        await strategy.CompactAsync(ctx);

        // First prompt message is the system instruction; remaining are the target entries.
        Assert.Equal(ChatRole.System, chat.LastPrompt![0].Role);
        Assert.Contains(chat.LastPrompt, m => m.Text == "First user turn.");
        Assert.Contains(chat.LastPrompt, m => m.Text == "Second user turn.");
        Assert.DoesNotContain(chat.LastPrompt, m => m.Text == "Recent turn — should NOT be in prompt.");
    }

    [Fact]
    public async Task Summarization_Marker_PinsSummaryStrategyIdsAndModel()
    {
        // S-T2-1: direct unit test for the SHIPPING compaction path
        // (SummarizationCompactionStrategy.CompactAsync — the dead RunCompactionSummary
        // activity tests were removed). Pins the full marker contract: summary text from the
        // chat client, strategy key, the supplied marker correlation id, the model id echoed
        // from the chat response, and the compacted/originating id lists set to the targets.
        var chat = new StubChat(
            reply: "Summary: user requested invoice export; agent confirmed.",
            modelId: "summarizer-model-v1");
        var strategy = new SummarizationCompactionStrategy();

        var targetIds = new[] { "entry-0", "entry-1" };
        var ctx = MakeContext(
            rawEntries: MakeHistory(3),
            targetIds: targetIds,
            markerCorrelationId: "marker-pinned-id",
            chatClient: chat);

        var result = await strategy.CompactAsync(ctx);
        var marker = result.Marker;

        // Summary text — carried in the marker's Messages.
        Assert.Single(marker.Messages);
        Assert.Contains("invoice export", marker.Messages[0].Text);

        // Strategy key.
        Assert.Equal(SummarizationCompactionStrategy.Key, marker.Strategy);

        // The pre-minted marker correlation id must be used verbatim (retry-idempotent id).
        Assert.Equal("marker-pinned-id", marker.CorrelationId);

        // Model id is echoed from the chat response so audit logs can distinguish the
        // summarizer model from the agent model.
        Assert.Equal("summarizer-model-v1", marker.ModelId);

        // Compacted + originating id lists are the trigger-selected targets.
        Assert.Equal(targetIds, marker.CompactedMessageIds);
        Assert.Equal(targetIds, marker.OriginatingTurnCorrelationIds);
    }

    [Fact]
    public async Task Summarization_EmptyChatResponse_FallsBackToPlaceholderSummary()
    {
        // When the chat client returns no messages, the strategy must still produce a
        // non-empty marker (placeholder rollup) rather than an empty Messages list — so the
        // post-compact projection always has something to show in place of collapsed entries.
        var chat = new StubChat(reply: null);
        var strategy = new SummarizationCompactionStrategy();

        var ctx = MakeContext(
            rawEntries: MakeHistory(2),
            targetIds: new[] { "entry-0" },
            markerCorrelationId: "marker-empty",
            chatClient: chat);

        var result = await strategy.CompactAsync(ctx);

        Assert.Single(result.Marker.Messages);
        Assert.False(string.IsNullOrWhiteSpace(result.Marker.Messages[0].Text));
    }

    [Fact]
    public void Summarization_InvalidCtorArgs_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SummarizationCompactionStrategy(0, 4, "prompt"));
        Assert.Throws<ArgumentException>(() =>
            new SummarizationCompactionStrategy(10, 4, ""));
        Assert.Throws<ArgumentException>(() =>
            new SummarizationCompactionStrategy(10, 10, "prompt"));
    }

    // =========================================================================
    // DI pre-registration contract
    // =========================================================================

    [Fact]
    public void TemporalAgentsRegistrar_PreRegistersAllThreeStrategies_UnderCanonicalKeys()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(A.Fake<ITemporalClient>());
        services.AddSingleton(new TemporalCommunity.Extensions.AI.DurableExecutionOptions { TaskQueue = "t" });
        var agentsOptions = new TemporalAgentsOptions();
        TemporalAgentsRegistrar.Register(services, builder: null, agentsOptions);

        var sp = services.BuildServiceProvider();

        Assert.IsType<TruncationCompactionStrategy>(
            sp.GetKeyedService<ICompactionStrategy>(TruncationCompactionStrategy.Key));
        Assert.IsType<SlidingWindowCompactionStrategy>(
            sp.GetKeyedService<ICompactionStrategy>(SlidingWindowCompactionStrategy.Key));
        Assert.IsType<SummarizationCompactionStrategy>(
            sp.GetKeyedService<ICompactionStrategy>(SummarizationCompactionStrategy.Key));
    }

    [Fact]
    public void TemporalAgentsRegistrar_CustomStrategyForBuiltinKey_Wins()
    {
        // TryAddKeyedSingleton means user-registered "truncation" wins. Verify this — the
        // built-in is only a default for users who don't supply their own.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(A.Fake<ITemporalClient>());
        services.AddSingleton(new TemporalCommunity.Extensions.AI.DurableExecutionOptions { TaskQueue = "t" });
        var custom = new StubStrategy("custom-truncation");
        services.AddKeyedSingleton<ICompactionStrategy>(TruncationCompactionStrategy.Key, custom);
        var agentsOptions = new TemporalAgentsOptions();
        TemporalAgentsRegistrar.Register(services, builder: null, agentsOptions);

        var sp = services.BuildServiceProvider();
        var resolved = sp.GetKeyedService<ICompactionStrategy>(TruncationCompactionStrategy.Key);

        Assert.Same(custom, resolved);
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    private static List<DurableSessionEntry> MakeHistory(int count)
    {
        var list = new List<DurableSessionEntry>(count);
        for (int i = 0; i < count; i++)
        {
            list.Add(MakeSource($"entry-{i}"));
        }
        return list;
    }

    private static AgentSessionRequest MakeSource(string id, string? text = null) => new()
    {
        CorrelationId = id,
        CreatedAt = DateTimeOffset.UtcNow,
        Messages = text is null
            ? Array.Empty<ChatMessage>()
            : new[] { new ChatMessage(ChatRole.User, text) },
    };

    private static CompactionMarkerEntry MakeMarker(string id, string[] compactedIds) => new()
    {
        CorrelationId = id,
        CreatedAt = DateTimeOffset.UtcNow,
        CompactedMessageIds = compactedIds,
        Strategy = "test",
        ModelId = string.Empty,
        OriginatingTurnCorrelationIds = new[] { id },
    };

    private static CompactionContext MakeContext(
        IReadOnlyList<DurableSessionEntry> rawEntries,
        IReadOnlyList<string> targetIds,
        string markerCorrelationId,
        IChatClient? chatClient = null) => new()
    {
        RawEntries = rawEntries,
        TargetMessageIds = targetIds,
        AgentName = "TestAgent",
        SessionId = "test-session",
        MarkerCorrelationId = markerCorrelationId,
        ChatClient = chatClient ?? new StubChat(reply: "ok"),
    };

    private sealed class StubChat : IChatClient
    {
        private readonly string? _reply;
        private readonly string? _modelId;
        public StubChat(string? reply, string? modelId = null) { _reply = reply; _modelId = modelId; }
        public int CallCount { get; private set; }
        public List<ChatMessage>? LastPrompt { get; private set; }

        public void Dispose() { }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastPrompt = messages.ToList();
            // A null reply models an empty chat response (no messages) so the strategy's
            // placeholder-summary fallback can be exercised.
            var response = _reply is null
                ? new ChatResponse(Array.Empty<ChatMessage>())
                : new ChatResponse([new ChatMessage(ChatRole.Assistant, _reply)]);
            response.ModelId = _modelId;
            return Task.FromResult(response);
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastPrompt = messages.ToList();
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, _reply);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }

    private sealed class StubStrategy(string id) : ICompactionStrategy
    {
        public string Id { get; } = id;

        public IReadOnlyList<string>? EvaluateTrigger(IReadOnlyList<DurableSessionEntry> history) => null;

        public Task<CompactionResult> CompactAsync(
            CompactionContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CompactionResult
            {
                Marker = new CompactionMarkerEntry
                {
                    CorrelationId = context.MarkerCorrelationId,
                    CreatedAt = DateTimeOffset.UtcNow,
                    CompactedMessageIds = Array.Empty<string>(),
                    Strategy = Id,
                    ModelId = string.Empty,
                    OriginatingTurnCorrelationIds = Array.Empty<string>(),
                },
            });
    }
}
