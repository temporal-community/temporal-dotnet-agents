using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.AI.Exceptions;

namespace TemporalCommunity.Extensions.AI.Internal;

/// <summary>
/// Defines the two distinct <see cref="ChatOptions"/> boundaries used by durable chat.
/// </summary>
internal static class ChatOptionsSanitizer
{
    // This removed decorator-routing key can still appear in recorded durable payloads. Continue
    // stripping it so replay never leaks Temporal-private metadata to a provider.
    private const string LegacyChatClientFactoryKey = "temporal.chatClientFactoryKey";

    /// <summary>
    /// Validates and clones options for durable transport, retaining serializable routing
    /// metadata while rejecting provider-owned state that cannot be resumed across an
    /// activity boundary.
    /// </summary>
    internal static ChatOptions? PrepareForDurableTransport(ChatOptions? options)
    {
        if (options is null)
        {
            return null;
        }

        if (options.ConversationId is not null)
        {
            throw new DurableConfigurationException(
                $"ChatOptions.{nameof(ChatOptions.ConversationId)} is not supported for durable chat. " +
                "Use the application conversation ID passed to DurableChatSessionClient instead.");
        }

        if (options.RawRepresentationFactory is not null)
        {
            throw new DurableConfigurationException(
                $"ChatOptions.{nameof(ChatOptions.RawRepresentationFactory)} cannot cross the " +
                "durable workflow/activity boundary. Remove it before sending the request.");
        }

        if (options.ContinuationToken is not null)
        {
            throw new DurableConfigurationException(
                $"ChatOptions.{nameof(ChatOptions.ContinuationToken)} is provider-owned state and " +
                "cannot be resumed through a durable workflow. Start a new durable turn instead.");
        }

        return options.Clone();
    }

    /// <summary>
    /// Clones options only when necessary and removes Temporal-private keys immediately before
    /// the provider call. Ordinary MEAI options and user-owned additional properties are retained.
    /// </summary>
    internal static ChatOptions? PrepareForProvider(ChatOptions? options)
    {
        if (options?.AdditionalProperties is not { Count: > 0 } properties ||
            !properties.Any(pair => IsTemporalKey(pair.Key)))
        {
            return options;
        }

        var prepared = options.Clone();
        AdditionalPropertiesDictionary? retained = null;
        foreach (var pair in properties)
        {
            if (IsTemporalKey(pair.Key))
            {
                continue;
            }

            (retained ??= [])[pair.Key] = pair.Value;
        }

        prepared.AdditionalProperties = retained;
        return prepared;
    }

    /// <summary>Returns whether a property key is owned by this Temporal integration.</summary>
    internal static bool IsTemporalKey(string key) =>
        key is TemporalChatOptionsExtensions.ActivityTimeoutKey
            or TemporalChatOptionsExtensions.HeartbeatTimeoutKey
            or TemporalChatOptionsExtensions.MaxRetryAttemptsKey
            or TemporalChatOptionsExtensions.ChatClientKeySettingKey
            or LegacyChatClientFactoryKey
            || key.StartsWith(
                TemporalChatOptionsExtensions.ChatClientTagsKeyPrefix,
                StringComparison.Ordinal);
}

/// <summary>
/// Provider boundary that removes Temporal-private option keys for both chat call shapes.
/// </summary>
internal sealed class ProviderBoundaryChatClient(IChatClient innerClient)
    : DelegatingChatClient(innerClient)
{
    /// <inheritdoc/>
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        base.GetResponseAsync(
            messages,
            ChatOptionsSanitizer.PrepareForProvider(options),
            cancellationToken);

    /// <inheritdoc/>
    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        base.GetStreamingResponseAsync(
            messages,
            ChatOptionsSanitizer.PrepareForProvider(options),
            cancellationToken);
}
