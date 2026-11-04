using Temporalio.Activities;

namespace TemporalCommunity.Extensions.AI.Internal;

internal static class DurableActivityHeartbeat
{
    internal static async Task<T> RunWithHeartbeatsAsync<T>(
        ActivityExecutionContext context,
        Func<Task<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(operation);

        using var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(
            context.CancellationToken);
        var heartbeatTask = Task.Run(() => HeartbeatAsync(context, heartbeatStop.Token));
        try
        {
            return await operation().ConfigureAwait(false);
        }
        finally
        {
            heartbeatStop.Cancel();
            await heartbeatTask.ConfigureAwait(false);
        }
    }

    private static async Task HeartbeatAsync(
        ActivityExecutionContext context,
        CancellationToken stopToken)
    {
        var interval = context.Info.HeartbeatTimeout is { } timeout && timeout > TimeSpan.Zero
            ? TimeSpan.FromTicks(Math.Max(
                TimeSpan.TicksPerMillisecond,
                Math.Min(timeout.Ticks / 2, TimeSpan.TicksPerSecond)))
            : TimeSpan.FromSeconds(1);
        try
        {
            while (!stopToken.IsCancellationRequested)
            {
                context.Heartbeat();
                await Task.Delay(interval, stopToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
        {
        }
    }
}
