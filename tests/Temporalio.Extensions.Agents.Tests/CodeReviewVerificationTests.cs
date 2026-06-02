using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Temporalio.Extensions.Agents.State;
using Temporalio.Extensions.Agents.Workflows;
using Temporalio.Testing;
using Xunit;

namespace Temporalio.Extensions.Agents.Tests;

/// <summary>
/// Pins the behaviour changes introduced by the v0.3 code review (plan:
/// temporalio-extensions-agents-review.md). One test file, one test class per finding.
/// </summary>
public class Crit2AppendAgentTurnThrowsWhenHistoryStoreIsNull
{
    // TemporalAgentsOptions has an internal parameterless constructor; reach it via Activator.
    private static TemporalAgentsOptions CreateOptions() =>
        (TemporalAgentsOptions)Activator.CreateInstance(typeof(TemporalAgentsOptions), nonPublic: true)!;

    [Fact]
    public async Task AppendAgentTurnAsync_WhenHistoryStoreIsNull_ThrowsInvalidOperationException()
    {
        // Arrange: register an agent with no HistoryStore.
        var options = CreateOptions();
        options.AddDurableAgent("NoStoreAgent", agent =>
        {
            agent.ChatClient = _ => new StubChatClient();
        });

        var services = new ServiceCollection();
        services.AddSingleton(options);
        var sp = services.BuildServiceProvider();

        var activities = new AgentActivities(sp);

        var input = new AppendAgentTurnInput
        {
            AgentName = "NoStoreAgent",
            SessionId = "wf-123",
            Request = new RunRequest("hello") { CorrelationId = "corr-1" },
            TurnResponse = new AgentResponse(),
        };

        // Act + Assert: must throw, not silently succeed.
        var env = new ActivityEnvironment();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => env.RunAsync(() => activities.AppendAgentTurnAsync(input)));

        Assert.Contains("NoStoreAgent", ex.Message);
        Assert.Contains("IAgentHistoryStore", ex.Message);
    }

    private sealed class StubChatClient : IChatClient
    {
        public void Dispose() { }

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
}

public class Smith1AddToolCoreConfigureThrowsLeavesNoResidue
{
    [Fact]
    public void AddTool_WhenConfigureThrows_ToolNameIsNotStuckInRegistry()
    {
        // Arrange: a builder with a configure delegate that always throws.
        var builder = new DurableAgentBuilder("Agent");
        var tool = AIFunctionFactory.Create(() => "ok", "my_tool");

        // Act: first call — configure throws.
        Assert.Throws<Exception>(() =>
            builder.AddTool(tool, _ => throw new Exception("configure-boom")));

        // Assert: second call with the same name must NOT throw ArgumentException.
        // If the name is stuck in _toolNames, this would throw "Tool 'my_tool' is already registered".
        var secondTool = AIFunctionFactory.Create(() => "ok", "my_tool");
        var ex = Record.Exception(() => builder.AddTool(secondTool));
        Assert.Null(ex);
    }

    [Fact]
    public void AddTool_WhenConfigureThrows_ToolsCollectionIsNotModified()
    {
        var builder = new DurableAgentBuilder("Agent");
        var tool = AIFunctionFactory.Create(() => "ok", "my_tool");

        Assert.Throws<Exception>(() =>
            builder.AddTool(tool, _ => throw new Exception("configure-boom")));

        // The failed registration must not appear in ToolRegistrations.
        Assert.Empty(builder.ToolRegistrations);
    }

    [Fact]
    public void AddTool_WhenConfigureSucceeds_ToolIsRegisteredNormally()
    {
        var builder = new DurableAgentBuilder("Agent");
        var tool = AIFunctionFactory.Create(() => "ok", "write_record");

        builder.AddTool(tool, opts => opts.NoRetry());

        Assert.Single(builder.ToolRegistrations);
        Assert.Equal("write_record", builder.ToolRegistrations[0].Name);
        Assert.Equal(1, builder.ToolRegistrations[0].Options.RetryPolicy!.MaximumAttempts);
    }
}

public class Conv1WhitespaceName_TemporalAgentsOptions
{
    private static TemporalAgentsOptions CreateOptions() =>
        (TemporalAgentsOptions)Activator.CreateInstance(typeof(TemporalAgentsOptions), nonPublic: true)!;

    [Fact]
    public void TryGetDurableRegistration_WhitespaceOnlyName_ThrowsArgumentException()
    {
        var options = CreateOptions();

        // TryGetDurableRegistration uses ThrowIfNullOrEmpty — CONV-1 fix changes it to
        // ThrowIfNullOrWhiteSpace. Pin that whitespace-only strings are rejected.
        Assert.Throws<ArgumentException>(() => options.TryGetDurableRegistration("   "));
    }
}

