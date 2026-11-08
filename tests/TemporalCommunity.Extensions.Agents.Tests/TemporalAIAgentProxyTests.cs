using FakeItEasy;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.Agents.Scheduling;
using TemporalCommunity.Extensions.Agents.Session;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.Tests;

/// <summary>
/// Tests for <see cref="TemporalAIAgentProxy"/>, the external-caller agent that delegates
/// to <see cref="ITemporalAgentClient"/> via Temporal workflow updates.
/// </summary>
public class TemporalAIAgentProxyTests
{
    // ─── Session creation ────────────────────────────────────────────────────

    [Fact]
    public async Task CreateSessionAsync_ReturnsTemporalAgentSession()
    {
        var proxy = CreateProxy("TestAgent");
        var session = await proxy.CreateSessionAsync();
        Assert.IsType<TemporalAgentSession>(session);
    }

    [Fact]
    public async Task CreateSessionAsync_SessionId_ContainsAgentName()
    {
        var proxy = CreateProxy("TestAgent");
        var session = (TemporalAgentSession)await proxy.CreateSessionAsync();
        Assert.Equal("TestAgent", session.SessionId.AgentName, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateSessionAsync_WorkflowId_StartsWithTaPrefix()
    {
        var proxy = CreateProxy("MyAgent");
        var session = (TemporalAgentSession)await proxy.CreateSessionAsync();
        Assert.StartsWith("ta-myagent-", session.SessionId.WorkflowId);
    }

    [Fact]
    public async Task CreateSessionAsync_ProducesUniqueKeys_OnEachCall()
    {
        var proxy = CreateProxy("TestAgent");
        var s1 = (TemporalAgentSession)await proxy.CreateSessionAsync();
        var s2 = (TemporalAgentSession)await proxy.CreateSessionAsync();
        Assert.NotEqual(s1.SessionId.Key, s2.SessionId.Key);
    }

    // ─── RunAsync delegates to ITemporalAgentClient ─────────────────────────

    [Fact]
    public async Task RunAsync_DelegatesToRunAgentAsync()
    {
        var fakeClient = A.Fake<ITemporalAgentClient>();
        A.CallTo(() => fakeClient.SendAsync(
                A<TemporalAgentSessionId>._,
                A<RunRequest>._,
                A<CancellationToken>._))
            .Returns(Task.FromResult(new AgentResponse
            {
                Messages = [new ChatMessage(ChatRole.Assistant, "response")],
                CreatedAt = DateTimeOffset.UtcNow
            }));

        var proxy = new TemporalAIAgentProxy("TestAgent", fakeClient);
        var session = (TemporalAgentSession)await proxy.CreateSessionAsync();

        var response = await proxy.RunAsync("Hello", session);

        A.CallTo(() => fakeClient.SendAsync(
                A<TemporalAgentSessionId>.That.Matches(id =>
                    id.AgentName.Equals("TestAgent", StringComparison.OrdinalIgnoreCase)),
                A<RunRequest>._,
                A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();

        Assert.NotNull(response);
    }

    [Fact]
    public async Task RunAsync_MessageContent_PassedInRequest()
    {
        RunRequest? capturedRequest = null;
        var fakeClient = A.Fake<ITemporalAgentClient>();
        A.CallTo(() => fakeClient.SendAsync(
                A<TemporalAgentSessionId>._,
                A<RunRequest>._,
                A<CancellationToken>._))
            .Invokes((TemporalAgentSessionId _, RunRequest r, CancellationToken _) => capturedRequest = r)
            .Returns(Task.FromResult(new AgentResponse()));

        var proxy = new TemporalAIAgentProxy("TestAgent", fakeClient);
        var session = (TemporalAgentSession)await proxy.CreateSessionAsync();

        await proxy.RunAsync("Hello agent!", session);

        Assert.NotNull(capturedRequest);
        Assert.Single(capturedRequest!.Messages);
        Assert.Equal("Hello agent!", capturedRequest.Messages[0].Text);
    }

    // ─── Fire-and-forget ─────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_WithIsFireAndForget_CallsFireAndForgetMethod()
    {
        var fakeClient = A.Fake<ITemporalAgentClient>();
        A.CallTo(() => fakeClient.RunAgentFireAndForgetAsync(
                A<TemporalAgentSessionId>._,
                A<RunRequest>._,
                A<CancellationToken>._))
            .Returns(Task.CompletedTask);

        var proxy = new TemporalAIAgentProxy("TestAgent", fakeClient);
        var session = (TemporalAgentSession)await proxy.CreateSessionAsync();

        var options = new TemporalAgentRunOptions { IsFireAndForget = true };
        await proxy.RunAsync("Fire!", session, options);

        // RunAgentAsync should NOT be called
        A.CallTo(() => fakeClient.SendAsync(
                A<TemporalAgentSessionId>._,
                A<RunRequest>._,
                A<CancellationToken>._))
            .MustNotHaveHappened();

        // RunAgentFireAndForgetAsync SHOULD be called
        A.CallTo(() => fakeClient.RunAgentFireAndForgetAsync(
                A<TemporalAgentSessionId>._,
                A<RunRequest>._,
                A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task RunAsync_WithIsFireAndForget_ReturnsEmptyResponse()
    {
        var fakeClient = A.Fake<ITemporalAgentClient>();
        A.CallTo(() => fakeClient.RunAgentFireAndForgetAsync(
                A<TemporalAgentSessionId>._,
                A<RunRequest>._,
                A<CancellationToken>._))
            .Returns(Task.CompletedTask);

        var proxy = new TemporalAIAgentProxy("TestAgent", fakeClient);
        var session = (TemporalAgentSession)await proxy.CreateSessionAsync();

        var options = new TemporalAgentRunOptions { IsFireAndForget = true };
        var response = await proxy.RunAsync("Fire!", session, options);

        // Fire-and-forget returns an empty (non-null) response immediately
        Assert.NotNull(response);
        Assert.Empty(response.Messages);
    }

    // ─── CorrelationId on TemporalAgentRunOptions ────────────────────────────

    [Fact]
    public async Task RunAsync_WithCorrelationIdInOptions_PropagatesToRunRequest()
    {
        // Caller-supplied correlation ID via TemporalAgentRunOptions must be passed through
        // to the RunRequest so it lands on the workflow-history entry — the proxy must NOT
        // overwrite it with a fresh GUID.
        RunRequest? capturedRequest = null;
        var fakeClient = A.Fake<ITemporalAgentClient>();
        A.CallTo(() => fakeClient.SendAsync(
                A<TemporalAgentSessionId>._,
                A<RunRequest>._,
                A<CancellationToken>._))
            .Invokes((TemporalAgentSessionId _, RunRequest r, CancellationToken _) => capturedRequest = r)
            .Returns(Task.FromResult(new AgentResponse()));

        var proxy = new TemporalAIAgentProxy("TestAgent", fakeClient);
        var session = (TemporalAgentSession)await proxy.CreateSessionAsync();

        var options = new TemporalAgentRunOptions { CorrelationId = "upstream-trace-42" };
        await proxy.RunAsync("Hello", session, options);

        Assert.NotNull(capturedRequest);
        Assert.Equal("upstream-trace-42", capturedRequest!.CorrelationId);
    }

    [Fact]
    public async Task RunAsync_WithRequestIdInOptions_PropagatesToRunRequest()
    {
        RunRequest? capturedRequest = null;
        var fakeClient = A.Fake<ITemporalAgentClient>();
        A.CallTo(() => fakeClient.SendAsync(
                A<TemporalAgentSessionId>._,
                A<RunRequest>._,
                A<CancellationToken>._))
            .Invokes((TemporalAgentSessionId _, RunRequest r, CancellationToken _) => capturedRequest = r)
            .Returns(Task.FromResult(new AgentResponse()));

        var proxy = new TemporalAIAgentProxy("TestAgent", fakeClient);
        var session = (TemporalAgentSession)await proxy.CreateSessionAsync();

        await proxy.RunAsync(
            "Hello",
            session,
            new TemporalAgentRunOptions { RequestId = "logical-request-1" });

        Assert.Equal("logical-request-1", capturedRequest!.RequestId);
    }

    [Fact]
    public async Task RunAsync_WithoutRequestIdInOptions_AutoGenerates()
    {
        RunRequest? capturedRequest = null;
        var fakeClient = A.Fake<ITemporalAgentClient>();
        A.CallTo(() => fakeClient.SendAsync(
                A<TemporalAgentSessionId>._,
                A<RunRequest>._,
                A<CancellationToken>._))
            .Invokes((TemporalAgentSessionId _, RunRequest r, CancellationToken _) => capturedRequest = r)
            .Returns(Task.FromResult(new AgentResponse()));

        var proxy = new TemporalAIAgentProxy("TestAgent", fakeClient);
        var session = (TemporalAgentSession)await proxy.CreateSessionAsync();

        await proxy.RunAsync("Hello", session);

        Assert.False(string.IsNullOrWhiteSpace(capturedRequest!.RequestId));
    }

    [Fact]
    public async Task RunAsync_WithoutCorrelationIdInOptions_AutoGenerates()
    {
        // When no CorrelationId is supplied, the proxy must auto-generate one — no null leak,
        // workflow code downstream relies on a non-empty correlation ID.
        RunRequest? capturedRequest = null;
        var fakeClient = A.Fake<ITemporalAgentClient>();
        A.CallTo(() => fakeClient.SendAsync(
                A<TemporalAgentSessionId>._,
                A<RunRequest>._,
                A<CancellationToken>._))
            .Invokes((TemporalAgentSessionId _, RunRequest r, CancellationToken _) => capturedRequest = r)
            .Returns(Task.FromResult(new AgentResponse()));

        var proxy = new TemporalAIAgentProxy("TestAgent", fakeClient);
        var session = (TemporalAgentSession)await proxy.CreateSessionAsync();

        await proxy.RunAsync("Hello", session);

        Assert.NotNull(capturedRequest);
        Assert.False(string.IsNullOrEmpty(capturedRequest!.CorrelationId));
    }

    [Fact]
    public async Task RunAsync_ChatClientAgentResponseFormat_IsForwarded()
    {
        RunRequest? capturedRequest = null;
        var fakeClient = A.Fake<ITemporalAgentClient>();
        A.CallTo(() => fakeClient.SendAsync(
                A<TemporalAgentSessionId>._,
                A<RunRequest>._,
                A<CancellationToken>._))
            .Invokes((TemporalAgentSessionId _, RunRequest request, CancellationToken _) => capturedRequest = request)
            .Returns(Task.FromResult(new AgentResponse()));

        var proxy = new TemporalAIAgentProxy("TestAgent", fakeClient);
        var session = await proxy.CreateSessionAsync();

        await proxy.RunAsync(
            "Return JSON.",
            session,
            new ChatClientAgentRunOptions
            {
                ChatOptions = new ChatOptions { ResponseFormat = ChatResponseFormat.Json },
            });

        Assert.IsType<ChatResponseFormatJson>(capturedRequest!.ResponseFormat);
    }

    [Fact]
    public async Task RunAsync_ChatClientAgentRunOptions_RejectsUnsupportedChatOptions()
    {
        var fakeClient = A.Fake<ITemporalAgentClient>();
        var proxy = new TemporalAIAgentProxy("TestAgent", fakeClient);
        var session = await proxy.CreateSessionAsync();

        var exception = await Assert.ThrowsAsync<NotSupportedException>(() =>
            proxy.RunAsync(
                "Hello",
                session,
                new ChatClientAgentRunOptions
                {
                    ChatOptions = new ChatOptions { Temperature = 0.5f },
                }));

        Assert.Contains(nameof(ChatOptions.Temperature), exception.Message);
        A.CallTo(() => fakeClient.SendAsync(
                A<TemporalAgentSessionId>._,
                A<RunRequest>._,
                A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task RunAsync_ChatClientAgentRunOptions_RejectsChatClientFactory()
    {
        var fakeClient = A.Fake<ITemporalAgentClient>();
        var proxy = new TemporalAIAgentProxy("TestAgent", fakeClient);
        var session = await proxy.CreateSessionAsync();

        var exception = await Assert.ThrowsAsync<NotSupportedException>(() =>
            proxy.RunAsync(
                "Hello",
                session,
                new ChatClientAgentRunOptions { ChatClientFactory = client => client }));

        Assert.Contains(nameof(ChatClientAgentRunOptions.ChatClientFactory), exception.Message);
    }

    [Fact]
    public async Task RunAsync_UnknownAgentRunOptions_AreRejected()
    {
        var fakeClient = A.Fake<ITemporalAgentClient>();
        var proxy = new TemporalAIAgentProxy("TestAgent", fakeClient);
        var session = await proxy.CreateSessionAsync();

        var exception = await Assert.ThrowsAsync<NotSupportedException>(() =>
            proxy.RunAsync("Hello", session, new UnknownRunOptions()));

        Assert.Contains(nameof(TemporalAgentRunOptions), exception.Message);
        Assert.Contains(nameof(ChatClientAgentRunOptions), exception.Message);
    }

    [Fact]
    public async Task RunAsync_BaseAgentRunOptions_ForwardsResponseFormat()
    {
        RunRequest? capturedRequest = null;
        var fakeClient = A.Fake<ITemporalAgentClient>();
        A.CallTo(() => fakeClient.SendAsync(
                A<TemporalAgentSessionId>._,
                A<RunRequest>._,
                A<CancellationToken>._))
            .Invokes((TemporalAgentSessionId _, RunRequest request, CancellationToken _) => capturedRequest = request)
            .Returns(Task.FromResult(new AgentResponse()));

        var proxy = new TemporalAIAgentProxy("TestAgent", fakeClient);
        var session = await proxy.CreateSessionAsync();

        await proxy.RunAsync("Return JSON.", session, new AgentRunOptions
        {
            ResponseFormat = ChatResponseFormat.Json,
        });

        Assert.IsType<ChatResponseFormatJson>(capturedRequest!.ResponseFormat);
    }

    [Fact]
    public async Task RunAsync_BaseAgentRunOptions_RejectsBackgroundResponses()
    {
        var proxy = CreateProxy("TestAgent");
        var session = await proxy.CreateSessionAsync();

        var exception = await Assert.ThrowsAsync<NotSupportedException>(() =>
            proxy.RunAsync("Hello", session, new AgentRunOptions { AllowBackgroundResponses = true }));

        Assert.Contains(nameof(AgentRunOptions.AllowBackgroundResponses), exception.Message);
    }

    [Fact]
    public async Task RunAsync_WithEmptyCorrelationIdInOptions_AutoGenerates()
    {
        // Empty string is treated the same as null — auto-generate a fresh GUID.
        RunRequest? capturedRequest = null;
        var fakeClient = A.Fake<ITemporalAgentClient>();
        A.CallTo(() => fakeClient.SendAsync(
                A<TemporalAgentSessionId>._,
                A<RunRequest>._,
                A<CancellationToken>._))
            .Invokes((TemporalAgentSessionId _, RunRequest r, CancellationToken _) => capturedRequest = r)
            .Returns(Task.FromResult(new AgentResponse()));

        var proxy = new TemporalAIAgentProxy("TestAgent", fakeClient);
        var session = (TemporalAgentSession)await proxy.CreateSessionAsync();

        var options = new TemporalAgentRunOptions { CorrelationId = string.Empty };
        await proxy.RunAsync("Hello", session, options);

        Assert.NotNull(capturedRequest);
        Assert.False(string.IsNullOrEmpty(capturedRequest!.CorrelationId));
    }

    [Fact]
    public async Task RunAsync_WithSessionOwnedByAnotherAgent_ThrowsAndDoesNotDispatch()
    {
        var fakeClient = A.Fake<ITemporalAgentClient>();
        var proxy = new TemporalAIAgentProxy("AgentA", fakeClient);
        var otherProxy = new TemporalAIAgentProxy("AgentB", fakeClient);
        var otherSession = (TemporalAgentSession)await otherProxy.CreateSessionAsync();

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            proxy.RunAsync("Hello", otherSession));

        Assert.Equal("session", exception.ParamName);
        Assert.Contains("belongs to agent 'AgentB'", exception.Message, StringComparison.Ordinal);
        A.CallTo(() => fakeClient.SendAsync(
                A<TemporalAgentSessionId>._,
                A<RunRequest>._,
                A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task RunStreamingAsync_ThrowsNotSupportedException()
    {
        var proxy = CreateProxy("TestAgent");

        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            await foreach (var _ in proxy.RunStreamingAsync("Hello"))
            {
            }
        });
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static TemporalAIAgentProxy CreateProxy(string name)
    {
        var fakeClient = A.Fake<ITemporalAgentClient>();
        A.CallTo(() => fakeClient.SendAsync(
                A<TemporalAgentSessionId>._,
                A<RunRequest>._,
                A<CancellationToken>._))
            .Returns(Task.FromResult(new AgentResponse()));

        return new TemporalAIAgentProxy(name, fakeClient);
    }

    private sealed class UnknownRunOptions : AgentRunOptions
    {
    }
}
