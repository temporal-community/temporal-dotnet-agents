using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using TemporalCommunity.Extensions.Agents.Session;
using TemporalCommunity.Extensions.Agents.Workflows;
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

        // First dispatch composes the agent (and runs the factory once); second dispatch reuses
        // the cached state. Pin the cache contract so a future change doesn't accidentally
        // reset state on every call.
        Assert.Equal(1, resolveCount);
        Assert.Equal("echoed:x", first.Result);
        Assert.Equal("echoed:x", second.Result);
    }

    [Fact]
    public async Task InvokeAgentTool_FactoryReturningWrongName_ThrowsInvalidOperationException()
    {
        // The factory returns an AIFunction whose Name doesn't match the AddTool-declared name.
        // Without this guard the tool would be invisible to dispatch (lookup happens by string
        // key against the AddTool-declared name). Pin the eager validation.
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
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            env.RunAsync(() => activities.InvokeAgentToolAsync(input)));
        Assert.Contains("declared_name", ex.Message);
        Assert.Contains("actual_name", ex.Message);
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

    // ── Unsupported-path diagnostics ─────────────────────────────────────────
    //
    // A tool reaching for TemporalAgentContext.Current on a path that cannot supply one used to
    // get a bare "No TemporalAgentContext is available in the current async context." These pin
    // that each unsupported path names itself and points at the supported alternative. The two
    // paths are asserted separately because they are reached by different code (parse failure vs
    // agent-name mismatch) and have different remedies.

    /// <summary>
    /// Workflow-local sub-agent: the activity runs under the customer's orchestrating workflow, so
    /// its workflow ID is not an agent session ID at all.
    /// </summary>
    [Fact]
    public async Task InvokeAgentTool_SubAgentPath_ErrorNamesPathAndAlternative()
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

        Assert.Contains("sub-agent", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WorkflowAgents.GetTemporalAgent", message, StringComparison.Ordinal);
        // The offending ID is named, so the reader can see why it did not parse.
        Assert.Contains("order-orchestrator-42", message, StringComparison.Ordinal);
        // The alternative must be accurate: workflow-parked approval is unsupported here too.
        Assert.Contains("RequireApproval()", message, StringComparison.Ordinal);
        Assert.Contains("TemporalAIAgentProxy", message, StringComparison.Ordinal);
        Assert.DoesNotContain("scheduled", message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Scheduled job: <c>ta-{agent}-scheduled-{runId}</c> parses, but to a different agent identity
    /// — so attaching a session would target the wrong workflow.
    /// </summary>
    [Fact]
    public async Task InvokeAgentTool_ScheduledJobPath_ErrorNamesPathAndAlternative()
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

        // Parses to agent "WidgetAgent-scheduled", key "run7" — deliberately not "WidgetAgent".
        var env = new ActivityEnvironment
        {
            Info = ActivityEnvironment.DefaultInfo with { WorkflowId = "ta-WidgetAgent-scheduled-run7" },
        };

        await env.RunAsync(() => activities.InvokeAgentToolAsync(new InvokeAgentToolInput
        {
            AgentName = "WidgetAgent",
            ToolName = "probe",
            CallId = "call-1",
        }));

        var message = Assert.IsType<InvalidOperationException>(probe.Captured).Message;

        Assert.Contains("scheduled", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ta-WidgetAgent-scheduled-run7", message, StringComparison.Ordinal);
        // Name both sides of the mismatch — the parsed identity and the tool's own agent.
        Assert.Contains("WidgetAgent-scheduled", message, StringComparison.Ordinal);
        Assert.Contains("'WidgetAgent'", message, StringComparison.Ordinal);
        Assert.Contains("RequireApproval()", message, StringComparison.Ordinal);
        Assert.Contains("TemporalAIAgentProxy", message, StringComparison.Ordinal);
        // Must not be mislabelled as the sub-agent path — different cause, different remedy.
        Assert.DoesNotContain("WorkflowAgents.GetTemporalAgent", message, StringComparison.Ordinal);
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

        Assert.Contains("sub-agent", probe.Captured!.Message, StringComparison.OrdinalIgnoreCase);

        // Same async context, outside any tool activity: back to the generic message.
        var leaked = Assert.Throws<InvalidOperationException>(() => TemporalAgentContext.Current);

        Assert.DoesNotContain("sub-agent", leaked.Message, StringComparison.OrdinalIgnoreCase);
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
