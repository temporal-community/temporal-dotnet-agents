using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.AI.Exceptions;
using TemporalCommunity.Extensions.AI.Session;

namespace TemporalCommunity.Extensions.AI;

/// <summary>Structured-output operations for durable chat sessions.</summary>
public static class DurableChatSessionStructuredOutputExtensions
{
    /// <summary>
    /// Sends a turn with a JSON schema generated for <typeparamref name="T"/> and returns the
    /// validated MEAI typed response, including the original response metadata.
    /// </summary>
    /// <param name="sessionClient">The durable session client.</param>
    /// <param name="conversationId">The application conversation identifier.</param>
    /// <param name="messages">The messages to send.</param>
    /// <param name="options">Optional chat options. ResponseFormat must be unset.</param>
    /// <param name="serializerOptions">
    /// Optional JSON options used for schema generation and result deserialization.
    /// </param>
    /// <param name="correlationId">Optional observability identifier.</param>
    /// <param name="requestId">
    /// Optional caller-known request ID. Reuse it only with identical messages, options, and
    /// generated schema.
    /// </param>
    /// <param name="expectedSessionEpoch">Optional session-lineage precondition.</param>
    /// <param name="cancellationToken">Cancellation token for the RPC wait.</param>
    /// <returns>A validated <see cref="ChatResponse{T}"/> preserving response metadata.</returns>
    /// <exception cref="DurableConfigurationException">
    /// The options already specify a response format or the requested type cannot be represented
    /// by the JSON serializer.
    /// </exception>
    /// <exception cref="DurableStructuredOutputException">
    /// The turn did not produce a final, non-null value deserializable as
    /// <typeparamref name="T"/>.
    /// </exception>
    public static async Task<ChatResponse<T>> SendStructuredAsync<T>(
        this IDurableChatSessionClient sessionClient,
        string conversationId,
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        JsonSerializerOptions? serializerOptions = null,
        string? correlationId = null,
        string? requestId = null,
        string? expectedSessionEpoch = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionClient);
        ArgumentNullException.ThrowIfNull(messages);

        if (options?.ResponseFormat is not null)
        {
            throw new DurableConfigurationException(
                $"ChatOptions.{nameof(ChatOptions.ResponseFormat)} must be unset when using " +
                $"{nameof(SendStructuredAsync)}; the requested type supplies the response schema.");
        }

        var effectiveSerializerOptions = PrepareSerializerOptions<T>(serializerOptions);
        ChatResponseFormatJson responseFormat;
        try
        {
            responseFormat = ChatResponseFormat.ForJsonSchema<T>(effectiveSerializerOptions);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or NotSupportedException or JsonException)
        {
            throw new DurableConfigurationException(
                $"A JSON schema could not be generated for structured response type '{typeof(T).Name}'.",
                exception);
        }

        if (responseFormat.Schema is not { ValueKind: JsonValueKind.Object } schema
            || !schema.TryGetProperty("type", out var schemaType)
            || schemaType.ValueKind != JsonValueKind.String
            || !string.Equals(schemaType.GetString(), "object", StringComparison.Ordinal))
        {
            throw new DurableConfigurationException(
                $"Structured response type '{typeof(T).Name}' must produce a JSON object at the schema root. " +
                "Wrap primitive or array values in an object type.");
        }

        var structuredOptions = options?.Clone() ?? new ChatOptions();
        structuredOptions.ResponseFormat = responseFormat;
        var response = await sessionClient.SendAsync(
            conversationId,
            messages,
            options: structuredOptions,
            correlationId: correlationId,
            requestId: requestId,
            expectedSessionEpoch: expectedSessionEpoch,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return response.ToStructuredChatResponse<T>(effectiveSerializerOptions);
    }

    /// <summary>
    /// Sends one user text message with a JSON schema generated for
    /// <typeparamref name="T"/> and returns the validated MEAI typed response.
    /// </summary>
    /// <param name="sessionClient">The durable session client.</param>
    /// <param name="conversationId">The application conversation identifier.</param>
    /// <param name="message">User text to send.</param>
    /// <param name="options">Optional chat options. ResponseFormat must be unset.</param>
    /// <param name="serializerOptions">Optional JSON schema and deserialization options.</param>
    /// <param name="correlationId">Optional observability identifier.</param>
    /// <param name="requestId">Optional caller-known request ID.</param>
    /// <param name="expectedSessionEpoch">Optional session-lineage precondition.</param>
    /// <param name="cancellationToken">Cancellation token for the RPC wait.</param>
    /// <returns>A validated <see cref="ChatResponse{T}"/> preserving response metadata.</returns>
    public static Task<ChatResponse<T>> SendStructuredAsync<T>(
        this IDurableChatSessionClient sessionClient,
        string conversationId,
        string message,
        ChatOptions? options = null,
        JsonSerializerOptions? serializerOptions = null,
        string? correlationId = null,
        string? requestId = null,
        string? expectedSessionEpoch = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        return SendStructuredAsync<T>(
            sessionClient,
            conversationId,
            [new ChatMessage(ChatRole.User, message)],
            options,
            serializerOptions,
            correlationId,
            requestId,
            expectedSessionEpoch,
            cancellationToken);
    }

    private static JsonSerializerOptions PrepareSerializerOptions<T>(
        JsonSerializerOptions? serializerOptions)
    {
        if (serializerOptions is null)
        {
            return DurableAIJsonUtilities.DefaultOptions;
        }

        var options = new JsonSerializerOptions(serializerOptions)
        {
            TypeInfoResolver = serializerOptions.TypeInfoResolver ?? new DefaultJsonTypeInfoResolver(),
        };
        try
        {
            _ = options.GetTypeInfo(typeof(T));
            options.MakeReadOnly();
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or NotSupportedException or ArgumentException)
        {
            throw new DurableConfigurationException(
                $"JSON serializer metadata is unavailable for structured response type '{typeof(T).Name}'.",
                exception);
        }

        return options;
    }
}
