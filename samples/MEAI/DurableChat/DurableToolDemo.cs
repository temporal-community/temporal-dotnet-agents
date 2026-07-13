using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.AI;

internal static class DurableToolDemo
{
    /// <summary>
    /// Runs a managed durable-tool session. Tool schemas and implementations both come from
    /// the worker's AddDurableTools registrations; callers do not pass ChatOptions.Tools.
    /// </summary>
    public static async Task<IEnumerable<string>> RunDurableToolDemoAsync(
        DurableChatSessionClient sessionClient)
    {
        Console.WriteLine("════════════════════════════════════════════════════════");
        Console.WriteLine(" Demo 4: Durable Tool Dispatch");
        Console.WriteLine("════════════════════════════════════════════════════════");
        Console.WriteLine(" Each registered tool call becomes its own Temporal activity.");
        Console.WriteLine(" Verify in Web UI at http://localhost:8233.\n");

        var conversationId = $"durable-tools-{Guid.NewGuid():N}";
        var question = "What is the weather like in Paris right now?";
        Console.WriteLine($" Conversation ID: {conversationId}");
        Console.WriteLine($" User : {question}");

        var response = await sessionClient.SendAsync(
            conversationId,
            [new ChatMessage(ChatRole.User, question)]);

        Console.WriteLine($" Agent: {response.Text}\n");
        Console.WriteLine(" Tool calls are separate");
        Console.WriteLine(" `TemporalCommunity.Extensions.AI.InvokeFunction` activities.");
        Console.WriteLine("════════════════════════════════════════════════════════\n");

        return [conversationId];
    }
}
