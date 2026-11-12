using TemporalCommunity.Extensions.AI.Exceptions;
using Temporalio.Workflows;

namespace WorkflowRouting;

internal static class AgentInvocationOptions
{
    internal static readonly ActivityOptions Default = new()
    {
        StartToCloseTimeout = TimeSpan.FromMinutes(35),
        ScheduleToCloseTimeout = TimeSpan.FromHours(2),
        RetryPolicy = new()
        {
            MaximumAttempts = 3,
            NonRetryableErrorTypes =
            [
                nameof(DurableTurnFailedException),
                nameof(DurableTurnOutcomeTooLargeException),
            ],
        },
    };
}
