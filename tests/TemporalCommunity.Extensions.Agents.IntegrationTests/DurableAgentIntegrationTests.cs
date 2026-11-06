using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Client;
using TemporalCommunity.Extensions.Agents.IntegrationTests.Helpers;
using TemporalCommunity.Extensions.Agents.Session;
using TemporalCommunity.Extensions.Agents.Tests.StepMode; // shared scaffolding (linked via .csproj)
using TemporalCommunity.Extensions.Agents.Tools;
using TemporalCommunity.Extensions.Agents.Workflows;
using TemporalCommunity.Extensions.AI;
using TemporalCommunity.Extensions.AI.Tools;
using Temporalio.Testing;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.IntegrationTests;

/// <summary>
/// End-to-end integration coverage for durable agents registered via
/// <c>TemporalAgentsOptions.AddDurableAgent</c>. Exercises the new workflow loop:
/// <c>TemporalCommunity.Extensions.Agents.RunDurableAgentStep</c> for the LLM call and
/// <c>TemporalCommunity.Extensions.Agents.InvokeAgentTool</c> per tool dispatch, in model order.
/// </summary>
[Trait("Category", "Integration")]
public class DurableAgentIntegrationTests : IClassFixture<DurableAgentEnvironmentFixture>
{
    private readonly DurableAgentEnvironmentFixture _fixture;
    private WorkflowEnvironment _env => _fixture.Environment;

    private const string RunDurableAgentStepActivity = "TemporalCommunity.Extensions.Agents.RunDurableAgentStep";
    private const string InvokeAgentToolActivity = "TemporalCommunity.Extensions.Agents.InvokeAgentTool";

    public DurableAgentIntegrationTests(DurableAgentEnvironmentFixture fixture)
    {
        _fixture = fixture;
    }

    // ── Single turn, no tool calls ──────────────────────────────────────────────

    [Fact]
    public async Task DurableAgent_SingleTurnNoTools_ReturnsResponse()
    {
        var scripted = new ScriptedChatClient([
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Final answer.")),
        ]);

        using var host = BuildHost(scripted, tools: [], configureAgent: null);
        await host.StartAsync();

        var proxy = host.Services.GetTemporalAgentProxy("DurableAgent");
        var session = await proxy.CreateSessionAsync();
        var response = await proxy.RunAsync("Hi", session);

        Assert.NotNull(response);
        Assert.Contains("Final answer.", response.Messages[^1].Text);

        var sessionId = ((TemporalAgentSession)session).SessionId;
        var handle = _env.Client.GetWorkflowHandle(sessionId.WorkflowId);
        var activityNames = await CollectActivityNamesAsync(handle);

        Assert.Contains(RunDurableAgentStepActivity, activityNames);
        Assert.DoesNotContain(InvokeAgentToolActivity, activityNames);

        await host.StopAsync();
    }

    [Fact]
    public async Task DurableAgent_PreservesEveryModelResponseMessage()
    {
        var scripted = new ScriptedChatClient([
            new ChatResponse([
                new ChatMessage(ChatRole.Assistant, "First response message."),
                new ChatMessage(ChatRole.Assistant, "Final answer."),
            ]),
        ]);

        using var host = BuildHost(scripted, tools: [], configureAgent: null);
        await host.StartAsync();

        var proxy = host.Services.GetTemporalAgentProxy("DurableAgent");
        var session = await proxy.CreateSessionAsync();
        var response = await proxy.RunAsync("Hi", session);

        Assert.Equal(
            ["First response message.", "Final answer."],
            response.Messages.Select(message => message.Text).ToArray());

        await host.StopAsync();
    }

    // ── Single tool call → final answer ─────────────────────────────────────────

