using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Agents.AI;
using TemporalCommunity.Extensions.Agents.Exceptions;
using TemporalCommunity.Extensions.Agents.Session;
using TemporalCommunity.Extensions.Agents.Workflows;
using Temporalio.Exceptions;
using Temporalio.Testing;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.Tests;

/// <summary>
/// coverage for the per-tool dispatch activity
/// (<c>TemporalCommunity.Extensions.Agents.InvokeAgentTool</c>) used by durable agents. Exercises the
/// activity directly via <see cref="ActivityEnvironment"/> — no Temporal server required.
/// </summary>
public class InvokeAgentToolActivityTests
{
    private static (AgentActivities activities, IServiceProvider services, TemporalAgentsOptions options)
        BuildHarness(Action<TemporalAgentsOptions> configureOptions)
    {
        var serviceCollection = new ServiceCollection();

        var options = new TemporalAgentsOptionsAccessor().Create();
        configureOptions(options);

        serviceCollection.AddSingleton(options);
        var sp = serviceCollection.BuildServiceProvider();

        var activities = new AgentActivities(sp, sp.GetRequiredService<IServiceScopeFactory>());
        return (activities, sp, options);
    }

    [Fact]
    public async Task InvokeAgentTool_WhenToolRegistered_InvokesTool()
    {
        var (activities, _, _) = BuildHarness(opts =>
        {
            opts.AddDurableAgent("WidgetAgent", agent =>
            {
                agent.ChatClient = _ => new StubChatClient();
                agent.AddTool(new RecordingFunction("echo"));
            });
        });

        var input = new InvokeAgentToolInput
        {
            AgentName = "WidgetAgent",
            ToolName = "echo",
            Arguments = new Dictionary<string, object?> { ["value"] = "hello" },
            CallId = "call-1",
        };

        var env = new ActivityEnvironment();
        var result = await env.RunAsync(() => activities.InvokeAgentToolAsync(input));

        Assert.Equal("call-1", result.CallId);
        Assert.Equal("echoed:hello", result.Result);
    }

    [Fact]
    public async Task InvokeAgentTool_WithUnknownAgent_ThrowsAgentNotRegisteredException()
    {
        var (activities, _, _) = BuildHarness(opts =>
        {
            opts.AddDurableAgent("Real", agent => agent.ChatClient = _ => new StubChatClient());
        });

        var input = new InvokeAgentToolInput
        {
            AgentName = "Phantom",
            ToolName = "any",
        };

        var env = new ActivityEnvironment();
        await Assert.ThrowsAsync<AgentNotRegisteredException>(() =>
            env.RunAsync(() => activities.InvokeAgentToolAsync(input)));
    }