public class Conv1WhitespaceName_TemporalWorkflowExtensions
{
    // GetAgent and NewAgentSessionId both have a Workflow.InWorkflow guard that fires before
    // the name validation when called outside a workflow. The whitespace ArgumentException is
    // only reachable inside workflow context. These tests document the expected post-fix
    // behaviour for the whitespace guard on GetAgent and NewAgentSessionId; the outside-workflow
    // guard behaviour is already pinned by TemporalWorkflowExtensionsGuardTests.

    [Fact]
    public void GetAgent_OutsideWorkflow_WhitespaceName_ThrowsInvalidOperationException()
    {
        // Outside workflow: the Workflow.InWorkflow guard fires first, regardless of name.
        // This confirms the current observable behaviour and prevents a regression where
        // the whitespace check is accidentally moved before the workflow guard.
        Assert.Throws<InvalidOperationException>(() =>
            TemporalWorkflowExtensions.GetAgent("   "));
    }

    [Fact]
    public void NewAgentSessionId_OutsideWorkflow_WhitespaceName_ThrowsInvalidOperationException()
    {
        // Same guard-ordering constraint as GetAgent above.
        Assert.Throws<InvalidOperationException>(() =>
            TemporalWorkflowExtensions.NewAgentSessionId("   "));
    }
}

public class Conv2WhitespaceValidation
{
    // ── AIAgentExtensions.RunFireAndForgetAsync ───────────────────────────────

    [Fact]
    public async Task RunFireAndForgetAsync_WhitespaceOnlyMessage_ThrowsArgumentException()
    {
        var agent = new StubAIAgentForConv2("TestAgent");

        // CONV-2: ThrowIfNullOrEmpty → ThrowIfNullOrWhiteSpace at AIAgentExtensions.cs:61.
        await Assert.ThrowsAsync<ArgumentException>(() =>
            agent.RunFireAndForgetAsync("   "));
    }

    [Fact]
    public async Task RunFireAndForgetAsync_EmptyMessage_ThrowsArgumentException()
    {
        // Guard the empty-string case is still caught (regression fence).
        var agent = new StubAIAgentForConv2("TestAgent");
        await Assert.ThrowsAsync<ArgumentException>(() =>
            agent.RunFireAndForgetAsync(string.Empty));
    }

    // ── AgentSessionRequest.FromRunRequest ────────────────────────────────────

    [Fact]
    public void AgentSessionRequest_FromRunRequest_WhitespaceCorrelationId_ThrowsInvalidOperationException()
    {
        // CONV-2: IsNullOrEmpty → IsNullOrWhiteSpace at AgentSessionRequest.cs:54.
        var request = new RunRequest("hello") { CorrelationId = "   " };

        Assert.Throws<InvalidOperationException>(() =>
            AgentSessionRequest.FromRunRequest(request, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void AgentSessionRequest_FromRunRequest_EmptyCorrelationId_ThrowsInvalidOperationException()
    {
        // Regression fence: empty string must also be rejected.
        var request = new RunRequest("hello") { CorrelationId = string.Empty };

        Assert.Throws<InvalidOperationException>(() =>
            AgentSessionRequest.FromRunRequest(request, DateTimeOffset.UtcNow));
    }

    // ── AgentSessionResponse.FromAgentResponse ────────────────────────────────

    [Fact]
    public void AgentSessionResponse_FromAgentResponse_WhitespaceCorrelationId_ThrowsArgumentException()
    {
        // CONV-2: IsNullOrEmpty → IsNullOrWhiteSpace at AgentSessionResponse.cs:46.
        var response = new AgentResponse();

        Assert.Throws<ArgumentException>(() =>
            AgentSessionResponse.FromAgentResponse("   ", response, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void AgentSessionResponse_FromAgentResponse_EmptyCorrelationId_ThrowsArgumentException()
    {
        // Regression fence: empty string must also be rejected.
        var response = new AgentResponse();

        Assert.Throws<ArgumentException>(() =>
            AgentSessionResponse.FromAgentResponse(string.Empty, response, DateTimeOffset.UtcNow));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private sealed class StubAIAgentForConv2(string name) : AIAgent
    {
        public override string? Name { get; } = name;

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default) =>
            new(new StubSession());

        protected override ValueTask<System.Text.Json.JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            System.Text.Json.JsonSerializerOptions? jsonSerializerOptions = null,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            System.Text.Json.JsonElement serializedState,
            System.Text.Json.JsonSerializerOptions? jsonSerializerOptions = null,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentResponse());

        protected override async System.Collections.Generic.IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new AgentResponseUpdate();
            await Task.CompletedTask;
        }
    }

    private sealed class StubSession : AgentSession { }
}
