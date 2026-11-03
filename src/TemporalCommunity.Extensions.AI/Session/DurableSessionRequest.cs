using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Temporalio.Workflows;

namespace TemporalCommunity.Extensions.AI.Session;

/// <summary>
/// Concrete <see cref="DurableSessionEntry"/> representing a request turn — the inbound
/// messages that triggered a model / agent invocation. Library-specific request fields
/// (e.g., MAF's <c>OrchestrationId</c> / <c>ResponseSchema</c>) live on subclasses.
/// </summary>
public class DurableSessionRequest : DurableSessionEntry
{
    /// <summary>
    /// Creates a request entry from a flat list of <see cref="ChatMessage"/>s.
    /// </summary>
    /// <param name="messages">The user-supplied messages for this turn.</param>
    /// <param name="correlationId">
    /// Optional per-turn correlation ID. When null or empty, an ID is auto-generated using
    /// <c>Workflow.NewGuid().ToString("N")</c> when called inside a Temporal workflow context
    /// (replay-safe, deterministic) or <c>Guid.NewGuid().ToString("N")</c> otherwise.
    /// </param>
    /// <param name="timestamp">
    /// Optional fallback creation timestamp used when none of the supplied <paramref name="messages"/>
    /// have a <see cref="ChatMessage.CreatedAt"/>. When null, defaults to <c>Workflow.UtcNow</c>
    /// inside a workflow context (replay-safe) or <see cref="DateTimeOffset.UtcNow"/> otherwise.
    /// </param>
    public static DurableSessionRequest FromMessages(
        IReadOnlyList<ChatMessage> messages,
        string? correlationId = null,
        DateTimeOffset? timestamp = null,
        string? requestId = null,
        string? inputFingerprint = null)
    {
        ArgumentNullException.ThrowIfNull(messages);

        if (requestId is not null && string.IsNullOrWhiteSpace(requestId))
        {
            throw new ArgumentException("requestId cannot be empty or whitespace.", nameof(requestId));
        }

        string effectiveCorrelationId = !string.IsNullOrEmpty(correlationId)
            ? correlationId
            : (Workflow.InWorkflow
                ? Workflow.NewGuid().ToString("N")
                : Guid.NewGuid().ToString("N"));

        DateTimeOffset effectiveTimestamp = timestamp
            ?? (Workflow.InWorkflow ? Workflow.UtcNow : DateTimeOffset.UtcNow);

        DateTimeOffset createdAt = messages.Count > 0
            ? messages.Min(m => m.CreatedAt) ?? effectiveTimestamp
            : effectiveTimestamp;

        return new DurableSessionRequest
        {
            CorrelationId = effectiveCorrelationId,
            RequestId = requestId ?? (Workflow.InWorkflow
                ? Workflow.NewGuid().ToString("N")
                : Guid.NewGuid().ToString("N")),
            InputFingerprint = inputFingerprint,
            CreatedAt = createdAt,
            Messages = messages.ToList(),
        };
    }

    /// <summary>
    /// Gets the logical request ID. It is generated deterministically when a workflow caller
    /// omits one, or may be supplied by an external caller before submission.
    /// </summary>
    [JsonInclude]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RequestId { get; internal set; }

    /// <summary>
    /// Gets the canonical fingerprint of effect-bearing serialized input for this request.
    /// </summary>
    [JsonInclude]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InputFingerprint { get; internal set; }
}
