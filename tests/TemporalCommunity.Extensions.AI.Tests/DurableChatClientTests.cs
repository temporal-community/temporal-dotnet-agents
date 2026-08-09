using FakeItEasy;
using Microsoft.Extensions.AI;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests;

public class DurableChatClientTests
{
    [Fact]
    public async Task GetResponseAsync_PassesThroughWhenNotInWorkflow()
    {
        var expectedResponse = new ChatResponse([new ChatMessage(ChatRole.Assistant, "Hello!")]);
        var innerClient = A.Fake<IChatClient>();
        A.CallTo(() => innerClient.GetResponseAsync(
                A<IEnumerable<ChatMessage>>._, A<ChatOptions?>._, A<CancellationToken>._))
            .Returns(Task.FromResult(expectedResponse));

        var options = new DurableExecutionOptions { TaskQueue = "test" };
        var client = new DurableChatClient(innerClient, options);

        var messages = new List<ChatMessage> { new(ChatRole.User, "Hi") };
        var response = await client.GetResponseAsync(messages);

        Assert.Same(expectedResponse, response);
        A.CallTo(() => innerClient.GetResponseAsync(
                A<IEnumerable<ChatMessage>>._, A<ChatOptions?>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task GetStreamingResponseAsync_PassesThroughWhenNotInWorkflow()
    {
        // Create a response and convert to updates (avoids read-only Text setter).
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, "Hello World")]);
        var updates = response.ToChatResponseUpdates().ToList();

        var innerClient = A.Fake<IChatClient>();
        A.CallTo(() => innerClient.GetStreamingResponseAsync(
                A<IEnumerable<ChatMessage>>._, A<ChatOptions?>._, A<CancellationToken>._))
            .Returns(updates.ToAsyncEnumerable());

        var options = new DurableExecutionOptions { TaskQueue = "test" };
        var client = new DurableChatClient(innerClient, options);

        var messages = new List<ChatMessage> { new(ChatRole.User, "Hi") };
        var result = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(messages))
        {
            result.Add(update);
        }

