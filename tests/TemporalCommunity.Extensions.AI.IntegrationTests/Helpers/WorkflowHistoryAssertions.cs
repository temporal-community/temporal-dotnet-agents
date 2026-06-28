using Temporalio.Client;

namespace TemporalCommunity.Extensions.AI.IntegrationTests.Helpers;

/// <summary>
/// Helpers for inspecting a workflow's event history via
/// <see cref="WorkflowHandle.FetchHistoryEventsAsync"/>.
/// </summary>
/// <remarks>
/// Pattern 3 tests assert behaviour like "this turn dispatched exactly 1
/// <c>GetChatStep</c> and 2 <c>InvokeFunction</c> activities." That is a
/// programmatic claim about Temporal's event history, not the chat response.
/// Centralising the boilerplate keeps tests readable.
/// </remarks>
public static class WorkflowHistoryAssertions
{
    /// <summary>
    /// Count <c>ActivityTaskScheduled</c> events whose activity type name matches
    /// <paramref name="activityTypeName"/>.
    /// </summary>
    public static async Task<int> CountActivityScheduledAsync(
        WorkflowHandle handle,
        string activityTypeName)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentException.ThrowIfNullOrEmpty(activityTypeName);

        var count = 0;
        await foreach (var ev in handle.FetchHistoryEventsAsync().ConfigureAwait(false))
        {
            if (ev.ActivityTaskScheduledEventAttributes is { } a &&
                a.ActivityType.Name == activityTypeName)
            {
                count++;
            }
        }
        return count;
    }

    /// <summary>
    /// Group <c>ActivityTaskScheduled</c> events by activity type name and return the counts.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, int>> CountAllScheduledByTypeAsync(
        WorkflowHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        await foreach (var ev in handle.FetchHistoryEventsAsync().ConfigureAwait(false))
        {
            if (ev.ActivityTaskScheduledEventAttributes is { } a)
            {
                var name = a.ActivityType.Name;
                counts[name] = counts.TryGetValue(name, out var c) ? c + 1 : 1;
            }
        }
        return counts;
    }

    /// <summary>
    /// Return the indices (within the full event stream) at which
    /// <c>ActivityTaskScheduled</c> events for <paramref name="activityTypeName"/> appeared,
    /// alongside the index of the first <c>ActivityTaskCompleted</c> event for the same type.
    /// </summary>
    /// <remarks>
    /// Useful for asserting parallel fan-out: if all schedule indices are less than the first
    /// complete index, the workflow scheduled every activity before any completed (i.e. they
    /// ran in parallel rather than serially).
    /// </remarks>
    public static async Task<(IReadOnlyList<int> ScheduleIndices, int FirstCompleteIndex)>
        CollectScheduleVsCompleteAsync(
            WorkflowHandle handle,
            string activityTypeName)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentException.ThrowIfNullOrEmpty(activityTypeName);

        var schedules = new List<int>();
        var firstComplete = -1;
        var scheduledIdToType = new Dictionary<long, string>();
        var index = 0;
        await foreach (var ev in handle.FetchHistoryEventsAsync().ConfigureAwait(false))
        {
            if (ev.ActivityTaskScheduledEventAttributes is { } a)
            {
                scheduledIdToType[ev.EventId] = a.ActivityType.Name;
                if (a.ActivityType.Name == activityTypeName)
                {
                    schedules.Add(index);
                }
            }
            else if (firstComplete < 0 && ev.ActivityTaskCompletedEventAttributes is { } c)
            {
                if (scheduledIdToType.TryGetValue(c.ScheduledEventId, out var typeName)
                    && typeName == activityTypeName)
                {
                    firstComplete = index;
                }
            }
            index++;
        }
        return (schedules, firstComplete);
    }
}