    [Fact]
    public async Task DurableAgent_SingleToolCall_DispatchesToolActivity()
    {
        var recorder = new RecordingTool { Name = "echo_tool" };
        var aiFunction = recorder.Build();
        var fc = new FunctionCallContent("call-1", "echo_tool",
            new Dictionary<string, object?> { ["input"] = "hello" });
        var scripted = ScriptedChatClient.WithToolCallsThenFinal([fc], "All done.");

        using var host = BuildHost(scripted, tools: [aiFunction], configureAgent: null);
        await host.StartAsync();

        var proxy = host.Services.GetTemporalAgentProxy("DurableAgent");
        var session = await proxy.CreateSessionAsync();
        var response = await proxy.RunAsync("Hi", session);

        Assert.NotNull(response);
        Assert.Equal(1, recorder.CallCount);

        var sessionId = ((TemporalAgentSession)session).SessionId;
        var handle = _env.Client.GetWorkflowHandle(sessionId.WorkflowId);
        var activityNames = await CollectActivityNamesAsync(handle);

        var stepCount = activityNames.Count(n => n == RunDurableAgentStepActivity);
        var toolCount = activityNames.Count(n => n == InvokeAgentToolActivity);

        Assert.True(stepCount >= 2, $"expected >= 2 RunDurableAgentStep activities; got {stepCount}");
        Assert.Equal(1, toolCount);
        // Sanity: legacy step-mode path should NOT have run.
        Assert.DoesNotContain("TemporalCommunity.Extensions.Agents.RunAgentStep", activityNames);

        await host.StopAsync();
    }

    [Fact]
    public async Task DurableAgent_DisabledToolCalls_BlocksModelReturnedCallBeforeDispatch()
    {
        var recorder = new RecordingTool { Name = "echo_tool" };
        var scripted = ScriptedChatClient.WithToolCallsThenFinal(
            [new FunctionCallContent("call-1", recorder.Name,
                new Dictionary<string, object?> { ["input"] = "hello" })],
            "Blocked call handled.");

        using var host = BuildHost(scripted, [recorder.Build()], configureAgent: agent =>
            agent.AddToolInterceptor(_ => new ProceedingInterceptor()));
        await host.StartAsync();

        var proxy = host.Services.GetTemporalAgentProxy("DurableAgent");
        var session = await proxy.CreateSessionAsync();
        var response = await proxy.RunAsync(
            "Hi",
            session,
            new TemporalAgentRunOptions { EnableToolCalls = false });

        Assert.Contains("Blocked call handled.", response.Messages[^1].Text);
        Assert.Equal(0, recorder.CallCount);
        Assert.Null(scripted.Calls[0].Options?.Tools);

        var sessionId = ((TemporalAgentSession)session).SessionId;
        var activityNames = await CollectActivityNamesAsync(
            _env.Client.GetWorkflowHandle(sessionId.WorkflowId));
        Assert.DoesNotContain(InvokeAgentToolActivity, activityNames);
        Assert.DoesNotContain("TemporalCommunity.Extensions.Agents.RunToolInterceptor", activityNames);

        await host.StopAsync();
    }

    [Fact]
    public async Task DurableAgent_RepeatedBlockedCalls_StopAtIterationLimitWithoutDispatch()
    {
        const int cap = 3;
        var recorder = new RecordingTool { Name = "loop_tool" };
        var responses = Enumerable.Range(0, cap + 2)
            .Select(i => new ChatResponse(new ChatMessage(
                ChatRole.Assistant,
                [new FunctionCallContent($"call-{i}", recorder.Name,
                    new Dictionary<string, object?> { ["input"] = "go" })])))
            .ToArray();
        var scripted = new ScriptedChatClient(responses);

        using var host = BuildHost(scripted, [recorder.Build()], agent =>
            agent.MaxToolCallsPerTurn = cap);
        await host.StartAsync();

        var proxy = host.Services.GetTemporalAgentProxy("DurableAgent");
        var session = await proxy.CreateSessionAsync();
        var response = await proxy.RunAsync(
            "Hi",
            session,
            new TemporalAgentRunOptions { EnableToolCalls = false });

        Assert.Equal(cap, scripted.CallCount);
        Assert.Equal(0, recorder.CallCount);
        Assert.Contains("Maximum tool-call iterations", response.Messages[^1].Text);

        var sessionId = ((TemporalAgentSession)session).SessionId;
        var activityNames = await CollectActivityNamesAsync(
            _env.Client.GetWorkflowHandle(sessionId.WorkflowId));
        Assert.DoesNotContain(InvokeAgentToolActivity, activityNames);

        await host.StopAsync();
    }

