using TemporalCommunity.Extensions.AI.Exceptions;
using Temporalio.Exceptions;

namespace TemporalCommunity.Extensions.AI.Internal;

internal static class DurableTurnFailureMapper
{
    internal const string OutcomeTooLargeErrorType = "DurableTurnOutcomeTooLarge";

    internal static Exception? Map(Exception exception, string? requestId)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is not ApplicationFailureException failure)
            {
                continue;
            }

            if (failure.ErrorType == "DurableTurnBusy")
            {
                return new DurableSessionBusyException(requestId, exception);
            }

            if (failure.ErrorType == OutcomeTooLargeErrorType)
            {
                return DurableTurnOutcomeTooLargeException.FromFailure(failure.Message, exception);
            }
        }

        return null;
    }
}
