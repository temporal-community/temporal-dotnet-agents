using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Exceptions;
using TemporalCommunity.Extensions.AI;
using TemporalCommunity.Extensions.Agents.IntegrationTests.Helpers;
using TemporalCommunity.Extensions.Agents.Session;
using TemporalCommunity.Extensions.Agents.Tests.StepMode; // shared scaffolding (linked via .csproj)
using Xunit;

namespace TemporalCommunity.Extensions.Agents.IntegrationTests;

/// <summary>
/// Integration coverage for split worker/client deployments. Verifies bugs P1-1 and P1-2:
/// <list type="bullet">
///   <item>P1-2: Write tools registered with <c>NoRetry()</c> use <c>MaximumAttempts = 1</c>
///   even when the workflow was started by a proxy-only client.</item>
/// </list>
/// </summary>
[Trait("Category", "Integration")]
public class SplitDeploymentTests
{
    private const string InvokeAgentToolActivity = "TemporalCommunity.Extensions.Agents.InvokeAgentTool";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SplitDeployment_MissingWorkerAgent_FailsPromptly(bool fireAndForget)
    {
        await using var env = await TestEnvironmentHelper.StartLocalAsync();
        env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;
        var scripted = new ScriptedChatClient(
        [
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done.")),
        ]);
        var taskQueue = $"split-missing-agent-{Guid.NewGuid():N}";
        using var workerHost = BuildWorkerHost(env.Client, scripted, taskQueue, null);
        await workerHost.StartAsync();
        try
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Services.AddSingleton<ITemporalClient>(env.Client);
            builder.Services.AddTemporalAgentProxies(taskQueue, "MissingAgent", "DurableAgent");
            using var clientHost = builder.Build();
            var proxy = clientHost.Services.GetTemporalAgentProxy("MissingAgent");
            var session = (TemporalAgentSession)await proxy.CreateSessionAsync();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            var exception = await Record.ExceptionAsync(() => proxy.RunAsync(
                "Hello",
                session,
                new TemporalAgentRunOptions { IsFireAndForget = fireAndForget },
                cancellation.Token));

            Assert.False(cancellation.IsCancellationRequested,
                $"Missing worker registration did not fail before the deadline: {exception}");
            var updateFailure = Assert.IsType<WorkflowUpdateFailedException>(exception);
            var failure = Assert.IsType<ApplicationFailureException>(updateFailure.InnerException);
            Assert.Equal(nameof(AgentNotRegisteredException), failure.ErrorType);
            Assert.True(failure.NonRetryable);
            Assert.Contains("MissingAgent", failure.Message);
            Assert.Contains("registered", failure.Message);

            var history = await env.Client.GetWorkflowHandle(session.SessionId.WorkflowId).FetchHistoryAsync();
            Assert.DoesNotContain(history.Events, ev => ev.WorkflowTaskFailedEventAttributes is not null);
            Assert.Contains(history.Events, ev => ev.WorkflowExecutionFailedEventAttributes is not null);

            var validProxy = clientHost.Services.GetTemporalAgentProxy("DurableAgent");
            using var validCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var response = await validProxy.RunAsync("Hello", cancellationToken: validCancellation.Token);
            Assert.Contains("Done.", response.Messages[^1].Text);
        }
        finally
        {
            await workerHost.StopAsync();
        }
    }

    [Fact]
    public async Task SplitDeployment_FireAndForget_DoesNotWaitForToolCompletion()
    {
        await using var env = await TestEnvironmentHelper.StartLocalAsync();
        env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;
        var toolStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTool = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tool = AIFunctionFactory.Create(async () =>
        {
            toolStarted.TrySetResult();
            await releaseTool.Task;
            return "Released";
        }, "wait_for_release");
        var scripted = ScriptedChatClient.WithToolCallsThenFinal(
            [new FunctionCallContent("blocked-tool", "wait_for_release", new Dictionary<string, object?>())],
            "Done.");
        var taskQueue = $"split-fire-and-forget-admission-{Guid.NewGuid():N}";
        using var workerHost = BuildWorkerHost(
            env.Client, scripted, taskQueue, agent => agent.AddTool(tool, options => options.NoRetry()));
        await workerHost.StartAsync();
        try
        {
            using var clientHost = BuildClientHost(env.Client, taskQueue);
            var proxy = clientHost.Services.GetTemporalAgentProxy("DurableAgent");
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var response = await proxy.RunAsync(
                "Hello",
                options: new TemporalAgentRunOptions { IsFireAndForget = true },
                cancellationToken: cancellation.Token);

            Assert.Empty(response.Messages);
            await toolStarted.Task.WaitAsync(cancellation.Token);
            Assert.False(releaseTool.Task.IsCompleted);
        }
        finally
        {
            releaseTool.TrySetResult();
            await workerHost.StopAsync();
        }
    }

    /// <summary>
    /// P1-2: In a split deployment (client registers only an agent proxy, worker hosts the
    /// full durable agent), a write tool registered with <c>opts.NoRetry()</c> must use
    /// <c>MaximumAttempts = 1</c> for its <c>InvokeAgentTool</c> activity.
    ///
    /// Before the fix, <c>BuildProxyOnlyAgentWorkflowInput</c> left
    /// <c>DurableAgentToolActivityOptions = null</c> permanently, causing all tools to inherit
    /// the flat job-level <c>RetryPolicy</c> (unbounded retries) regardless of per-tool overrides.
    /// </summary>
    [Fact]
    public async Task SplitDeployment_WriteToolWithNoRetry_UsesMaximumAttempts1()
    {
        await using var env = await TestEnvironmentHelper.StartLocalAsync();
        env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        var recorder = new RecordingTool
        {
            Name = "write_record",
            Behavior = RecordingToolBehavior.AlwaysFail,
        };
        var aiFunction = recorder.Build();

        var fc = new FunctionCallContent("call-1", "write_record",
            new Dictionary<string, object?> { ["input"] = "data" });
        var scripted = ScriptedChatClient.WithToolCallsThenFinal([fc], "Done.");

        var taskQueue = $"split-deploy-noretry-{Guid.NewGuid():N}";

        // Build the worker host (full registration with NoRetry on write tool).
        using var workerHost = BuildWorkerHost(env.Client, scripted, taskQueue,
            configureAgent: agent =>
            {
                agent.AddTool(aiFunction, opts => opts.NoRetry());
            });
        await workerHost.StartAsync();

        try
        {
            // Build the client host (proxy-only, no AddDurableAgent, no IChatClient).
            using var clientHost = BuildClientHost(env.Client, taskQueue);

            var proxy = clientHost.Services.GetTemporalAgentProxy("DurableAgent");
            var session = (TemporalAgentSession)await proxy.CreateSessionAsync();

            // Tool always fails — exception surfaces to caller.
            await Assert.ThrowsAnyAsync<Exception>(async () =>
                await proxy.RunAsync("Hello", session));

            // Only one attempt should have been made (NoRetry = MaximumAttempts 1).
            Assert.Equal(1, recorder.CallCount);

            // Inspect history: the InvokeAgentTool scheduled event must carry MaximumAttempts=1.
            var handle = env.Client.GetWorkflowHandle(session.SessionId.WorkflowId);
            var foundToolSchedule = false;
            await foreach (var ev in handle.FetchHistoryEventsAsync())
            {
                if (ev.ActivityTaskScheduledEventAttributes is { } a &&
                    a.ActivityType.Name == InvokeAgentToolActivity)
                {
                    foundToolSchedule = true;
                    Assert.NotNull(a.RetryPolicy);
                    Assert.Equal(1, a.RetryPolicy.MaximumAttempts);
                    break;
                }
            }

            Assert.True(foundToolSchedule, "Expected at least one InvokeAgentTool ActivityTaskScheduled event.");
        }
        finally
        {
            await workerHost.StopAsync();
        }
    }

    [Fact]
    public async Task SplitDeployment_UnconfiguredTool_UsesOneAttemptDefault()
    {
        await using var env = await TestEnvironmentHelper.StartLocalAsync();
        env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        var recorder = new RecordingTool
        {
            Name = "bounded_failure",
            Behavior = RecordingToolBehavior.AlwaysFail,
        };
        var call = new FunctionCallContent(
            "call-1",
            "bounded_failure",
            new Dictionary<string, object?> { ["input"] = "data" });
        var scripted = ScriptedChatClient.WithToolCallsThenFinal([call], "unreachable");
        var taskQueue = $"split-deploy-bounded-{Guid.NewGuid():N}";

        using var workerHost = BuildWorkerHost(
            env.Client,
            scripted,
            taskQueue,
            agent => agent.AddTool(recorder.Build()));
        await workerHost.StartAsync();
        try
        {
            using var clientHost = BuildClientHost(env.Client, taskQueue);
            var proxy = clientHost.Services.GetTemporalAgentProxy("DurableAgent");
            var session = (TemporalAgentSession)await proxy.CreateSessionAsync();

            await Assert.ThrowsAnyAsync<Exception>(() => proxy.RunAsync("Hello", session));

            Assert.Equal(1, recorder.CallCount);
            var handle = env.Client.GetWorkflowHandle(session.SessionId.WorkflowId);
            await foreach (var ev in handle.FetchHistoryEventsAsync())
            {
                if (ev.ActivityTaskScheduledEventAttributes is { } scheduled
                    && scheduled.ActivityType.Name == InvokeAgentToolActivity)
                {
                    Assert.Equal(1, scheduled.RetryPolicy.MaximumAttempts);
                    return;
                }
            }

            Assert.Fail("Expected an InvokeAgentTool ActivityTaskScheduled event.");
        }
        finally
        {
            await workerHost.StopAsync();
        }
    }

    // ── Host builders ────────────────────────────────────────────────────────────

    [Fact]
    public async Task SplitDeployment_PreparationAndModelUseWorkerActivityPolicy()
    {
        await using var env = await TestEnvironmentHelper.StartLocalAsync();
        env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;
        var scripted = ScriptedChatClient.WithToolCallsThenFinal([], "Done.");
        var taskQueue = $"split-worker-policy-{Guid.NewGuid():N}";
        using var workerHost = BuildWorkerHost(
            env.Client, scripted, taskQueue, configureAgent: null,
            configureOpts: options =>
            {
                options.DefaultActivityTimeout = TimeSpan.FromMinutes(8);
                options.DefaultHeartbeatTimeout = TimeSpan.FromMinutes(3);
                options.DefaultRetryPolicy = new RetryPolicy { MaximumAttempts = 4 };
            });
        using var clientHost = BuildClientHost(env.Client, taskQueue);
        await workerHost.StartAsync();
        try
        {
            var proxy = clientHost.Services.GetTemporalAgentProxy("DurableAgent");
            var session = (TemporalAgentSession)await proxy.CreateSessionAsync();
            await proxy.RunAsync("Hello", session);

            var activityNames = new HashSet<string>(StringComparer.Ordinal);
            await foreach (var ev in env.Client.GetWorkflowHandle(session.SessionId.WorkflowId).FetchHistoryEventsAsync())
            {
                if (ev.ActivityTaskScheduledEventAttributes is not { } scheduled
                    || scheduled.ActivityType.Name is not (
                        "TemporalCommunity.Extensions.Agents.PrepareAgentTurn"
                        or "TemporalCommunity.Extensions.Agents.RunDurableAgentStep"))
                {
                    continue;
                }

                activityNames.Add(scheduled.ActivityType.Name);
                Assert.Equal(TimeSpan.FromMinutes(8), scheduled.StartToCloseTimeout.ToTimeSpan());
                Assert.Equal(TimeSpan.FromMinutes(3), scheduled.HeartbeatTimeout.ToTimeSpan());
                Assert.Equal(4, scheduled.RetryPolicy.MaximumAttempts);
            }

            Assert.Contains("TemporalCommunity.Extensions.Agents.PrepareAgentTurn", activityNames);
            Assert.Contains("TemporalCommunity.Extensions.Agents.RunDurableAgentStep", activityNames);
        }
        finally
        {
            await workerHost.StopAsync();
        }
    }

    [Fact]
    public async Task ToolRetryPolicy_IsAgentOnly_CohostedAndProxyOnlyClients()
    {
        await using var env = await TestEnvironmentHelper.StartLocalAsync();
        env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        foreach (var splitClient in new[] { false, true })
        foreach (var agentRetries in new[] { false, true })
        {
            var recorder = new RecordingTool
            {
                Name = "retry_safe_lookup",
                Behavior = RecordingToolBehavior.AlwaysFail,
            };
            var call = new FunctionCallContent("call-1", recorder.Name,
                new Dictionary<string, object?> { ["input"] = "data" });
            var scripted = ScriptedChatClient.WithToolCallsThenFinal([call], "unreachable");
            var taskQueue = $"worker-retry-default-{Guid.NewGuid():N}";
            using var workerHost = BuildWorkerHost(
                env.Client, scripted, taskQueue,
                agent =>
                {
                    if (agentRetries)
                    {
                        agent.RetryPolicy = new RetryPolicy
                        {
                            MaximumAttempts = 2,
                            InitialInterval = TimeSpan.FromMilliseconds(10),
                            MaximumInterval = TimeSpan.FromMilliseconds(10),
                        };
                    }

                    agent.AddTool(recorder.Build());
                },
                options => options.DefaultRetryPolicy = new RetryPolicy
                {
                    MaximumAttempts = 2,
                    InitialInterval = TimeSpan.FromMilliseconds(10),
                    MaximumInterval = TimeSpan.FromMilliseconds(10),
                });
            using var clientHost = BuildClientHost(env.Client, taskQueue);
            await workerHost.StartAsync();
            try
            {
                var services = splitClient ? clientHost.Services : workerHost.Services;
                var proxy = services.GetTemporalAgentProxy("DurableAgent");
                var session = (TemporalAgentSession)await proxy.CreateSessionAsync();

                await Assert.ThrowsAnyAsync<Exception>(() => proxy.RunAsync("Hello", session));

                var expectedAttempts = agentRetries ? 2 : 1;
                Assert.Equal(expectedAttempts, recorder.CallCount);
                var policies = new List<int>();
                await foreach (var ev in env.Client.GetWorkflowHandle(session.SessionId.WorkflowId).FetchHistoryEventsAsync())
                {
                    if (ev.ActivityTaskScheduledEventAttributes is { } scheduled
                        && scheduled.ActivityType.Name == InvokeAgentToolActivity)
                    {
                        policies.Add(scheduled.RetryPolicy.MaximumAttempts);
                    }
                }
                Assert.Equal(expectedAttempts, Assert.Single(policies));
            }
            finally
            {
                await workerHost.StopAsync();
            }
        }
    }

    private static IHost BuildWorkerHost(
        ITemporalClient client,
        ScriptedChatClient scripted,
        string taskQueue,
        Action<DurableAgentBuilder>? configureAgent,
        Action<TemporalAgentsOptions>? configureOpts = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(client);
        builder.Services.AddSingleton<IChatClient>(scripted);

        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddTemporalAgents(opts =>
            {
                configureOpts?.Invoke(opts);
                opts.AddDurableAgent("DurableAgent", agent =>
                {
                    agent.Instructions = "You are a helpful agent.";
                    agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
                    configureAgent?.Invoke(agent);
                });
            });

        return builder.Build();
    }

    /// <summary>
    /// Builds a client-only host that declares the agent proxy but hosts no worker.
    /// </summary>
    private static IHost BuildClientHost(ITemporalClient client, string taskQueue)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(client);

        // Client registration declares only proxy identity; execution settings live on the worker.
        builder.Services.AddTemporalAgentProxies(taskQueue, "DurableAgent");

        return builder.Build();
    }

}
