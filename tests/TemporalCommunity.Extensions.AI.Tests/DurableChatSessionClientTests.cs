using FakeItEasy;
using Microsoft.Extensions.AI;
using Temporalio.Client;
using TemporalCommunity.Extensions.AI.Exceptions;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests;

public class DurableChatSessionClientTests
{
    // ── Construction ──────────────────────────────────────────────────────

    [Fact]
    public void Constructor_ThrowsOnNullClient()
    {
        var options = new DurableExecutionOptions { TaskQueue = "test" };
        Assert.Throws<ArgumentNullException>(
            () => new DurableChatSessionClient(null!, options));
    }

    [Fact]
    public void Constructor_ThrowsOnNullOptions()
    {
        var client = A.Fake<ITemporalClient>();
        Assert.Throws<ArgumentNullException>(
            () => new DurableChatSessionClient(client, null!));
    }

    [Fact]
    public void Constructor_ThrowsWhenTaskQueueNotSet()
    {
        var client = A.Fake<ITemporalClient>();
        var options = new DurableExecutionOptions(); // TaskQueue is null
        Assert.Throws<InvalidOperationException>(
            () => new DurableChatSessionClient(client, options));
    }

    // ── Interface ─────────────────────────────────────────────────────────

    [Fact]
    public void ImplementsIDurableChatSessionClient()
    {
        var client = A.Fake<ITemporalClient>();
        var options = new DurableExecutionOptions { TaskQueue = "test" };
        var sessionClient = new DurableChatSessionClient(client, options);

        Assert.IsAssignableFrom<IDurableChatSessionClient>(sessionClient);
    }

    // ── Workflow ID generation ─────────────────────────────────────────────

    [Fact]
    public void GetWorkflowId_AppliesCustomPrefix()
    {
        var client = A.Fake<ITemporalClient>();
        var options = new DurableExecutionOptions
        {
            TaskQueue = "test",
            WorkflowIdPrefix = "my-prefix-"
        };
        var sessionClient = new DurableChatSessionClient(client, options);

        Assert.Equal("my-prefix-conversation-123", sessionClient.GetWorkflowId("conversation-123"));
    }

    [Fact]
    public void GetWorkflowId_UsesDefaultPrefix()
    {
        var client = A.Fake<ITemporalClient>();
        var options = new DurableExecutionOptions { TaskQueue = "test" };
        var sessionClient = new DurableChatSessionClient(client, options);

        Assert.Equal("chat-my-conversation", sessionClient.GetWorkflowId("my-conversation"));
    }

    [Fact]
    public void GetWorkflowId_SameInputAlwaysProducesSameId()
    {
        var client = A.Fake<ITemporalClient>();
        var options = new DurableExecutionOptions { TaskQueue = "test" };
        var sessionClient = new DurableChatSessionClient(client, options);

        var id1 = sessionClient.GetWorkflowId("abc");
        var id2 = sessionClient.GetWorkflowId("abc");

        Assert.Equal(id1, id2);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GetWorkflowId_RejectsMissingConversationId(string? conversationId)
    {
        var client = A.Fake<ITemporalClient>();
        var sessionClient = new DurableChatSessionClient(
            client,
            new DurableExecutionOptions { TaskQueue = "test" });

        Assert.ThrowsAny<ArgumentException>(() => sessionClient.GetWorkflowId(conversationId!));
    }

    [Fact]
    public async Task SendAsync_CallerSuppliedTools_ThrowsBeforeWorkflowDispatch()
    {
        var client = A.Fake<ITemporalClient>();
        var sessionClient = new DurableChatSessionClient(
            client,
            new DurableExecutionOptions { TaskQueue = "test" });
        var options = new ChatOptions
        {
            Tools = [AIFunctionFactory.Create(() => "result", "tool", "test tool")],
        };

        var exception = await Assert.ThrowsAsync<DurableConfigurationException>(
            () => sessionClient.SendAsync("conversation", [new ChatMessage(ChatRole.User, "hello")], options));

        Assert.Contains("AddDurableTools", exception.Message, StringComparison.Ordinal);
        A.CallTo(client).Where(call => call.Method.Name.Contains("UpdateWithStart", StringComparison.Ordinal)).MustNotHaveHappened();
    }

    [Theory]
    [InlineData(nameof(ChatOptions.ConversationId))]
    [InlineData(nameof(ChatOptions.RawRepresentationFactory))]
    [InlineData(nameof(ChatOptions.ContinuationToken))]
    public async Task SendAsync_UnsupportedProviderOption_ThrowsBeforeWorkflowDispatch(string propertyName)
    {
        var client = A.Fake<ITemporalClient>();
        var sessionClient = new DurableChatSessionClient(
            client,
            new DurableExecutionOptions { TaskQueue = "test" });
        var options = propertyName switch
        {
            nameof(ChatOptions.ConversationId) => new ChatOptions { ConversationId = "provider-id" },
            nameof(ChatOptions.RawRepresentationFactory) => new ChatOptions { RawRepresentationFactory = _ => null },
            nameof(ChatOptions.ContinuationToken) => new ChatOptions
            {
                ContinuationToken = ResponseContinuationToken.FromBytes(new byte[] { 1, 2, 3 }),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(propertyName)),
        };

        var exception = await Assert.ThrowsAsync<DurableConfigurationException>(
            () => sessionClient.SendAsync(
                "conversation",
                [new ChatMessage(ChatRole.User, "hello")],
                options));

        Assert.Contains(propertyName, exception.Message, StringComparison.Ordinal);
        A.CallTo(client).Where(call => call.Method.Name.Contains("UpdateWithStart", StringComparison.Ordinal)).MustNotHaveHappened();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SendAsync_RejectsInvalidRequestIdBeforeWorkflowDispatch(string requestId)
    {
        var client = A.Fake<ITemporalClient>();
        var sessionClient = new DurableChatSessionClient(
            client,
            new DurableExecutionOptions { TaskQueue = "test" });

        await Assert.ThrowsAsync<ArgumentException>(() => sessionClient.SendAsync(
            "conversation",
            [new ChatMessage(ChatRole.User, "hello")],
            requestId: requestId));

        A.CallTo(client).Where(call => call.Method.Name.Contains("UpdateWithStart", StringComparison.Ordinal)).MustNotHaveHappened();
    }

    [Fact]
    public async Task SendAsync_GeneratedRequestIdIsIncludedWhenSubmissionFails()
    {
        var client = A.Fake<ITemporalClient>();
        A.CallTo(client)
            .Where(call => call.Method.Name.Contains("UpdateWithStart", StringComparison.Ordinal))
            .Throws(new TimeoutException("submission response was lost"));
        var sessionClient = new DurableChatSessionClient(
            client,
            new DurableExecutionOptions { TaskQueue = "test" });

        var exception = await Assert.ThrowsAsync<DurableChatRequestException>(() =>
            sessionClient.SendAsync(
                "conversation",
                [new ChatMessage(ChatRole.User, "hello")]));

        Assert.Matches("^[0-9a-f]{32}$", exception.RequestId);
        Assert.IsType<TimeoutException>(exception.InnerException);
    }
}
