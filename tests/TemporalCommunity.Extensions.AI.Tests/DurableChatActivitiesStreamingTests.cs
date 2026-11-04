using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Temporalio.Exceptions;
using Temporalio.Testing;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests;

public class DurableChatActivitiesStreamingTests
{
    private static DurableChatActivities BuildActivities(IChatClient client)
    {
        var provider = new ServiceCollection()
            .AddSingleton(client)
            .BuildServiceProvider();
        return new DurableChatActivities(provider, loggerFactory: null);
    }

    private static DurableChatInput SimpleInput() =>
        new() { Messages = [new ChatMessage(ChatRole.User, "ping")] };

    [Fact]
    public async Task GetResponseAsync_UsesStreamingPath()
    {
        var client = new TrackingChatClient();
        var activities = BuildActivities(client);

        await activities.GetResponseAsync(SimpleInput());

        Assert.True(client.WasStreamingCalled);
        Assert.False(client.WasNonStreamingCalled);
    }

    [Fact]
    public async Task GetResponseAsync_AssemblesResponseFromStreamingUpdates()
    {
        var client = new MultiChunkChatClient();
        var activities = BuildActivities(client);

        var response = await activities.GetResponseAsync(SimpleInput());

        Assert.Single(response.Messages);
        Assert.Equal(ChatRole.Assistant, response.Messages[0].Role);
        // ToChatResponse() coalesces consecutive same-role updates.
        // ChatMessage.Text concatenates all TextContent items in the message.
        Assert.Equal("Hello world!", response.Messages[0].Text);
    }

    [Fact]
    public async Task GetResponseAsync_PropagatesExceptionFromStream()
    {
        var client = new ThrowingChatClient();
        var activities = BuildActivities(client);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => activities.GetResponseAsync(SimpleInput()));
    }

    [Fact]
    public async Task GetResponseAsync_RejectsOversizedActivityResultAsBoundedFailure()
    {
        var activities = BuildActivities(new LargeResponseChatClient());
        var input = new DurableChatInput
        {
            Messages = [new ChatMessage(ChatRole.User, "ping")],
            MaximumTransportPayloadSizeBytes = 2_048,
            EnableTransportPayloadGuard = true,
        };
        var env = new ActivityEnvironment
        {
            PayloadConverter = DurableAIDataConverter.Instance.PayloadConverter,
        };

        var exception = await Assert.ThrowsAsync<ApplicationFailureException>(
            () => env.RunAsync(() => activities.GetResponseAsync(input)));

        Assert.Equal("DurableModelResultTooLarge", exception.ErrorType);
        Assert.True(exception.NonRetryable);
        Assert.DoesNotContain(new string('x', 100), exception.Message, StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public async Task GetChatStepAsync_RejectsOversizedManagedModelResultAsBoundedFailure()
    {
        var activities = BuildActivities(new LargeResponseChatClient());
        var input = new DurableChatInput
        {
            Messages = [new ChatMessage(ChatRole.User, "ping")],
            MaximumTransportPayloadSizeBytes = 2_048,
            EnableTransportPayloadGuard = true,
        };
        var env = new ActivityEnvironment
        {
            PayloadConverter = DurableAIDataConverter.Instance.PayloadConverter,
        };

        var exception = await Assert.ThrowsAsync<ApplicationFailureException>(
            () => env.RunAsync(() => activities.GetChatStepAsync(input)));

        Assert.Equal("DurableModelResultTooLarge", exception.ErrorType);
        Assert.True(exception.NonRetryable);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public async Task GetResponseAsync_BoundsProviderFailureWithoutChangingRetryability()
    {
        var activities = BuildActivities(new LargeFailureChatClient());
        var env = new ActivityEnvironment();

        var exception = await Assert.ThrowsAsync<ApplicationFailureException>(
            () => env.RunAsync(() => activities.GetResponseAsync(SimpleInput())));

        Assert.Equal(nameof(InvalidOperationException), exception.ErrorType);
        Assert.False(exception.NonRetryable);
        Assert.DoesNotContain(new string('s', 100), exception.Message, StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private sealed class TrackingChatClient : IChatClient
    {
        public bool WasStreamingCalled { get; private set; }
        public bool WasNonStreamingCalled { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            WasNonStreamingCalled = true;
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            WasStreamingCalled = true;
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class MultiChunkChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "Hello world!")]));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "Hello");
            yield return new ChatResponseUpdate(ChatRole.Assistant, " world");
            yield return new ChatResponseUpdate(ChatRole.Assistant, "!");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class ThrowingChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            throw new OperationCanceledException("simulated cancellation");
#pragma warning disable CS0162 // yield break required for iterator-method shape; unreachable due to throw above
            yield break;
#pragma warning restore CS0162
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class LargeResponseChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, new string('x', 10_000))]));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, new string('x', 10_000));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class LargeFailureChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(new string('s', 10_000));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            throw new InvalidOperationException(new string('s', 10_000));
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
