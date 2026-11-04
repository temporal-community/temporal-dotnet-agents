using ScheduledChat;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Client.Schedules;

namespace DocSnippets.MEAI;

internal static class Scheduling
{
    internal static async Task<ScheduleHandle> CreateAsync(
        ITemporalClient client, string scheduleId, string conversationId, string taskQueue)
    {
        // BEGIN SNIPPET docs/how-to/MEAI/scheduling.md#create-the-schedule
        var schedule = await client.CreateScheduleAsync(scheduleId, new Schedule(
            ScheduleActionStartWorkflow.Create(
                (ScheduledTurnWorkflow workflow) => workflow.RunAsync(
                    new ScheduledTurnInput(conversationId, "Report the service status.")),
                new WorkflowOptions($"trigger-{scheduleId}", taskQueue)),
            new ScheduleSpec { Intervals = [new ScheduleIntervalSpec(TimeSpan.FromHours(1))] })
        {
            Policy = new SchedulePolicy
            {
                Overlap = ScheduleOverlapPolicy.Skip,
                CatchupWindow = TimeSpan.FromMinutes(10),
                PauseOnFailure = true,
            },
            State = new ScheduleState { Paused = true },
        });
        // END SNIPPET docs/how-to/MEAI/scheduling.md#create-the-schedule
        return schedule;
    }

    internal static async Task BackfillAsync(ScheduleHandle schedule)
    {
        // BEGIN SNIPPET docs/how-to/MEAI/scheduling.md#overlap-and-backfill
        var now = DateTime.UtcNow;
        var end = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc);
        await schedule.BackfillAsync(
            [new ScheduleBackfill(end.AddHours(-2).AddSeconds(1), end, ScheduleOverlapPolicy.BufferAll)]);
        // END SNIPPET docs/how-to/MEAI/scheduling.md#overlap-and-backfill
    }
}