    [Fact]
    public async Task DurableAgent_SubsetSelection_PreservesMixedResultOrderAndDispatchesOnlyAllowedCall()
    {
        var allowed = new RecordingTool { Name = "allowed_tool" };
        var excluded = new RecordingTool { Name = "excluded_tool" };
        var excludedCallId = "excluded-call-id";
        var allowedCallId = "allowed-call-id";
        var scripted = ScriptedChatClient.WithToolCallsThenFinal(
        [
            new FunctionCallContent(excludedCallId, excluded.Name,
                new Dictionary<string, object?> { ["input"] = "blocked" }),
            new FunctionCallContent(allowedCallId, allowed.Name,
                new Dictionary<string, object?> { ["input"] = "allowed" }),
        ], "Mixed batch handled.");

        using var host = BuildHost(scripted, [allowed.Build(), excluded.Build()], configureAgent: null);
        await host.StartAsync();

        var proxy = host.Services.GetTemporalAgentProxy("DurableAgent");
        var session = await proxy.CreateSessionAsync();
        await proxy.RunAsync(
            "Hi",
            session,
            new TemporalAgentRunOptions { EnableToolNames = ["ALLOWED_TOOL"] });

        Assert.Equal(1, allowed.CallCount);
        Assert.Equal(0, excluded.CallCount);
        Assert.Equal(["allowed_tool"], scripted.Calls[0].Options!.Tools!.Select(t => t.Name));

        var resultMessage = scripted.Calls[1].Messages.Single(message => message.Role == ChatRole.Tool);
        var results = resultMessage.Contents.OfType<FunctionResultContent>().ToArray();
        Assert.Equal(2, results.Length);
        Assert.Equal(excludedCallId, results[0].CallId);
        Assert.Equal(AgentRunToolSelectionPolicy.BlockedResult, results[0].Result?.ToString());
        Assert.Equal(allowedCallId, results[1].CallId);

        var sessionId = ((TemporalAgentSession)session).SessionId;
        var activityNames = await CollectActivityNamesAsync(
            _env.Client.GetWorkflowHandle(sessionId.WorkflowId));
        Assert.Equal(1, activityNames.Count(name => name == InvokeAgentToolActivity));

        await host.StopAsync();
    }

