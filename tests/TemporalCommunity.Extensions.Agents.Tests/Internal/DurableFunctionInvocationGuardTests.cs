using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.Agents.Internal;
using TemporalCommunity.Extensions.AI.Exceptions;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.Tests.Internal;

/// <summary>
/// Guards the rule that a durable agent's chat client may not contain an in-process
/// function-invocation loop. Rejection is unconditional — see the AdditionalTools test for why a
/// tool-count gate is not a safe substitute.
/// </summary>
public class DurableFunctionInvocationGuardTests
{
    private sealed class StubChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "stub")));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    /// <summary>
    /// A wrapper that is not a <see cref="DelegatingChatClient"/> but forwards
    /// <see cref="IChatClient.GetService"/> — the shape the walker's fallback exists to catch.
    /// </summary>
    private sealed class ForwardingWrapper(IChatClient inner) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            inner.GetResponseAsync(messages, options, cancellationToken);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            inner.GetStreamingResponseAsync(messages, options, cancellationToken);

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            inner.GetService(serviceType, serviceKey);

        public void Dispose() { }
    }

    private sealed class PassThroughClient(IChatClient inner) : DelegatingChatClient(inner);

    [Fact]
    public void PlainClient_IsAllowed()
    {
        using var client = new StubChatClient();
        DurableFunctionInvocationGuard.ThrowIfChatClientInvokesFunctions("Assistant", client);
    }

    [Fact]
    public void NullClient_IsAllowed()
    {
        DurableFunctionInvocationGuard.ThrowIfChatClientInvokesFunctions("Assistant", null);
    }

    [Fact]
    public void DirectFunctionInvokingChatClient_IsRejected()
    {
        using var client = new ChatClientBuilder(new StubChatClient())
            .UseFunctionInvocation()
            .Build();

        var ex = Assert.Throws<DurableFunctionInvocationConflictException>(() =>
            DurableFunctionInvocationGuard.ThrowIfChatClientInvokesFunctions("Assistant", client));

        Assert.Contains("Assistant", ex.Message, StringComparison.Ordinal);
        Assert.Contains("agent.ChatClient", ex.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(FunctionInvokingChatClient), ex.OffendingType, StringComparison.Ordinal);

        // The chat-client message must not send the reader to ConfigureAgentPipeline, which they
        // may never have written.
        Assert.DoesNotContain("ConfigureAgentPipeline", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NestedFunctionInvokingChatClient_IsRejected()
    {
        // Buried under a conventional delegating decorator, as a user's own logging wrapper would be.
        using var inner = new ChatClientBuilder(new StubChatClient())
            .UseFunctionInvocation()
            .Build();
        using var outer = new PassThroughClient(inner);

        Assert.Throws<DurableFunctionInvocationConflictException>(() =>
            DurableFunctionInvocationGuard.ThrowIfChatClientInvokesFunctions("Assistant", outer));
    }

    [Fact]
    public void NonDelegatingWrapperThatForwardsGetService_IsRejected()
    {
        // Not a DelegatingChatClient, so the InnerClient walk cannot reach the middleware. The
        // GetService fallback is what catches this.
        using var inner = new ChatClientBuilder(new StubChatClient())
            .UseFunctionInvocation()
            .Build();
        using var outer = new ForwardingWrapper(inner);

        Assert.Throws<DurableFunctionInvocationConflictException>(() =>
            DurableFunctionInvocationGuard.ThrowIfChatClientInvokesFunctions("Assistant", outer));
    }

    /// <summary>
    /// REGRESSION GUARD — do not delete. An earlier design gated rejection on the agent having at
    /// least one registered tool, reasoning that an empty <c>ChatOptions.Tools</c> makes the
    /// middleware inert. It does not.
    /// </summary>
    /// <remarks>
    /// <see cref="FunctionInvokingChatClient.AdditionalTools"/> is consulted when the inner client
    /// requests a tool that was not sent on the request, so a tool-less durable registration can
    /// still execute functions in-process. This test exists so that reintroducing the tool-count
    /// gate fails the build rather than shipping a hole described as a safety boundary.
    /// </remarks>
    [Fact]
    public void FunctionInvokingChatClientCarryingAdditionalTools_IsRejected()
    {
        var additional = AIFunctionFactory.Create(
            () => "done", new AIFunctionFactoryOptions { Name = "not_on_the_request" });

        using var client = new ChatClientBuilder(new StubChatClient())
            .UseFunctionInvocation(configure: fic => fic.AdditionalTools = [additional])
            .Build();

        // No durable tools are registered anywhere in this scenario; the middleware can still run
        // `not_on_the_request` in-process, which is exactly the case a tool-count gate would miss.
        Assert.Throws<DurableFunctionInvocationConflictException>(() =>
            DurableFunctionInvocationGuard.ThrowIfChatClientInvokesFunctions("Assistant", client));
    }
}
