#pragma warning disable MAAI001 // experimental AIContextProvider.InvokingContext ctor; see ExperimentalApiSuppressions.cs
#pragma warning disable TA001 // IDurableToolSource is experimental; intentional consumption in these tests
using System.Runtime.CompilerServices;
using FakeItEasy;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TemporalCommunity.Extensions.Agents.Scheduling;
using TemporalCommunity.Extensions.Agents.Session;
using TemporalCommunity.Extensions.Agents.Tools;
using TemporalCommunity.Extensions.Agents.Workflows;
using TemporalCommunity.Extensions.AI.Exceptions;
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
        BuildHarness(
            Action<TemporalAgentsOptions> configure,
            Action<IServiceCollection>? configureServices = null)
    {
        var options = (TemporalAgentsOptions)Activator.CreateInstance(
            typeof(TemporalAgentsOptions), nonPublic: true)!;
        configure(options);

        var services = new ServiceCollection();
        services.AddSingleton(options);
        configureServices?.Invoke(services);
        var sp = services.BuildServiceProvider();

        var loggerFactory = new CapturingLoggerFactory();
        var activities = new AgentActivities(sp, sp.GetRequiredService<IServiceScopeFactory>(), loggerFactory);
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

    [Fact]
    public async Task RunDurableAgentStep_SkipDryRun_RejectsSeveredLivePipelineBeforeInvocation()
    {
        var replacement = new CountingReplacementAgent();
        var (activities, _) = BuildHarness(opts =>
        {
            opts.SkipDryRunCCheck = true;
            opts.AddDurableAgent("SeveredAgent", agent =>
            {
                agent.ChatClient = _ => new SimpleStreamingChatClient();
                agent.ConfigureAgentPipeline = builder => builder.Use(_ => replacement);
            });
        });
        var env = new ActivityEnvironment { TemporalClient = A.Fake<ITemporalClient>() };

        var ex = await Assert.ThrowsAsync<DurableConfigurationException>(
            () => env.RunAsync(() =>
                activities.RunDurableAgentStepAsync(MakeInput("SeveredAgent"))));

        Assert.Contains("SeveredAgent", ex.Message);
        Assert.Equal(0, replacement.StreamingRunCount);
    }

    [Fact]
    public async Task RunDurableAgentStep_RejectsCustomDisposableLiveMiddleware()
    {
        var (activities, _) = BuildHarness(opts =>
        {
            opts.SkipDryRunCCheck = true;
            opts.AddDurableAgent("DisposableAgent", agent =>
            {
                agent.ChatClient = _ => new SimpleStreamingChatClient();
                agent.ConfigureAgentPipeline = builder => builder.Use(inner =>
                    new DisposableRecordingAgent(inner));
            });
        });
        var env = new ActivityEnvironment { TemporalClient = A.Fake<ITemporalClient>() };

        var ex = await Assert.ThrowsAsync<DurableConfigurationException>(
            () => env.RunAsync(() =>
                activities.RunDurableAgentStepAsync(MakeInput("DisposableAgent"))));

        Assert.Contains("DisposableAgent", ex.Message);
        Assert.Contains("ownership", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunDurableAgentStep_DelegatingMiddlewareRunsBeforeAndAfterModelStep()
    {
        var beforeCount = 0;
        var afterCount = 0;
        var (activities, _) = BuildHarness(opts =>
        {
            opts.AddDurableAgent("DelegatingAgent", agent =>
            {
                agent.ChatClient = _ => new SimpleStreamingChatClient();
                agent.ConfigureAgentPipeline = builder => builder.Use(inner =>
                    new RecordingDelegatingAgent(
                        inner,
                        () => Interlocked.Increment(ref beforeCount),
                        () => Interlocked.Increment(ref afterCount)));
            });
        });
        var env = new ActivityEnvironment { TemporalClient = A.Fake<ITemporalClient>() };

        await env.RunAsync(() =>
            activities.RunDurableAgentStepAsync(MakeInput("DelegatingAgent")));

        Assert.Equal(1, beforeCount);
        Assert.Equal(1, afterCount);
    }

    /// <summary>
    /// When a context provider returns <see cref="AIContext.Tools"/>, exactly one
    /// <see cref="LogLevel.Error"/> is emitted per turn, regardless of how many tools
    /// the provider returned.
    /// </summary>
    [Fact]
    public async Task RunDurableAgentStep_ProviderReturnsTools_EmitsExactlyOneLogError()
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
        Assert.Contains("IDurableToolSource", error);
    }

    /// <summary>
    /// When no context provider is registered, no error is emitted.
    /// </summary>
    [Fact]
    public async Task RunDurableAgentStep_NoProviders_EmitsNoLogError()
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
    public async Task RunDurableAgentStep_TwoProviders_OnlyFirstReturnsTools_EmitsExactlyOneLogError()
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

    /// <summary>
    /// When a context provider implements <see cref="IDurableToolSource"/>, the per-iteration strip
    /// in <c>AgentActivities</c> nulls out its <c>AIContext.Tools</c> before the LogError sentinel
    /// fires. No <see cref="LogLevel.Error"/> should be emitted even though the provider's
    /// <c>ProvideAIContextAsync</c> returns tools — those tools are already registered as durable
    /// activities and are intentionally stripped from the aggregated context.
    /// </summary>
    [Fact]
    public async Task RunDurableAgentStep_IDurableToolSourceProvider_EmitsNoLogError()
    {
        var durableTool = AIFunctionFactory.Create(() => "result", "durable_provider_tool");

        var (activities, logFactory) = BuildHarness(opts =>
        {
            opts.AddDurableAgent("DurableToolSourceAgent", agent =>
            {
                agent.ChatClient = _ => new SimpleStreamingChatClient();
                // Register the provider via instance overload — this also calls AddToolCore
                // for the spec's tool, mirroring what IDurableToolSource auto-detection does.
                agent.AddContextProvider(new DurableToolSourceProvider(durableTool));
            });
        });

        var env = new ActivityEnvironment
        {
            TemporalClient = A.Fake<ITemporalClient>(),
        };
        await env.RunAsync(() => activities.RunDurableAgentStepAsync(MakeInput("DurableToolSourceAgent")));

        // IDurableToolSource providers have their tools stripped before the LogError check;
        // no error should be emitted.
        Assert.Empty(logFactory.Errors);
    }

    /// <summary>
    /// Provider factories resolve from a new activity scope for each LLM-step attempt. This keeps
    /// a scoped dependency from becoming a captive worker singleton or leaking across sessions.
    /// </summary>
    [Fact]
    public async Task RunDurableAgentStep_ContextProviderFactory_UsesFreshActivityScope()
    {
        var observedScopeIds = new List<Guid>();
        var (activities, _) = BuildHarness(
            opts =>
            {
                opts.AddDurableAgent("ScopedProviderAgent", agent =>
                {
                    agent.ChatClient = _ => new SimpleStreamingChatClient();
                    agent.AddContextProvider(sp => new ScopeRecordingProvider(
                        sp.GetRequiredService<ScopedMarker>(),
                        observedScopeIds));
                });
            },
            services => services.AddScoped<ScopedMarker>());

        var env = new ActivityEnvironment { TemporalClient = A.Fake<ITemporalClient>() };
        await env.RunAsync(() => activities.RunDurableAgentStepAsync(MakeInput("ScopedProviderAgent", "first")));
        await env.RunAsync(() => activities.RunDurableAgentStepAsync(MakeInput("ScopedProviderAgent", "second")));

        Assert.Equal(2, observedScopeIds.Count);
        Assert.NotEqual(observedScopeIds[0], observedScopeIds[1]);
    }

    /// <summary>
    /// A provider's session state is returned from the LLM-step activity and restored before the
    /// next step, rather than being retained in the provider's process-local fields.
    /// </summary>
    [Fact]
    public async Task RunDurableAgentStep_ContextProviderStateBag_IsWrittenBackAndRestored()
    {
        var provider = new StateBagProvider();
        var (activities, _) = BuildHarness(opts =>
        {
            opts.AddDurableAgent("StateBagProviderAgent", agent =>
            {
                agent.ChatClient = _ => new SimpleStreamingChatClient();
                agent.AddContextProvider(provider);
            });
        });

        var env = new ActivityEnvironment { TemporalClient = A.Fake<ITemporalClient>() };
        var first = await env.RunAsync(() =>
            activities.RunDurableAgentStepAsync(MakeInput("StateBagProviderAgent", "first")));

        Assert.NotNull(first.UpdatedStateBag);
        Assert.Equal([0], provider.ObservedCounts);

        var secondInput = new AgentStepInput
        {
            AgentName = "StateBagProviderAgent",
            Request = new RunRequest("second"),
            AccumulatedMessages = [new ChatMessage(ChatRole.User, "second")],
            SessionId = TemporalAgentSessionId.WithRandomKey("StateBagProviderAgent"),
            SerializedStateBag = first.UpdatedStateBag,
        };
        var second = await env.RunAsync(() => activities.RunDurableAgentStepAsync(secondInput));

        Assert.NotNull(second.UpdatedStateBag);
        Assert.Equal([0, 1], provider.ObservedCounts);
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

    private sealed class RecordingDelegatingAgent(
        AIAgent inner,
        Action? before,
        Action? after) : DelegatingAIAgent(inner)
    {
        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            before?.Invoke();
            await foreach (var update in base.RunCoreStreamingAsync(
                messages,
                session,
                options,
                cancellationToken))
            {
                yield return update;
            }
            after?.Invoke();
        }
    }

    private sealed class DisposableRecordingAgent(AIAgent inner)
        : DelegatingAIAgent(inner), IDisposable
    {
        public void Dispose()
        {
        }
    }

    private sealed class CountingReplacementAgent : AIAgent
    {
        public int StreamingRunCount { get; private set; }

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(
            CancellationToken cancellationToken = default) =>
            new(new TemporalAgentSession(TemporalAgentSessionId.WithRandomKey("replacement")));

        protected override ValueTask<System.Text.Json.JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            System.Text.Json.JsonSerializerOptions? jsonSerializerOptions = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            System.Text.Json.JsonElement serializedState,
            System.Text.Json.JsonSerializerOptions? jsonSerializerOptions = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentResponse());

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            StreamingRunCount++;
            await Task.CompletedTask;
            yield return new AgentResponseUpdate();
        }
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

    /// <summary>
    /// A combined <see cref="AIContextProvider"/> / <see cref="IDurableToolSource"/> stub.
    /// <para>
    /// <c>ProvideAIContextAsync</c> returns the tool in <c>AIContext.Tools</c> — simulating what a
    /// real provider (e.g. a search or code-act provider) does internally. The per-iteration strip
    /// in <c>AgentActivities</c> removes those tools from the aggregated context before the
    /// <c>LogError</c> sentinel fires, so no error should appear.
    /// </para>
    /// <para>
    /// <c>GetDurableTools()</c> returns one <see cref="DurableToolRegistrationSpec"/> so the tool
    /// is registered as a durable activity at <c>AddContextProvider</c> time (via <c>AddToolCore</c>).
    /// </para>
    /// </summary>
    private sealed class DurableToolSourceProvider : AIContextProvider, IDurableToolSource
    {
        private readonly AIFunction _tool;

        internal DurableToolSourceProvider(AIFunction tool)
            : base(provideInputMessageFilter: m => m)
        {
            _tool = tool;
        }

        public IReadOnlyList<DurableToolRegistrationSpec> GetDurableTools() =>
            [new DurableToolRegistrationSpec(_tool)];

        protected override ValueTask<AIContext> ProvideAIContextAsync(
            InvokingContext context,
            CancellationToken cancellationToken = default)
        {
            // Return the tool in AIContext.Tools just as a real provider would.
            // AgentActivities strips these out because this provider implements IDurableToolSource.
            return ValueTask.FromResult(new AIContext
            {
                Tools = [(AITool)_tool],
            });
        }
    }

    private sealed class ScopedMarker
    {
        internal Guid Id { get; } = Guid.NewGuid();
    }

    private sealed class ScopeRecordingProvider(ScopedMarker marker, List<Guid> observedScopeIds) : AIContextProvider
    {
        protected override ValueTask<AIContext> ProvideAIContextAsync(
            InvokingContext context,
            CancellationToken cancellationToken = default)
        {
            observedScopeIds.Add(marker.Id);
            return ValueTask.FromResult(new AIContext());
        }
    }

    private sealed class StateBagProvider : AIContextProvider
    {
        internal const string CountKey = "test.provider_count";

        internal List<int> ObservedCounts { get; } = [];

        protected override ValueTask<AIContext> ProvideAIContextAsync(
            InvokingContext context,
            CancellationToken cancellationToken = default)
        {
            var session = context.Session ?? throw new InvalidOperationException("A durable provider requires a session.");
            _ = session.StateBag.TryGetValue(CountKey, out ProviderState? state);
            int count = state?.Count ?? 0;
            ObservedCounts.Add(count);
            session.StateBag.SetValue(CountKey, new ProviderState(count + 1));
            return ValueTask.FromResult(new AIContext());
        }

        private sealed record ProviderState(int Count);
    }
}