    [Fact]
    public async Task DurableAgent_InvalidDuplicateCallIdsFailBeforeToolDispatch()
    {
        var recorder = new RecordingTool { Name = "echo_tool" };
        var scripted = new ScriptedChatClient([
            new ChatResponse([
                new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("duplicate-call", recorder.Name)]),
                new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("duplicate-call", recorder.Name)]),
            ]),
        ]);

        using var host = BuildHost(scripted, [recorder.Build()], configureAgent: null);
        await host.StartAsync();

        var proxy = host.Services.GetTemporalAgentProxy("DurableAgent");
        var session = await proxy.CreateSessionAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => proxy.RunAsync("Hi", session));

        Assert.Equal(0, recorder.CallCount);
        var sessionId = ((TemporalAgentSession)session).SessionId;
        var activityNames = await CollectActivityNamesAsync(
            _env.Client.GetWorkflowHandle(sessionId.WorkflowId));
        Assert.DoesNotContain(InvokeAgentToolActivity, activityNames);

        await host.StopAsync();
    }

    // ── Multiple tool calls dispatch serially in model order ────────────────────

    [Fact]
    public async Task DurableAgent_MultipleToolCalls_DispatchSeriallyInModelOrder()
    {
        var recorder = new RecordingTool { Name = "echo_tool" };
        var aiFunction = recorder.Build();

        var toolCalls = new List<FunctionCallContent>
        {
            new("call-A", "echo_tool", new Dictionary<string, object?> { ["input"] = "A" }),
            new("call-B", "echo_tool", new Dictionary<string, object?> { ["input"] = "B" }),
            new("call-C", "echo_tool", new Dictionary<string, object?> { ["input"] = "C" }),
        };
        var scripted = ScriptedChatClient.WithToolCallsThenFinal(toolCalls, "Final.");

        using var host = BuildHost(scripted, tools: [aiFunction], configureAgent: null);
        await host.StartAsync();

        var proxy = host.Services.GetTemporalAgentProxy("DurableAgent");
        var session = await proxy.CreateSessionAsync();
        var response = await proxy.RunAsync("Hi", session);
        Assert.NotNull(response);

        Assert.Equal(3, recorder.CallCount);

        var sessionId = ((TemporalAgentSession)session).SessionId;
        var handle = _env.Client.GetWorkflowHandle(sessionId.WorkflowId);
        var scheduleIndices = new List<long>();
        var completionIndices = new List<long>();
        var scheduledTypes = new Dictionary<long, string>();
        await foreach (var ev in handle.FetchHistoryEventsAsync())
        {
            if (ev.ActivityTaskScheduledEventAttributes is { } scheduled)
            {
                scheduledTypes[ev.EventId] = scheduled.ActivityType.Name;
                if (scheduled.ActivityType.Name == InvokeAgentToolActivity)
                {
                    scheduleIndices.Add(ev.EventId);
                }
            }
            else if (ev.ActivityTaskCompletedEventAttributes is { } completed
                && scheduledTypes.TryGetValue(completed.ScheduledEventId, out var typeName)
                && typeName == InvokeAgentToolActivity)
            {
                completionIndices.Add(ev.EventId);
            }
        }

        Assert.Equal(3, scheduleIndices.Count);
        Assert.Equal(3, completionIndices.Count);
        for (var index = 0; index < scheduleIndices.Count; index++)
        {
            Assert.True(scheduleIndices[index] < completionIndices[index],
                $"tool {index} completed before it was scheduled");
            if (index + 1 < scheduleIndices.Count)
            {
                Assert.True(completionIndices[index] < scheduleIndices[index + 1],
                    $"tool {index + 1} was scheduled before tool {index} completed");
            }
        }

        await host.StopAsync();
    }

    // ── Iteration cap returns structured error response ─────────────────────────

    [Fact]
    public async Task DurableAgent_HitsIterationCap_ReturnsStructuredError()
    {
        var recorder = new RecordingTool { Name = "loop_tool" };
        var aiFunction = recorder.Build();

        // Scripted client always returns a tool call — never converges on a final answer.
        var responses = new List<ChatResponse>();
        for (var i = 0; i < 50; i++)
        {
            var fc = new FunctionCallContent($"call-{i}", "loop_tool",
                new Dictionary<string, object?> { ["input"] = "go" });
            responses.Add(new ChatResponse(new ChatMessage(ChatRole.Assistant, [fc])));
        }
        var scripted = new ScriptedChatClient(responses);

        const int cap = 3;
        using var host = BuildHost(scripted, tools: [aiFunction], configureAgent: agent =>
        {
            agent.MaxToolCallsPerTurn = cap;
        });
        await host.StartAsync();

        var proxy = host.Services.GetTemporalAgentProxy("DurableAgent");
        var session = await proxy.CreateSessionAsync();
        var response = await proxy.RunAsync("Hi", session);

        Assert.Equal(cap, recorder.CallCount);

        var finalText = response.Messages[^1].Text;
        Assert.Contains("Maximum tool-call iterations", finalText, StringComparison.Ordinal);

        await host.StopAsync();
    }

    // ── Default tool policy allows one activity attempt ─────────────────────────

    [Fact]
    public async Task DurableAgent_DefaultToolPolicy_DoesNotRetry()
    {
        var recorder = new RecordingTool
        {
            Name = "send_email",
            Behavior = RecordingToolBehavior.AlwaysFail,
        };
        var aiFunction = recorder.Build();
        var fc = new FunctionCallContent("call-1", "send_email",
            new Dictionary<string, object?> { ["input"] = "draft" });
        // The final response is never reached because the tool fails. We still script it for the
        // case where the workflow would recover.
        var scripted = ScriptedChatClient.WithToolCallsThenFinal([fc], "Done.");

        using var host = BuildHost(scripted, tools: [], configureAgent: null, registerToolsViaBuilder: builder =>
        {
            builder.AddTool(aiFunction);
        });
        await host.StartAsync();

        var proxy = host.Services.GetTemporalAgentProxy("DurableAgent");
        var session = await proxy.CreateSessionAsync();

        // Tool failure surfaces to caller; we don't pin the exact exception type.
        await Assert.ThrowsAnyAsync<Exception>(async () => await proxy.RunAsync("Hi", session));

        Assert.Equal(1, recorder.CallCount);

        // Defensive: confirm exactly one InvokeAgentTool ActivityTaskScheduled event.
        var sessionId = ((TemporalAgentSession)session).SessionId;
        var handle = _env.Client.GetWorkflowHandle(sessionId.WorkflowId);
        var invokeAgentToolScheduleCount = 0;
        await foreach (var ev in handle.FetchHistoryEventsAsync())
        {
            if (ev.ActivityTaskScheduledEventAttributes is { } a &&
                a.ActivityType.Name == InvokeAgentToolActivity)
            {
                invokeAgentToolScheduleCount++;
            }
        }
        Assert.Equal(1, invokeAgentToolScheduleCount);

        await host.StopAsync();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static async Task<List<string>> CollectActivityNamesAsync(WorkflowHandle handle)
    {
        var activityNames = new List<string>();
        await foreach (var ev in handle.FetchHistoryEventsAsync())
        {
            if (ev.ActivityTaskScheduledEventAttributes is { } a)
            {
                activityNames.Add(a.ActivityType.Name);
            }
        }
        return activityNames;
    }

    private IHost BuildHost(
        ScriptedChatClient scripted,
        IEnumerable<AIFunction> tools,
        Action<DurableAgentBuilder>? configureAgent,
        Action<DurableAgentBuilder>? registerToolsViaBuilder = null)
    {
        var taskQueue = $"durable-agent-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(_env.Client);
        builder.Services.AddSingleton<IChatClient>(scripted);

        var workerBuilder = builder.Services.AddHostedTemporalWorker(taskQueue);

        workerBuilder.AddTemporalAgents(opts =>
        {
            opts.AddDurableAgent("DurableAgent", agent =>
            {
                agent.Instructions = "You are a helpful agent.";
                agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();

                if (registerToolsViaBuilder is not null)
                {
                    registerToolsViaBuilder(agent);
                }
                else
                {
                    foreach (var tool in tools)
                    {
                        agent.AddTool(tool);
                    }
                }

                configureAgent?.Invoke(agent);
            });
        });

        return builder.Build();
    }

    private sealed class ProceedingInterceptor : IAgentToolInterceptor
    {
        public Task<DurableToolDecision> BeforeToolCallAsync(
            AgentToolContext context,
            CancellationToken cancellationToken) =>
            Task.FromResult(DurableToolDecision.Proceed());
    }
}

/// <summary>
/// Class fixture: starts a single embedded Temporal server shared by all tests in
/// <see cref="DurableAgentIntegrationTests"/>.
/// </summary>
public sealed class DurableAgentEnvironmentFixture : IAsyncLifetime
{
    public WorkflowEnvironment Environment { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Environment = await TestEnvironmentHelper.StartLocalAsync();
        // The MAF-aware data converter is required so AgentSession{Request,Response}
        // discriminators round-trip through the workflow → activity payload boundary.
        Environment.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;
    }

    public Task DisposeAsync() => Environment.ShutdownAsync();
}
