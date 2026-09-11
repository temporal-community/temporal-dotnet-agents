// HARNESS for docs/how-to/MAF/usage.md § "Inheritance — per-agent vs worker-level".
//
// This section's retry-hierarchy claim lives in PROSE (a numbered list of inline code spans), not
// in a fenced block, which is why the marker below is SNIPPET-PROSE: the coverage checker holds it
// to the heading still existing, not to a fenced block existing.
//
// DOC DEFECT (as of this file's commit): item 1 of that list reads
//     agent.AddTool(t, opts => opts.DefaultRetryPolicy = ...)
// DurableToolOptions has no DefaultRetryPolicy — the per-tool property is RetryPolicy. The
// Default* prefix belongs to TemporalAgentsOptions, which the table four lines above uses
// CORRECTLY (`opts.DefaultRetryPolicy`). Identical token, different lambda binding: no regex can
// separate the two, which is the whole argument for compiling doc snippets.
using Microsoft.Extensions.AI;
using Temporalio.Common;
using TemporalCommunity.Extensions.Agents;

namespace DocSnippets.Usage;

internal static class InheritancePerAgentVsWorkerLevel
{
    internal static void Configure(DurableAgentBuilder agent, AIFunction t, RetryPolicy policy)
    {
        // BEGIN SNIPPET-PROSE docs/how-to/MAF/usage.md#inheritance--per-agent-vs-worker-level (line 1062)
        agent.AddTool(t, opts => opts.RetryPolicy = policy);
        // END SNIPPET-PROSE docs/how-to/MAF/usage.md#inheritance--per-agent-vs-worker-level
    }
}
