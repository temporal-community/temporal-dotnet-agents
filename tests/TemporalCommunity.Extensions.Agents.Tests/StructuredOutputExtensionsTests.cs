using System.Text.Json;
using FakeItEasy;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.Agents.Scheduling;
using TemporalCommunity.Extensions.Agents.Session;
using TemporalCommunity.Extensions.Agents.Workflows;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.Tests;

/// <summary>
/// Tests for the optional <c>correlationId</c> parameter on
/// <see cref="StructuredOutputExtensions.RunAgentAsync{T}"/> and the AIAgent overload.
/// The proxy / agent overloads thread the value through <see cref="TemporalAgentRunOptions"/>;
/// the <see cref="ITemporalAgentClient"/> overload threads it directly into a fresh
/// <see cref="RunRequest"/>.
/// </summary>
public class StructuredOutputExtensionsTests
{
    private record Reply(string Answer);

    [Fact]
    public async Task RunAgentAsync_WithCorrelationId_OverridesRequestCorrelationId()
    {
        RunRequest? capturedRequest = null;
        var fakeClient = A.Fake<ITemporalAgentClient>();
        A.CallTo(() => fakeClient.SendAsync(
                A<TemporalAgentSessionId>._,
                A<RunRequest>._,
                A<CancellationToken>._))
            .Invokes((TemporalAgentSessionId _, RunRequest r, CancellationToken _) => capturedRequest = r)
            .Returns(Task.FromResult(new AgentResponse
            {
                Messages = [new ChatMessage(ChatRole.Assistant, JsonSerializer.Serialize(new Reply("ok")))],
            }));

        var sessionId = TemporalAgentSessionId.WithRandomKey("StructuredAgent");
        var request = new RunRequest("question") { CorrelationId = "original" };

        var result = await fakeClient.RunStructuredAsync<Reply>(
            sessionId,
            request,
            options: null,
            correlationId: "caller-supplied-corr");

        Assert.Equal("ok", result.Answer);
        Assert.NotNull(capturedRequest);
        Assert.Equal("caller-supplied-corr", capturedRequest!.CorrelationId);
    }

    [Fact]
    public async Task RunAgentAsync_WithoutCorrelationId_UsesRequestCorrelationId()
    {
        RunRequest? capturedRequest = null;
        var fakeClient = A.Fake<ITemporalAgentClient>();
        A.CallTo(() => fakeClient.SendAsync(
                A<TemporalAgentSessionId>._,
                A<RunRequest>._,
                A<CancellationToken>._))
            .Invokes((TemporalAgentSessionId _, RunRequest r, CancellationToken _) => capturedRequest = r)
            .Returns(Task.FromResult(new AgentResponse
            {
                Messages = [new ChatMessage(ChatRole.Assistant, JsonSerializer.Serialize(new Reply("ok")))],
            }));

        var sessionId = TemporalAgentSessionId.WithRandomKey("StructuredAgent");
        var request = new RunRequest("question") { CorrelationId = "original" };

        var result = await fakeClient.RunStructuredAsync<Reply>(sessionId, request);

        Assert.Equal("ok", result.Answer);
        Assert.NotNull(capturedRequest);
        Assert.Equal("original", capturedRequest!.CorrelationId);
    }

    [Fact]
    public async Task RunAsyncOnAIAgent_WithCorrelationId_PassesViaTemporalAgentRunOptions()
    {
        // The AIAgent overload feeds the optional correlationId through TemporalAgentRunOptions
        // so that the underlying TemporalAIAgentProxy.RunCoreAsync picks it up via the
        // pattern-match path. Use the proxy directly with a fake client to verify.
        RunRequest? capturedRequest = null;
        var fakeClient = A.Fake<ITemporalAgentClient>();
        A.CallTo(() => fakeClient.SendAsync(
                A<TemporalAgentSessionId>._,
                A<RunRequest>._,
                A<CancellationToken>._))
            .Invokes((TemporalAgentSessionId _, RunRequest r, CancellationToken _) => capturedRequest = r)
            .Returns(Task.FromResult(new AgentResponse
            {
                Messages = [new ChatMessage(ChatRole.Assistant, JsonSerializer.Serialize(new Reply("ok")))],
            }));

        var proxy = new TemporalAIAgentProxy("TestAgent", fakeClient);
        var session = (TemporalAgentSession)await proxy.CreateSessionAsync();

        // Deliberately the bare shape: no named argument, no StructuredOutputOptions. Under the
        // old name this bound to MAF's AIAgent.RunAsync<T> and returned AgentResponse<Reply>.
        var result = await ((AIAgent)proxy).RunStructuredAsync<Reply>(
            [new ChatMessage(ChatRole.User, "q")],
            session,
            correlationId: "structured-corr");

        Assert.Equal("ok", result.Answer);
        Assert.NotNull(capturedRequest);
        Assert.Equal("structured-corr", capturedRequest!.CorrelationId);
    }
    /// <summary>
    /// Requirement: camelCase model output must populate the target on the FIRST attempt.
    /// </summary>
    /// <remarks>
    /// The old default was <c>JsonSerializerOptions.Default</c> — PascalCase and case-sensitive.
    /// Against <c>{"answer":"ok"}</c> it did not throw; it returned <c>Reply(null)</c>. Because
    /// nothing threw, the retry loop never fired and the caller received a silently empty object.
    /// Counting sends is the load-bearing assertion: asserting only on Answer would still pass if
    /// the value were recovered on a retry.
    /// </remarks>
    [Fact]
    public async Task RunStructuredAsync_CamelCaseOutput_DeserializesWithoutRetrying()
    {
        var sends = 0;
        var fakeClient = A.Fake<ITemporalAgentClient>();
        A.CallTo(() => fakeClient.SendAsync(
                A<TemporalAgentSessionId>._, A<RunRequest>._, A<CancellationToken>._))
            .Invokes(() => sends++)
            .Returns(Task.FromResult(new AgentResponse
            {
                Messages = [new ChatMessage(ChatRole.Assistant, """{"answer":"ok"}""")],
            }));

        var proxy = new TemporalAIAgentProxy("TestAgent", fakeClient);
        var session = (TemporalAgentSession)await proxy.CreateSessionAsync();

        var result = await ((AIAgent)proxy).RunStructuredAsync<Reply>(
            [new ChatMessage(ChatRole.User, "q")], session);

        Assert.Equal("ok", result.Answer);
        Assert.Equal(1, sends);
    }

