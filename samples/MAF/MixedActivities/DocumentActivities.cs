// DocumentActivities.cs — regular Temporal [Activity] methods for document data I/O.
//
// These are plain activities, not AI activities. They handle data operations:
// fetching documents from an in-memory store, persisting analysis results, and
// sending reviewer notifications. Temporal replays them from event history on
// worker restart — no LLM is involved.

using System.Collections.Concurrent;
using Temporalio.Activities;

namespace MixedActivities;

/// <summary>
/// Plain Temporal activity class that handles document data operations.
/// Registered via <c>.AddSingletonActivities&lt;DocumentActivities&gt;()</c> on the
/// worker builder — the same Temporal mechanism as any other activity class, just
/// without any AI involvement.
/// </summary>
public sealed class DocumentActivities
{
    // Seed the in-memory document store in the constructor so FetchDocumentAsync
    // has deterministic data to return for the three demo doc IDs.
    private readonly Dictionary<string, string> _documents = new(StringComparer.OrdinalIgnoreCase)
    {
        ["doc-001"] = "Customer reports login failures after password reset on mobile app.",
        ["doc-002"] = "Request to add dark mode to the dashboard.",
        ["doc-003"] = "Payment processing fails intermittently for EU customers.",
    };

    // Analysis results are stored here by StoreAnalysisAsync so the driver can
    // inspect them after all workflows complete.
    private readonly ConcurrentDictionary<string, AnalysisResult> _analyses = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the text of the document identified by <paramref name="docId"/>.
    /// Throws <see cref="KeyNotFoundException"/> for unknown IDs so the workflow
    /// surfaces a clear failure rather than silently analyzing an empty string.
    /// </summary>
    [Activity]
    public Task<string> FetchDocumentAsync(string docId)
    {
        if (_documents.TryGetValue(docId, out var text))
            return Task.FromResult(text);

        throw new KeyNotFoundException($"Document '{docId}' not found in the in-memory store.");
    }

    /// <summary>
    /// Persists the AI analysis result for <paramref name="docId"/> and returns a
    /// human-readable confirmation string that the workflow can include in its result.
    /// </summary>
    [Activity]
    public Task<string> StoreAnalysisAsync(string docId, string category, string summary)
    {
        _analyses[docId] = new AnalysisResult(docId, category, summary);
        return Task.FromResult($"Analysis stored for {docId}: category={category}");
    }

    /// <summary>
    /// Prints a console notification simulating an email or ticketing-system alert.
    /// In production this would call an SMTP relay, webhook, or internal API.
    /// </summary>
    [Activity]
    public Task NotifyReviewerAsync(string docId, string category)
    {
        Console.WriteLine($"  [Reviewer notification] doc={docId} category={category} — assigned for {category} review.");
        return Task.CompletedTask;
    }

    /// <summary>Returns a snapshot of all analyses stored so far (for driver inspection).</summary>
    public IReadOnlyDictionary<string, AnalysisResult> Analyses => _analyses;
}

/// <summary>Immutable analysis result returned by the AI agent and persisted by the activity.</summary>
public sealed record AnalysisResult(string DocId, string Category, string Summary);
