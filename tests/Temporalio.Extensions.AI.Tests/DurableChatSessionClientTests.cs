using FakeItEasy;
using Temporalio.Client;
using Xunit;

namespace Temporalio.Extensions.AI.Tests;

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
}