        Assert.NotEmpty(result);
    }

    [Fact]
    public void Constructor_ThrowsOnNullOptions()
    {
        var innerClient = A.Fake<IChatClient>();
        Assert.Throws<ArgumentNullException>(() => new DurableChatClient(innerClient, null!));
    }

    [Fact]
    public void GetService_ReturnsDurableExecutionOptions()
    {
        var innerClient = A.Fake<IChatClient>();
        var options = new DurableExecutionOptions { TaskQueue = "test" };
        var client = new DurableChatClient(innerClient, options);

        var result = client.GetService<DurableExecutionOptions>();
        Assert.Same(options, result);
    }

    [Fact]
    public void CreateActivityOptions_NullPolicy_UsesBoundedDefault()
    {
        var innerClient = A.Fake<IChatClient>();
        var client = new DurableChatClient(innerClient, new DurableExecutionOptions { TaskQueue = "test" });

        var activityOptions = client.CreateActivityOptions();

        Assert.Equal("test", activityOptions.TaskQueue);
        Assert.NotNull(activityOptions.RetryPolicy);
        Assert.Equal(
            global::TemporalCommunity.Extensions.AI.Internal.DefaultRetryPolicy.DefaultMaximumAttempts,
            activityOptions.RetryPolicy.MaximumAttempts);
        Assert.Equal(
            TimeSpan.FromSeconds(
                global::TemporalCommunity.Extensions.AI.Internal.DefaultRetryPolicy.DefaultMaximumIntervalSeconds),
            activityOptions.RetryPolicy.MaximumInterval);
    }

    [Fact]
    public void CreateActivityOptions_ExplicitPolicy_IsPreserved()
    {
        var innerClient = A.Fake<IChatClient>();
        var retryPolicy = new Temporalio.Common.RetryPolicy { MaximumAttempts = 3 };
        var client = new DurableChatClient(
            innerClient,
            new DurableExecutionOptions { TaskQueue = "test", RetryPolicy = retryPolicy });

        var activityOptions = client.CreateActivityOptions();

        Assert.Same(retryPolicy, activityOptions.RetryPolicy);
    }

    [Fact]
    public async Task GetResponseAsync_StripsTemporalKeysBeforeForwardingToInner()
    {
        ChatOptions? capturedOptions = null;
        var expectedResponse = new ChatResponse([new ChatMessage(ChatRole.Assistant, "hi")]);
        var innerClient = A.Fake<IChatClient>();
        A.CallTo(() => innerClient.GetResponseAsync(
                A<IEnumerable<ChatMessage>>._, A<ChatOptions?>._, A<CancellationToken>._))
            .Invokes((IEnumerable<ChatMessage> _, ChatOptions? opts, CancellationToken _) =>
                capturedOptions = opts)
            .Returns(Task.FromResult(expectedResponse));

        var execOptions = new DurableExecutionOptions { TaskQueue = "test" };
        var client = new DurableChatClient(innerClient, execOptions);

        var chatOptions = new ChatOptions()
            .WithActivityTimeout(TimeSpan.FromMinutes(5))
            .WithHeartbeatTimeout(TimeSpan.FromMinutes(1))
            .WithMaxRetryAttempts(3);
        chatOptions.Instructions = "retain instructions";
        chatOptions.Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High };
        chatOptions.AllowMultipleToolCalls = true;
        chatOptions.AllowBackgroundResponses = true;
        chatOptions.ContinuationToken = ResponseContinuationToken.FromBytes(new byte[] { 1, 2, 3 });
        chatOptions.RawRepresentationFactory = _ => null;
        chatOptions.AdditionalProperties!["user.custom.key"] = "keep-me";

        var messages = new List<ChatMessage> { new(ChatRole.User, "hello") };
        await client.GetResponseAsync(messages, chatOptions);

        // The inner client must not see Temporal-internal keys.
        Assert.NotNull(capturedOptions?.AdditionalProperties);
        Assert.False(capturedOptions!.AdditionalProperties!.ContainsKey(TemporalChatOptionsExtensions.ActivityTimeoutKey));
        Assert.False(capturedOptions.AdditionalProperties.ContainsKey(TemporalChatOptionsExtensions.HeartbeatTimeoutKey));
        Assert.False(capturedOptions.AdditionalProperties.ContainsKey(TemporalChatOptionsExtensions.MaxRetryAttemptsKey));
        // Non-Temporal keys must be preserved.
        Assert.True(capturedOptions.AdditionalProperties.ContainsKey("user.custom.key"));
        Assert.Equal("keep-me", capturedOptions.AdditionalProperties["user.custom.key"]);
        Assert.Equal("retain instructions", capturedOptions.Instructions);
        Assert.Equal(ReasoningEffort.High, capturedOptions.Reasoning?.Effort);
        Assert.True(capturedOptions.AllowMultipleToolCalls);
        Assert.True(capturedOptions.AllowBackgroundResponses);
        Assert.Same(chatOptions.ContinuationToken, capturedOptions.ContinuationToken);
        Assert.Same(chatOptions.RawRepresentationFactory, capturedOptions.RawRepresentationFactory);
        Assert.NotSame(chatOptions, capturedOptions);
        Assert.True(chatOptions.AdditionalProperties.ContainsKey(TemporalChatOptionsExtensions.ActivityTimeoutKey));
    }

    [Fact]
    public async Task GetStreamingResponseAsync_PreservesOrdinaryOptionsWhileStrippingTemporalKeys()
    {
        ChatOptions? capturedOptions = null;
        var innerClient = A.Fake<IChatClient>();
        var updates = new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")])
            .ToChatResponseUpdates()
            .ToAsyncEnumerable();
        A.CallTo(() => innerClient.GetStreamingResponseAsync(
                A<IEnumerable<ChatMessage>>._, A<ChatOptions?>._, A<CancellationToken>._))
            .Invokes((IEnumerable<ChatMessage> _, ChatOptions? opts, CancellationToken _) =>
                capturedOptions = opts)
            .Returns(updates);

        var client = new DurableChatClient(
            innerClient,
            new DurableExecutionOptions { TaskQueue = "test" });
        var chatOptions = new ChatOptions
        {
            Instructions = "stream instructions",
            ContinuationToken = ResponseContinuationToken.FromBytes(new byte[] { 4, 5, 6 }),
            RawRepresentationFactory = _ => null,
        }.WithChatClientFactoryKey("factory");
        chatOptions.AdditionalProperties!["user.custom"] = "keep";

        await foreach (var _ in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")], chatOptions))
        {
        }

        Assert.NotNull(capturedOptions);
        Assert.Equal("stream instructions", capturedOptions!.Instructions);
        Assert.Same(chatOptions.ContinuationToken, capturedOptions.ContinuationToken);
        Assert.Same(chatOptions.RawRepresentationFactory, capturedOptions.RawRepresentationFactory);
        Assert.Equal("keep", capturedOptions.AdditionalProperties!["user.custom"]);
        Assert.DoesNotContain(
            capturedOptions.AdditionalProperties,
            pair => pair.Key.StartsWith("temporal.", StringComparison.Ordinal));
        Assert.Equal("factory", chatOptions.GetChatClientFactoryKey());
    }

    // ── Activity Summary (visible in Temporal Web UI activity list) ────────

    [Fact]
    public void BuildActivitySummary_ReturnsModelId_WhenSet()
    {
        var opts = new ChatOptions { ModelId = "gpt-4o-mini" };
        Assert.Equal("gpt-4o-mini", DurableChatClient.BuildActivitySummary(opts));
    }

    [Fact]
    public void BuildActivitySummary_ReturnsNull_WhenChatOptionsNull() =>
        Assert.Null(DurableChatClient.BuildActivitySummary(null));

    [Fact]
    public void BuildActivitySummary_ReturnsNull_WhenModelIdMissing()
    {
        Assert.Null(DurableChatClient.BuildActivitySummary(new ChatOptions()));
        Assert.Null(DurableChatClient.BuildActivitySummary(new ChatOptions { ModelId = "" }));
        Assert.Null(DurableChatClient.BuildActivitySummary(new ChatOptions { ModelId = "   " }));
    }

    [Fact]
    public async Task GetResponseAsync_StripsChartClientKey_BeforeForwardingToInnerClient()
    {
        ChatOptions? capturedOptions = null;
        var expectedResponse = new ChatResponse([new ChatMessage(ChatRole.Assistant, "hi")]);
        var innerClient = A.Fake<IChatClient>();
        A.CallTo(() => innerClient.GetResponseAsync(
                A<IEnumerable<ChatMessage>>._, A<ChatOptions?>._, A<CancellationToken>._))
            .Invokes((IEnumerable<ChatMessage> _, ChatOptions? opts, CancellationToken _) =>
                capturedOptions = opts)
            .Returns(Task.FromResult(expectedResponse));

        var execOptions = new DurableExecutionOptions { TaskQueue = "test" };
        var client = new DurableChatClient(innerClient, execOptions);

        // Set ChatClientKey AND one non-Temporal additional property.
        var chatOptions = new ChatOptions().WithChatClientKey("gpt-4o");
        chatOptions.AdditionalProperties!["custom-key"] = "custom-value";

        var messages = new List<ChatMessage> { new(ChatRole.User, "hello") };
        await client.GetResponseAsync(messages, chatOptions);

        // ChatClientKey must not leak to the inner client.
        Assert.NotNull(capturedOptions?.AdditionalProperties);
        Assert.False(capturedOptions!.AdditionalProperties!.ContainsKey(TemporalChatOptionsExtensions.ChatClientKeySettingKey));
        // Non-Temporal key must be preserved.
        Assert.True(capturedOptions.AdditionalProperties.ContainsKey("custom-key"));
        Assert.Equal("custom-value", capturedOptions.AdditionalProperties["custom-key"]);
    }
}
