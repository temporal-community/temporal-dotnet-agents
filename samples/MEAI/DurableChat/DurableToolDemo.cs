// DurableToolDemo — Pattern 3 demonstration.
//
// Shows durable tool dispatch via DurableChatSessionClient + AddDurableTools()
// (no UseFunctionInvocation() in the chat client chain). Each tool call becomes
// its own Temporal activity, visible in the Web UI and configurable per-tool.
//
// Two scenarios:
//   1. Explicit ChatOptions.Tools — caller controls the tool subset for this call.
//   2. Null ChatOptions.Tools — the workflow auto-populates from the
//      DurableFunctionRegistry, so every AddDurableTools()-registered tool is
//      available.

using Microsoft.Extensions.AI;
using Temporalio.Extensions.AI;

internal static class DurableToolDemo
{
    /// <summary>
    /// Runs two Pattern 3 scenarios against the supplied <see cref="DurableChatSessionClient"/>.
    /// </summary>
    /// <remarks>
    /// Prerequisites in <c>Program.cs</c>:
    /// <list type="bullet">
    ///   <item>The chat client pipeline does NOT call <c>.UseFunctionInvocation()</c>.</item>
    ///   <item><c>AddDurableTools(weatherTool, ...)</c> was called on the worker builder.</item>
    /// </list>
    /// </remarks>
    public static async Task RunDurableToolDemoAsync(
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
        // The workflow honors the explicit list as-is; it does NOT add other
        // tools from the registry. Use this when you want to scope which tools
        // are available for a specific call.
        await RunExplicitToolsScenarioAsync(sessionClient, weatherTool);

        // ── Scenario 2: ChatOptions.Tools is null → auto-populate ────────────
        // When the caller doesn't provide tools, the activity fills in every
        // tool currently in the DurableFunctionRegistry. This is the most
        // convenient mode for chat-style applications where you want the LLM
        // to be able to pick from anything you've registered.
        await RunAutoPopulatedToolsScenarioAsync(sessionClient);

        Console.WriteLine(" Tool calls visible in Temporal Web UI as separate");
        Console.WriteLine(" `Temporalio.Extensions.AI.InvokeFunction` activities.");
        Console.WriteLine("════════════════════════════════════════════════════════\n");
    }

    private static async Task RunExplicitToolsScenarioAsync(
        DurableChatSessionClient sessionClient,
        AIFunction weatherTool)
    {
        Console.WriteLine(" ── Scenario 1: explicit ChatOptions.Tools = [weatherTool] ──");

        var conversationId = $"durable-tools-explicit-{Guid.NewGuid():N}";
        Console.WriteLine($" Conversation ID: {conversationId}");

        var q = "What is the weather like in Tokyo right now?";
        Console.WriteLine($" User : {q}");

        // Explicit Tools list. The workflow respects it — no auto-population.
        var options = new ChatOptions { Tools = [weatherTool] };
        var response = await sessionClient.ChatAsync(
            conversationId,
            [new ChatMessage(ChatRole.User, q)],
            options: options);

        Console.WriteLine($" Agent: {response.Text}\n");
    }

    private static async Task RunAutoPopulatedToolsScenarioAsync(
        DurableChatSessionClient sessionClient)
    {
        Console.WriteLine(" ── Scenario 2: ChatOptions.Tools = null (auto-populated) ──");

        var conversationId = $"durable-tools-auto-{Guid.NewGuid():N}";
        Console.WriteLine($" Conversation ID: {conversationId}");

        var q = "Compare the weather in Paris and Berlin right now.";
        Console.WriteLine($" User : {q}");

        // No options at all. The activity will auto-populate ChatOptions.Tools
        // from the DurableFunctionRegistry. Since the LLM asks about two cities,
        // expect two parallel InvokeFunction activities (one per FunctionCallContent
        // in the assistant turn) — fanned out via Workflow.WhenAllAsync.
        var response = await sessionClient.ChatAsync(
            conversationId,
            [new ChatMessage(ChatRole.User, q)]);

        Console.WriteLine($" Agent: {response.Text}\n");
    }
}
