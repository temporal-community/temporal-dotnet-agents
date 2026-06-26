using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Temporalio.Exceptions;
using Temporalio.Extensions.AI.Exceptions;
using Xunit;

namespace Temporalio.Extensions.AI.Tests;

/// <summary>
/// Pins the S-X-7 contract: when <c>SwapPlaceholderTools</c> cannot resolve a
/// <see cref="ToolNamePlaceholder"/> name in the <see cref="DurableFunctionRegistry"/>
/// it must fail fast with a non-retryable <see cref="ApplicationFailureException"/> whose
/// <see cref="ApplicationFailureException.ErrorType"/> is
/// <c>nameof(DurablePlaceholderToolNotRegisteredException)</c> and whose message names the
/// missing tool — rather than warn-and-drop (which would silently ship the LLM request
/// without the tool it was told to use). Non-retryable is deliberate: retrying a
/// configuration error would loop forever. Supersedes the earlier SMITH-1 warn-and-drop behavior.
/// </summary>
public class DurableChatActivitiesSwapPlaceholderToolsTests
{
    /// <summary>
    /// The unknown tool name used to produce a drop-without-warning scenario before the fix.
    /// </summary>
    private const string UnknownToolName = "ghost-tool";

    /// <summary>
    /// A tool that IS in the registry — used to confirm the registry is wired correctly
    /// and that only the missing name triggers the warning.
    /// </summary>
    private const string KnownToolName = "real-tool";

    [Fact]
    public async Task SwapPlaceholderTools_UnknownName_ThrowsWithToolName()
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

        // Build input with a ToolNamePlaceholder for the unknown tool name.
        // ChatOptions.Tools contains two entries: one that IS in the registry (known)
        // and one that is NOT (ghost-tool). The missing one must fail fast.
        var options = new ChatOptions
        {
            Tools =
            [
                new ToolNamePlaceholder(KnownToolName),
                new ToolNamePlaceholder(UnknownToolName),
            ],
        };

        var input = new DurableChatInput
        {
            Messages = [new ChatMessage(ChatRole.User, "ping")],
            Options = options,
            ConversationId = "conv-sx7-test",
            TurnNumber = 1,
        };

        // Act + Assert — an unresolved placeholder must fail fast with a non-retryable
        // ApplicationFailureException naming the missing tool, not silently drop it (S-X-7).
        // GetChatStepAsync calls SwapPlaceholderTools on the Pattern 3 path.
        var ex = await Assert.ThrowsAsync<ApplicationFailureException>(
            () => activities.GetChatStepAsync(input));

        Assert.True(ex.NonRetryable, "Placeholder-not-registered must be non-retryable.");
        Assert.Equal(nameof(DurablePlaceholderToolNotRegisteredException), ex.ErrorType);
        Assert.Contains(UnknownToolName, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SwapPlaceholderTools_KnownName_DoesNotEmitWarning()
    {
        // Arrange — only the known tool is in the options; no warning should fire.
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
            Tools = [new ToolNamePlaceholder(KnownToolName)],
        };

        var input = new DurableChatInput
        {
            Messages = [new ChatMessage(ChatRole.User, "ping")],
            Options = options,
            ConversationId = "conv-smith1-known",
            TurnNumber = 1,
        };

        // Act
        await activities.GetChatStepAsync(input);

        // Assert — no warning for a name that IS in the registry
        var toolDropWarnings = logEntries
            .Where(e => e.Level == LogLevel.Warning &&
                        e.Message.Contains(KnownToolName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Empty(toolDropWarnings);
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
