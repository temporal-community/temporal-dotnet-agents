// HARNESS for docs/how-to/MEAI/tool-functions.md § "Retry safety".
using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.AI;
using Temporalio.Extensions.Hosting;

namespace DocSnippets.ToolFunctions;

internal static class RetrySafety
{
    internal static void Register(
        ITemporalWorkerServiceOptionsBuilder worker,
        AIFunction readTool,
        AIFunction writeTool)
    {
        // BEGIN SNIPPET docs/how-to/MEAI/tool-functions.md#retry-safety
        worker.AddDurableTool(readTool, options => options.Idempotent().WithMaxAttempts(3));
        worker.AddDurableTool(writeTool, options => options.NoRetry());
        // END SNIPPET docs/how-to/MEAI/tool-functions.md#retry-safety
    }
}
