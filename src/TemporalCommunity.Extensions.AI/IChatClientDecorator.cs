using Microsoft.Extensions.AI;

namespace TemporalCommunity.Extensions.AI;

/// <summary>
/// Contract for a per-request decorator that wraps an <see cref="IChatClient"/> inside the
/// durable-chat activity dispatch path. Resolved by name via keyed DI when
/// <see cref="TemporalChatOptionsExtensions.WithChatClientFactoryKey(ChatOptions, string)"/>
/// is set on a per-call <see cref="ChatOptions"/>, or via
/// <see cref="DurableExecutionOptions.DefaultChatClientFactoryKey"/> as the worker-level fallback.
/// </summary>
/// <remarks>
/// <para>
/// <b>Use case.</b> Per-request decoration of the LLM call without registering a fresh
/// <see cref="IChatClient"/> instance per tenant / per correlation ID / per A-B variant.
/// Examples:
/// </para>
/// <list type="bullet">
///   <item><description>Per-tenant logging — wrap with a <see cref="DelegatingChatClient"/> that adds tenant tags</description></item>
///   <item><description>Per-request OTel correlation — attach <c>temporal.agent.correlation_id</c> to <c>Activity.Current</c></description></item>
///   <item><description>A-B response shadowing — for some fraction of requests, dual-dispatch to a comparison model</description></item>
///   <item><description>Request-scoped retry policies — wrap with a layer that catches/retries on specific upstream errors</description></item>
/// </list>
/// <para>
/// <b>Built-in implementation.</b> The library pre-registers a built-in <c>"tags"</c> decorator
/// (<c>TagsChatClientDecorator</c>) via <c>AddDurableAI</c> and <c>AddTemporalAgents</c>.
/// Combined with <see cref="TemporalChatOptionsExtensions.WithChatClientTag(ChatOptions, string, string)"/>,
/// it covers the 80% case (per-tenant tagging, correlation IDs) without users having to register
/// their own decorator.
/// </para>
/// <para>
/// <b>Lifecycle.</b> Decorators are registered as keyed DI singletons. The
/// <see cref="Decorate(IChatClient, ChatOptions?)"/> method is called once per activity invocation
/// to wrap the resolved <see cref="IChatClient"/>. The decorator should be cheap to invoke —
/// hot-path code — and should not perform DI resolution itself (any dependencies should be
/// injected via the decorator's constructor).
/// </para>
/// <para>
/// <b>Composition with other Temporal middleware.</b> The decorator runs INSIDE the durable-chat
/// activity, after <see cref="IChatClient"/> resolution and before
/// <c>GetStreamingResponseAsync</c>. Its per-call options include Temporal routing metadata so the
/// decorator can consume factory keys, tags, and custom values. The supplied <c>inner</c> client
/// is a provider boundary that removes Temporal-private keys while
/// preserving ordinary MEAI options and user-owned properties. Decorators must delegate provider
/// calls through that supplied inner client; replacing or bypassing it is unsupported. The
/// decorated chain is validated on first use, so decorators
/// MUST NOT insert <see cref="Microsoft.Extensions.AI.FunctionInvokingChatClient"/> when the
/// managed session has durable tools registered. That middleware would bypass the workflow-owned
/// tool activity loop.
/// </para>
/// </remarks>
public interface IChatClientDecorator
{
    /// <summary>
    /// Wraps <paramref name="inner"/> with this decorator's behavior and returns the resulting
    /// chain.
    /// </summary>
    /// <param name="inner">The resolved <see cref="IChatClient"/> to wrap. Never <see langword="null"/>.</param>
    /// <param name="options">
    /// The per-call <see cref="ChatOptions"/> for this invocation. May be <see langword="null"/>.
    /// Decorators that read per-call data (e.g., tag values via
    /// <see cref="TemporalChatOptionsExtensions.WithChatClientTag(ChatOptions, string, string)"/>)
    /// pull it from <see cref="ChatOptions.AdditionalProperties"/>.
    /// </param>
    /// <returns>
    /// A wrapper around the supplied <paramref name="inner"/>. MUST NOT return
    /// <see langword="null"/> or bypass/replace <paramref name="inner"/>.
    /// </returns>
    IChatClient Decorate(IChatClient inner, ChatOptions? options);
}
