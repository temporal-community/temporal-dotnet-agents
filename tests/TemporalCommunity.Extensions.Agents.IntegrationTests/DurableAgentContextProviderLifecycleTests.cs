using System.Collections.Concurrent;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Client;
using Temporalio.Worker;
using TemporalCommunity.Extensions.AI;
using TemporalCommunity.Extensions.Agents.IntegrationTests.Helpers;
using TemporalCommunity.Extensions.Agents.Session;
using TemporalCommunity.Extensions.Agents.Tests.StepMode; // shared scaffolding (linked via .csproj)
using TemporalCommunity.Extensions.Agents.Workflows;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.IntegrationTests;

/// <summary>
/// integration coverage for the load-bearing lifecycle contract Q10/CP1 —
/// <see cref="AIContextProvider.InvokingAsync"/> runs once before the model/tool loop, and
/// <see cref="AIContextProvider.InvokedAsync"/> runs once after the logical turn reaches a
/// terminal result.
/// </summary>
[Trait("Category", "Integration")]
public class DurableAgentContextProviderLifecycleTests
{
    [Fact]
    public async Task DurableAgent_AddContextProvider_FiresOnceForMultiStepTurn()
    {
        await using var env = await TestEnvironmentHelper.StartLocalAsync();
        env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        // Drive a 3-LLM-call turn:
        //   step 1 → 2 tool calls,
        //   step 2 → 1 tool call,
        //   step 3 → final answer.
        // Provider hooks should each fire once for the logical turn.
        var recorder = new RecordingTool { Name = "echo_tool" };
        var aiFunction = recorder.Build();

        var step1Calls = new[]
        {
            new FunctionCallContent("call-1A", "echo_tool", new Dictionary<string, object?> { ["input"] = "A" }),
            new FunctionCallContent("call-1B", "echo_tool", new Dictionary<string, object?> { ["input"] = "B" }),
        };
        var step2Calls = new[]
        {
            new FunctionCallContent("call-2A", "echo_tool", new Dictionary<string, object?> { ["input"] = "C" }),
        };

        var responses = new List<ChatResponse>
        {
            new(new ChatMessage(ChatRole.Assistant, [.. step1Calls])),
            new(new ChatMessage(ChatRole.Assistant, [.. step2Calls])),
            new(new ChatMessage(ChatRole.Assistant, "Final answer.")),
        };
        var scripted = new ScriptedChatClient(responses);

        var provider = new CountingContextProvider();

        using var host = BuildHost(env.Client, scripted, aiFunction, provider);
        await host.StartAsync();
        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("DurableAgent");
            var session = (TemporalAgentSession)await proxy.CreateSessionAsync();
            var response = await proxy.RunAsync("Run multi-step turn.", session);

            Assert.Contains("Final answer.", response.Messages[^1].Text);
            // Three tool invocations across the three iterations.
            Assert.Equal(3, recorder.CallCount);

            Assert.Equal(1, provider.InvokingCount);
            Assert.Equal(1, provider.InvokedCount);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task DurableAgent_AddContextProviderInstance_RegistersAndFires()
    {
        await using var env = await TestEnvironmentHelper.StartLocalAsync();
        env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        var scripted = new ScriptedChatClient(new[]
        {
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Hello.")),
        });
        var provider = new CountingContextProvider();

        // Use the AddContextProvider(instance) overload (no factory) — verifies the simpler
        // entry point also wires through.
        using var host = BuildHost(env.Client, scripted, tool: null, providerInstance: provider);
        await host.StartAsync();
        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("DurableAgent");
            var session = (TemporalAgentSession)await proxy.CreateSessionAsync();
            await proxy.RunAsync("Hi", session);

            Assert.Equal(1, provider.InvokingCount);
            Assert.Equal(1, provider.InvokedCount);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    /// <summary>
    /// A provider that faults in its post-call hook must not cause a second notification of the
    /// providers that already succeeded, and must not discard an LLM response that was produced.
    /// </summary>
    /// <remarks>
    /// The success-path and failure-path <c>InvokedAsync</c> loops are separate. Without a guard
    /// on the success loop, one provider throwing sends the whole step into the failure path,
    /// which notifies <em>every</em> provider again — so a provider earlier in registration order
    /// observes the same LLM step twice, and the completed model call is thrown away.
    /// </remarks>
    [Fact]
    public async Task DurableAgent_ProviderThrowsFromInvokedAsync_NotifiesOthersOnceAndKeepsResult()
    {
        await using var env = await TestEnvironmentHelper.StartLocalAsync();
        env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        var scripted = new ScriptedChatClient(new[]
        {
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Hello.")),
        });

        var counting = new CountingContextProvider();
        var faulting = new ThrowingInvokedProvider();

        // counting is registered first, so it receives the success notification before the
        // faulting provider throws.
        using var host = BuildHost(env.Client, scripted, tool: null, providerInstance: counting,
            extraProvider: faulting);
        await host.StartAsync();
        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("DurableAgent");
            var session = (TemporalAgentSession)await proxy.CreateSessionAsync();

            var response = await proxy.RunAsync("Hi", session);

            // The model call succeeded; a post-invocation fault must not discard it.
            Assert.Contains("Hello.", response.Messages[^1].Text);

            // Exactly one notification for one LLM step — not two.
            Assert.Equal(1, counting.InvokedCount);
            Assert.Equal(1, counting.InvokingCount);

            // The faulting provider was reached once and threw once.
            Assert.Equal(1, faulting.Attempts);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task DurableAgent_ModelFailure_NotifiesProvidersWithInvokeException()
    {
        await using var env = await TestEnvironmentHelper.StartLocalAsync();
        env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        var provider = new FailureRecordingContextProvider();
        using var host = BuildHost(
            env.Client,
            new FailThenSucceedChatClient(failCount: 10),
            tool: null,
            providerInstance: provider);
        await host.StartAsync();
        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("DurableAgent");
            var session = (TemporalAgentSession)await proxy.CreateSessionAsync();

            await Assert.ThrowsAnyAsync<Exception>(() => proxy.RunAsync("Fail this turn.", session));

            Assert.Equal(1, provider.InvokingCount);
            Assert.Equal(1, provider.InvokedCount);
            Assert.Contains("ActivityFailureException", provider.FailureMessage);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task DurableAgent_ProviderSelectsRegisteredTool_UsesFrozenDurableRoute()
    {
        await using var env = await TestEnvironmentHelper.StartLocalAsync();
        env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        var selectedCalls = 0;
        var hiddenCalls = 0;
        var selectedTool = AIFunctionFactory.Create(
            () => { selectedCalls++; return "selected"; },
            new AIFunctionFactoryOptions { Name = "selected_tool" });
        var hiddenTool = AIFunctionFactory.Create(
            () => { hiddenCalls++; return "hidden"; },
            new AIFunctionFactoryOptions { Name = "hidden_tool" });
        var scripted = new ScriptedChatClient(
        [
            new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [
                new FunctionCallContent(
                    "selected-call",
                    "selected_tool",
                    new Dictionary<string, object?>()),
            ])),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done.")),
        ]);
        var taskQueue = $"provider-tool-selection-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(env.Client);
        builder.Services.AddSingleton<IChatClient>(scripted);
        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddTemporalAgents(options => options.AddDurableAgent("ProviderTools", agent =>
            {
                agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
                agent.AddTool(selectedTool);
                agent.AddTool(hiddenTool);
                agent.AddContextProvider(new SelectingRegisteredToolProvider("selected_tool"));
            }));

        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("ProviderTools");
            var session = (TemporalAgentSession)await proxy.CreateSessionAsync();
            await proxy.RunAsync("Use the selected tool.", session);

            Assert.Equal(1, selectedCalls);
            Assert.Equal(0, hiddenCalls);
            Assert.Equal(
                ["selected_tool"],
                scripted.Calls[0].Options!.Tools!.Select(tool => tool.Name).ToArray());
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task DurableAgent_ModelCallOutsidePreparedToolSet_FailsBeforeDispatch()
    {
        await using var env = await TestEnvironmentHelper.StartLocalAsync();
        env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        var selectedCalls = 0;
        var hiddenCalls = 0;
        var selectedTool = AIFunctionFactory.Create(
            () => { selectedCalls++; return "selected"; },
            new AIFunctionFactoryOptions { Name = "selected_tool" });
        var hiddenTool = AIFunctionFactory.Create(
            () => { hiddenCalls++; return "hidden"; },
            new AIFunctionFactoryOptions { Name = "hidden_tool" });
        var scripted = new ScriptedChatClient(
        [
            new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [
                new FunctionCallContent(
                    "hidden-call",
                    "hidden_tool",
                    new Dictionary<string, object?>()),
            ])),
        ]);
        var taskQueue = $"provider-tool-outside-manifest-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(env.Client);
        builder.Services.AddSingleton<IChatClient>(scripted);
        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddTemporalAgents(options => options.AddDurableAgent("ProviderToolManifest", agent =>
            {
                agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
                agent.AddTool(selectedTool);
                agent.AddTool(hiddenTool);
                agent.AddContextProvider(new SelectingRegisteredToolProvider("selected_tool"));
            }));

        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("ProviderToolManifest");
            var session = (TemporalAgentSession)await proxy.CreateSessionAsync();

            await Assert.ThrowsAnyAsync<Exception>(() => proxy.RunAsync("Try the hidden tool.", session));

            Assert.Equal(0, selectedCalls);
            Assert.Equal(0, hiddenCalls);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task DurableAgent_ProviderCompositionPreservesOrderFilteringAttributionAndTools()
    {
        await using var env = await TestEnvironmentHelper.StartLocalAsync();
        env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        var observedOrder = new List<string>();
        var first = new ContextCompositionProvider("first", observedOrder);
        var second = new ContextCompositionProvider("second", observedOrder);
        var tool = AIFunctionFactory.Create(
            () => "tool result",
            new AIFunctionFactoryOptions { Name = "registered_tool" });
        var scripted = new ScriptedChatClient(
        [
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Composed context.")),
        ]);
        var capturing = new CapturingChatClient(scripted);
        using var host = BuildHost(env.Client, capturing, tool, first, second);
        await host.StartAsync();
        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("DurableAgent");
            var session = (TemporalAgentSession)await proxy.CreateSessionAsync();
            await proxy.RunAsync("Use the provider context.", session);

            var call = Assert.Single(scripted.Calls);
            Assert.Equal(["first", "second"], observedOrder);
            Assert.Equal("You are a helpful agent.\nfirst-instructions", second.ObservedInstructions);
            Assert.Equal(["Use the provider context."], second.ObservedInputMessages);
            Assert.Equal(1, second.ObservedToolCount);
            Assert.Equal(
                ["Use the provider context.", "first-message", "second-message"],
                capturing.LastMessages!.Select(message => message.Text).ToArray());
            Assert.All(
                capturing.LastMessages!.Skip(1),
                message => Assert.Equal(
                    AgentRequestMessageSourceType.AIContextProvider,
                    message.GetAgentRequestMessageSourceType()));
            Assert.Equal("You are a helpful agent.\nfirst-instructions\nsecond-instructions", call.Options!.Instructions);
            Assert.Equal(["registered_tool"], call.Options.Tools!.Select(registered => registered.Name));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task DurableAgent_ProviderStateBagPersistsAcrossLogicalTurns()
    {
        await using var env = await TestEnvironmentHelper.StartLocalAsync();
        env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        var scripted = new ScriptedChatClient(
        [
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "First turn.")),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Second turn.")),
        ]);
        using var host = BuildHost(
            env.Client,
            scripted,
            tool: null,
            providerInstance: new TurnStateBagProvider());
        await host.StartAsync();
        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("DurableAgent");
            var session = (TemporalAgentSession)await proxy.CreateSessionAsync();
            await proxy.RunAsync("First.", session);
            await proxy.RunAsync("Second.", session);

            Assert.Equal(2, scripted.Calls.Count);
            Assert.Contains("turn=1", scripted.Calls[0].Messages[^1].Text);
            Assert.Contains("turn=2", scripted.Calls[1].Messages[^1].Text);
            Assert.True(session.StateBag.TryGetValue<string>(
                TurnStateBagProvider.StateBagKey,
                out var finalCount,
                System.Text.Json.JsonSerializerOptions.Default));
            Assert.Equal("2", finalCount);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task DurableAgent_ProviderStateAndLifecycleSurviveContinueAsNewAndReplay()
    {
        await using var env = await TestEnvironmentHelper.StartLocalAsync().WaitAsync(TimeSpan.FromSeconds(30));
        env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;
        var scripted = new ScriptedChatClient(
        [
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "First turn.")),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Second turn.")),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Third turn.")),
        ]);
        var provider = new CountingContextProvider();
        var taskQueue = $"provider-rollover-{Guid.NewGuid():N}";
        using var host = BuildHost(
            env.Client, scripted, null, new TurnStateBagProvider(), provider, taskQueue);
        await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(30));
        var sessionId = TemporalAgentSessionId.WithRandomKey("DurableAgent");
        var workflow = await env.Client.StartWorkflowAsync(
            (AgentWorkflow wf) => wf.RunAsync(new AgentWorkflowInput
            {
                AgentName = "DurableAgent",
                TaskQueue = taskQueue,
                EnableSearchAttributes = false,
                MaxEntryCount = 4,
                ActivityTimeout = TimeSpan.FromSeconds(15),
                RetryPolicy = new Temporalio.Common.RetryPolicy { MaximumAttempts = 1 },
                HistoryCompactionStrategy = DurableHistoryCompactionStrategy.TrimRecentTurns,
                ResolvedWorkerConfig = new AgentWorkerToolConfig
                {
                    MaxToolCallsPerTurn = 20,
                    ToolActivityOptions = new Dictionary<string, Temporalio.Workflows.ActivityOptions>(),
                },
            }),
            new WorkflowOptions(sessionId.WorkflowId, taskQueue)).WaitAsync(TimeSpan.FromSeconds(30));
        var shutdownRequested = false;
        try
        {
            var originalRunId = (await workflow.DescribeAsync()).RunId;
            var proxy = host.Services.GetTemporalAgentProxy("DurableAgent");
            var session = new TemporalAgentSession(sessionId);
            await proxy.RunAsync("First.", session).WaitAsync(TimeSpan.FromSeconds(30));
            await proxy.RunAsync("Second.", session).WaitAsync(TimeSpan.FromSeconds(30));
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while ((await workflow.DescribeAsync()).RunId == originalRunId && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }
            Assert.NotEqual(originalRunId, (await workflow.DescribeAsync()).RunId);
            await proxy.RunAsync("Third.", session).WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(3, provider.InvokingCount);
            Assert.Equal(3, provider.InvokedCount);
            Assert.Equal(3, scripted.CallCount);
            Assert.Contains("turn=3", scripted.Calls[2].Messages[^1].Text);
            Assert.True(session.StateBag.TryGetValue<string>(
                TurnStateBagProvider.StateBagKey, out var count,
                System.Text.Json.JsonSerializerOptions.Default));
            Assert.Equal("3", count);

            await workflow.ExecuteUpdateAsync(wf => wf.RequestShutdownAsync())
                .WaitAsync(TimeSpan.FromSeconds(30));
            shutdownRequested = true;
            await workflow.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(30));
            var options = new WorkflowReplayerOptions
            {
                DataConverter = TemporalAgentDataConverter.Instance,
            };
            options.AddWorkflow(AgentWorkflowWorkerFactory.CreateDefinition(
                host.Services.GetRequiredService<TemporalAgentsOptions>(),
                taskQueue));
            var replayer = new WorkflowReplayer(options);
            var original = env.Client.GetWorkflowHandle<AgentWorkflow>(sessionId.WorkflowId, originalRunId);
            foreach (var handle in new[] { original, workflow })
            {
                var replay = await replayer.ReplayWorkflowAsync(
                    await handle.FetchHistoryAsync().WaitAsync(TimeSpan.FromSeconds(30)),
                    throwOnReplayFailure: false).WaitAsync(TimeSpan.FromSeconds(30));
                Assert.Null(replay.ReplayFailure);
            }
            Assert.Equal(3, provider.InvokingCount);
            Assert.Equal(3, provider.InvokedCount);
            Assert.Equal(3, scripted.CallCount);
        }
        finally
        {
            if (!shutdownRequested)
            {
                await env.Client.GetWorkflowHandle<AgentWorkflow>(sessionId.WorkflowId)
                    .ExecuteUpdateAsync(wf => wf.RequestShutdownAsync())
                    .WaitAsync(TimeSpan.FromSeconds(30));
            }
            await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    [Fact]
    public async Task DurableAgent_ContextProviderFactoryUsesSeparateActivityScopesForPreparationAndCompletion()
    {
        await using var env = await TestEnvironmentHelper.StartLocalAsync();
        env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        var recorder = new ProviderScopeRecorder();
        var scripted = new ScriptedChatClient(
        [
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Scoped provider.")),
        ]);
        var taskQueue = $"provider-scope-lifecycle-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(env.Client);
        builder.Services.AddSingleton<IChatClient>(scripted);
        builder.Services.AddSingleton(recorder);
        builder.Services.AddScoped<ProviderScopeMarker>();
        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddTemporalAgents(options => options.AddDurableAgent("ProviderScope", agent =>
            {
                agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
                agent.AddContextProvider(sp => new ScopedLifecycleProvider(
                    sp.GetRequiredService<ProviderScopeMarker>(),
                    sp.GetRequiredService<ProviderScopeRecorder>()));
            }));

        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("ProviderScope");
            var session = (TemporalAgentSession)await proxy.CreateSessionAsync();
            await proxy.RunAsync("Check scoped lifecycle.", session);

            var invokingScope = Assert.Single(recorder.InvokingScopeIds);
            var invokedScope = Assert.Single(recorder.InvokedScopeIds);
            Assert.NotEqual(invokingScope, invokedScope);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task DurableAgent_Cancellation_RunsDetachedCompletionAndRollsBackProviderState()
    {
        await using var env = await TestEnvironmentHelper.StartLocalAsync();
        env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        var chatClient = new BlockingThenFinalChatClient();
        var provider = new CancellationRecordingProvider();
        var taskQueue = $"provider-cancellation-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(env.Client);
        builder.Services.AddSingleton<IChatClient>(chatClient);
        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddTemporalAgents(options => options.AddDurableAgent("ProviderCancellation", agent =>
            {
                agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
                agent.AddContextProvider(provider);
            }));

        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("ProviderCancellation");
            var session = (TemporalAgentSession)await proxy.CreateSessionAsync();
            const string requestId = "provider-cancellation-request";
            var runTask = proxy.RunAsync(
                "Cancel this turn.",
                session,
                new TemporalAgentRunOptions { RequestId = requestId });

            await chatClient.FirstCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await host.Services.GetRequiredService<ITemporalAgentClient>()
                .CancelTurnAsync(session.SessionId, requestId);
            await Assert.ThrowsAnyAsync<Exception>(() => runTask);

            Assert.True(provider.SawCancellation);

            await proxy.RunAsync("Check rollback.", session);
            Assert.False(provider.NextTurnSawCancelledState);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    /// <summary>Faults from the post-call hook only; its pre-call hook is inert.</summary>
    private sealed class ThrowingInvokedProvider : AIContextProvider
    {
        private long _attempts;

        public ThrowingInvokedProvider()
            : base(provideInputMessageFilter: null,
                   storeInputRequestMessageFilter: null,
                   storeInputResponseMessageFilter: null)
        {
        }

        public int Attempts => (int)Interlocked.Read(ref _attempts);

        protected override ValueTask<AIContext> ProvideAIContextAsync(
            InvokingContext context,
            CancellationToken cancellationToken = default) =>
            new(new AIContext());

        protected override ValueTask InvokedCoreAsync(
            InvokedContext context,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _attempts);
            throw new InvalidOperationException("post-invocation fault");
        }
    }

    private static IHost BuildHost(
        ITemporalClient client,
        IChatClient scripted,
        AIFunction? tool,
        AIContextProvider providerInstance,
        AIContextProvider? extraProvider = null,
        string? taskQueue = null)
    {
        taskQueue ??= $"durable-agent-ctxprov-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(client);
        builder.Services.AddSingleton<IChatClient>(scripted);

        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddTemporalAgents(opts =>
            {
                opts.AddDurableAgent("DurableAgent", agent =>
                {
                    agent.Instructions = "You are a helpful agent.";
                    agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
                    if (tool is not null)
                    {
                        agent.AddTool(tool);
                    }
                    agent.AddContextProvider(providerInstance);
                    if (extraProvider is not null)
                    {
                        agent.AddContextProvider(extraProvider);
                    }
                });
            });

        return builder.Build();
    }

    /// <summary>
    /// Test-only <see cref="AIContextProvider"/> that records logical-turn callback counts.
    /// The provider returns an empty <see cref="AIContext"/> so it has no effect on model output.
    /// </summary>
    /// <remarks>
    /// The two counters must be incremented in <em>different</em> hooks. Incrementing both inside
    /// <c>ProvideAIContextAsync</c> makes them equal by construction, so the post-call assertion
    /// cannot fail no matter what the activity does. <c>InvokedCoreAsync</c> is the overridable
    /// post-call hook on this surface, so the invoked counter is taken there.
    /// </remarks>
    private sealed class CountingContextProvider : AIContextProvider
    {
        private long _invoking;
        private long _invoked;

        public CountingContextProvider()
            : base(provideInputMessageFilter: null,
                   storeInputRequestMessageFilter: null,
                   storeInputResponseMessageFilter: null)
        {
        }

        public int InvokingCount => (int)Interlocked.Read(ref _invoking);

        /// <summary>Counts the real post-call hook, so a double notification is observable.</summary>
        public int InvokedCount => (int)Interlocked.Read(ref _invoked);

        protected override ValueTask<AIContext> ProvideAIContextAsync(
            InvokingContext context,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _invoking);
            return new ValueTask<AIContext>(new AIContext());
        }

        protected override ValueTask InvokedCoreAsync(
            InvokedContext context,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _invoked);
            return default;
        }
    }

    private sealed class FailureRecordingContextProvider : AIContextProvider
    {
        private long _invoking;
        private long _invoked;

        public FailureRecordingContextProvider()
            : base(provideInputMessageFilter: null,
                   storeInputRequestMessageFilter: null,
                   storeInputResponseMessageFilter: null)
        {
        }

        public int InvokingCount => (int)Interlocked.Read(ref _invoking);

        public int InvokedCount => (int)Interlocked.Read(ref _invoked);

        public string? FailureMessage { get; private set; }

        protected override ValueTask<AIContext> ProvideAIContextAsync(
            InvokingContext context,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _invoking);
            return new(new AIContext());
        }

        protected override ValueTask InvokedCoreAsync(
            InvokedContext context,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _invoked);
            FailureMessage = context.InvokeException?.Message;
            return default;
        }
    }

