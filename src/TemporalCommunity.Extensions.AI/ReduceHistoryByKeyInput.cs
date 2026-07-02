using TemporalCommunity.Extensions.AI.Session;

namespace TemporalCommunity.Extensions.AI;

/// <summary>
/// Input for the <c>TemporalCommunity.Extensions.AI.ReduceHistoryByKey</c> activity.
/// Carries the keyed-service key and the current history so the activity can resolve the
/// reducer from DI and return the trimmed list.
/// </summary>
internal sealed class ReduceHistoryByKeyInput
{
    /// <summary>
    /// The keyed-service key used to resolve the
    /// <c>Func&lt;IList&lt;DurableSessionEntry&gt;, IList&lt;DurableSessionEntry&gt;&gt;</c>
    /// from the worker's DI container.
    /// </summary>
    public required string ReducerKey { get; init; }

    /// <summary>
    /// The history entries to be reduced. Passed by the workflow at continue-as-new time.
    /// </summary>
    public required List<DurableSessionEntry> History { get; init; }
}
