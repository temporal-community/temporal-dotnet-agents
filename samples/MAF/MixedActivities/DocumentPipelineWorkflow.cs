// DocumentPipelineWorkflow.cs — mixes plain [Activity] calls with a durable agent turn.
//
// Step 1: FetchDocumentAsync  — regular Temporal activity (data I/O, no AI)
// Step 2: InvokeAgentAsync    — scoped activity invoking the registered proxy
// Step 3: StoreAnalysisAsync  — regular Temporal activity (data I/O, no AI)
// Step 4: NotifyReviewerAsync — regular Temporal activity (side-effect, no AI)
//
// All four steps are durable. A worker crash at any point replays completed
// steps from event history without re-executing them.

using TemporalCommunity.Extensions.Agents;
using TemporalCommunity.Extensions.AI.Exceptions;
using Temporalio.Activities;
using Temporalio.Workflows;

namespace MixedActivities;

/// <summary>
/// Orchestrating workflow that processes a support document through a four-step
/// pipeline: fetch, AI analysis, store, notify.
/// <para>
/// The workflow orchestrates regular activities and an agent-invocation activity. That
/// activity resolves the registered proxy, while the workflow owns stable retry identities.
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

        // Step 2 — Invoke the registered agent through an activity. The session and request IDs
        // are deterministic and are reused if Temporal retries this activity.
        var sessionId = WorkflowAgents.NewAgentSessionId("DocumentAnalyst");
        var prompt = $"Analyze the following support document and reply with exactly two lines:\n" +
                     $"Category: <Bug|Feature|Billing|Other>\n" +
                     $"Summary: <one-sentence summary>\n\n" +
                     $"Document: {docText}";

        var analysisText = await Workflow.ExecuteActivityAsync(
            (AgentInvocationActivities activities) => activities.RunAsync(
                sessionId.WorkflowId, Workflow.NewGuid().ToString("N"), prompt),
            new ActivityOptions
            {
                StartToCloseTimeout = TimeSpan.FromMinutes(35),
                RetryPolicy = new()
                {
                    NonRetryableErrorTypes =
                    [
                        nameof(DurableTurnFailedException),
                        nameof(DurableTurnOutcomeTooLargeException),
                    ],
                },
            }).ConfigureAwait(true);

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
