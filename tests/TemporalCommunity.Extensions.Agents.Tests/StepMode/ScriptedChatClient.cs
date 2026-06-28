// Keep in sync with tests/TemporalCommunity.Extensions.AI.IntegrationTests/Helpers/ScriptedChatClient.cs
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace TemporalCommunity.Extensions.Agents.Tests.StepMode;

/// <summary>
/// An <see cref="IChatClient"/> that returns a pre-defined sequence of
/// <see cref="ChatResponse"/> values. Used to drive the step-mode tool loop
/// deterministically without hitting a real LLM.
/// </summary>
/// <remarks>
/// <para>
/// Each call to <see cref="GetResponseAsync"/> dequeues the next scripted response.
/// The typical step-mode test scripts:
/// <list type="number">
///   <item>turn 1 — assistant response containing one or more <see cref="FunctionCallContent"/> items</item>
///   <item>turn 2 — assistant response with a final text answer (no tool calls)</item>
/// </list>
/// </para>
/// <para>
/// Streaming is implemented by chunking the scripted response back through
/// <see cref="ChatResponseExtensions.ToChatResponseUpdates(ChatResponse)"/>.
/// </para>
/// </remarks>
internal sealed class ScriptedChatClient : IChatClient
{
    private readonly Queue<ChatResponse> _scripted;
    private readonly List<CapturedCall> _calls = [];
    private readonly object _gate = new();

    public ScriptedChatClient(IEnumerable<ChatResponse> scriptedResponses)
    {
        ArgumentNullException.ThrowIfNull(scriptedResponses);
        _scripted = new Queue<ChatResponse>(scriptedResponses);
    }

    /// <summary>
    /// Append an additional response to the script after construction.
    /// Useful for tests that build the script across multiple steps.
    /// </summary>
    public void Enqueue(ChatResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        lock (_gate)
        {
            _scripted.Enqueue(response);
        }
    }

    /// <summary>Gets the captured calls in arrival order.</summary>
    public IReadOnlyList<CapturedCall> Calls
    {
        get
        {
            lock (_gate)
                return _calls.ToArray();
        }
    }

    /// <summary>Total chat-completion calls received (success and failure).</summary>
    public int CallCount
    {
        get
        {
            lock (_gate)
                return _calls.Count;
        }
    }

    public ChatClientMetadata Metadata { get; } = new("scripted-test");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var snapshot = messages.ToArray();
        ChatResponse response;
        lock (_gate)
        {
            if (_scripted.Count == 0)
            {
                throw new InvalidOperationException(
                    "ScriptedChatClient ran out of scripted responses; the test script is too short.");
            }
            response = _scripted.Dequeue();
            _calls.Add(new CapturedCall(snapshot, options, response));
        }

        return Task.FromResult(response);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        foreach (var update in response.ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }

    /// <summary>
    /// Convenience constructor for the canonical step-mode test pattern:
    /// turn 1 returns N tool calls, turn 2 returns a final text answer.
    /// </summary>
    public static ScriptedChatClient WithToolCallsThenFinal(
        IEnumerable<FunctionCallContent> toolCalls,
        string finalText)
    {
        var assistantWithToolCalls = new ChatMessage(ChatRole.Assistant, [.. toolCalls]);
        var assistantFinal = new ChatMessage(ChatRole.Assistant, finalText);

        return new ScriptedChatClient(
        [
            new ChatResponse(assistantWithToolCalls),
            new ChatResponse(assistantFinal),
        ]);
    }

    /// <summary>
    /// Builds a script with two assistant turns that each request a single tool call,
    /// followed by a final text answer. Useful for asserting error-counter resets and
    /// repeated tool dispatch.
    /// </summary>
    public static ScriptedChatClient WithRepeatingToolThenFinal(
        Func<int, FunctionCallContent> toolCallForTurn,
        int repeatCount,
        string finalText)
    {
        ArgumentNullException.ThrowIfNull(toolCallForTurn);
        if (repeatCount < 0) throw new ArgumentOutOfRangeException(nameof(repeatCount));

        var responses = new List<ChatResponse>(repeatCount + 1);
        for (var i = 0; i < repeatCount; i++)
        {
            responses.Add(new ChatResponse(new ChatMessage(ChatRole.Assistant, [toolCallForTurn(i)])));
        }
        responses.Add(new ChatResponse(new ChatMessage(ChatRole.Assistant, finalText)));
        return new ScriptedChatClient(responses);
    }

    /// <summary>Snapshot of a single LLM call captured during the test.</summary>
    internal sealed record CapturedCall(
        IReadOnlyList<ChatMessage> Messages,
        ChatOptions? Options,
        ChatResponse Response);
}
