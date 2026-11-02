using Microsoft.Extensions.AI;
using System.Text.Json;
using TemporalCommunity.Extensions.AI.Approvals;
using TemporalCommunity.Extensions.Tests.Shared;
using Temporalio.Exceptions;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests.Research;

public class DeterministicHarnessContractTests
{
    [Fact]
    public async Task ProviderScripts_AreRepeatableAndCaptureDetachedMessages()
    {
        static ScriptedChatClient Provider() => new(
        [
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "answer"))
            {
                ConversationId = "provider-conversation",
                Usage = new UsageDetails { InputTokenCount = 7, OutputTokenCount = 3 },
            },
        ]);
        using var first = Provider();
        using var second = Provider();
        var message = new ChatMessage(ChatRole.User, "question");
        var options = new ChatOptions { ConversationId = "provider-id", MaxOutputTokens = 12 };

        var a = await first.GetResponseAsync([message], options);
        var b = await second.GetResponseAsync([message], options);
        message.Contents.Clear();
        message.Contents.Add(new TextContent("changed"));
        options.ConversationId = "changed";

        Assert.Equal(a.Text, b.Text);
        Assert.Equal(a.ConversationId, b.ConversationId);
        Assert.Equal(7, a.Usage!.InputTokenCount);
        Assert.Equal(3, a.Usage.OutputTokenCount);
        Assert.Equal("question", first.Calls.Single().Messages.Single().Text);
        Assert.Equal("provider-id", first.Calls.Single().Options!.ConversationId);
        Assert.Equal(12, first.Calls.Single().Options!.MaxOutputTokens);
        Assert.NotSame(message, first.Calls.Single().Messages.Single());
        var exhausted = await Assert.ThrowsAsync<ApplicationFailureException>(() => first.GetResponseAsync([message]));
        Assert.True(exhausted.NonRetryable);
    }

    [Fact]
    public async Task CancelledProvider_DoesNotConsumeScript()
    {
        using var provider = new ScriptedChatClient([new ChatResponse(new ChatMessage(ChatRole.Assistant, "answer"))]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            provider.GetResponseAsync([], cancellationToken: cancellation.Token));
        Assert.Empty(provider.Calls);
        Assert.Equal("answer", (await provider.GetResponseAsync([])).Text);
    }

    [Fact]
    public async Task ToolEffect_IsObservableBeforeCompletion()
    {
        var attempt = new ControlledActivityAttempt<string>();
        var effects = 0;
        var tool = AIFunctionFactory.Create(
            (CancellationToken token) => attempt.ExecuteAsync(() => Interlocked.Increment(ref effects), token),
            "controlled_write");
        var result = tool.InvokeAsync(new AIFunctionArguments()).AsTask();
        await attempt.Entered.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, effects);
        Assert.False(result.IsCompleted);
        attempt.Complete("written");
        Assert.Equal("written", Assert.IsType<JsonElement>(await result).GetString());
        Assert.Equal(1, attempt.Invocations);
        Assert.Throws<InvalidOperationException>(() => attempt.Complete("duplicate"));
    }

    [Fact]
    public async Task CancelledAttempt_DoesNotUndoAlreadyAppliedEffect()
    {
        var attempt = new ControlledActivityAttempt<string>();
        var effects = 0;
        using var cancellation = new CancellationTokenSource();
        var result = attempt.ExecuteAsync(() => effects++, cancellation.Token);
        await attempt.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => result);
        Assert.Equal(1, effects);
        attempt.Complete("late external completion");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ApprovalAndFunctionExchange_RoundTripThroughDurableConverter(bool approved)
    {
        var converter = DurableAIDataConverter.Instance.PayloadConverter;
        var call = new FunctionCallContent("call-1", "write", new Dictionary<string, object?> { ["key"] = "operation-1" });
        ChatMessage[] messages =
        [
            new(ChatRole.Assistant, [new ToolApprovalRequestContent("approval-1", call)]),
            new(ChatRole.User, [new ToolApprovalResponseContent("approval-1", approved, call)]),
            new(ChatRole.Assistant, [call]),
            new(ChatRole.Tool, [new FunctionResultContent("call-1", "written")]),
        ];
        var roundTrip = Assert.IsType<ChatMessage[]>(
            converter.ToValue(converter.ToPayload(messages), typeof(ChatMessage[])));
        var request = Assert.IsType<ToolApprovalRequestContent>(roundTrip[0].Contents.Single());
        var response = Assert.IsType<ToolApprovalResponseContent>(roundTrip[1].Contents.Single());
        Assert.Equal("approval-1", request.RequestId);
        Assert.Equal("approval-1", response.RequestId);
        Assert.Equal(approved, response.Approved);
        Assert.Equal("call-1", Assert.IsType<FunctionCallContent>(roundTrip[2].Contents.Single()).CallId);
        Assert.Equal("call-1", Assert.IsType<FunctionResultContent>(roundTrip[3].Contents.Single()).CallId);
        var decision = new DurableApprovalDecision { RequestId = "approval-1", Approved = approved, Reason = "reviewed" };
        var restored = Assert.IsType<DurableApprovalDecision>(
            converter.ToValue(converter.ToPayload(decision), typeof(DurableApprovalDecision)));
        Assert.Equal(decision.RequestId, restored.RequestId);
        Assert.Equal(approved, restored.Approved);
        Assert.Equal("reviewed", restored.Reason);
    }
}
