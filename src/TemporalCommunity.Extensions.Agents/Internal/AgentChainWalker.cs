using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace TemporalCommunity.Extensions.Agents.Internal;

/// <summary>
/// Walks MAF agent and MEAI chat-client decorator chains for Agents-specific pipeline validation.
/// </summary>
/// <remarks>
/// This is deliberately local to the Agents package. MEAI must not reference Microsoft Agent
/// Framework merely to provide traversal for MAF's <see cref="AIAgent"/> pipeline.
/// </remarks>
internal static class AgentChainWalker
{
    private static readonly PropertyInfo? InnerClientProperty =
        typeof(DelegatingChatClient).GetProperty(
            "InnerClient",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

    private static readonly PropertyInfo? InnerAgentProperty =
        typeof(DelegatingAIAgent).GetProperty(
            "InnerAgent",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

    public static IEnumerable<IChatClient> WalkChatClient(IChatClient? root)
    {
        if (root is null)
        {
            yield break;
        }

        var visited = new HashSet<object>(ReferenceObjectComparer.Instance);
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

    public static IEnumerable<AIAgent> WalkAIAgent(AIAgent? root)
    {
        if (root is null)
        {
            yield break;
        }

        var visited = new HashSet<object>(ReferenceObjectComparer.Instance);
        var current = root;
        while (current is not null && visited.Add(current))
        {
            yield return current;

            if (current is DelegatingAIAgent delegating && InnerAgentProperty is not null)
            {
                current = InnerAgentProperty.GetValue(delegating) as AIAgent;
            }
            else
            {
                yield break;
            }
        }
    }

    public static bool Contains<T>(IChatClient? root)
        where T : class =>
        FindFirst<T>(root) is not null;

    public static bool Contains<T>(AIAgent? root)
        where T : class =>
        FindFirst<T>(root) is not null;

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
            return null;
        }
    }

    public static T? FindFirst<T>(AIAgent? root)
        where T : class
    {
        if (root is null)
        {
            return null;
        }

        foreach (var link in WalkAIAgent(root))
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
            return null;
        }
    }

    private sealed class ReferenceObjectComparer : IEqualityComparer<object>
    {
        public static ReferenceObjectComparer Instance { get; } = new();

        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);

        public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
