using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TemporalCommunity.Extensions.Tests.Shared;
using Temporalio.Client;
using TemporalCommunity.Extensions.Agents.IntegrationTests.Helpers;
using TemporalCommunity.Extensions.Agents.Tests.StepMode; // shared scaffolding (linked via .csproj)
using TemporalCommunity.Extensions.Agents.Scheduling;
using TemporalCommunity.Extensions.Agents.Workflows;
using Temporalio.Testing;
using Temporalio.Workflows;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.IntegrationTests;

/// <summary>
/// Integration coverage for scheduled / deferred agent jobs (<see cref="AgentJobWorkflow"/>).
/// </summary>
[Trait("Category", "Integration")]
public class ScheduledJobTests : IClassFixture<ScheduledJobEnvironmentFixture>
{
    private readonly ScheduledJobEnvironmentFixture _fixture;
    private WorkflowEnvironment _env => _fixture.Environment;

    private const string InvokeAgentToolActivity = "TemporalCommunity.Extensions.Agents.InvokeAgentTool";
    private const string RunDurableAgentStepActivity = "TemporalCommunity.Extensions.Agents.RunDurableAgentStep";

    public ScheduledJobTests(ScheduledJobEnvironmentFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// P1-4: A write tool registered with <c>opts.NoRetry()</c> must produce an
    /// <c>InvokeAgentTool</c> <c>ActivityTaskScheduled</c> event with
    /// <c>RetryPolicy.MaximumAttempts == 1</c> when dispatched from <see cref="AgentJobWorkflow"/>.
    /// </summary>
    [Fact]
    public async Task ScheduledJob_WriteToolWithNoRetry_UsesMaximumAttempts1()
    {
        var recorder = new RecordingTool
        {
            Name = "write_record",
            Behavior = RecordingToolBehavior.AlwaysFail,
        };
        var aiFunction = recorder.Build();

        var fc = new FunctionCallContent("call-1", "write_record",
            new Dictionary<string, object?> { ["input"] = "data" });
        var scripted = ScriptedChatClient.WithToolCallsThenFinal([fc], "Done.");

        var taskQueue = $"scheduled-job-noretry-{Guid.NewGuid():N}";

        using var workerHost = BuildWorkerHost(scripted, taskQueue,
            registerToolsViaBuilder: builder =>
            {
                builder.AddTool(aiFunction, opts => opts.NoRetry());
            });
        await workerHost.StartAsync();

        try
        {
            var workflowId = $"ta-write-record-scheduled-{Guid.NewGuid():N}";
            var request = new RunRequest("Process this record.");

            var agentsOptions = workerHost.Services.GetRequiredService<TemporalAgentsOptions>();
            var defaultTimeout = agentsOptions.DefaultActivityTimeout;
            var defaultHeartbeat = agentsOptions.DefaultHeartbeatTimeout;
            var defaultRetry = agentsOptions.DefaultRetryPolicy;

            Dictionary<string, ActivityOptions>? toolActivityOptions = null;
            if (agentsOptions.DurableAgentRegistrations.TryGetValue("DurableAgent", out var reg))
            {
                toolActivityOptions = DefaultTemporalAgentClient.BuildDurableAgentToolActivityOptions(
                    reg,
                    reg.ActivityTimeout ?? defaultTimeout,
                    reg.HeartbeatTimeout ?? defaultHeartbeat,
                    reg.RetryPolicy ?? defaultRetry);
            }

            var jobInput = new AgentJobInput
            {
                AgentName = "DurableAgent",
                TaskQueue = taskQueue,
                Request = request,
                ActivityTimeout = defaultTimeout,
                HeartbeatTimeout = defaultHeartbeat,
                RetryPolicy = defaultRetry,
                DurableAgentToolActivityOptions = toolActivityOptions,
            };

            var handle = await _env.Client.StartWorkflowAsync(
                (AgentJobWorkflow wf) => wf.RunAsync(jobInput),
                new WorkflowOptions(workflowId, taskQueue));

            try
            {
                await handle.GetResultAsync();
            }
            catch
            {
                // Expected: tool always fails, workflow errors.
            }

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

            Assert.True(foundToolSchedule,
                "Expected at least one InvokeAgentTool ActivityTaskScheduled event in the job workflow.");
        }
        finally
        {
            await workerHost.StopAsync();
        }
    }

    /// <summary>
    /// Verifies that <see cref="AgentJobWorkflow"/> stops dispatching
    /// <c>InvokeAgentTool</c> activities once it has run <c>MaxToolCallsPerTurn</c> iterations.
    /// </summary>
    [Fact]
    public async Task AgentJobWorkflow_RespectsMaxToolCallsPerTurnFromInput()
    {
        const int maxToolCalls = 2;

        var responses = new List<ChatResponse>();
        for (var i = 0; i < 50; i++)
        {
            var fc = new FunctionCallContent($"call-{i}", "cap_tool",
                new Dictionary<string, object?> { ["input"] = "go" });
            responses.Add(new ChatResponse(new ChatMessage(ChatRole.Assistant, [fc])));
        }

        var scripted = new ScriptedChatClient(responses);
        var recorder = new RecordingTool { Name = "cap_tool" };
        var aiFunction = recorder.Build();

        var taskQueue = $"scheduled-job-{Guid.NewGuid():N}";
        using var host = BuildWorkerHost(scripted, taskQueue,
            registerToolsViaBuilder: builder => builder.AddTool(aiFunction),
            agentName: "CapAgent");
        await host.StartAsync();

        var workflowId = $"ta-capagent-captest{Guid.NewGuid():N}";
        var options = host.Services.GetRequiredService<TemporalAgentsOptions>();
        var jobInput = DefaultTemporalAgentClient.BuildAgentJobInput(
            "CapAgent",
            new RunRequest("Run until cap."),
            options,
            taskQueue) with
        {
            MaxToolCallsPerTurn = maxToolCalls,
        };

        var handle = await _env.Client.StartWorkflowAsync(
            (AgentJobWorkflow wf) => wf.RunAsync(jobInput),
            new WorkflowOptions(workflowId, taskQueue));

        await handle.GetResultAsync();

        var toolScheduleCount = 0;
        await foreach (var ev in handle.FetchHistoryEventsAsync())
        {
            if (ev.ActivityTaskScheduledEventAttributes is { } a &&
                a.ActivityType.Name == InvokeAgentToolActivity)
            {
                toolScheduleCount++;
            }
        }

        Assert.Equal(maxToolCalls, toolScheduleCount);

        await host.StopAsync();
    }

    [Fact]
    public async Task AgentJobWorkflow_PauseForApproval_BlocksToolInsteadOfParking()
    {
        var recorder = new RecordingTool { Name = "destructive_tool" };
        var scripted = ScriptedChatClient.WithToolCallsThenFinal(
            [new FunctionCallContent("call-1", recorder.Name, new Dictionary<string, object?> { ["input"] = "data" })],
            "Blocked tool handled.");
        var taskQueue = $"scheduled-job-approval-{Guid.NewGuid():N}";

        using var host = BuildWorkerHost(scripted, taskQueue, builder =>
        {
            builder.UseApprovalScopes();
            builder.AddTool(recorder.Build(), options => options.RequireApproval().ScopeAware());
        });
        await host.StartAsync();

        try
        {
            var jobInput = DefaultTemporalAgentClient.BuildAgentJobInput(
                "DurableAgent",
                new RunRequest("Attempt the destructive operation."),
                host.Services.GetRequiredService<TemporalAgentsOptions>(),
                taskQueue);

            var handle = await _env.Client.StartWorkflowAsync(
                (AgentJobWorkflow wf) => wf.RunAsync(jobInput),
                new WorkflowOptions($"ta-job-approval-{Guid.NewGuid():N}", taskQueue));
            await handle.GetResultAsync();

            Assert.Equal(0, recorder.CallCount);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task AgentJobWorkflow_EmptyToolSelection_BlocksCallBeforeToolActivity()
    {
        var recorder = new RecordingTool { Name = "job_tool" };
        var scripted = ScriptedChatClient.WithToolCallsThenFinal(
            [new FunctionCallContent("call-1", recorder.Name,
                new Dictionary<string, object?> { ["input"] = "data" })],
            "Blocked call handled.");
        var taskQueue = $"scheduled-job-selection-{Guid.NewGuid():N}";

        using var host = BuildWorkerHost(
            scripted,
            taskQueue,
            builder => builder.AddTool(recorder.Build()),
            agentName: "SelectionAgent");
        await host.StartAsync();

        try
        {
            var request = new RunRequest("Run the job.", enableToolNames: []);
            var input = DefaultTemporalAgentClient.BuildAgentJobInput(
                "SelectionAgent",
                request,
                host.Services.GetRequiredService<TemporalAgentsOptions>(),
                taskQueue);
            var handle = await _env.Client.StartWorkflowAsync(
                (AgentJobWorkflow workflow) => workflow.RunAsync(input),
                new WorkflowOptions($"ta-job-selection-{Guid.NewGuid():N}", taskQueue));

            await handle.GetResultAsync();

            Assert.Equal(0, recorder.CallCount);
            var toolSchedules = 0;
            await foreach (var ev in handle.FetchHistoryEventsAsync())
            {
                if (ev.ActivityTaskScheduledEventAttributes?.ActivityType.Name == InvokeAgentToolActivity)
                    toolSchedules++;
            }
            Assert.Equal(0, toolSchedules);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>OneTimeAgentRun.RetryPolicy</c> must reach the started job. It was a public property
    /// nothing read — <c>ScheduleActivities</c> built its job input without passing it, so a caller
    /// who set it silently got the worker default.
    ///
    /// <para>
    /// This asserts through <c>ScheduleActivities</c> rather than <c>BuildAgentJobInput</c>, because
    /// the defect was in the wiring between them: a unit test on the builder alone passes even with
    /// the argument removed again.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ScheduleOneTime_PerRunRetryPolicy_ReachesTheModelStepActivity()
    {
        var scripted = ScriptedChatClient.WithToolCallsThenFinal([], "Done.");
        var taskQueue = $"scheduled-job-perrun-retry-{Guid.NewGuid():N}";

        using var workerHost = BuildWorkerHost(scripted, taskQueue);
        await workerHost.StartAsync();

        try
        {
            var runId = $"perrun-retry-{Guid.NewGuid():N}";
            var activities = new ScheduleActivities(
                _env.Client,
                taskQueue,
                workerHost.Services.GetRequiredService<TemporalAgentsOptions>());

            await activities.ScheduleOneTimeAgentRunAsync(new OneTimeAgentRun
            {
                AgentName = "DurableAgent",
                RunId = runId,
                Request = new RunRequest("Run the job."),
                // In the past on purpose: the delay clamps to zero and the run starts now.
                RunAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1),
                RetryPolicy = new Temporalio.Common.RetryPolicy { MaximumAttempts = 3 },
            });

            var handle = _env.Client.GetWorkflowHandle($"ta-durableagent-scheduled-{runId}");
            await handle.GetResultAsync();

            int? observed = null;
            await foreach (var ev in handle.FetchHistoryEventsAsync())
            {
                var scheduled = ev.ActivityTaskScheduledEventAttributes;
                if (scheduled?.ActivityType.Name == RunDurableAgentStepActivity)
                {
                    observed = scheduled.RetryPolicy?.MaximumAttempts;
                    break;
                }
            }

            // 3 is the per-run value. 5 would mean the bounded backstop was used instead — i.e. the
            // per-run policy was dropped on the way in.
            Assert.Equal(3, observed);
        }
        finally
        {
            await workerHost.StopAsync();
        }
    }

    /// <summary>
    /// A retry after the one-time job has completed must not create another execution with the
    /// same workflow ID. This is the crash-after-server-acceptance window: conflict policy alone
    /// only protects a still-running execution, while the reuse policy protects the closed one.
    /// </summary>
    [Fact]
    public async Task ScheduleOneTime_RetryAfterJobCompletes_DoesNotRunAgentTwice()
    {
        var scripted = new ScriptedChatClient(
        [
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done.")),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "This must never run.")),
        ]);
        var taskQueue = $"scheduled-job-idempotency-{Guid.NewGuid():N}";

        using var workerHost = BuildWorkerHost(scripted, taskQueue);
        await workerHost.StartAsync();

        try
        {
            var runId = $"idempotency-{Guid.NewGuid():N}";
            var activities = new ScheduleActivities(
                _env.Client,
                taskQueue,
                workerHost.Services.GetRequiredService<TemporalAgentsOptions>());
            var run = new OneTimeAgentRun
            {
                AgentName = "DurableAgent",
                RunId = runId,
                Request = new RunRequest("Run once."),
                RunAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1),
            };

            await activities.ScheduleOneTimeAgentRunAsync(run);
            await _env.Client
                .GetWorkflowHandle($"ta-durableagent-scheduled-{runId}")
                .GetResultAsync();

            await activities.ScheduleOneTimeAgentRunAsync(run);

            // Await the latest execution again. With the old AllowDuplicate reuse policy, the
            // second activity call creates a new run and this wait makes the regression
            // deterministic instead of racing its model activity.
            await _env.Client
                .GetWorkflowHandle($"ta-durableagent-scheduled-{runId}")
                .GetResultAsync();

            Assert.Equal(1, scripted.CallCount);
        }
        finally
        {
            await workerHost.StopAsync();
        }
    }

    /// <summary>
    /// The idempotent duplicate start is silent: an operator watching a retried schedule activity
    /// cannot tell a deduplicated retry from a run that never started. This asserts the debug
    /// breadcrumb exists, fires exactly once, and — because the log sits next to a full
    /// <c>RunRequest</c> — that it carries identifiers only and no prompt content.
    /// </summary>
    [Fact]
    public async Task ScheduleOneTime_DuplicateStart_LogsIdentifiersOnlyExactlyOnce()
    {
        // Distinctive so a substring assertion cannot pass by accident on a common word.
        const string PromptSentinel = "SENTINEL-PROMPT-a3f19c-do-not-log-me";

        var scripted = new ScriptedChatClient(
        [
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done.")),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "This must never run.")),
        ]);
        var taskQueue = $"scheduled-job-dup-log-{Guid.NewGuid():N}";
        var capturing = new CapturingLoggerProvider();

        using var workerHost = BuildWorkerHost(scripted, taskQueue, logging: capturing);
        await workerHost.StartAsync();

        try
        {
            var runId = $"dup-log-{Guid.NewGuid():N}";
            var workflowId = $"ta-durableagent-scheduled-{runId}";

            // Resolved from DI on purpose: the registrar owns the logger wiring, and constructing
            // ScheduleActivities directly here would silently pass with an unwired registration.
            var activities = workerHost.Services.GetRequiredService<ScheduleActivities>();
            var run = new OneTimeAgentRun
            {
                AgentName = "DurableAgent",
                RunId = runId,
                Request = new RunRequest(PromptSentinel),
                RunAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1),
            };

            await activities.ScheduleOneTimeAgentRunAsync(run);
            await _env.Client.GetWorkflowHandle(workflowId).GetResultAsync();

            // Post-completion retry: RejectDuplicate rejects it and the activity swallows that.
            await activities.ScheduleOneTimeAgentRunAsync(run);
            await _env.Client.GetWorkflowHandle(workflowId).GetResultAsync();

            // (a) the agent ran exactly once
            Assert.Equal(1, scripted.CallCount);

            // (b) exactly one duplicate-success debug event
            var duplicateLogs = capturing.Logs
                .Where(l => l.Category == typeof(ScheduleActivities).FullName &&
                            l.Level == LogLevel.Debug &&
                            l.Message.Contains("already exists", StringComparison.Ordinal))
                .ToList();

            Assert.Single(duplicateLogs);
            var entry = duplicateLogs[0];
            Assert.Equal(34, entry.EventId.Id);
            Assert.Null(entry.Exception);

            // It has to be useful: agent, run, and the workflow ID that acted as idempotency key.
            Assert.Contains("DurableAgent", entry.Message, StringComparison.Ordinal);
            Assert.Contains(runId, entry.Message, StringComparison.Ordinal);
            Assert.Contains(workflowId, entry.Message, StringComparison.Ordinal);
            Assert.Contains("idempotency key", entry.Message, StringComparison.Ordinal);

            // (c) no request/prompt content — checked on the entry and across every captured log,
            // so a future refactor that moves the prompt into any log line here also fails.
            Assert.DoesNotContain(PromptSentinel, entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(
                capturing.Logs,
                l => l.Message.Contains(PromptSentinel, StringComparison.Ordinal));
        }
        finally
        {
            await workerHost.StopAsync();
        }
    }

    private IHost BuildWorkerHost(
        ScriptedChatClient scripted,
        string taskQueue,
        Action<DurableAgentBuilder>? registerToolsViaBuilder = null,
        string agentName = "DurableAgent",
        CapturingLoggerProvider? logging = null)
    {
        var builder = Host.CreateApplicationBuilder();
        if (logging is not null)
        {
            builder.Logging.SetMinimumLevel(LogLevel.Debug);
            builder.Logging.AddProvider(logging);
        }

        builder.Services.AddSingleton<ITemporalClient>(_env.Client);
        builder.Services.AddSingleton<IChatClient>(scripted);

        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddTemporalAgents(opts =>
            {
                opts.AddDurableAgent(agentName, agent =>
                {
                    agent.Instructions = "You are a helpful agent.";
                    agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
                    registerToolsViaBuilder?.Invoke(agent);
                });
            });

        return builder.Build();
    }

}

/// <summary>
/// Shared embedded Temporal server fixture for <see cref="ScheduledJobTests"/>.
/// </summary>
public sealed class ScheduledJobEnvironmentFixture : IAsyncLifetime
{
    public WorkflowEnvironment Environment { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Environment = await TestEnvironmentHelper.StartLocalAsync();
        Environment.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;
    }

    public Task DisposeAsync() => Environment.ShutdownAsync();
}
