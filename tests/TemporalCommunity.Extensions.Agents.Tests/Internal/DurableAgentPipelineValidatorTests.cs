using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using TemporalCommunity.Extensions.Agents.Internal;
using TemporalCommunity.Extensions.Agents.Testing;
using TemporalCommunity.Extensions.AI.Exceptions;
using Temporalio.Extensions.Hosting;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.Tests.Internal;

public class DurableAgentPipelineValidatorTests
{
    private const string FunctionInvocationDelegatingAgentFullName =
        "Microsoft.Agents.AI.FunctionInvocationDelegatingAgent";

    [Fact]
    public void PostConfigure_NoAgents_DoesNotThrow()
    {
        var (validator, options) = BuildValidator();

        var ex = Record.Exception(() => validator.PostConfigure(null, options));

        Assert.Null(ex);
    }

    [Fact]
    public void PostConfigure_AgentWithoutConfigurePipeline_DoesNotThrow()
    {
        var (validator, options) = BuildValidator(opts =>
        {
            opts.AddDurableAgent("agent-without-pipeline", a =>
            {
                a.ChatClient = _ => new NoopChatClient();
            });
        });

        var ex = Record.Exception(() => validator.PostConfigure(null, options));

        Assert.Null(ex);
    }

    [Fact]
    public void PostConfigure_PipelineWithFunctionInvocation_ThrowsConflictException()
    {
        // Real MAF API: Use(funcInvocationCallback) — the validator must catch the
        // InvalidOperationException MAF's pre-flight emits when the inner doesn't expose
        // FunctionInvokingChatClient and translate it to our typed exception.
        var (validator, options) = BuildValidator(opts =>
        {
            opts.AddDurableAgent("conflicting-agent", a =>
            {
                a.ChatClient = _ => new NoopChatClient();
                a.ConfigureAgentPipeline = builder =>
                {
                    builder.Use(static (innerAgent, ctx, next, ct) =>
                        next(ctx, ct));
                };
            });
        });

        var ex = Assert.Throws<DurableFunctionInvocationConflictException>(
            () => validator.PostConfigure(null, options));

        Assert.Equal(FunctionInvocationDelegatingAgentFullName, ex.OffendingType);
        Assert.Contains("conflicting-agent", ex.Message);
        Assert.NotNull(ex.InnerException);
        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }

    [Fact]
    public void PostConfigure_PipelineWithBenignDecorator_DoesNotThrow()
    {
        // A pipeline with custom DelegatingAIAgent (no function-invocation middleware) should
        // pass cleanly. This is the standard expected use case.
        var (validator, options) = BuildValidator(opts =>
        {
            opts.AddDurableAgent("benign-agent", a =>
            {
                a.ChatClient = _ => new NoopChatClient();
                a.ConfigureAgentPipeline = builder =>
                {
                    builder.Use(inner => new BenignDelegatingAgent(inner));
                };
            });
        });

        var ex = Record.Exception(() => validator.PostConfigure(null, options));

        Assert.Null(ex);
    }

    [Fact]
    public void PostConfigure_RejectsFactoryThatIgnoresInnerAgent()
    {
        var (validator, options) = BuildValidator(opts =>
        {
            opts.AddDurableAgent("replacement-agent", agent =>
            {
                agent.ChatClient = _ => new NoopChatClient();
                agent.ConfigureAgentPipeline = builder =>
                    builder.Use(_ => new NoOpAgent());
            });
        });

        var ex = Assert.Throws<DurableConfigurationException>(
            () => validator.PostConfigure(null, options));

        Assert.Contains("replacement-agent", ex.Message);
        Assert.Contains("DelegatingAIAgent", ex.Message);
        Assert.Contains("inner", ex.Message);
    }

    [Fact]
    public void PostConfigure_AcceptsDelegatingAgentThatPreservesInner()
    {
        var (validator, options) = BuildValidator(opts =>
        {
            opts.AddDurableAgent("preserved-agent", agent =>
            {
                agent.ChatClient = _ => new NoopChatClient();
                agent.ConfigureAgentPipeline = builder =>
                    builder.Use(inner => new BenignDelegatingAgent(inner));
            });
        });

        var ex = Record.Exception(() => validator.PostConfigure(null, options));

        Assert.Null(ex);
    }

    [Fact]
    public void PostConfigure_AcceptsMafLoggingPipeline()
    {
        var (validator, options) = BuildValidator(opts =>
        {
            opts.AddDurableAgent("logging-agent", agent =>
            {
                agent.ChatClient = _ => new NoopChatClient();
                agent.ConfigureAgentPipeline = builder => builder.UseLogging();
            });
        });

        Assert.Null(Record.Exception(() => validator.PostConfigure(null, options)));
    }

    [Fact]
    public void PostConfigure_AcceptsMafOpenTelemetryPipeline()
    {
        var (validator, options) = BuildValidator(opts =>
        {
            opts.AddDurableAgent("otel-agent", agent =>
            {
                agent.ChatClient = _ => new NoopChatClient();
                agent.ConfigureAgentPipeline = builder => builder.UseOpenTelemetry();
            });
        });

        Assert.Null(Record.Exception(() => validator.PostConfigure(null, options)));
    }

