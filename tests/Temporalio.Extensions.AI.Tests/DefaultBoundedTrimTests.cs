using System.Reflection;
using Microsoft.Extensions.AI;
using Temporalio.Extensions.AI.Session;
using Xunit;

namespace Temporalio.Extensions.AI.Tests;

/// <summary>
/// C-2 (no-reducer fallback) — unit tests for the deterministic <c>DefaultBoundedTrim</c> applied at
/// continue-as-new when no <see cref="DurableChatWorkflowInput.HistoryReducer"/> is configured.
///
/// <para>
/// Before this fix the no-reducer path carried the full history into the fresh run. When CAN was
/// triggered by the count threshold (<c>history.Count &gt;= MaxEntryCount</c>) the new run
/// immediately re-tripped the same threshold — a back-to-back CAN loop. The trim must guarantee the
/// carried count is <strong>strictly below</strong> <c>MaxEntryCount</c> (target = <c>MaxEntryCount/2</c>,
/// floored, min 1) so the new run has headroom and does not CAN again next turn.
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
            .GetType("Temporalio.Extensions.AI.DurableChatWorkflow", throwOnError: true)!;
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

    [Fact]
    public void Trim_TinyMaxEntryCount_KeepsAtLeastOne()
    {
        // max=1 → target = Math.Max(1, 0) = 1: keep the most-recent entry, never empty.
        var history = MakeHistory(3);

        var trimmed = InvokeTrim(history, 1);

        Assert.Single(trimmed);
        Assert.Equal("corr-2", trimmed[0].CorrelationId);
    }
}
