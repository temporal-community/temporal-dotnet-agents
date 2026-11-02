using Microsoft.Extensions.AI;
using Temporalio.Workflows;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests.Research;

/// <summary>Package-contract research, not acceptance of persisted compaction.</summary>
public class PinnedReducerContractTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Counting_RejectsNonPositiveTarget(int target) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new MessageCountingChatReducer(target));

    [Fact]
    public async Task Counting_KeepsFirstSystemAndRecentNonToolMessages()
    {
        ChatMessage[] input =
        [
            new(ChatRole.System, "first instructions"),
            new(ChatRole.User, "old"),
            new(ChatRole.System, "later instructions"),
            new(ChatRole.Assistant, [new FunctionCallContent("call", "write")]),
            new(ChatRole.Tool, [new FunctionResultContent("call", "done")]),
            new(ChatRole.User, "recent"),
            new(ChatRole.Assistant, "reply"),
        ];

        var output = (await new MessageCountingChatReducer(2).ReduceAsync(input, default)).ToArray();

        Assert.Equal([input[0], input[5], input[6]], output);
        Assert.Equal(7, input.Length);
    }

    [Fact]
    public async Task Summarizing_ThresholdDoesNotCallProvider()
    {
        using var provider = new SummaryClient();
        var input = Conversation();
        var output = (await new SummarizingChatReducer(provider, 2, 2)
            .ReduceAsync(input, default)).ToArray();

        Assert.Equal(input, output);
        Assert.Empty(provider.Inputs);
    }

    [Fact]
    public async Task Summarizing_MarksDroppedInputButReturnsUnmarkedAssistantSummary()
    {
        using var provider = new SummaryClient();
        var input = Conversation();
        var output = (await new SummarizingChatReducer(provider, 2, 0)
            .ReduceAsync(input, default)).ToArray();

        Assert.Equal(3, output.Length);
        Assert.Equal(ChatRole.Assistant, output[0].Role);
        Assert.Equal("summary-1", output[0].Text);
        Assert.Null(output[0].AdditionalProperties);
        Assert.Equal("summary-1", input[1].AdditionalProperties!["__summary__"]);
        Assert.DoesNotContain(input[1], output);
        Assert.Equal([input[2], input[3]], output.Skip(1));
        Assert.Equal(["u1", "a1"], provider.Inputs.Single()
            .Where(message => message.Role != ChatRole.System).Select(message => message.Text));
    }

    [Fact]
    public async Task Summarizing_ReusingOriginalHistoryRecognizesMarker()
    {
        using var provider = new SummaryClient();
        var input = Conversation();
        var reducer = new SummarizingChatReducer(provider, 2, 0);
        await reducer.ReduceAsync(input, default);
        var output = (await reducer.ReduceAsync(input, default)).ToArray();

        Assert.Single(provider.Inputs);
        Assert.Equal("summary-1", output[0].Text);
        Assert.Equal([input[2], input[3]], output.Skip(1));
    }

    [Fact]
    public async Task Summarizing_ExcludesToolAndApprovalContentFromProviderInput()
    {
        using var provider = new SummaryClient();
        var call = new FunctionCallContent("call", "write");
        ChatMessage[] input =
        [
            new(ChatRole.User, "old question"),
            new(ChatRole.Assistant, [call]),
            new(ChatRole.Tool, [new FunctionResultContent("call", "done")]),
            new(ChatRole.Assistant, [new ToolApprovalRequestContent("approval", call)]),
            new(ChatRole.User, [new ToolApprovalResponseContent("approval", true, call)]),
            new(ChatRole.Assistant, "old reply"),
            new(ChatRole.User, "new question"),
            new(ChatRole.Assistant, "new reply"),
        ];

        await new SummarizingChatReducer(provider, 2, 0).ReduceAsync(input, default);

        Assert.Equal(["old question", "old reply"], provider.Inputs.Single()
            .Where(message => message.Role != ChatRole.System).Select(message => message.Text));
    }

    [Fact]
    public void Temporal_HistoryCountersAndValidatorAreAvailable()
    {
        Assert.Equal(typeof(int), typeof(Workflow).GetProperty(nameof(Workflow.CurrentHistoryLength))!.PropertyType);
        Assert.Equal(typeof(int), typeof(Workflow).GetProperty(nameof(Workflow.CurrentHistorySize))!.PropertyType);
        Assert.Equal("Resolve", new WorkflowUpdateValidatorAttribute("Resolve").UpdateMethod);
    }

    private static ChatMessage[] Conversation() =>
    [
        new(ChatRole.User, "u1"),
        new(ChatRole.Assistant, "a1"),
        new(ChatRole.User, "u2"),
        new(ChatRole.Assistant, "a2"),
    ];

    private sealed class SummaryClient : IChatClient
    {
        public List<ChatMessage[]> Inputs { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Inputs.Add(messages.ToArray());
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, $"summary-{Inputs.Count}")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Reducer contract requires a buffered response.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
