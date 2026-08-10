using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace ExtensibleDurableTurns;

internal sealed class ScriptedChatClient : IChatClient
{
    public ChatClientMetadata Metadata { get; } = new("extensible-durable-turns-sample");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var hasToolResults = messages.Any(message =>
            message.Contents.Any(content => content is FunctionResultContent));
        if (hasToolResults)
        {
            return Task.FromResult(new ChatResponse(
                [new ChatMessage(ChatRole.Assistant, "The durable turn completed.")]));
        }

        return Task.FromResult(new ChatResponse(new ChatMessage(
            ChatRole.Assistant,
            [
                new FunctionCallContent(
                    "call-read",
                    "read_reference",
                    new Dictionary<string, object?> { ["reference"] = "sample" }),
                new FunctionCallContent(
                    "call-first",
                    "apply_first",
                    new Dictionary<string, object?> { ["value"] = "one" }),
                new FunctionCallContent(
                    "call-second",
                    "apply_second",
                    new Dictionary<string, object?> { ["value"] = "two" }),
            ])));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var update in response.ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
