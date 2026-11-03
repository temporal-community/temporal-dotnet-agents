using Temporalio.Common;

namespace TemporalCommunity.Extensions.AI.Internal;

internal static class DurableManagedToolRetryPolicy
{
    internal static RetryPolicy Resolve(
        RetryPolicy? perTool,
        RetryPolicy? workerDefault,
        bool isIdempotent,
        string toolName)
    {
        var configured = perTool ?? workerDefault;
        if (configured is null)
        {
            return isIdempotent
                ? DefaultRetryPolicy.ResolveForTool(null)
                : new RetryPolicy { MaximumAttempts = 1 };
        }

        DurableTransportPayloadGuard.EnsureBoundedRetryPolicy(
            configured,
            $"RetryPolicy for tool '{toolName}'");

        if (!isIdempotent && configured.MaximumAttempts > 1)
        {
            throw new InvalidOperationException(
                $"Tool '{toolName}' configures multiple activity attempts without declaring " +
                "Idempotent(). Use Idempotent() only when the downstream operation enforces " +
                "deduplication with the logical-call idempotency key.");
        }

        return configured;
    }
}