#pragma warning disable MAAI001 // Internal lifecycle override is required to exercise MAF tool selection.
    private sealed class SelectingRegisteredToolProvider(string selectedToolName) : AIContextProvider
    {
        protected override ValueTask<AIContext> ProvideAIContextAsync(
            InvokingContext context,
            CancellationToken cancellationToken = default) =>
            new(new AIContext());

        protected override ValueTask<AIContext> InvokingCoreAsync(
            InvokingContext context,
            CancellationToken cancellationToken = default) =>
            new(new AIContext
            {
                Instructions = context.AIContext.Instructions,
                Messages = context.AIContext.Messages,
                Tools = context.AIContext.Tools?.Where(
                    tool => string.Equals(tool.Name, selectedToolName, StringComparison.Ordinal)),
            });
    }
#pragma warning restore MAAI001

    private sealed class CancellationRecordingProvider : AIContextProvider
    {
        public bool SawCancellation { get; private set; }

        public bool NextTurnSawCancelledState { get; private set; }

        protected override ValueTask<AIContext> ProvideAIContextAsync(
            InvokingContext context,
            CancellationToken cancellationToken = default)
        {
            if (context.Session is TemporalAgentSession session
                && SawCancellation
                && session.StateBag.TryGetValue<string>(
                    "provider.cancelled",
                    out _,
                    System.Text.Json.JsonSerializerOptions.Default))
            {
                NextTurnSawCancelledState = true;
            }

            return new(new AIContext());
        }

        protected override ValueTask InvokedCoreAsync(
            InvokedContext context,
            CancellationToken cancellationToken = default)
        {
            if (context.InvokeException is OperationCanceledException
                && context.Session is TemporalAgentSession session)
            {
                SawCancellation = true;
                session.StateBag.SetValue(
                    "provider.cancelled",
                    "rollback-me",
                    System.Text.Json.JsonSerializerOptions.Default);
            }

            return default;
        }
    }

    private sealed class ContextCompositionProvider(
        string name,
        List<string> observedOrder) : AIContextProvider
    {
        public string? ObservedInstructions { get; private set; }

        public string[] ObservedInputMessages { get; private set; } = [];

        public int ObservedToolCount { get; private set; }

        protected override ValueTask<AIContext> ProvideAIContextAsync(
            InvokingContext context,
            CancellationToken cancellationToken = default)
        {
            observedOrder.Add(name);
            ObservedInstructions = context.AIContext.Instructions;
            ObservedInputMessages = context.AIContext.Messages?
                .Select(message => message.Text ?? string.Empty)
                .ToArray() ?? [];
            ObservedToolCount = context.AIContext.Tools?.Count() ?? 0;

            return new(new AIContext
            {
                Instructions = $"{name}-instructions",
                Messages = [new ChatMessage(ChatRole.System, $"{name}-message")],
            });
        }
    }

    private sealed class TurnStateBagProvider : AIContextProvider
    {
        public const string StateBagKey = "provider.turn-count";

        protected override ValueTask<AIContext> ProvideAIContextAsync(
            InvokingContext context,
            CancellationToken cancellationToken = default)
        {
            var session = context.Session
                ?? throw new InvalidOperationException("The managed provider must receive its agent session.");
            var count = 1;
            if (session.StateBag.TryGetValue<string>(
                    StateBagKey,
                    out var stored,
                    System.Text.Json.JsonSerializerOptions.Default)
                && int.TryParse(stored, out var previous))
            {
                count = previous + 1;
            }

            session.StateBag.SetValue(
                StateBagKey,
                count.ToString(),
                System.Text.Json.JsonSerializerOptions.Default);
            return new(new AIContext
            {
                Messages = [new ChatMessage(ChatRole.System, $"turn={count}")],
            });
        }
    }

    private sealed class ProviderScopeMarker
    {
        public Guid Id { get; } = Guid.NewGuid();
    }

    private sealed class ProviderScopeRecorder
    {
        public ConcurrentQueue<Guid> InvokingScopeIds { get; } = new();

        public ConcurrentQueue<Guid> InvokedScopeIds { get; } = new();
    }

    private sealed class ScopedLifecycleProvider(
        ProviderScopeMarker marker,
        ProviderScopeRecorder recorder) : AIContextProvider
    {
        protected override ValueTask<AIContext> ProvideAIContextAsync(
            InvokingContext context,
            CancellationToken cancellationToken = default)
        {
            recorder.InvokingScopeIds.Enqueue(marker.Id);
            return new(new AIContext());
        }

        protected override ValueTask InvokedCoreAsync(
            InvokedContext context,
            CancellationToken cancellationToken = default)
        {
            recorder.InvokedScopeIds.Enqueue(marker.Id);
            return default;
        }
    }

    private sealed class CapturingChatClient(IChatClient inner) : IChatClient
    {
        public IReadOnlyList<ChatMessage>? LastMessages { get; private set; }

        public ChatClientMetadata Metadata { get; } = new("capturing");

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            LastMessages = messages.ToArray();
            return await inner.GetResponseAsync(messages, options, cancellationToken);
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            await foreach (var update in inner.GetStreamingResponseAsync(messages, options, cancellationToken))
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            inner.GetService(serviceType, serviceKey);

        public void Dispose() => inner.Dispose();
    }

    private sealed class BlockingThenFinalChatClient : IChatClient
    {
        private int _callCount;

        public TaskCompletionSource FirstCallStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ChatClientMetadata Metadata { get; } = new("blocking-then-final");

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _callCount) == 1)
            {
                FirstCallStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "Recovered."));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            foreach (var message in response.Messages)
            {
                yield return new ChatResponseUpdate(message.Role, message.Text);
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
