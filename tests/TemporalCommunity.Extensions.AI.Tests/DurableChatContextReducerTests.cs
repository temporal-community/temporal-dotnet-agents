#pragma warning disable MEAI001 // The pinned MEAI reducers and interface are experimental.
using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.AI.Exceptions;
using TemporalCommunity.Extensions.AI.Internal;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests;

public sealed class DurableChatContextReducerTests
{
    [Fact]
    public async Task MessageCounting_ReducesOnlyEligiblePriorTurnsAndKeepsToolGrammarValid()
    {
        ChatMessage[] messages =
        [
            new(ChatRole.System, "system instructions"),
            new(ChatRole.User, "old request"),
            new(ChatRole.Assistant, [new FunctionCallContent("call-1", "lookup")]),
            new(ChatRole.Tool, [new FunctionResultContent("call-1", "old result")]),
            new(ChatRole.Assistant, "old response"),
            new(ChatRole.User, "active request"),
        ];

        var projection = await DurableChatContextReducer.ReduceAsync(
            new MessageCountingChatReducer(1),
            messages,
            currentTurnStartIndex: 5,
            previousTurnRanges: [new DurableChatTurnRange(1, 5, IsProtected: false)],
            CancellationToken.None);

        Assert.Equal(ChatRole.System, projection.Messages[0].Role);
        Assert.Equal("system instructions", projection.Messages[0].Text);
        Assert.Equal("old response", projection.Messages[1].Text);
        Assert.Equal("active request", projection.Messages[2].Text);
        Assert.Equal(2, projection.CurrentTurnStartIndex);
        Assert.Empty(projection.Messages.SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>());
        Assert.Empty(projection.Messages.SelectMany(message => message.Contents)
            .OfType<FunctionResultContent>());
        Assert.Equal("old request", messages[1].Text);
        Assert.Equal(2, projection.PreviousTurnRanges.Count);
        Assert.All(projection.PreviousTurnRanges, range => Assert.True(range.IsProtected));
    }

    [Fact]
    public async Task Summarizing_RunsOnDeepCopiesAndLeavesActiveTurnAndRetainedMessagesUnchanged()
    {
        var client = new SummaryClient();
        ChatMessage[] messages =
        [
            new(ChatRole.User, "older question 1"),
            new(ChatRole.Assistant, "older answer 1"),
            new(ChatRole.User, "older question 2"),
            new(ChatRole.Assistant, "older answer 2"),
            new(ChatRole.User, "active question"),
        ];

        var projection = await DurableChatContextReducer.ReduceAsync(
            new SummarizingChatReducer(client, 2, 0),
            messages,
            currentTurnStartIndex: 4,
            previousTurnRanges: [new DurableChatTurnRange(0, 4, IsProtected: false)],
            CancellationToken.None);

        Assert.Equal("older question 1", messages[0].Text);
        Assert.Null(messages[1].AdditionalProperties);
        Assert.Equal("active question", projection.Messages[^1].Text);
        Assert.Equal(3, projection.CurrentTurnStartIndex);
        Assert.Equal("summary", projection.Messages[0].Text);
        Assert.Equal(["older question 1", "older answer 1"], client.LastInput!
            .Where(message => message.Role != ChatRole.System)
            .Select(message => message.Text));
    }

    [Fact]
    public async Task Reducer_DoesNotReceiveProtectedInstructionsOrInterruptedTurns()
    {
        ChatMessage[] messages =
        [
            new(ChatRole.System, "instructions"),
            new(ChatRole.User, "protected request"),
            new(ChatRole.Assistant, "protected answer"),
            new(ChatRole.User, "active request"),
        ];

        var projection = await DurableChatContextReducer.ReduceAsync(
            new MessageCountingChatReducer(1),
            messages,
            currentTurnStartIndex: 3,
            previousTurnRanges:
            [
                new DurableChatTurnRange(0, 1, IsProtected: false),
                new DurableChatTurnRange(1, 3, IsProtected: true),
            ],
            CancellationToken.None);

        Assert.Equal(messages.Select(message => message.Text), projection.Messages.Select(message => message.Text));
    }

    [Fact]
    public async Task Reducer_InvalidToolGrammarFailsBeforeModelCall()
    {
        var messages = new ChatMessage[]
        {
            new(ChatRole.User, "older question"),
            new(ChatRole.Assistant, "older answer"),
            new(ChatRole.User, "active question"),
        };
        var reducer = new FixedReducer(
            [new ChatMessage(ChatRole.Tool, [new FunctionResultContent("orphan", "result")])]);

        var exception = await Assert.ThrowsAsync<DurableContextReductionException>(() =>
            DurableChatContextReducer.ReduceAsync(
                reducer,
                messages,
                currentTurnStartIndex: 2,
                previousTurnRanges: [new DurableChatTurnRange(0, 2, IsProtected: false)],
                CancellationToken.None));

        Assert.Contains("orphaned", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReducerOutputThatStillExceedsBudgetIsRejected()
    {
        var messages = new ChatMessage[]
        {
            new(ChatRole.User, "older question"),
            new(ChatRole.Assistant, "older answer"),
            new(ChatRole.User, "active question"),
        };
        var projection = await DurableChatContextReducer.ReduceAsync(
            new FixedReducer(messages),
            messages,
            currentTurnStartIndex: 2,
            previousTurnRanges: [new DurableChatTurnRange(0, 2, IsProtected: false)],
            CancellationToken.None);

        Assert.Throws<DurableContextBudgetExceededException>(() =>
            DurableModelContextBudget.EnsureFits(
                projection.Messages,
                new ChatOptions(),
                maximumContextTokens: 10,
                outputTokenReserve: 1,
                new CountingEstimator()));
    }

    private sealed class FixedReducer(IReadOnlyList<ChatMessage> output) : IChatReducer
    {
        public Task<IEnumerable<ChatMessage>> ReduceAsync(
            IEnumerable<ChatMessage> messages,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(output.AsEnumerable());
        }
    }

    private sealed class SummaryClient : IChatClient
    {
        public IReadOnlyList<ChatMessage>? LastInput { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastInput = messages.ToArray();
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "summary")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("SummarizingChatReducer requires a buffered response.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class CountingEstimator : IDurableChatTokenEstimator
    {
        public int EstimateInputTokens(IReadOnlyList<ChatMessage> messages, ChatOptions options) => 100;
    }
}
