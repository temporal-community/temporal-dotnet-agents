using System.Reflection;
using Microsoft.Extensions.AI;

namespace TemporalCommunity.Extensions.AI.Internal;

/// <summary>
/// Internal utility for walking an <see cref="IChatClient"/> decorator chain in order to detect
/// or extract a specific link type.
/// </summary>
/// <remarks>
/// <para>
/// The MAF <c>AIAgent</c> traversal lives in the Agents package. Keeping that dependency out of
/// this package ensures a bare MEAI consumer does not acquire Microsoft Agent Framework.
/// </para>
/// <para>
/// <b>Dual traversal strategy.</b> Each walk performs two independent lookups, in this order:
/// </para>
/// <list type="number">
///   <item>
///     <description>
///       Inner-chain traversal: walk <see cref="DelegatingChatClient.InnerClient"/> recursively.
///       This property is <c>protected</c> in MEAI, so the walker accesses it via reflection.
///       The chain terminates when a non-delegating link is reached.
///     </description>
///   </item>
///   <item>
///     <description>
///       Idiomatic MEAI service lookup: call <c>GetService&lt;T&gt;()</c> on the root.
///       Some decorators wrap an opaque inner that is not reachable via the
///       <c>InnerClient</c> property (e.g. a third-party adapter that
///       captures the inner in a closure). The standard MEAI pattern is for such wrappers to
///       forward <c>GetService</c> requests through the chain; <see cref="DelegatingChatClient"/>
///       implements that forwarding by default.
///     </description>
///   </item>
/// </list>
/// <para>
/// <b>Side-effect freedom.</b> The walker performs no I/O, does not invoke
/// <c>GetResponseAsync</c> / <c>RunAsync</c>, and does not allocate workflow-visible state.
/// It is safe to call from any context — including workflow and activity boundaries — and
/// from validation / startup hooks that run before any user traffic.
/// </para>
/// <para>
/// <b>Cycle safety.</b> Both walks maintain a reference-equality <see cref="HashSet{T}"/> of
/// visited nodes; a cycle terminates the traversal silently rather than throwing. Pathological
/// cycles in user-supplied decorator chains should not crash worker startup or activity
/// execution.
/// </para>
/// <para>
/// <b>Idempotence.</b> The walker captures no state across calls; walking the same chain
/// twice produces identical results. Callers may invoke any of these methods from validation
/// (eagerly, at registration) and again from cold-path / first-use sites without worrying
/// about double-side-effects.
/// </para>
/// </remarks>
internal static class AgentChainWalker
{
    private static readonly PropertyInfo? InnerClientProperty =
        typeof(DelegatingChatClient).GetProperty(
            "InnerClient",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

    /// <summary>
    /// Walks the <see cref="IChatClient"/> decorator chain starting at <paramref name="root"/>,
    /// yielding each link in order from outermost to innermost. The traversal stops at the
    /// first non-<see cref="DelegatingChatClient"/> link (the chain "leaf"), which is also
    /// yielded. Cycles and <see langword="null"/> inputs are tolerated silently.
    /// </summary>
    /// <param name="root">The outermost chat client. May be <see langword="null"/>; in that
    /// case an empty sequence is returned.</param>
    public static IEnumerable<IChatClient> WalkChatClient(IChatClient? root)
    {
        if (root is null)
        {
            yield break;
        }

        var visited = new HashSet<object>(ReferenceComparer<object>.Instance);
        var current = root;
        while (current is not null && visited.Add(current))
        {
            yield return current;

            if (current is DelegatingChatClient delegating && InnerClientProperty is not null)
            {
                current = InnerClientProperty.GetValue(delegating) as IChatClient;
            }
            else
            {
                yield break;
            }
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> if any link in the <see cref="IChatClient"/> chain
    /// starting at <paramref name="root"/> is assignable to <typeparamref name="T"/>, OR if
    /// <c>root.GetService&lt;T&gt;()</c> returns a non-<see langword="null"/> instance.
    /// Subclass matches count.
    /// </summary>
    /// <typeparam name="T">Type to search for; matches subclasses.</typeparam>
    /// <param name="root">The outermost chat client. <see langword="null"/> returns
    /// <see langword="false"/>.</param>
    public static bool Contains<T>(IChatClient? root)
        where T : class
        => FindFirst<T>(root) is not null;

    /// <summary>
    /// Walks the <see cref="IChatClient"/> chain and returns the first link assignable to
    /// <typeparamref name="T"/>. If no chain link matches, falls back to
    /// <c>root.GetService&lt;T&gt;()</c>. Returns <see langword="null"/> if neither lookup
    /// succeeds.
    /// </summary>
    /// <typeparam name="T">Type to find; matches subclasses.</typeparam>
    /// <param name="root">The outermost chat client. <see langword="null"/> returns
    /// <see langword="null"/>.</param>
    public static T? FindFirst<T>(IChatClient? root)
        where T : class
    {
        if (root is null)
        {
            return null;
        }

        foreach (var link in WalkChatClient(root))
        {
            if (link is T match)
            {
                return match;
            }
        }

        try
        {
            return root.GetService(typeof(T)) as T;
        }
        catch
        {
            // GetService implementations on user types may misbehave; the walker must remain
            // side-effect free from the caller's perspective and never throw.
            return null;
        }
    }

}
