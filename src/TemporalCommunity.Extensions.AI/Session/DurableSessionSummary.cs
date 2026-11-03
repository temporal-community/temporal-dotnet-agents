using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace TemporalCommunity.Extensions.AI.Session;

/// <summary>
/// Bounded result contract for a library-owned history summarization operation.
/// </summary>
public sealed class DurableSessionSummary
{
    /// <summary>The summary text used to represent the replaced history range.</summary>
    public required string Text { get; init; }

    /// <summary>The model that produced the summary, when model-backed summarization was used.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ModelId { get; init; }

    /// <summary>Usage reported by the summarization model, when available.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UsageDetails? Usage { get; init; }
}
