using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.AI.Internal;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests;

public class ChatOptionsSanitizerTests
{
    [Fact]
    public async Task ProviderBoundary_NonStreaming_StripsOnlyTemporalKeys()
    {
        var inner = new RecordingChatClient();
        var boundary = new ProviderBoundaryChatClient(inner);
        var options = BuildProviderOptions();

        await boundary.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")], options);

        AssertProviderOptions(options, inner.Options);
    }

    [Fact]
    public async Task ProviderBoundary_Streaming_StripsOnlyTemporalKeys()
    {
        var inner = new RecordingChatClient();
        var boundary = new ProviderBoundaryChatClient(inner);
        var options = BuildProviderOptions();

        await foreach (var _ in boundary.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")], options))
        {
        }

        AssertProviderOptions(options, inner.Options);
    }

    private static ChatOptions BuildProviderOptions()
    {
        var options = new ChatOptions
        {
            Instructions = "keep instructions",
            ContinuationToken = ResponseContinuationToken.FromBytes(new byte[] { 1, 2, 3 }),
            RawRepresentationFactory = _ => null,
        }
            .WithChatClientFactoryKey("factory")
            .WithChatClientTag("tenant", "acme")
            .WithActivityTimeout(TimeSpan.FromSeconds(10));
        options.AdditionalProperties!["user.custom"] = "keep";
        return options;
    }

    private static void AssertProviderOptions(ChatOptions original, ChatOptions? actual)
    {
        Assert.NotNull(actual);
        Assert.NotSame(original, actual);
        Assert.NotSame(original.AdditionalProperties, actual!.AdditionalProperties);
        Assert.Equal("keep instructions", actual.Instructions);
        Assert.Same(original.ContinuationToken, actual.ContinuationToken);
        Assert.Same(original.RawRepresentationFactory, actual.RawRepresentationFactory);
        Assert.Equal("keep", actual.AdditionalProperties!["user.custom"]);
        Assert.DoesNotContain(
            actual.AdditionalProperties,
            pair => ChatOptionsSanitizer.IsTemporalKey(pair.Key));
        Assert.Equal("factory", original.GetChatClientFactoryKey());
        Assert.Contains(original.GetChatClientTags(), pair => pair.Key == "tenant");
    }

    private sealed class RecordingChatClient : IChatClient
    {
        public ChatOptions? Options { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Options = options;
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Options = options;
            await Task.CompletedTask;
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
