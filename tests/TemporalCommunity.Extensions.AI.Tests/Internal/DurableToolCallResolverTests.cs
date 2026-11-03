using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.AI.Internal;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests.Internal;

public sealed class DurableToolCallResolverTests
{
    [Fact]
    public void Resolve_IgnoresInformationalAndAlreadyAnsweredCalls()
    {
        var priorMessages = new[]
        {
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("answered", "done")]),
        };
        var responseMessages = new[]
        {
            new ChatMessage(ChatRole.Assistant,
            [
                new FunctionCallContent("informational", "tool") { InformationalOnly = true },
                new FunctionCallContent("answered", "tool"),
                new FunctionCallContent("actionable", "tool"),
            ]),
        };

        var result = DurableToolCallResolver.Resolve(priorMessages, responseMessages);

        Assert.Null(result.Failure);
        Assert.Equal("actionable", Assert.Single(result.ActionableCalls).CallId);
    }

    [Fact]
    public void Resolve_IgnoresCallsAnsweredInSameProviderResponse()
    {
        var responseMessages = new[]
        {
            new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("same-response", "tool")]),
            new ChatMessage(
                ChatRole.Tool,
                [new FunctionResultContent("same-response", "already done")]),
        };

        var result = DurableToolCallResolver.Resolve([], responseMessages);

        Assert.Null(result.Failure);
        Assert.Empty(result.ActionableCalls);
    }

    [Fact]
    public void Resolve_RejectsDuplicateActionableCallIdsAsOneInvalidBatch()
    {
        var responseMessages = new[]
        {
            new ChatMessage(ChatRole.Assistant,
            [
                new FunctionCallContent("duplicate", "first"),
                new FunctionCallContent("duplicate", "second"),
            ]),
        };

        var result = DurableToolCallResolver.Resolve([], responseMessages);

        Assert.Equal("duplicate_call_id", result.Failure);
        Assert.Empty(result.ActionableCalls);
    }

    [Theory]
    [InlineData("", "tool")]
    [InlineData("call", " ")]
    public void Resolve_RejectsMalformedCallsAsOneInvalidBatch(string callId, string name)
    {
        var result = DurableToolCallResolver.Resolve(
            [],
            [new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(callId, name)])]);

        Assert.Equal("invalid_call", result.Failure);
        Assert.Empty(result.ActionableCalls);
    }

    [Fact]
    public void Resolve_RejectsCallsWithArgumentMappingErrors()
    {
        var result = DurableToolCallResolver.Resolve(
            [],
            [
                new ChatMessage(
                    ChatRole.Assistant,
                    [
                        new FunctionCallContent("invalid", "tool")
                        {
                            Exception = new InvalidOperationException("untrusted detail"),
                        },
                    ]),
            ]);

        Assert.Equal("invalid_call", result.Failure);
        Assert.Empty(result.ActionableCalls);
    }
}
