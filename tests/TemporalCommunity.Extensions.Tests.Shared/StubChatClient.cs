using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace TemporalCommunity.Extensions.Tests.Shared;

/// <summary>
/// A minimal <see cref="IChatClient"/> for use in unit tests where the client is
/// required by the object graph but no actual LLM calls are expected.
/// All methods throw <see cref="NotImplementedException"/> when called.
/// </summary>
/// <remarks>
/// Prefer this over private nested stubs that omit <see cref="ChatClientMetadata"/>:
/// the MEAI pipeline inspects <c>Metadata</c> during client resolution and missing
/// metadata can mask routing bugs in tests.
/// </remarks>
public sealed class StubChatClient : IChatClient
{
    public ChatClientMetadata Metadata { get; } = new("stub");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException($"{nameof(StubChatClient)} does not serve responses.");

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        throw new NotImplementedException($"{nameof(StubChatClient)} does not serve streaming responses.");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
