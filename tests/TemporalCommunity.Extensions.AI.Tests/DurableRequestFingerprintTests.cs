using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.AI.Internal;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests;

public class DurableRequestFingerprintTests
{
    [Fact]
    public void CreateChat_CanonicalizesObjectKeyOrderAndIgnoresRequestCorrelation()
    {
        var firstOptions = new ChatOptions
        {
            ModelId = "model-1",
            AdditionalProperties = new AdditionalPropertiesDictionary(),
        };
        firstOptions.AdditionalProperties["z"] = 1;
        firstOptions.AdditionalProperties["a"] = 2;

        var secondOptions = new ChatOptions
        {
            ModelId = "model-1",
            AdditionalProperties = new AdditionalPropertiesDictionary(),
        };
        secondOptions.AdditionalProperties["a"] = 2;
        secondOptions.AdditionalProperties["z"] = 1;

        var first = DurableRequestFingerprint.CreateChat(
            new DurableChatInput
            {
                Messages = [new ChatMessage(ChatRole.User, "hello")],
                Options = firstOptions,
                CorrelationId = "trace-one",
                RequestId = "request-one",
            },
            new DurableChatWorkflowInput { DefaultChatClientKey = "primary" });
        var second = DurableRequestFingerprint.CreateChat(
            new DurableChatInput
            {
                Messages = [new ChatMessage(ChatRole.User, "hello")],
                Options = secondOptions,
                CorrelationId = "trace-two",
                RequestId = "request-two",
            },
            new DurableChatWorkflowInput { DefaultChatClientKey = "primary" });

        Assert.Equal(first, second);
        Assert.StartsWith("tai-request-v1:", first, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateChat_ChangesForEffectBearingRequestOptionsAndAuthority()
    {
        var messages = new[] { new ChatMessage(ChatRole.User, "hello") };
        var workflowInput = new DurableChatWorkflowInput { DefaultChatClientKey = "primary" };
        var baseline = DurableRequestFingerprint.CreateChat(
            new DurableChatInput { Messages = messages, Options = new ChatOptions { ModelId = "model-1" } },
            workflowInput);
        var changedMessage = DurableRequestFingerprint.CreateChat(
            new DurableChatInput
            {
                Messages = [new ChatMessage(ChatRole.User, "different")],
                Options = new ChatOptions { ModelId = "model-1" },
            },
            workflowInput);
        var changedModel = DurableRequestFingerprint.CreateChat(
            new DurableChatInput { Messages = messages, Options = new ChatOptions { ModelId = "model-2" } },
            workflowInput);
        var changedProfile = DurableRequestFingerprint.CreateChat(
            new DurableChatInput { Messages = messages, Options = new ChatOptions { ModelId = "model-1" } },
            new DurableChatWorkflowInput { DefaultChatClientKey = "secondary" });

        Assert.NotEqual(baseline, changedMessage);
        Assert.NotEqual(baseline, changedModel);
        Assert.NotEqual(baseline, changedProfile);
    }

    [Fact]
    public void CreateTurn_IncludesTypedRequestDataAndTurnToolsetSelection()
    {
        var workflowInput = new DurableChatWorkflowInput { DefaultChatClientKey = "primary" };
        var request = new DurableTurnRequest<string, string>
        {
            Messages = [new ChatMessage(ChatRole.User, "hello")],
            RequestData = "domain-input",
            InitialTurnState = "state",
            Options = new DurableTurnOptions { ToolsetIds = ["weather"] },
        };
        var original = DurableRequestFingerprint.CreateTurn(request, workflowInput, "manifest-one");
        var changedData = DurableRequestFingerprint.CreateTurn(
            new DurableTurnRequest<string, string>
            {
                Messages = request.Messages,
                RequestData = "different-domain-input",
                InitialTurnState = request.InitialTurnState,
                Options = request.Options,
            },
            workflowInput,
            "manifest-one");
        var changedAuthority = DurableRequestFingerprint.CreateTurn(
            request,
            workflowInput,
            "manifest-two");

        Assert.NotEqual(original, changedData);
        Assert.NotEqual(original, changedAuthority);
    }
}
