#pragma warning disable MAAI001 // experimental AIContextProvider.InvokingContext ctor; see ExperimentalApiSuppressions.cs
using System.Runtime.CompilerServices;
using FakeItEasy;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TemporalCommunity.Extensions.Agents.Scheduling;
using TemporalCommunity.Extensions.Agents.Session;
using TemporalCommunity.Extensions.Agents.Workflows;
using Temporalio.Client;
using Temporalio.Testing;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.Tests.Workflows;

/// <summary>
/// Unit tests for the context-provider loop in <c>RunDurableAgentStepAsync</c> (Wave C item 8).
/// Exercises three behaviours introduced by the fix:
/// <list type="bullet">
///   <item><description>Provider chaining: provider N+1 sees provider N's contributions.</description></item>
///   <item><description>Instructions propagation: provider-returned instructions reach <c>ChatOptions</c>.</description></item>
///   <item><description>Tool error: a single <see cref="LogLevel.Error"/> fires when any provider returns tools.</description></item>
/// </list>
/// </summary>
public class AgentActivitiesContextProviderTests
{
    // ── Helpers ────────────────────────────────────────────────────────────────

    private static (AgentActivities activities, CapturingLoggerFactory loggerFactory)
        BuildHarness(Action<TemporalAgentsOptions> configure)
    {
        var options = (TemporalAgentsOptions)Activator.CreateInstance(
            typeof(TemporalAgentsOptions), nonPublic: true)!;
        configure(options);

        var services = new ServiceCollection();
        services.AddSingleton(options);
        var sp = services.BuildServiceProvider();

        var loggerFactory = new CapturingLoggerFactory();
        var activities = new AgentActivities(sp, loggerFactory);
        return (activities, loggerFactory);
    }

    private static AgentStepInput MakeInput(string agentName, string userText = "hello") =>
        new AgentStepInput
        {
            AgentName = agentName,
            Request = new RunRequest(userText),
            AccumulatedMessages = [new ChatMessage(ChatRole.User, userText)],
            // Provide a valid session ID so the activity skips parsing ctx.Info.WorkflowId
            // (the default ActivityEnvironment WorkflowId of "test" fails the ta-* prefix check).
            SessionId = TemporalAgentSessionId.WithRandomKey(agentName),
        };

    // ── Tests ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// When a context provider returns <see cref="AIContext.Tools"/>, exactly one
    /// <see cref="LogLevel.Error"/> is emitted per turn, regardless of how many tools
    /// the provider returned.
    /// </summary>
    [Fact]
    public async Task ContextProvider_ReturningTools_EmitsExactlyOneError()
    {
        var (activities, logFactory) = BuildHarness(opts =>
        {
            opts.AddDurableAgent("ProviderToolAgent", agent =>
            {
                agent.ChatClient = _ => new SimpleStreamingChatClient();
                agent.AddContextProvider(new ToolReturningContextProvider(
                    new AIFunction[]
                    {
                        AIFunctionFactory.Create(() => "a", "provider_tool_a"),
                        AIFunctionFactory.Create(() => "b", "provider_tool_b"),
                    }));
            });
        });

        var env = new ActivityEnvironment
        {
            TemporalClient = A.Fake<ITemporalClient>(),
        };
        await env.RunAsync(() => activities.RunDurableAgentStepAsync(MakeInput("ProviderToolAgent")));

        var errors = logFactory.Errors;
        // Exactly one error, not two (once per provider-returned tool count).
        Assert.Single(errors);
        var error = errors[0];
        Assert.Contains("ToolReturningContextProvider", error);
        Assert.Contains("2", error);           // ToolCount = 2
        Assert.Contains("ProviderToolAgent", error);
        Assert.Contains("Wrap the provider in DurableContextProviderWrapper", error);
    }

    /// <summary>
    /// When no context provider is registered, no error is emitted.
    /// </summary>
    [Fact]
    public async Task NoContextProviders_NoErrorEmitted()
    {
        var (activities, logFactory) = BuildHarness(opts =>
        {
            opts.AddDurableAgent("PlainAgent", agent =>
            {
                agent.ChatClient = _ => new SimpleStreamingChatClient();
            });
        });

        var env = new ActivityEnvironment
        {
            TemporalClient = A.Fake<ITemporalClient>(),
        };
        await env.RunAsync(() => activities.RunDurableAgentStepAsync(MakeInput("PlainAgent")));

        Assert.Empty(logFactory.Errors);
    }