    [Fact]
    public void PostConfigure_RejectsOpaqueAIAgentThatManuallyDelegates()
    {
        var (validator, options) = BuildValidator(opts =>
        {
            opts.AddDurableAgent("opaque-agent", agent =>
            {
                agent.ChatClient = _ => new NoopChatClient();
                agent.ConfigureAgentPipeline = builder =>
                    builder.Use(inner => new OpaqueForwardingAgent(inner));
            });
        });

        var ex = Assert.Throws<DurableConfigurationException>(
            () => validator.PostConfigure(null, options));

        Assert.Contains("opaque-agent", ex.Message);
        Assert.Contains("DelegatingAIAgent", ex.Message);
    }

    [Fact]
    public void PostConfigure_SkipDryRunCCheck_True_DoesNotValidate()
    {
        // Even with a conflicting pipeline, SkipDryRunCCheck = true makes the validator a no-op.
        var (validator, options) = BuildValidator(opts =>
        {
            opts.SkipDryRunCCheck = true;
            opts.AddDurableAgent("would-conflict", a =>
            {
                a.ChatClient = _ => new NoopChatClient();
                a.ConfigureAgentPipeline = builder =>
                {
                    builder.Use(static (innerAgent, ctx, next, ct) =>
                        next(ctx, ct));
                };
            });
        });

        var ex = Record.Exception(() => validator.PostConfigure(null, options));

        Assert.Null(ex);
    }

    [Fact]
    public void PostConfigure_DefaultConfigureAgentPipelineUsed_WhenPerAgentNull()
    {
        // When per-agent ConfigureAgentPipeline is unset, the worker-level default is used.
        // A conflicting default should still fail validation.
        TemporalAgentsOptions? capturedOptions = null;
        var (validator, options) = BuildValidator(opts =>
        {
            opts.DefaultConfigureAgentPipeline = builder =>
            {
                builder.Use(static (innerAgent, ctx, next, ct) => next(ctx, ct));
            };
            opts.AddDurableAgent("inherits-default", a =>
            {
                a.ChatClient = _ => new NoopChatClient();
                // No per-agent ConfigureAgentPipeline — inherits the worker default.
            });
            capturedOptions = opts;
        });

        Assert.NotNull(capturedOptions);
        Assert.NotNull(capturedOptions.DefaultConfigureAgentPipeline);

        var ex = Assert.Throws<DurableFunctionInvocationConflictException>(
            () => validator.PostConfigure(null, options));

        Assert.Contains("inherits-default", ex.Message);
    }

    [Fact]
    public void PostConfigure_PerAgentOverridesDefault()
    {
        // Per-agent ConfigureAgentPipeline takes precedence over the worker default.
        // A safe per-agent override should bypass a conflicting default.
        var (validator, options) = BuildValidator(opts =>
        {
            opts.DefaultConfigureAgentPipeline = builder =>
            {
                builder.Use(static (innerAgent, ctx, next, ct) => next(ctx, ct));
            };
            opts.AddDurableAgent("overrides-default", a =>
            {
                a.ChatClient = _ => new NoopChatClient();
                a.ConfigureAgentPipeline = builder =>
                {
                    builder.Use(inner => new BenignDelegatingAgent(inner));
                };
            });
        });

        var ex = Record.Exception(() => validator.PostConfigure(null, options));

        Assert.Null(ex);
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static (DurableAgentPipelineValidator Validator, TemporalWorkerServiceOptions WorkerOptions)
        BuildValidator(Action<TemporalAgentsOptions>? configure = null)
    {
        var agentsOptions = TemporalAgentsOptionsTestAccessor.New();
        configure?.Invoke(agentsOptions);

        var services = new ServiceCollection();
        services.AddLogging();
        var serviceProvider = services.BuildServiceProvider();

        var validator = new DurableAgentPipelineValidator(agentsOptions, serviceProvider);
        var workerOptions = new TemporalWorkerServiceOptions();

        return (validator, workerOptions);
    }

    /// <summary>
    /// Reflection-based accessor for the internal <c>TemporalAgentsOptions</c> constructor.
    /// The type's ctor is internal — tests in this assembly can use it directly because of
    /// <c>InternalsVisibleTo</c>, but the C# compiler still requires an `internal` keyword path.
    /// </summary>
    private static class TemporalAgentsOptionsTestAccessor
    {
        public static TemporalAgentsOptions New() => new();
    }

    private sealed class NoopChatClient : IChatClient
    {
        public void Dispose() { }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse());

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            AsyncEnumerable.Empty<ChatResponseUpdate>();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }

    private sealed class BenignDelegatingAgent : DelegatingAIAgent
    {
        public BenignDelegatingAgent(AIAgent inner) : base(inner) { }
    }

    private sealed class OpaqueForwardingAgent(AIAgent inner) : AIAgent
    {
        protected override ValueTask<AgentSession> CreateSessionCoreAsync(
            CancellationToken cancellationToken = default) =>
            inner.CreateSessionAsync(cancellationToken);

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement serializedState,
            JsonSerializerOptions? jsonSerializerOptions = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            CancellationToken cancellationToken = default) =>
            inner.RunAsync(messages, session, options, cancellationToken);

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var update in inner.RunStreamingAsync(
                messages,
                session,
                options,
                cancellationToken))
            {
                yield return update;
            }
        }
    }
}
