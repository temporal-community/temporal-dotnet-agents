#pragma warning disable TA002 // compaction API surface is experimental but referenced by name in tests

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Temporalio.Extensions.AI;
using Temporalio.Extensions.AI.Session;
using Temporalio.Extensions.Agents.Compaction;
using Xunit;

namespace Temporalio.Extensions.Agents.Tests.Compaction;

/// <summary>
/// Step 6a tests: pin that the public compaction-API surface threads through builder →
/// registration → resolved config without runtime behavior. Trigger logic + strategy
/// implementations land in Step 6b/6c/6d.
/// </summary>
public class CompactionApiSurfaceTests
{
    [Fact]
    public void DurableAgentBuilder_CompactionStrategyKey_DefaultsToNull()
    {
        // Compaction is opt-in; users that don't call UseCompaction see no behavior change.
        var options = new TemporalAgentsOptions();
        DurableAgentBuilder? captured = null;
        options.AddDurableAgent("A", b =>
        {
            b.ChatClient = _ => new StubChat();
            captured = b;
        });

        Assert.NotNull(captured);
        Assert.Null(captured.CompactionStrategyKey);
    }

    [Fact]
    public void DurableAgentBuilder_CompactionStrategyKey_FlowsToRegistration()
    {
        var options = new TemporalAgentsOptions();
        options.AddDurableAgent("Agent1", b =>
        {
            b.ChatClient = _ => new StubChat();
            b.CompactionStrategyKey = "summarization";
        });

        var registration = options.TryGetDurableRegistration("Agent1");
        Assert.NotNull(registration);
        Assert.Equal("summarization", registration!.CompactionStrategyKey);
    }

    [Fact]
    public void TemporalAgentsOptions_DefaultCompactionStrategy_StartsNull()
    {
        var options = new TemporalAgentsOptions();
        Assert.Null(options.DefaultCompactionStrategy);
    }

    [Fact]
    public void TemporalAgentsOptions_DefaultCompactionStrategy_IsSettableForWorkerDefault()
    {
        var options = new TemporalAgentsOptions();
        options.DefaultCompactionStrategy = "truncation";
        Assert.Equal("truncation", options.DefaultCompactionStrategy);
    }

    [Fact]
    public void CompactionResult_RequiresMarker()
    {
        // Compile-time required-field guard — Marker is required.
        var marker = new CompactionMarkerEntry
        {
            CorrelationId = "m1",
            CreatedAt = DateTimeOffset.UtcNow,
            CompactedMessageIds = new[] { "a" },
            Strategy = "test",
            ModelId = string.Empty,
            OriginatingTurnCorrelationIds = new[] { "t" },
        };
        var result = new CompactionResult { Marker = marker };
        Assert.Same(marker, result.Marker);
    }

    [Fact]
    public void CompactionContext_RequiresAllFields()
    {
        var rawEntries = Array.Empty<DurableSessionEntry>();
        var ctx = new CompactionContext
        {
            RawEntries = rawEntries,
            TargetMessageIds = new[] { "a", "b" },
            AgentName = "agent",
            SessionId = "session",
            MarkerCorrelationId = "marker-1",
            ChatClient = new StubChat(),
        };
        Assert.Same(rawEntries, ctx.RawEntries);
        Assert.Equal("agent", ctx.AgentName);
    }

    [Fact]
    public void CustomCompactionStrategy_CanBeRegistered_ViaKeyedDI()
    {
        // Pin the registration shape — same pattern as IChatClientDecorator (Step 4b).
        // Built-in pre-registration happens in Step 6c; here we verify the user-side
        // contract works.
        var services = new ServiceCollection();
        var strategy = new StubStrategy();
        services.AddKeyedSingleton<ICompactionStrategy>("my-strategy", strategy);
        var sp = services.BuildServiceProvider();

        var resolved = sp.GetKeyedService<ICompactionStrategy>("my-strategy");
        Assert.Same(strategy, resolved);
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    private sealed class StubChat : IChatClient
    {
        public void Dispose() { }
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]));
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            AsyncEnumerable.Empty<ChatResponseUpdate>();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }

    private sealed class StubStrategy : ICompactionStrategy
    {
        public IReadOnlyList<string>? EvaluateTrigger(
            IReadOnlyList<DurableSessionEntry> history) => null;

        public Task<CompactionResult> CompactAsync(
            CompactionContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CompactionResult
            {
                Marker = new CompactionMarkerEntry
                {
                    CorrelationId = "stub",
                    CreatedAt = DateTimeOffset.UtcNow,
                    CompactedMessageIds = Array.Empty<string>(),
                    Strategy = "stub",
                    ModelId = string.Empty,
                    OriginatingTurnCorrelationIds = Array.Empty<string>(),
                },
            });
    }
}
