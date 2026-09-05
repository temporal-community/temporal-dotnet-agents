// DirectAdaptersDemo — starts ResearchWorkflow and prints the durable tool + durable LLM result.

using Temporalio.Client;

// ── Demo runner ───────────────────────────────────────────────────────────────
internal static class DirectAdaptersDemo
{
    public const string TaskQueue = "direct-adapters";

    public static async Task RunAsync(ITemporalClient client, string city)
    {
        Console.WriteLine("════════════════════════════════════════════════════════");
        Console.WriteLine(" Direct Workflow Adapters — Activity + AsDurable()");
        Console.WriteLine("════════════════════════════════════════════════════════");
        Console.WriteLine(" One durable tool call (AsDurable) feeds one durable LLM call");
        Console.WriteLine(" (a hand-written Activity) — no session, history, or HITL machinery.\n");

        var workflowId = $"research-{Guid.NewGuid():N}";
        Console.WriteLine($" Workflow ID: {workflowId}");
        Console.WriteLine($" City      : {city}\n");

        var handle = await client.StartWorkflowAsync(
            (ResearchWorkflow wf) => wf.RunAsync(new ResearchRequest(city)),
            new WorkflowOptions(workflowId, TaskQueue));

        var result = await handle.GetResultAsync();

        Console.WriteLine($" Summary: {result}");
        Console.WriteLine("════════════════════════════════════════════════════════\n");
    }
}
