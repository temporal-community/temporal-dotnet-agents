// DocumentPipelineWorkflow.cs — mixes plain [Activity] calls with a durable agent turn.
//
// Step 1: FetchDocumentAsync  — regular Temporal activity (data I/O, no AI)
// Step 2: agent.RunAsync      — durable AI agent turn (LLM reasoning)
// Step 3: StoreAnalysisAsync  — regular Temporal activity (data I/O, no AI)
// Step 4: NotifyReviewerAsync — regular Temporal activity (side-effect, no AI)
//
// All four steps are durable. A worker crash at any point replays completed
// steps from event history without re-executing them.

using Microsoft.Extensions.AI;
using Temporalio.Activities;
using Temporalio.Workflows;
using static TemporalCommunity.Extensions.Agents.TemporalWorkflowExtensions;

namespace MixedActivities;

/// <summary>
/// Orchestrating workflow that processes a support document through a four-step
/// pipeline: fetch, AI analysis, store, notify.
/// <para>
/// The key pattern: regular Temporal activities (<see cref="DocumentActivities"/>)
/// and a durable AI agent turn (<c>GetTemporalAgent("DocumentAnalyst").RunAsync</c>) coexist
/// in the same workflow. The Temporal runtime treats both as durable, replay-safe
/// operations — the author does not need to coordinate them specially.
/// </para>
/// </summary>
[Workflow("MixedActivities.DocumentPipelineWorkflow")]
public sealed class DocumentPipelineWorkflow
{
    private static readonly ActivityOptions DefaultActivityOptions = new()
    {
        StartToCloseTimeout = TimeSpan.FromSeconds(30),
    };

    /// <summary>
    /// Runs the four-step document analysis pipeline for <paramref name="docId"/>.
    /// Returns the raw analysis text produced by the AI agent.
    /// </summary>
    [WorkflowRun]
    public async Task<string> RunAsync(string docId)
    {
        // Step 1 — Regular activity: fetch the document text from the in-memory store.
        // This is an ordinary Temporal activity with no AI involvement.
        var docText = await Workflow.ExecuteActivityAsync(
            (DocumentActivities a) => a.FetchDocumentAsync(docId),
            DefaultActivityOptions).ConfigureAwait(true);

        // Step 2 — Durable agent turn: analyze the document.
        // GetTemporalAgent resolves "DocumentAnalyst" from the worker's registered agents.
        // RunAsync dispatches a RunDurableAgentStep activity internally — the LLM call
        // is durable and replay-cached just like the plain activities above and below.
        var agent = GetTemporalAgent("DocumentAnalyst");
        var session = await agent.CreateSessionAsync().ConfigureAwait(true);

        var prompt = $"Analyze the following support document and reply with exactly two lines:\n" +
                     $"Category: <Bug|Feature|Billing|Other>\n" +
                     $"Summary: <one-sentence summary>\n\n" +
                     $"Document: {docText}";

        var response = await agent.RunAsync(
            [new ChatMessage(ChatRole.User, prompt)],
            session).ConfigureAwait(true);

        var analysisText = response.Text ?? string.Empty;

        // Parse the category from the agent response for use in subsequent steps.
        var category = ParseCategory(analysisText);

        // Step 3 — Regular activity: persist the analysis result.
        await Workflow.ExecuteActivityAsync(
            (DocumentActivities a) => a.StoreAnalysisAsync(docId, category, analysisText),
            DefaultActivityOptions).ConfigureAwait(true);

        // Step 4 — Regular activity: notify the reviewer.
        await Workflow.ExecuteActivityAsync(
            (DocumentActivities a) => a.NotifyReviewerAsync(docId, category),
            DefaultActivityOptions).ConfigureAwait(true);

        return analysisText;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Scans <paramref name="text"/> for a "Category: X" line and returns the matched
    /// value. Falls back to <c>"Unknown"</c> if the agent response is unparseable.
    /// </summary>
    private static string ParseCategory(string text)
    {
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith("Category:", StringComparison.OrdinalIgnoreCase))
                continue;

            var value = line["Category:".Length..].Trim();
            if (value.Equals("Bug", StringComparison.OrdinalIgnoreCase))      return "Bug";
            if (value.Equals("Feature", StringComparison.OrdinalIgnoreCase))  return "Feature";
            if (value.Equals("Billing", StringComparison.OrdinalIgnoreCase))  return "Billing";
            if (value.Equals("Other", StringComparison.OrdinalIgnoreCase))    return "Other";

            // Agent may include punctuation or a trailing period — return as-is trimmed.
            return value.TrimEnd('.', ',', ';');
        }

        return "Unknown";
    }
}