    [Fact]
    public async Task RunStructuredAsync_CallerSuppliedJsonOptions_OverrideTheWebDefault()
    {
        // Case-sensitive PascalCase options against camelCase output: the caller's choice must
        // win over the library default, which here means it fails rather than silently succeeding.
        var fakeClient = A.Fake<ITemporalAgentClient>();
        A.CallTo(() => fakeClient.SendAsync(
                A<TemporalAgentSessionId>._, A<RunRequest>._, A<CancellationToken>._))
            .Returns(Task.FromResult(new AgentResponse
            {
                Messages = [new ChatMessage(ChatRole.Assistant, """{"answer":"ok"}""")],
            }));

        var proxy = new TemporalAIAgentProxy("TestAgent", fakeClient);
        var session = (TemporalAgentSession)await proxy.CreateSessionAsync();

        var strict = new JsonSerializerOptions { PropertyNameCaseInsensitive = false };

        var result = await ((AIAgent)proxy).RunStructuredAsync<Reply>(
            [new ChatMessage(ChatRole.User, "q")],
            session,
            new StructuredOutputOptions { JsonSerializerOptions = strict, MaxRetries = 0 });

        // Proof the caller's options were used rather than the web default: the property did not bind.
        Assert.Null(result.Answer);
    }

    [Fact]
    public async Task RunStructuredAsync_FencedOutput_IsStrippedAndParsed()
    {
        var sends = 0;
        var fakeClient = A.Fake<ITemporalAgentClient>();
        A.CallTo(() => fakeClient.SendAsync(
                A<TemporalAgentSessionId>._, A<RunRequest>._, A<CancellationToken>._))
            .Invokes(() => sends++)
            .Returns(Task.FromResult(new AgentResponse
            {
                Messages = [new ChatMessage(ChatRole.Assistant, "```json\n{\"answer\":\"ok\"}\n```")],
            }));

        var proxy = new TemporalAIAgentProxy("TestAgent", fakeClient);
        var session = (TemporalAgentSession)await proxy.CreateSessionAsync();

        var result = await ((AIAgent)proxy).RunStructuredAsync<Reply>(
            [new ChatMessage(ChatRole.User, "q")], session);

        // Fence stripping is the salvage path, not a retry — this is what MAF's native API cannot do.
        Assert.Equal("ok", result.Answer);
        Assert.Equal(1, sends);
    }

    /// <summary>
    /// Requirement: going through MAF's typed run must put the schema generated from
    /// <c>T</c> on the wire. The previous implementation called the untyped overload, so the
    /// first attempt asked the model for JSON in prose and constrained nothing.
    /// </summary>
    [Fact]
    public async Task RunStructuredAsync_SendsGeneratedJsonSchemaToTheModel()
    {
        RunRequest? captured = null;
        var fakeClient = A.Fake<ITemporalAgentClient>();
        A.CallTo(() => fakeClient.SendAsync(
                A<TemporalAgentSessionId>._, A<RunRequest>._, A<CancellationToken>._))
            .Invokes((TemporalAgentSessionId _, RunRequest r, CancellationToken _) => captured = r)
            .Returns(Task.FromResult(new AgentResponse
            {
                Messages = [new ChatMessage(ChatRole.Assistant, """{"answer":"ok"}""")],
            }));

        var proxy = new TemporalAIAgentProxy("TestAgent", fakeClient);
        var session = (TemporalAgentSession)await proxy.CreateSessionAsync();

        await ((AIAgent)proxy).RunStructuredAsync<Reply>(
            [new ChatMessage(ChatRole.User, "q")], session);

        Assert.NotNull(captured);
        var json = Assert.IsType<ChatResponseFormatJson>(captured!.ResponseFormat);
        // The schema is derived from Reply, so it must mention the property the model has to emit.
        Assert.Contains("answer", json.Schema.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunStructuredAsync_ClientOverload_SendsGeneratedJsonSchemaToTheModel()
    {
        RunRequest? captured = null;
        var fakeClient = A.Fake<ITemporalAgentClient>();
        A.CallTo(() => fakeClient.SendAsync(
                A<TemporalAgentSessionId>._, A<RunRequest>._, A<CancellationToken>._))
            .Invokes((TemporalAgentSessionId _, RunRequest r, CancellationToken _) => captured = r)
            .Returns(Task.FromResult(new AgentResponse
            {
                Messages = [new ChatMessage(ChatRole.Assistant, """{"answer":"ok"}""")],
            }));

        var result = await fakeClient.RunStructuredAsync<Reply>(
            new TemporalAgentSessionId("TestAgent", "k"),
            new RunRequest("q"));

        Assert.Equal("ok", result.Answer);
        Assert.NotNull(captured);
        var json = Assert.IsType<ChatResponseFormatJson>(captured!.ResponseFormat);
        Assert.Contains("answer", json.Schema.ToString(), StringComparison.OrdinalIgnoreCase);
    }

}
