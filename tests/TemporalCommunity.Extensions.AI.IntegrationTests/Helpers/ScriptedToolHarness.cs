using System.Collections.Concurrent;
using Microsoft.Extensions.AI;

namespace TemporalCommunity.Extensions.AI.IntegrationTests.Helpers;

/// <summary>
/// Test harness for building <see cref="AIFunction"/> tools whose behaviour
/// can vary across invocations — required by Pattern 3 error-handling tests
/// (catch-and-feed-back, consecutive-error threshold, mixed success/failure).
/// </summary>
/// <remarks>
/// <para>
/// Activity retries cause the same tool to be invoked multiple times for what
/// appears to the LLM as a single call. The harness keeps an invocation counter
/// per tool so test scripts can express "fail twice, then succeed."
/// </para>
/// <para>
/// Counters are process-wide and thread-safe; integration tests that share a host
/// must use distinct tool names per test to avoid cross-contamination.
/// </para>
/// </remarks>
public sealed class ScriptedToolHarness
{
    private readonly ConcurrentDictionary<string, int> _invocationCounts = new();
    private readonly ConcurrentDictionary<string, ConcurrentBag<ToolInvocation>> _invocations = new();

    /// <summary>
    /// Snapshot of invocation count for a given tool (by name).
    /// </summary>
    public int GetInvocationCount(string toolName) =>
        _invocationCounts.TryGetValue(toolName, out var count) ? count : 0;

    /// <summary>
    /// Snapshot of every captured invocation for a tool, in arrival order
    /// (concurrent ordering is best-effort but each entry is complete).
    /// </summary>
    public IReadOnlyList<ToolInvocation> GetInvocations(string toolName) =>
        _invocations.TryGetValue(toolName, out var bag)
            ? bag.ToArray()
            : Array.Empty<ToolInvocation>();

    /// <summary>
    /// Build an <see cref="AIFunction"/> that always succeeds with the given result.
    /// </summary>
    public AIFunction BuildAlwaysSucceeds(string toolName, string description, Func<int, object?> resultForInvocation)
    {
        ArgumentNullException.ThrowIfNull(resultForInvocation);
        return AIFunctionFactory.Create(
            (string? input = null) =>
            {
                var n = RecordInvocation(toolName, input);
                return resultForInvocation(n);
            },
            toolName,
            description);
    }

    /// <summary>
    /// Build an <see cref="AIFunction"/> that always throws.
    /// </summary>
    public AIFunction BuildAlwaysThrows(string toolName, string description, string errorMessage)
    {
        return AIFunctionFactory.Create(
            (string? input = null) =>
            {
                RecordInvocation(toolName, input);
                throw new InvalidOperationException(errorMessage);
            },
            toolName,
            description);
    }

    /// <summary>
    /// Build an <see cref="AIFunction"/> that throws on the first <paramref name="failCount"/>
    /// invocations and then returns <paramref name="successResult"/> on every subsequent call.
    /// Use for catch-and-feed-back and threshold-reset tests.
    /// </summary>
    public AIFunction BuildFailThenSucceed(
        string toolName,
        string description,
        int failCount,
        object? successResult,
        string errorMessage = "scripted tool failure")
    {
        if (failCount < 0) throw new ArgumentOutOfRangeException(nameof(failCount));
        return AIFunctionFactory.Create(
            (string? input = null) =>
            {
                var n = RecordInvocation(toolName, input);
                if (n <= failCount)
                {
                    throw new InvalidOperationException($"{errorMessage} (invocation {n})");
                }
                return successResult;
            },
            toolName,
            description);
    }

    private int RecordInvocation(string toolName, string? input)
    {
        var newCount = _invocationCounts.AddOrUpdate(toolName, 1, (_, c) => c + 1);
        var bag = _invocations.GetOrAdd(toolName, _ => new ConcurrentBag<ToolInvocation>());
        bag.Add(new ToolInvocation(newCount, input));
        return newCount;
    }

    /// <summary>One captured tool invocation (1-indexed).</summary>
    public sealed record ToolInvocation(int Index, string? Input);
}
