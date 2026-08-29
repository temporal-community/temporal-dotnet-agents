using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests;

public sealed class DurableChatCompletionPolicyTests
{
    public static TheoryData<
        ChatFinishReason?,
        int,
        int,
        bool> ClassificationCases => new()
    {
        { ChatFinishReason.Length, 0, (int)DurableChatStepDisposition.IncompleteResponse, false },
        { ChatFinishReason.Length, 2, (int)DurableChatStepDisposition.IncompleteResponse, true },
        { ChatFinishReason.ContentFilter, 0, (int)DurableChatStepDisposition.IncompleteResponse, false },
        { ChatFinishReason.ContentFilter, 2, (int)DurableChatStepDisposition.IncompleteResponse, true },
        { ChatFinishReason.ToolCalls, 2, (int)DurableChatStepDisposition.ContinueWithTools, false },
        { ChatFinishReason.ToolCalls, 0, (int)DurableChatStepDisposition.IncompleteResponse, true },
        { ChatFinishReason.Stop, 0, (int)DurableChatStepDisposition.FinalResponse, false },
        { ChatFinishReason.Stop, 2, (int)DurableChatStepDisposition.IncompleteResponse, true },
        { null, 2, (int)DurableChatStepDisposition.ContinueWithTools, false },
        { null, 0, (int)DurableChatStepDisposition.FinalResponse, false },
        { new ChatFinishReason("future_reason"), 0, (int)DurableChatStepDisposition.IncompleteResponse, true },
        { new ChatFinishReason("future_reason"), 2, (int)DurableChatStepDisposition.IncompleteResponse, true },
    };

    [Theory]
    [MemberData(nameof(ClassificationCases))]
    public void Classify_FinishReasonAndCalls_ReturnsExpectedDisposition(
        ChatFinishReason? finishReason,
        int toolCallCount,
        int expectedDisposition,
        bool expectedContradiction)
    {
        var actual = DurableChatCompletionPolicy.Classify(finishReason, toolCallCount);

        Assert.Equal(expectedDisposition, (int)actual.Disposition);
        Assert.Equal(expectedContradiction, actual.IsProviderOutputContradictory);
    }

    [Fact]
    public async Task GetChatStepAsync_PreservesFinishReasonAndClassification()
    {
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, []))
        {
            FinishReason = ChatFinishReason.Length,
        };
        using var client = new SingleResponseChatClient(response);
        using var provider = new ServiceCollection()
            .AddSingleton<IChatClient>(client)
            .BuildServiceProvider();
        var activities = new DurableChatActivities(provider);

        var result = await activities.GetChatStepAsync(new DurableChatInput
        {
            Messages = [new ChatMessage(ChatRole.User, "answer fully")],
        });

        Assert.True(result.IsFinal);
        Assert.Null(result.ToolCalls);
        Assert.Equal(ChatFinishReason.Length, result.FinishReason);
        Assert.Equal(DurableTurnCompletionReason.IncompleteResponse, result.CompletionReason);
        Assert.Empty(result.AssistantMessage.Contents);
    }

    private sealed class SingleResponseChatClient(ChatResponse response) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(response);

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            foreach (var update in response.ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
