using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Temporalio.Exceptions;
using TemporalCommunity.Extensions.AI.Exceptions;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests;

/// <summary>
/// Caller-supplied <see cref="ChatOptions.Tools"/> cannot cross a durable activity boundary.
/// Durable sessions construct model-facing schemas exclusively from the registered tool registry.
/// </summary>
public class DurableChatActivitiesCallerToolsTests
{
    /// <summary>
    /// The unknown tool name confirms validation does not depend on registry membership.
    /// </summary>
    private const string UnknownToolName = "ghost-tool";

    /// <summary>
    /// A tool that is in the registry still cannot be supplied by a caller through ChatOptions.
    /// </summary>
    private const string KnownToolName = "real-tool";

    [Fact]
    public async Task GetChatStepAsync_CallerSuppliedUnknownTool_ThrowsConfigurationFailure()
    {
        // Arrange
        var logEntries = new List<LogEntry>();
        var loggerFactory = new CapturingLoggerFactory(logEntries);

        var realTool = AIFunctionFactory.Create(() => "real-result", KnownToolName, "A real tool.");

        var services = new ServiceCollection();
        services.AddSingleton<IChatClient>(new MinimalChatClient());
        services.AddSingleton<DurableFunctionRegistry>();
        // Register only the known tool; "ghost-tool" is intentionally absent.
        services.AddSingleton<Action<DurableFunctionRegistry>>(r => r.Register(realTool));

        var provider = services.BuildServiceProvider();

        // Wire the registry by invoking the registered configurator (mirrors what
        // DurableAIRegistrar does, without triggering the startup A-check validator).
        var registry = provider.GetRequiredService<DurableFunctionRegistry>();
        foreach (var configurator in provider.GetServices<Action<DurableFunctionRegistry>>())
        {
            configurator(registry);
        }

        var activities = new DurableChatActivities(provider, loggerFactory);

        // Caller tool definitions are rejected regardless of whether individual names happen
        // to exist in the durable registry.
        var options = new ChatOptions
        {
            Tools =
            [
                AIFunctionFactory.Create(() => "real-result", KnownToolName),
                AIFunctionFactory.Create(() => "ghost-result", UnknownToolName),
            ],
        };

        var input = new DurableChatInput
        {
            Messages = [new ChatMessage(ChatRole.User, "ping")],
            Options = options,
            ConversationId = "conv-sx7-test",
            TurnNumber = 1,
        };

        // Act + Assert — configuration errors are non-retryable.
        var ex = await Assert.ThrowsAsync<ApplicationFailureException>(
            () => activities.GetChatStepAsync(input));

        Assert.True(ex.NonRetryable);
        Assert.Equal(nameof(DurableConfigurationException), ex.ErrorType);
        Assert.Contains("ChatOptions.Tools", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetChatStepAsync_CallerSuppliedRegisteredTool_ThrowsConfigurationFailure()
    {
        // Arrange — a registered tool is also rejected when supplied through ChatOptions.
        var logEntries = new List<LogEntry>();
        var loggerFactory = new CapturingLoggerFactory(logEntries);

        var realTool = AIFunctionFactory.Create(() => "real-result", KnownToolName, "A real tool.");

        var services = new ServiceCollection();
        services.AddSingleton<IChatClient>(new MinimalChatClient());
        services.AddSingleton<DurableFunctionRegistry>();
        services.AddSingleton<Action<DurableFunctionRegistry>>(r => r.Register(realTool));

        var provider = services.BuildServiceProvider();

        var registry = provider.GetRequiredService<DurableFunctionRegistry>();
        foreach (var configurator in provider.GetServices<Action<DurableFunctionRegistry>>())
        {
            configurator(registry);
        }

        var activities = new DurableChatActivities(provider, loggerFactory);

        var options = new ChatOptions
        {
            Tools = [AIFunctionFactory.Create(() => "real-result", KnownToolName)],
        };

        var input = new DurableChatInput
        {
            Messages = [new ChatMessage(ChatRole.User, "ping")],
            Options = options,
            ConversationId = "conv-smith1-known",
            TurnNumber = 1,
        };

        var ex = await Assert.ThrowsAsync<ApplicationFailureException>(
            () => activities.GetChatStepAsync(input));

        Assert.True(ex.NonRetryable);
        Assert.Equal(nameof(DurableConfigurationException), ex.ErrorType);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    internal record LogEntry(LogLevel Level, string Message, Exception? Exception);

    /// <summary>
    /// Captures log entries from <see cref="DurableChatActivities"/>. Enabled for all levels.
    /// </summary>
    private sealed class CapturingLoggerFactory(List<LogEntry> entries) : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(entries);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
    }

    private sealed class CapturingLogger(List<LogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (entries)
            {
                entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
            }
        }
    }

    /// <summary>
    /// Minimal <see cref="IChatClient"/> that returns a single assistant text message.
    /// Used so that <c>GetChatStepAsync</c> can complete without hitting a real LLM.
    /// </summary>
    private sealed class MinimalChatClient : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new("minimal-test");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
