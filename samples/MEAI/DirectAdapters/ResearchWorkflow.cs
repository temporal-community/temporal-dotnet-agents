// ResearchWorkflow — a fully custom [Workflow] with none of the session/history/HITL machinery
// that DurableChatSessionClient or DurableChatWorkflowBase<TOutput> provide: one durable tool
// call via AIFunction.AsDurable(), feeding a hand-written Activity that makes a single LLM call.
// See samples/MEAI/CustomWorkflow for the broader recommended pattern when you own a custom
// workflow and want durable LLM calls with an inline tool-invocation loop.

using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.AI;
using Temporalio.Workflows;

// ── Input ─────────────────────────────────────────────────────────────────────
internal record ResearchRequest(string City);

// ── Workflow ──────────────────────────────────────────────────────────────────
[Workflow]
internal sealed class ResearchWorkflow
{
    [WorkflowRun]
    public async Task<string> RunAsync(ResearchRequest request)
    {
        // Look up a fact with a durable tool call — dispatches to DurableFunctionActivities and
        // resolves "get_current_weather" from the worker's AddDurableTool/AddDurableTools registry.
        // AsDurable() always uses this workflow's own task queue, so its worker must be the same
        // one registering the tool. The lambda below is a stub — Workflow.InWorkflow == true
        // intercepts the call before it runs.
        var weatherTool = AIFunctionFactory.Create(
            (string city) => "[stub — not invoked in workflow context]",
            name: "get_current_weather",
            description: "Returns the current weather conditions for a given city.")
            .AsDurable();

        var weatherResult = await weatherTool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["city"] = request.City }));
        var weather = weatherResult?.ToString() ?? string.Empty;

        // Feed the tool result into a durable LLM call via a hand-written Activity —
        // ResearchActivities.SummarizeWeatherAsync is constructor-injected with the real,
        // worker-side IChatClient and dispatched as a standard Temporal activity.
        var activityOptions = new ActivityOptions { StartToCloseTimeout = TimeSpan.FromMinutes(2) };
        var summary = await Workflow.ExecuteActivityAsync(
            (ResearchActivities a) => a.SummarizeWeatherAsync(request.City, weather),
            activityOptions);

        return summary;
    }
}
