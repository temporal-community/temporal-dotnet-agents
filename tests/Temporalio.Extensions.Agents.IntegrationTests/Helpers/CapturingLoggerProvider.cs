using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Temporalio.Extensions.Agents.IntegrationTests.Helpers;

/// <summary>
/// Test-only <see cref="ILoggerProvider"/> that captures every emitted log entry. Registered in the
/// worker host's DI <see cref="ILoggerFactory"/> so it observes <c>Workflow.Logger</c> output
/// (which routes through the worker-level logger factory). Thread-safe — workflow and activity
/// threads write concurrently.
/// </summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<CapturedLog> _logs = new();

    /// <summary>All captured log entries in emission order.</summary>
    public IReadOnlyList<CapturedLog> Logs => _logs.ToArray();

    /// <summary>Returns true if any captured log at the given level contains all of the given substrings.</summary>
    public bool ContainsLog(LogLevel level, params string[] substrings) =>
        _logs.Any(l => l.Level == level && substrings.All(s => l.Message.Contains(s, StringComparison.Ordinal)));

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(_logs, categoryName);

    public void Dispose() { }

    /// <summary>A single captured log entry.</summary>
    public sealed record CapturedLog(string Category, LogLevel Level, EventId EventId, string Message, Exception? Exception);

    private sealed class CapturingLogger(ConcurrentQueue<CapturedLog> sink, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            sink.Enqueue(new CapturedLog(category, logLevel, eventId, formatter(state, exception), exception));
        }
    }
}