    [Fact]
    public async Task InvokeAgentTool_WithUnknownTool_ThrowsInvalidOperationException()
    {
        var (activities, _, _) = BuildHarness(opts =>
        {
            opts.AddDurableAgent("KnownAgent", agent =>
            {
                agent.ChatClient = _ => new StubChatClient();
                agent.AddTool(new RecordingFunction("known_tool"));
            });
        });

        var input = new InvokeAgentToolInput
        {
            AgentName = "KnownAgent",
            ToolName = "unknown_tool",
        };

        var env = new ActivityEnvironment();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            env.RunAsync(() => activities.InvokeAgentToolAsync(input)));
        Assert.Contains("unknown_tool", ex.Message);
        Assert.Contains("KnownAgent", ex.Message);
    }

    [Fact]
    public async Task InvokeAgentTool_PropagatesToolException()
    {
        var (activities, _, _) = BuildHarness(opts =>
        {
            opts.AddDurableAgent("ErrAgent", agent =>
            {
                agent.ChatClient = _ => new StubChatClient();
                agent.AddTool(new ThrowingFunction("boom"));
            });
        });

        var input = new InvokeAgentToolInput
        {
            AgentName = "ErrAgent",
            ToolName = "boom",
        };

        var env = new ActivityEnvironment();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            env.RunAsync(() => activities.InvokeAgentToolAsync(input)));
        Assert.Equal("boom!", ex.Message);
    }

    [Fact]
    public async Task InvokeAgentTool_ReturnsExplicitlyClassifiedSafeFailureToModel()
    {
        var tool = new SafeFailureFunction("lookup");
        var (activities, _, _) = BuildHarness(opts =>
        {
            opts.AddDurableAgent("SafeFailureAgent", agent =>
            {
                agent.ChatClient = _ => new StubChatClient();
                agent.AddTool(tool);
            });
        });

        var result = await new ActivityEnvironment().RunAsync(() =>
            activities.InvokeAgentToolAsync(new InvokeAgentToolInput
            {
                AgentName = "SafeFailureAgent",
                ToolName = "lookup",
                CallId = "call-safe-failure",
            }));

        Assert.Equal("call-safe-failure", result.CallId);
        Assert.Equal("[Tool failed safely] Lookup failed before any change was made.", result.Result);
        Assert.Equal(1, tool.InvocationCount);
    }

    [Fact]
    public async Task InvokeAgentTool_ApprovalRequiredFunctionRejectsUnapprovedDirectInvocation()
    {
        var inner = new RecordingFunction("gated");
        var (activities, _, _) = BuildHarness(opts =>
        {
            opts.AddDurableAgent("ApprovalAgent", agent =>
            {
                agent.ChatClient = _ => new StubChatClient();
                agent.AddTool(new ApprovalRequiredAIFunction(inner));
            });
        });

        var ex = await Assert.ThrowsAsync<ApplicationFailureException>(() =>
            new ActivityEnvironment().RunAsync(() =>
                activities.InvokeAgentToolAsync(new InvokeAgentToolInput
                {
                    AgentName = "ApprovalAgent",
                    ToolName = "gated",
                })));

        Assert.True(ex.NonRetryable);
        Assert.Equal("DurableToolApprovalRequired", ex.ErrorType);
        Assert.Equal(0, inner.InvocationCount);
    }

    [Fact]
    public async Task InvokeAgentTool_ApprovalRequiredFunctionRunsAfterApproval()
    {
        var inner = new RecordingFunction("gated");
        var (activities, _, _) = BuildHarness(opts =>
        {
            opts.AddDurableAgent("ApprovalAgent", agent =>
            {
                agent.ChatClient = _ => new StubChatClient();
                agent.AddTool(new ApprovalRequiredAIFunction(inner));
            });
        });

        var result = await new ActivityEnvironment().RunAsync(() =>
            activities.InvokeAgentToolAsync(new InvokeAgentToolInput
            {
                AgentName = "ApprovalAgent",
                ToolName = "gated",
                Arguments = new Dictionary<string, object?> { ["value"] = "approved" },
                ApprovalAccepted = true,
            }));

        Assert.Equal("echoed:approved", result.Result);
        Assert.Equal(1, inner.InvocationCount);
    }

    [Fact]
    public async Task InvokeAgentTool_ReplacesOversizedResultWithBoundedModelMessage()
    {
        var tool = new LargeResultFunction("large");
        var (activities, _, _) = BuildHarness(opts =>
        {
            opts.AddDurableAgent("LargeResultAgent", agent =>
            {
                agent.ChatClient = _ => new StubChatClient();
                agent.AddTool(tool);
            });
        });

        var result = await new ActivityEnvironment().RunAsync(() =>
            activities.InvokeAgentToolAsync(new InvokeAgentToolInput
            {
                AgentName = "LargeResultAgent",
                ToolName = "large",
                MaximumTransportPayloadSizeBytes = 512,
            }));

        Assert.Equal(InvokeAgentToolResult.ResultTooLargeMessage, result.Result);
        Assert.Equal(1, tool.InvocationCount);
    }

    [Fact]
    public async Task InvokeAgentTool_FactoryRegisteredTool_ResolvesViaFactory()
    {
        var resolveCount = 0;
        var (activities, _, _) = BuildHarness(opts =>
        {
            opts.AddDurableAgent("FactoryAgent", agent =>
            {
                agent.ChatClient = _ => new StubChatClient();
                agent.AddTool("factory_tool", _ =>
                {
                    Interlocked.Increment(ref resolveCount);
                    return new RecordingFunction("factory_tool");
                });
            });
        });

        var input = new InvokeAgentToolInput
        {
            AgentName = "FactoryAgent",
            ToolName = "factory_tool",
            Arguments = new Dictionary<string, object?> { ["value"] = "x" },
        };

        var env = new ActivityEnvironment();
        var first = await env.RunAsync(() => activities.InvokeAgentToolAsync(input));
        var second = await env.RunAsync(() => activities.InvokeAgentToolAsync(input));

        // The implementation factory runs inside each tool activity scope. The immutable
        // registration is cached, but scoped dependencies are not.
        Assert.Equal(2, resolveCount);
        Assert.Equal("echoed:x", first.Result);
        Assert.Equal("echoed:x", second.Result);
    }

    [Fact]
    public async Task InvokeAgentTool_FactoryReturningWrongName_ThrowsNonRetryableApplicationFailure()
    {
        // The factory returns an AIFunction whose Name doesn't match the AddTool-declared name.
        // Without this guard the tool would be invisible to dispatch (lookup happens by string
        // key against the AddTool-declared name). Pin the eager validation — and pin that it is
        // NON-RETRYABLE: a factory cannot resolve a different name on a retry, so a retryable
        // failure only burns the attempt budget before reaching the same outcome. Same shape as
        // the other configuration guards on this path (errorType carries
        // DurableConfigurationException because TemporalFailureInspector matches on that string).
        var (activities, _, _) = BuildHarness(opts =>
        {
            opts.AddDurableAgent("Mismatched", agent =>
            {
                agent.ChatClient = _ => new StubChatClient();
                agent.AddTool("declared_name", _ => new RecordingFunction("actual_name"));
            });
        });

        var input = new InvokeAgentToolInput
        {
            AgentName = "Mismatched",
            ToolName = "declared_name",
        };

        var env = new ActivityEnvironment();
        var ex = await Assert.ThrowsAsync<ApplicationFailureException>(() =>
            env.RunAsync(() => activities.InvokeAgentToolAsync(input)));
        Assert.True(ex.NonRetryable);
        Assert.Equal(
            nameof(TemporalCommunity.Extensions.AI.Exceptions.DurableConfigurationException),
            ex.ErrorType);
        Assert.Contains("declared_name", ex.Message);
        Assert.Contains("actual_name", ex.Message);
        Assert.IsType<TemporalCommunity.Extensions.AI.Exceptions.DurableConfigurationException>(
            ex.InnerException);
    }

    [Fact]
    public async Task InvokeAgentTool_NoArguments_PassesEmptyArguments()
    {
        var (activities, _, _) = BuildHarness(opts =>
        {
            opts.AddDurableAgent("NoArgAgent", agent =>
            {
                agent.ChatClient = _ => new StubChatClient();
                agent.AddTool(new RecordingFunction("ping"));
            });
        });

        var input = new InvokeAgentToolInput
        {
            AgentName = "NoArgAgent",
            ToolName = "ping",
            Arguments = null,
        };

        var env = new ActivityEnvironment();
        var result = await env.RunAsync(() => activities.InvokeAgentToolAsync(input));

        // RecordingFunction returns "echoed:<value>" — when the arg is absent it emits
        // "echoed:<missing>" so we can pin the empty-args path.
        Assert.Equal("echoed:<missing>", result.Result);
    }

    [Fact]
    public async Task InvokeAgentTool_ResolvesToolFromFreshScopeAndSetsArgumentsServices()
    {
        var observedScopes = new List<IServiceProvider>();
        var services = new ServiceCollection();
        services.AddScoped<ScopedToolDependency>();
        var options = new TemporalAgentsOptionsAccessor().Create();
        options.AddDurableAgent("ScopedAgent", agent =>
        {
            agent.ChatClient = _ => new StubChatClient();
            agent.AddTool("scoped", provider =>
            {
                observedScopes.Add(provider);
                return new ScopedServicesFunction("scoped", provider);
            });
        });
        services.AddSingleton(options);
        var root = services.BuildServiceProvider();
        var activities = new AgentActivities(root, root.GetRequiredService<IServiceScopeFactory>());
        var environment = new ActivityEnvironment();
        var input = new InvokeAgentToolInput
        {
            AgentName = "ScopedAgent",
            ToolName = "scoped",
        };

        var first = await environment.RunAsync(() => activities.InvokeAgentToolAsync(input));
        var second = await environment.RunAsync(() => activities.InvokeAgentToolAsync(input));

        Assert.Equal("scope-1", first.Result);
        Assert.Equal("scope-2", second.Result);
        Assert.Equal(2, observedScopes.Count);
        Assert.NotSame(observedScopes[0], observedScopes[1]);
    }

    [Fact]
    public async Task InvokeAgentTool_RejectsOversizedCallIdBeforeInvokingTool()
    {
        var tool = new RecordingFunction("echo");
        var (activities, _, _) = BuildHarness(options =>
            options.AddDurableAgent("BoundedIdAgent", agent =>
            {
                agent.ChatClient = _ => new StubChatClient();
                agent.AddTool(tool);
            }));

        var exception = await Assert.ThrowsAsync<ApplicationFailureException>(() =>
            new ActivityEnvironment().RunAsync(() => activities.InvokeAgentToolAsync(
                new InvokeAgentToolInput
                {
                    AgentName = "BoundedIdAgent",
                    ToolName = "echo",
                    CallId = new string('c', 257),
                })));

        Assert.True(exception.NonRetryable);
        Assert.Equal("DurableToolCallIdTooLong", exception.ErrorType);
        Assert.Equal(0, tool.InvocationCount);
    }

    // ── Test helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Provides access to <see cref="TemporalAgentsOptions"/>'s internal constructor for tests.
    /// In production, options are created via the <see cref="AddTemporalAgents"/> delegate.
    /// </summary>
    private sealed class TemporalAgentsOptionsAccessor
    {
        public TemporalAgentsOptions Create()
        {
            // Reflection: TemporalAgentsOptions has an internal parameterless constructor.
            return (TemporalAgentsOptions)Activator.CreateInstance(typeof(TemporalAgentsOptions), nonPublic: true)!;
        }
    }

    private sealed class StubChatClient : IChatClient
    {
        public void Dispose()
        {
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }

    /// <summary>Records invocations and echoes the "value" argument.</summary>
    private sealed class RecordingFunction : AIFunction
    {
        public RecordingFunction(string name)
        {
            Name = name;
        }

        public override string Name { get; }

        public int InvocationCount { get; private set; }

        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            InvocationCount++;
            var value = arguments.TryGetValue("value", out var v) ? v?.ToString() : "<missing>";
            return new ValueTask<object?>($"echoed:{value}");
        }
    }

    private sealed class ScopedToolDependency
    {
        private static int _nextId;

        public ScopedToolDependency() =>
            this.Id = Interlocked.Increment(ref _nextId);

        public int Id { get; }
    }

    private sealed class ScopedServicesFunction(
        string name,
        IServiceProvider factoryServices) : AIFunction
    {
        public override string Name { get; } = name;

        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            if (!ReferenceEquals(arguments.Services, factoryServices))
            {
                throw new InvalidOperationException("The arguments did not receive the tool scope.");
            }

            var dependency = arguments.Services.GetRequiredService<ScopedToolDependency>();
            return ValueTask.FromResult<object?>($"scope-{dependency.Id}");
        }
    }

    private sealed class ThrowingFunction : AIFunction
    {
        public ThrowingFunction(string name)
        {
            Name = name;
        }

        public override string Name { get; }

        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException($"{Name}!");
    }

    private sealed class SafeFailureFunction : AIFunction
    {
        public SafeFailureFunction(string name) => Name = name;

        public override string Name { get; }
        public int InvocationCount { get; private set; }

        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            InvocationCount++;
            throw new DurableToolFailureException("Lookup failed before any change was made.");
        }
    }

    private sealed class LargeResultFunction : AIFunction
    {
        public LargeResultFunction(string name) => Name = name;

        public override string Name { get; }
        public int InvocationCount { get; private set; }

        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            InvocationCount++;
            return ValueTask.FromResult<object?>(new string('x', 2048));
        }
    }

    // ── Unsupported-path diagnostics ─────────────────────────────────────────
    //
    // A tool reaching for TemporalAgentContext.Current on a path that cannot supply one used to
    // get a bare "No TemporalAgentContext is available in the current async context." These pin
    // that each unsupported path names itself and points at the supported alternative. The two
    // paths are asserted separately because they are reached by different code (parse failure vs
    // agent-name mismatch) and have different remedies.

    /// <summary>
    /// A tool activity without an agent-session workflow ID cannot establish session context.
    /// </summary>
    [Fact]
    public async Task InvokeAgentTool_OutsideAgentWorkflow_ErrorNamesRequiredContext()
    {
        var probe = new ContextProbeFunction("probe");
        var (activities, _, _) = BuildHarness(opts =>
        {
            opts.AddDurableAgent("WidgetAgent", agent =>
            {
                agent.ChatClient = _ => new StubChatClient();
                agent.AddTool(probe);
            });
        });

        var env = new ActivityEnvironment
        {
            // An orchestrating workflow's own ID — no "ta-" prefix, so the parse fails.
            Info = ActivityEnvironment.DefaultInfo with { WorkflowId = "order-orchestrator-42" },
        };

        await env.RunAsync(() => activities.InvokeAgentToolAsync(new InvokeAgentToolInput
        {
            AgentName = "WidgetAgent",
            ToolName = "probe",
            CallId = "call-1",
        }));

        var message = Assert.IsType<InvalidOperationException>(probe.Captured).Message;

        Assert.Contains("workflow ID is not an agent session ID", message, StringComparison.Ordinal);
        Assert.Contains("registered managed agent session", message, StringComparison.Ordinal);
        // The offending ID is named, so the reader can see why it did not parse.
        Assert.Contains("order-orchestrator-42", message, StringComparison.Ordinal);
        Assert.Contains("TemporalAIAgentProxy", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeAgentTool_MismatchedAgentIdentity_UsesGenericDiagnostic()
    {
        var probe = new ContextProbeFunction("probe");
        var (activities, _, _) = BuildHarness(opts =>
        {
            opts.AddDurableAgent("WidgetAgent", agent =>
            {
                agent.ChatClient = _ => new StubChatClient();
                agent.AddTool(probe);
            });
        });

        // The workflow ID parses as another agent session, not the agent owning this tool.
        var env = new ActivityEnvironment
        {
            Info = ActivityEnvironment.DefaultInfo with { WorkflowId = "ta-OtherAgent-run7" },
        };

        await env.RunAsync(() => activities.InvokeAgentToolAsync(new InvokeAgentToolInput
        {
            AgentName = "WidgetAgent",
            ToolName = "probe",
            CallId = "call-1",
        }));

        var message = Assert.IsType<InvalidOperationException>(probe.Captured).Message;

        Assert.Contains("ta-OtherAgent-run7", message, StringComparison.Ordinal);
        Assert.Contains("names agent 'OtherAgent', not 'WidgetAgent'", message, StringComparison.Ordinal);
        Assert.Contains("target the wrong workflow", message, StringComparison.Ordinal);
        Assert.Contains("TemporalAIAgentProxy", message, StringComparison.Ordinal);
        Assert.DoesNotContain("scheduled", message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A recorded reason must not outlive the invocation that recorded it. Without the cleanup in
    /// the activity's <c>finally</c>, an unrelated later failure inherits an unsupported-path
    /// label and sends the reader chasing the wrong problem.
    /// </summary>
    [Fact]
    public async Task InvokeAgentTool_AfterUnsupportedPath_DoesNotLeakReasonToLaterAccess()
    {
        var probe = new ContextProbeFunction("probe");
        var (activities, _, _) = BuildHarness(opts =>
        {
            opts.AddDurableAgent("WidgetAgent", agent =>
            {
                agent.ChatClient = _ => new StubChatClient();
                agent.AddTool(probe);
            });
        });

        var env = new ActivityEnvironment
        {
            Info = ActivityEnvironment.DefaultInfo with { WorkflowId = "order-orchestrator-42" },
        };

        await env.RunAsync(() => activities.InvokeAgentToolAsync(new InvokeAgentToolInput
        {
            AgentName = "WidgetAgent",
            ToolName = "probe",
            CallId = "call-1",
        }));

        Assert.Contains("agent session", probe.Captured!.Message, StringComparison.OrdinalIgnoreCase);

        // Same async context, outside any tool activity: back to the generic message.
        var leaked = Assert.Throws<InvalidOperationException>(() => TemporalAgentContext.Current);

        Assert.DoesNotContain("agent session", leaked.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("order-orchestrator-42", leaked.Message, StringComparison.Ordinal);
    }

    /// <summary>Captures what <c>TemporalAgentContext.Current</c> throws, without failing the tool.</summary>
    private sealed class ContextProbeFunction : AIFunction
    {
        public ContextProbeFunction(string name) => Name = name;

        public override string Name { get; }

        public InvalidOperationException? Captured { get; private set; }

        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            try
            {
                _ = TemporalAgentContext.Current;
                return new ValueTask<object?>("context-available");
            }
            catch (InvalidOperationException ex)
            {
                Captured = ex;
                return new ValueTask<object?>("context-unavailable");
            }
        }
    }
}
