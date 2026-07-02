// DurableToolDemo — Pattern 3 demonstration.
//
// Shows durable tool dispatch via DurableChatSessionClient + AddDurableTools()
// (no UseFunctionInvocation() in the chat client chain). Each tool call becomes
// its own Temporal activity, visible in the Web UI and configurable per-tool.
//
// Two scenarios:
//   1. Explicit ChatOptions.Tools — caller controls the tool subset for this
//      call. With two tools registered (weather + time-of-day), passing only
//      the weather tool demonstrates that the workflow honors the subset and
//      does NOT auto-add the time-of-day tool from the registry.
//   2. Null ChatOptions.Tools — the workflow auto-populates from the
//      DurableFunctionRegistry, so every AddDurableTools()-registered tool is
//      available.

using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.AI;

internal static class DurableToolDemo
{
    /// <summary>
    /// Runs two Pattern 3 scenarios against the supplied <see cref="DurableChatSessionClient"/>.
    /// Returns the conversation IDs started so the caller can signal Shutdown to
    /// each workflow before exiting.
    /// </summary>
    /// <remarks>
    /// Prerequisites in <c>Program.cs</c>:
    /// <list type="bullet">
    ///   <item>The chat client pipeline does NOT call <c>.UseFunctionInvocation()</c>.</item>
    ///   <item><c>AddDurableTools(weatherTool, ...)</c> AND <c>AddDurableTools(timeOfDayTool, ...)</c>
    ///   were called on the worker builder so the registry contains more than one tool — that's
    ///   what makes Scenario 1's "explicit subset" observable.</item>
    /// </list>
    /// </remarks>
    public static async Task<IEnumerable<string>> RunDurableToolDemoAsync(
        DurableChatSessionClient sessionClient,
        AIFunction weatherTool)
    {
        Console.WriteLine("════════════════════════════════════════════════════════");
        Console.WriteLine(" Demo 4: Durable Tool Dispatch (Pattern 3)");
        Console.WriteLine("════════════════════════════════════════════════════════");
        Console.WriteLine(" Tools registered via AddDurableTools() — each tool call");
        Console.WriteLine(" becomes its own Temporal activity. Verify in Web UI at");
        Console.WriteLine(" http://localhost:8233.\n");

        // ── Scenario 1: caller passes ChatOptions.Tools explicitly ───────────
        // Two tools are registered in the worker (weather + time-of-day) but
        // this scenario passes ONLY weatherTool. The workflow honors the
        // explicit list as-is and does NOT add time-of-day from the registry.
        // Use this when you want to scope which tools are available for a
        // specific call.
        var explicitId = await RunExplicitToolsScenarioAsync(sessionClient, weatherTool);

        // ── Scenario 2: ChatOptions.Tools is null → auto-populate ────────────
        // When the caller doesn't provide tools, the activity fills in every
        // tool currently in the DurableFunctionRegistry (both weather AND
        // time-of-day). This is the most convenient mode for chat-style
        // applications where you want the LLM to be able to pick from
        // anything you've registered.
        var autoId = await RunAutoPopulatedToolsScenarioAsync(sessionClient);

        Console.WriteLine(" Tool calls visible in Temporal Web UI as separate");
        Console.WriteLine(" `TemporalCommunity.Extensions.AI.InvokeFunction` activities.");
        Console.WriteLine("════════════════════════════════════════════════════════\n");

        return [explicitId, autoId];
    }

    private static async Task<string> RunExplicitToolsScenarioAsync(
        DurableChatSessionClient sessionClient,
        AIFunction weatherTool)
    {
        Console.WriteLine(" ── Scenario 1: explicit ChatOptions.Tools = [weatherTool] ──");
        Console.WriteLine(" (registry has both weather and time-of-day; only weather is exposed)");

        var conversationId = $"durable-tools-explicit-{Guid.NewGuid():N}";
        Console.WriteLine($" Conversation ID: {conversationId}");

        // Ask a question that COULD plausibly invoke either tool. Because the
        // caller restricted the available set to [weatherTool], the LLM cannot
        // call get_time_of_day even though it's in the registry. This is the
        // observable difference from Scenario 2.
        var q = "What is the weather like in Tokyo right now, and what time is it there?";
        Console.WriteLine($" User : {q}");

        // Explicit Tools list. The workflow respects it — no auto-population.
        var options = new ChatOptions { Tools = [weatherTool] };
        var response = await sessionClient.SendAsync(
            conversationId,
            [new ChatMessage(ChatRole.User, q)],
            options: options);

        Console.WriteLine($" Agent: {response.Text}\n");
        return conversationId;
    }

    private static async Task<string> RunAutoPopulatedToolsScenarioAsync(
        DurableChatSessionClient sessionClient)
    {
        Console.WriteLine(" ── Scenario 2: ChatOptions.Tools = null (auto-populated) ──");

        var conversationId = $"durable-tools-auto-{Guid.NewGuid():N}";
        Console.WriteLine($" Conversation ID: {conversationId}");

        // Same flavor of question as Scenario 1, but with auto-population the
        // LLM has both tools available — expect get_current_weather AND
        // get_time_of_day to fire, each as its own InvokeFunction activity.
        var q = "What is the weather like in Paris right now, and what time is it there?";
        Console.WriteLine($" User : {q}");

        // No options at all. The activity will auto-populate ChatOptions.Tools
        // from the DurableFunctionRegistry. When the LLM emits multiple
        // FunctionCallContent in one assistant turn, they fan out in parallel
        // via Workflow.WhenAllAsync (verified at DurableChatWorkflow.cs:204-226).
        // Some models may emit them sequentially across turns instead.
        var response = await sessionClient.SendAsync(
            conversationId,
            [new ChatMessage(ChatRole.User, q)]);

        Console.WriteLine($" Agent: {response.Text}\n");
        return conversationId;
    }
}
