using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.AI.Exceptions;

namespace TemporalCommunity.Extensions.Agents.Internal;

/// <summary>
/// Rejects in-process function-invocation middleware inside a durable agent, at either of the two
/// layers a user can install it: the <c>ConfigureAgentPipeline</c> agent chain, or the
/// <see cref="IChatClient"/> returned from <c>agent.ChatClient</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>AddDurableAgent</c> always dispatches tool calls as separate <c>InvokeAgentTool</c>
/// activities — that is the contract, and <c>ChatClientAgent</c> is built with
/// <c>UseProvidedChatClientAsIs = true</c> to keep MAF from installing its own loop. A
/// <see cref="FunctionInvokingChatClient"/> anywhere in the supplied chat pipeline consumes the
/// tool call before the workflow sees one, so per-tool retry policies, <c>NoRetry()</c> on write
/// tools, per-tool timeouts, and Temporal event-history visibility all stop applying. The agent
/// keeps answering questions; it is simply no longer durable.
/// </para>
/// <para>
/// <strong>Rejection is unconditional — there is deliberately no "agent has no tools" exemption.</strong>
/// An earlier design gated on the registered tool count on the theory that an empty
/// <c>ChatOptions.Tools</c> makes the middleware inert. It does not:
/// <see cref="FunctionInvokingChatClient.AdditionalTools"/> is consulted when the inner client
/// requests a tool that was not sent on the request, so a tool-less durable registration can still
/// execute functions in-process. Any gate would have to reason about that mutable collection as
/// well, which is more to get wrong for no benefit. One rule instead: a durable agent may not
/// contain an inline function-invocation loop.
/// </para>
/// <para>
/// <strong>Detection is positive structural detection, not a closed door.</strong>
/// <see cref="AgentChainWalker"/> follows <c>DelegatingChatClient.InnerClient</c> and then falls
/// back to <see cref="IChatClient.GetService"/>. Because
/// <see cref="FunctionInvokingChatClient"/> derives from <c>DelegatingChatClient</c>, any chain
/// built by convention is found by one path or the other. A wrapper that neither derives from
/// <c>DelegatingChatClient</c> nor forwards <c>GetService</c> to its inner client remains
/// undetectable — this guard is a backstop for the documented rule, not a replacement for it.
/// </para>
/// <para>
/// The MEAI library deliberately takes the opposite position for its own Pattern 1: a
/// <c>DurableChatSession</c> with no durable tools registered may contain a
/// <see cref="FunctionInvokingChatClient"/>, because the caller has explicitly chosen the
/// in-process loop and no durability was promised. Do not "harmonize" the two — see
/// <c>DurableChatActivities</c>.
/// </para>
/// </remarks>
internal static class DurableFunctionInvocationGuard
{
    /// <summary>
    /// Throws when the chat client the agent's factory produced contains function-invocation
    /// middleware.
    /// </summary>
    /// <exception cref="DurableFunctionInvocationConflictException">
    /// A <see cref="FunctionInvokingChatClient"/> was found in <paramref name="chatClient"/>.
    /// </exception>
    internal static void ThrowIfChatClientInvokesFunctions(string agentName, IChatClient? chatClient)
    {
        if (!AgentChainWalker.Contains<FunctionInvokingChatClient>(chatClient))
        {
            return;
        }

        var offendingType = typeof(FunctionInvokingChatClient).FullName!;

        throw new DurableFunctionInvocationConflictException(
            BuildChatClientConflictMessage(agentName, offendingType))
        {
            OffendingType = offendingType,
        };
    }

    /// <summary>
    /// Remediation text for the chat-client layer. Kept separate from the agent-pipeline message
    /// because the two name different places to look: telling someone to edit a
    /// <c>ConfigureAgentPipeline</c> they never wrote sends them hunting through the wrong file.
    /// </summary>
    internal static string BuildChatClientConflictMessage(string agentName, string offendingType) =>
        $"Agent '{agentName}' has '{offendingType}' in the IChatClient returned from " +
        "agent.ChatClient. The durable agent library dispatches every tool call as a separate " +
        "Temporal activity (InvokeAgentTool); an in-process function-invocation loop consumes the " +
        "tool call first, silently disabling per-tool retry policies, NoRetry(), per-tool timeouts, " +
        "and event-history visibility. Remove the .UseFunctionInvocation() call from the chat " +
        "client your agent.ChatClient factory returns. If you need an in-process tool loop for " +
        "other, non-durable work, register a separate decorated IChatClient for it and give the " +
        "durable agent an undecorated one. See docs/how-to/MAF/llm-call-interception.md.";
}
