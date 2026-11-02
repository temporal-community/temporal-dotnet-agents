namespace TemporalCommunity.Extensions.Tests.Shared;

/// <summary>A single attempt whose observable effect precedes explicitly controlled completion.</summary>
/// <remarks>Use only in provider/tool/activity code, never on the workflow scheduler.</remarks>
public sealed class ControlledActivityAttempt<T>
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _invocations;

    public Task Entered => _entered.Task;
    public int Invocations => Volatile.Read(ref _invocations);
    public bool IsCompletionSupplied => _completion.Task.IsCompleted;

    public async Task<T> ExecuteAsync(Action? sideEffect = null, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Increment(ref _invocations) != 1)
            throw new InvalidOperationException("Use a separate controlled attempt for each retry.");
        cancellationToken.ThrowIfCancellationRequested();
        sideEffect?.Invoke();
        _entered.TrySetResult();
        return await _completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Complete(T result)
    {
        if (!_completion.TrySetResult(result))
            throw new InvalidOperationException("Attempt completion was already supplied.");
    }

    public void Fail(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (!_completion.TrySetException(exception))
            throw new InvalidOperationException("Attempt completion was already supplied.");
    }
}
