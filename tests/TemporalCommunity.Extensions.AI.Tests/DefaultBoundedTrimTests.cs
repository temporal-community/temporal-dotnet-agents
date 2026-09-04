using System.Reflection;
using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.AI.Session;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests;

/// <summary>
/// C-2 (no-reducer fallback) — unit tests for the deterministic <c>DefaultBoundedTrim</c> applied at
/// continue-as-new when no <see cref="DurableChatWorkflowInput.HistoryReducerKey"/> is configured.
///
/// <para>
/// Before this fix the no-reducer path carried the full history into the fresh run. When CAN was
/// triggered by the count threshold (<c>history.Count &gt;= MaxEntryCount</c>) the new run
/// immediately re-tripped the same threshold — a back-to-back CAN loop. The trim must guarantee the
/// carried count is <strong>strictly below</strong> <c>MaxEntryCount</c> (target = <c>MaxEntryCount/2</c>,
/// floored) so the new run has headroom and does not CAN again next turn. Values below four are
/// invalid because that policy cannot retain a complete request/response turn.
/// </para>
///
/// <para>
/// <c>DefaultBoundedTrim</c> is a pure <c>private static</c> on
/// <c>DurableChatWorkflowBase&lt;TOutput&gt;</c> — no Temporal context — so we invoke it by
/// reflection. The matching integration test (no back-to-back CAN observed end-to-end) lives in the
/// AI integration suite.
/// </para>
/// </summary>
public class DefaultBoundedTrimTests
{
    private static List<DurableSessionEntry> InvokeTrim(List<DurableSessionEntry> history, int maxEntryCount)
    {
        // The method lives on the open generic base; use the closed DurableChatWorkflow's base type.
        var wfType = typeof(DurableChatClient).Assembly
            .GetType("TemporalCommunity.Extensions.AI.DurableChatWorkflow", throwOnError: true)!;
        var baseType = wfType.BaseType!; // DurableChatWorkflowBase<ChatResponse>

        var method = baseType.GetMethod(
            "DefaultBoundedTrim", BindingFlags.Static | BindingFlags.NonPublic)!;

        return (List<DurableSessionEntry>)method.Invoke(null, [history, maxEntryCount])!;
    }

    private static List<DurableSessionEntry> MakeHistory(int count)
    {
        var list = new List<DurableSessionEntry>(count);
        for (var i = 0; i < count; i++)
        {
            list.Add(new DurableSessionRequest
            {
                CorrelationId = $"corr-{i}",
                CreatedAt = DateTimeOffset.UnixEpoch.AddSeconds(i),
                Messages = [new ChatMessage(ChatRole.User, $"msg-{i}")],
            });
        }
        return list;
    }

    private static List<DurableSessionEntry> MakeCompletedTurns(int count)
    {
        var list = new List<DurableSessionEntry>(count * 2);
        for (var i = 0; i < count; i++)
        {
            var correlationId = $"turn-{i}";
            list.Add(new DurableSessionRequest
            {
                CorrelationId = correlationId,
                CreatedAt = DateTimeOffset.UnixEpoch.AddSeconds(i * 2),
                Messages = [new ChatMessage(ChatRole.User, $"request-{i}")],
            });
            list.Add(new DurableSessionResponse
            {
                CorrelationId = correlationId,
                CreatedAt = DateTimeOffset.UnixEpoch.AddSeconds((i * 2) + 1),
                Messages = [new ChatMessage(ChatRole.Assistant, $"response-{i}")],
            });
        }

        return list;
    }

    [Fact]
    public void Trim_AtThreshold_CarriesStrictlyBelowMaxEntryCount()
    {
        const int max = 10;
        // CAN-triggering history: count == MaxEntryCount.
        var history = MakeHistory(max);

        var trimmed = InvokeTrim(history, max);

        // Target = max/2 = 5; strictly below the trigger so no back-to-back CAN.
        Assert.Equal(5, trimmed.Count);
        Assert.True(trimmed.Count < max, "Carried history must be strictly below MaxEntryCount.");
    }

    [Fact]
    public void Trim_KeepsMostRecentEntries_InOrder()
    {
        const int max = 6; // target = 3
        var history = MakeHistory(max); // corr-0 .. corr-5

        var trimmed = InvokeTrim(history, max);

        Assert.Equal(3, trimmed.Count);
        // Most-recent entries kept, original order preserved (TakeLast).
        Assert.Equal("corr-3", trimmed[0].CorrelationId);
        Assert.Equal("corr-4", trimmed[1].CorrelationId);
        Assert.Equal("corr-5", trimmed[2].CorrelationId);
    }

    [Fact]
    public void Trim_HistoryAtOrBelowTarget_ReturnedUnchanged()
    {
        const int max = 10; // target = 5
        // SDK-suggested CAN with a small history (below target) — not perturbed.
        var history = MakeHistory(4);

        var trimmed = InvokeTrim(history, max);

        Assert.Equal(4, trimmed.Count);
        Assert.Same(history, trimmed); // pass-through, no copy
    }

    [Fact]
    public void Trim_OddMaxEntryCount_FloorsTarget()
    {
        const int max = 9; // target = floor(9/2) = 4
        var history = MakeHistory(max);

        var trimmed = InvokeTrim(history, max);

        Assert.Equal(4, trimmed.Count);
        Assert.True(trimmed.Count < max);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Trim_RejectsThresholdThatCannotRetainACompleteTurn(int maxEntryCount)
    {
        var exception = Assert.Throws<TargetInvocationException>(
            () => InvokeTrim(MakeCompletedTurns(2), maxEntryCount));

        Assert.IsType<ArgumentOutOfRangeException>(exception.InnerException);
    }

    [Fact]
    public void Trim_CompletedTurns_DoesNotCarryAnOrphanedResponse()
    {
        // max=6 targets the last three entries. A naive suffix would start at response-1:
        // [request-0, response-0, request-1, response-1, request-2, response-2]. The fallback
        // must instead retain only the complete final turn.
        var trimmed = InvokeTrim(MakeCompletedTurns(3), maxEntryCount: 6);

        Assert.Collection(
            trimmed,
            entry => Assert.Equal("turn-2", Assert.IsType<DurableSessionRequest>(entry).CorrelationId),
            entry => Assert.Equal("turn-2", Assert.IsType<DurableSessionResponse>(entry).CorrelationId));
    }

    [Fact]
    public void Trim_MinimumValidThreshold_RetainsTheCompleteLatestTurn()
    {
        var trimmed = InvokeTrim(MakeCompletedTurns(2), maxEntryCount: 4);

        Assert.Collection(
            trimmed,
            entry => Assert.Equal("turn-1", Assert.IsType<DurableSessionRequest>(entry).CorrelationId),
            entry => Assert.Equal("turn-1", Assert.IsType<DurableSessionResponse>(entry).CorrelationId));
    }

    [Fact]
    public void Trim_CompletedTurns_PreservesPairedSuffix_WhenTargetIsEven()
    {
        var trimmed = InvokeTrim(MakeCompletedTurns(4), maxEntryCount: 9);

        Assert.Collection(
            trimmed,
            entry => Assert.Equal("turn-2", Assert.IsType<DurableSessionRequest>(entry).CorrelationId),
            entry => Assert.Equal("turn-2", Assert.IsType<DurableSessionResponse>(entry).CorrelationId),
            entry => Assert.Equal("turn-3", Assert.IsType<DurableSessionRequest>(entry).CorrelationId),
            entry => Assert.Equal("turn-3", Assert.IsType<DurableSessionResponse>(entry).CorrelationId));
    }
}
