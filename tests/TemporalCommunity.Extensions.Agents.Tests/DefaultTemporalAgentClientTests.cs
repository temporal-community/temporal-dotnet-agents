using FakeItEasy;
using System.Linq.Expressions;
using Temporalio.Client;
using Temporalio.Client.Schedules;
using TemporalCommunity.Extensions.Agents.Scheduling;
using TemporalCommunity.Extensions.Agents.Approvals;
using TemporalCommunity.Extensions.Agents.Session;
using TemporalCommunity.Extensions.Agents.Workflows;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.Tests;

/// <summary>
/// Tests for <see cref="DefaultTemporalAgentClient"/> guard clauses and validation logic.
/// These test the validation layer — all guards throw before touching the Temporal client.
/// </summary>
public class DefaultTemporalAgentClientTests
{
    private readonly ITemporalClient _fakeClient = A.Fake<ITemporalClient>();
    private readonly TemporalAgentsOptions _options = new();
    private const string TaskQueue = "test-queue";

    private DefaultTemporalAgentClient CreateClient() =>
        new(_fakeClient, _options, TaskQueue, logger: null);

    // ─── SendAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task SendAsync_NullRequest_ThrowsArgumentNullException()
    {
        var client = CreateClient();
        var sessionId = TemporalAgentSessionId.WithRandomKey("Agent");

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            client.SendAsync(sessionId, null!));
    }

    // ─── RunAgentFireAndForgetAsync ──────────────────────────────────────────

    [Fact]
    public async Task RunAgentFireAndForgetAsync_NullRequest_ThrowsArgumentNullException()
    {
        var client = CreateClient();
        var sessionId = TemporalAgentSessionId.WithRandomKey("Agent");

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            client.RunAgentFireAndForgetAsync(sessionId, null!));
    }

    [Fact]
    public async Task RunAgentFireAndForgetAsync_UsesSignalWithStart()
    {
        WorkflowOptions? capturedOptions = null;
        A.CallTo(() => _fakeClient.StartWorkflowAsync(
                A<Expression<Func<AgentWorkflow, Task>>>._,
                A<WorkflowOptions>._))
            .Invokes((Expression<Func<AgentWorkflow, Task>> _, WorkflowOptions options) => capturedOptions = options)
            .Returns(Task.FromResult<WorkflowHandle<AgentWorkflow>>(null!));
        var client = CreateClient();
        var sessionId = TemporalAgentSessionId.WithRandomKey("Agent");
        var request = new RunRequest("hello");

        await client.RunAgentFireAndForgetAsync(sessionId, request);

        Assert.NotNull(capturedOptions);
        Assert.Equal("RunFireAndForget", capturedOptions!.StartSignal);
        Assert.Single(capturedOptions.StartSignalArgs!);
        Assert.Same(request, capturedOptions.StartSignalArgs!.Single());
        A.CallTo(() => _fakeClient.GetWorkflowHandle<AgentWorkflow>(
                A<string>._,
                A<string?>._,
                A<string?>._))
            .MustNotHaveHappened();
    }

    // ─── ResolveApprovalAsync ────────────────────────────────────────────────

    [Fact]
    public async Task ResolveApprovalAsync_NullDecision_ThrowsArgumentNullException()
    {
        var client = CreateClient();
        var sessionId = TemporalAgentSessionId.WithRandomKey("Agent");

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            client.ResolveApprovalAsync(sessionId, null!));
    }

    // ─── RunAgentDelayedAsync ────────────────────────────────────────────────

    [Fact]
    public async Task RunAgentDelayedAsync_NullRequest_ThrowsArgumentNullException()
    {
        var client = CreateClient();
        var sessionId = TemporalAgentSessionId.WithRandomKey("Agent");

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            client.RunAgentDelayedAsync(sessionId, null!, TimeSpan.FromMinutes(5)));
    }

    // ─── ScheduleAgentAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task ScheduleAgentAsync_NullAgentName_ThrowsArgumentException()
    {
        var client = CreateClient();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            client.ScheduleAgentAsync(null!, "schedule-1", new RunRequest("test"),
                new ScheduleSpec()));
    }

    [Fact]
    public async Task ScheduleAgentAsync_WhitespaceAgentName_ThrowsArgumentException()
    {
        var client = CreateClient();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.ScheduleAgentAsync("  ", "schedule-1", new RunRequest("test"),
                new ScheduleSpec()));
    }

    [Fact]
    public async Task ScheduleAgentAsync_NullScheduleId_ThrowsArgumentException()
    {
        var client = CreateClient();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            client.ScheduleAgentAsync("Agent", null!, new RunRequest("test"),
                new ScheduleSpec()));
    }

    [Fact]
    public async Task ScheduleAgentAsync_NullRequest_ThrowsArgumentNullException()
    {
        var client = CreateClient();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            client.ScheduleAgentAsync("Agent", "schedule-1", null!,
                new ScheduleSpec()));
    }

    [Fact]
    public async Task ScheduleAgentAsync_NullSpec_ThrowsArgumentNullException()
    {
        var client = CreateClient();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            client.ScheduleAgentAsync("Agent", "schedule-1", new RunRequest("test"),
                null!));
    }

    // ─── GetAgentScheduleHandle ──────────────────────────────────────────────

    [Fact]
    public void GetAgentScheduleHandle_NullScheduleId_ThrowsArgumentException()
    {
        var client = CreateClient();

        Assert.Throws<ArgumentNullException>(() =>
            client.GetAgentScheduleHandle(null!));
    }

    [Fact]
    public void GetAgentScheduleHandle_WhitespaceScheduleId_ThrowsArgumentException()
    {
        var client = CreateClient();

        Assert.Throws<ArgumentException>(() =>
            client.GetAgentScheduleHandle("   "));
    }
}
