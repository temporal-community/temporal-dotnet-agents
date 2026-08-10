using Temporalio.Exceptions;

namespace TemporalCommunity.Extensions.AI.Internal;

/// <summary>
/// Finds typed Temporal application failures through task, activity, and aggregate wrappers.
/// </summary>
internal static class TemporalFailureInspector
{
    internal static ApplicationFailureException? FindNonRetryableApplicationFailure(
        Exception? exception,
        string errorType)
    {
        if (exception is null)
        {
            return null;
        }

        var pending = new Stack<Exception>();
        pending.Push(exception);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (current is ApplicationFailureException applicationFailure
                && applicationFailure.NonRetryable
                && string.Equals(applicationFailure.ErrorType, errorType, StringComparison.Ordinal))
            {
                return applicationFailure;
            }

            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    pending.Push(inner);
                }
            }
            else if (current.InnerException is { } inner)
            {
                pending.Push(inner);
            }
        }

        return null;
    }
}
