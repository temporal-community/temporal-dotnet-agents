using System.Diagnostics.CodeAnalysis;

namespace Temporalio.Extensions.AI.Exceptions;

/// <summary>
/// Thrown when compaction-marker handling encounters a structural problem — e.g. a marker
/// references source-entry correlation IDs that are no longer present in the store
/// (projection validation failure), or a marker is missing required fields after a
/// custom-implementor mistake bypassed the compile-time required-field guard.
/// </summary>
/// <remarks>
/// <para>
/// Step 5 of the maf-feature-gap-analysis (Cypher mitigation #3 — projection validation on
/// load surfaces tombstones loudly rather than silently producing a misleading reduced view).
/// Distinct from <see cref="DurableReplayCompatibilityException"/>: that one fires on
/// <c>$type</c> discriminator misses (mixed-fleet skew); this one fires when the marker
/// itself is structurally inconsistent with the store contents around it.
/// </para>
/// <para>
/// <see cref="MarkerCorrelationId"/> identifies the marker entry whose validation failed.
/// <see cref="MissingMessageIds"/> is the subset of
/// <see cref="CompactionMarkerEntry.CompactedMessageIds"/> the store could not resolve when
/// applying the projection — populated for the "marker references entries that were erased
/// out of band" sub-case, empty for other structural issues.
/// </para>
/// <para>
/// <c>[Experimental("TA002")]</c> while compaction is in active development; the wire
/// shape will become stable when the feature ships in a non-preview release.
/// </para>
/// </remarks>
[Experimental("TA002")]
public sealed class DurableCompactionMarkerException : DurableConfigurationException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DurableCompactionMarkerException"/>
    /// class with a structured payload describing the failed marker projection.
    /// </summary>
    /// <param name="markerCorrelationId">
    /// The <see cref="DurableSessionEntry.CorrelationId"/> of the marker entry that failed
    /// validation. Wire-format-safe — correlation IDs are GUIDs minted by the library, not
    /// user data.
    /// </param>
    /// <param name="message">
    /// Human-readable description of the validation failure. Should name the failure mode
    /// (e.g. "marker references entries no longer in store", "marker missing required
    /// strategy field") without quoting user data.
    /// </param>
    /// <param name="missingMessageIds">
    /// The subset of <see cref="CompactionMarkerEntry.CompactedMessageIds"/> the store
    /// could not resolve when applying the projection. May be empty when the failure mode
    /// is not "missing source entries". Never <see langword="null"/> — empty lists are the
    /// "not applicable" sentinel.
    /// </param>
    /// <param name="innerException">
    /// Optional originating exception (e.g. a <see cref="System.Text.Json.JsonException"/>
    /// for structural marker problems detected during deserialization).
    /// </param>
    public DurableCompactionMarkerException(
        string markerCorrelationId,
        string message,
        IReadOnlyList<string>? missingMessageIds = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        MarkerCorrelationId = markerCorrelationId;
        MissingMessageIds = missingMessageIds ?? Array.Empty<string>();
    }

    /// <summary>
    /// Gets the correlation ID of the marker entry whose validation failed.
    /// </summary>
    public string MarkerCorrelationId { get; init; }

    /// <summary>
    /// Gets the subset of marker-referenced source IDs the store could not resolve.
    /// Empty when the failure mode is not "missing source entries".
    /// </summary>
    public IReadOnlyList<string> MissingMessageIds { get; init; }
}