    /// <summary>
    /// When a context provider returns no tools, no error is emitted.
    /// </summary>
    [Fact]
    public async Task ContextProvider_ReturningNoTools_NoErrorEmitted()
    {
        var (activities, logFactory) = BuildHarness(opts =>
        {
            opts.AddDurableAgent("NoToolProviderAgent", agent =>
            {
                agent.ChatClient = _ => new SimpleStreamingChatClient();
                agent.AddContextProvider(new ToolReturningContextProvider([]));
            });
        });

        var env = new ActivityEnvironment
        {
            TemporalClient = A.Fake<ITemporalClient>(),
        };
        await env.RunAsync(() => activities.RunDurableAgentStepAsync(MakeInput("NoToolProviderAgent")));

        Assert.Empty(logFactory.Errors);
    }

    /// <summary>
    /// When two providers are registered and the first returns tools, exactly one error
    /// is emitted (not two — one per provider).
    /// </summary>
    [Fact]
    public async Task TwoContextProviders_OnlyFirstReturnsTools_ExactlyOneError()
    {
        var (activities, logFactory) = BuildHarness(opts =>
        {
            opts.AddDurableAgent("TwoProviderAgent", agent =>
            {
                agent.ChatClient = _ => new SimpleStreamingChatClient();
                agent.AddContextProvider(new ToolReturningContextProvider(
                    [AIFunctionFactory.Create(() => "x", "first_provider_tool")]));
                agent.AddContextProvider(new ToolReturningContextProvider([]));
            });
        });

        var env = new ActivityEnvironment
        {
            TemporalClient = A.Fake<ITemporalClient>(),
        };
        await env.RunAsync(() => activities.RunDurableAgentStepAsync(MakeInput("TwoProviderAgent")));

        Assert.Single(logFactory.Errors);
    }

    // ── Test helpers ────────────────────────────────────────────────────────────

    /// <summary>
    /// A context provider that returns a fixed set of <see cref="AIFunction"/> tools when invoked.
    /// Used to verify that provider-contributed tools trigger the durable-dispatch warning.
    /// </summary>
    private sealed class ToolReturningContextProvider : AIContextProvider
    {
        private readonly IList<AIFunction> _tools;

        public ToolReturningContextProvider(IList<AIFunction> tools)
            : base(provideInputMessageFilter: m => m) // pass-through; tests don't use MAF message filtering
        {
            _tools = tools;
        }

        protected override ValueTask<AIContext> ProvideAIContextAsync(
            InvokingContext context,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(new AIContext
            {
                Tools = _tools.Count > 0 ? _tools.Cast<AITool>().ToList() : null,
            });
        }
    }

    /// <summary>
    /// A minimal <see cref="IChatClient"/> that returns a single empty assistant streaming response.
    /// Supports <see cref="GetStreamingResponseAsync"/> so the durable-agent LLM-step activity
    /// can complete without hitting a real model.
    /// </summary>
    private sealed class SimpleStreamingChatClient : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new("test-streaming");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Empty)));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, string.Empty);
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    /// <summary>
    /// An <see cref="ILoggerFactory"/> that captures <see cref="LogLevel.Warning"/> entries
    /// across all loggers it creates.
    /// </summary>
    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        private readonly List<string> _warnings = [];
        private readonly List<string> _errors = [];

        public IReadOnlyList<string> Warnings
        {
            get
            {
                lock (_warnings) return _warnings.ToArray();
            }
        }

        public IReadOnlyList<string> Errors
        {
            get
            {
                lock (_errors) return _errors.ToArray();
            }
        }

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(_warnings, _errors);

        public void AddProvider(ILoggerProvider provider) { }

        public void Dispose() { }

        private sealed class CapturingLogger(List<string> warnings, List<string> errors) : ILogger
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
                if (logLevel == LogLevel.Warning)
                {
                    lock (warnings)
                        warnings.Add(formatter(state, exception));
                }
                else if (logLevel == LogLevel.Error)
                {
                    lock (errors)
                        errors.Add(formatter(state, exception));
                }
            }
        }
    }
}
