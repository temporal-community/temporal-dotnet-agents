using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace TemporalCommunity.Extensions.AI.Session;

/// <summary>
/// Concrete <see cref="DurableSessionEntry"/> representing a response turn — the outbound
/// messages produced by a model / agent invocation, optional <see cref="UsageDetails"/>, and
/// serializable provider response metadata.
/// </summary>
public class DurableSessionResponse : DurableSessionEntry
{
    /// <summary>
    /// Token-usage details reported by the model for this turn, when available.
    /// </summary>
    public UsageDetails? Usage { get; init; }

    /// <summary>The provider's response ID, when supplied.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResponseId { get; init; }

    /// <summary>The model ID reported by the provider, when supplied.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ModelId { get; init; }

    /// <summary>
    /// Serializable provider metadata from <see cref="ChatResponse.AdditionalProperties"/>.
    /// Values are represented as JSON so their original CLR runtime types are not implied.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, JsonElement>? ResponseAdditionalProperties { get; init; }

    /// <summary>Gets the provider-reported reason the terminal model step stopped.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ChatFinishReason? FinishReason { get; init; }

    /// <summary>
    /// Gets why the package-managed turn returned. The default preserves session entries written
    /// before this property was introduced.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public DurableTurnCompletionReason CompletionReason { get; init; } =
        DurableTurnCompletionReason.FinalResponse;

    /// <summary>
    /// Returns the text of the last assistant message in <see cref="DurableSessionEntry.Messages"/>,
    /// or an empty string if no assistant message is present. Convenience accessor for the common
    /// "give me the reply text" pattern that <see cref="ChatResponse.Text"/> previously served.
    /// </summary>
    [JsonIgnore]
    public string Text =>
        Messages.LastOrDefault(m => m.Role == ChatRole.Assistant)?.Text ?? string.Empty;

    /// <summary>
    /// Creates a response entry from a <see cref="ChatResponse"/>.
    /// </summary>
    /// <param name="correlationId">
    /// Correlation ID of the originating request. Must be non-null and non-empty.
    /// </param>
    /// <param name="response">The chat response to capture.</param>
    /// <param name="timestamp">
    /// Fallback creation timestamp used when neither the response nor any of its messages have a
    /// <c>CreatedAt</c> set. Workflow callers must pass <c>Workflow.UtcNow</c>; activity / external
    /// callers should pass <see cref="DateTimeOffset.UtcNow"/>.
    /// </param>
    public static DurableSessionResponse FromChatResponse(
        string correlationId,
        ChatResponse response,
        DateTimeOffset timestamp) =>
        FromChatResponse(
            correlationId,
            response,
            timestamp,
            DurableTurnCompletionReason.FinalResponse);

    /// <summary>
    /// Creates a response entry from a <see cref="ChatResponse"/> and its package-level completion
    /// reason.
    /// </summary>
    /// <param name="correlationId">Correlation ID of the originating request.</param>
    /// <param name="response">The chat response to capture.</param>
    /// <param name="timestamp">Fallback creation timestamp.</param>
    /// <param name="completionReason">The package-level reason the managed turn returned.</param>
    public static DurableSessionResponse FromChatResponse(
        string correlationId,
        ChatResponse response,
        DateTimeOffset timestamp,
        DurableTurnCompletionReason completionReason)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (string.IsNullOrEmpty(correlationId))
        {
            throw new ArgumentException(
                "correlationId is required and must be non-empty.",
                nameof(correlationId));
        }

        DateTimeOffset createdAt = response.CreatedAt
            ?? (response.Messages.Count > 0 ? response.Messages.Max(m => m.CreatedAt) : null)
            ?? timestamp;

        return new DurableSessionResponse
        {
            CorrelationId = correlationId,
            CreatedAt = createdAt,
            Messages = response.Messages.ToList(),
            Usage = response.Usage,
            ResponseId = response.ResponseId,
            ModelId = response.ModelId,
            ResponseAdditionalProperties = SerializeAdditionalProperties(response.AdditionalProperties),
            FinishReason = response.FinishReason,
            CompletionReason = completionReason,
        };
    }

    private static IReadOnlyDictionary<string, JsonElement>? SerializeAdditionalProperties(
        AdditionalPropertiesDictionary? properties)
    {
        if (properties is not { Count: > 0 })
        {
            return null;
        }

        var result = new Dictionary<string, JsonElement>(properties.Count, StringComparer.Ordinal);
        foreach (var (key, value) in properties)
        {
            result.Add(key, JsonSerializer.SerializeToElement(value, DurableAIJsonUtilities.DefaultOptions));
        }

        return result;
    }
}
