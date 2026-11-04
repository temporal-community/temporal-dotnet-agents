using System.Globalization;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace ScheduledChat;

public sealed record ScheduledTurnInput(string ConversationId, string Prompt);

public sealed record ScheduledTurnOccurrence(
    string ScheduleId,
    DateTimeOffset ScheduledAt,
    string ConversationId,
    string Prompt)
{
    // Use nominal schedule time, not activity time, attempt number, or workflow run ID.
    public string RequestId =>
        $"scheduled/{ScheduleId}/{ScheduledAt.UtcTicks.ToString(CultureInfo.InvariantCulture)}";
}

public sealed record ScheduledTurnReceipt(string RequestId, string Text);

[Workflow]
public sealed class ScheduledTurnWorkflow
{
    [WorkflowRun]
    public Task<ScheduledTurnReceipt> RunAsync(ScheduledTurnInput input)
    {
        if (!Workflow.TypedSearchAttributes.TryGetValue(
                SearchAttributeKey.CreateKeyword("TemporalScheduledById"), out var scheduleId)
            || !Workflow.TypedSearchAttributes.TryGetValue(
                SearchAttributeKey.CreateDateTimeOffset("TemporalScheduledStartTime"), out var scheduledAt))
        {
            throw new ApplicationFailureException(
                "Start this trigger workflow through a Temporal Schedule.",
                errorType: "MissingScheduleOccurrence",
                nonRetryable: true);
        }

        var occurrence = new ScheduledTurnOccurrence(
            scheduleId, scheduledAt, input.ConversationId, input.Prompt);
        return Workflow.ExecuteActivityAsync(
            (ScheduledTurnActivities activities) => activities.SubmitAsync(occurrence),
            new()
            {
                StartToCloseTimeout = TimeSpan.FromMinutes(2),
                ScheduleToCloseTimeout = TimeSpan.FromMinutes(5),
                RetryPolicy = new RetryPolicy { MaximumAttempts = 3 },
            });
    }
}
