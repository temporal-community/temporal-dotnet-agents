using Microsoft.Extensions.AI;

namespace TemporalCommunity.Extensions.Agents.Workflows;

/// <summary>
/// Applies the caller's per-run tool exposure policy at both the model and dispatch boundaries.
/// This type is intentionally pure because its dispatch decision is evaluated in workflow code.
/// </summary>
internal static class AgentRunToolSelectionPolicy
{
    internal const string BlockedResult = "[Blocked] This tool call is not enabled for this run.";

    internal static IReadOnlyList<AITool> FilterProviderTools(
        IReadOnlyList<AITool> registeredTools,
        bool enableToolCalls,
        IReadOnlyList<string>? enabledNames)
    {
        ArgumentNullException.ThrowIfNull(registeredTools);

        if (!enableToolCalls || enabledNames is { Count: 0 })
            return [];

        var filtered = new List<AITool>(registeredTools.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in registeredTools)
        {
            var name = tool.Name;
            if (string.IsNullOrWhiteSpace(name)
                || !seen.Add(name)
                || (enabledNames is not null
                    && !enabledNames.Contains(name, StringComparer.OrdinalIgnoreCase)))
            {
                continue;
            }

            filtered.Add(tool);
        }

        return filtered;
    }

    internal static bool IsCallEnabled(
        string? toolName,
        IReadOnlyCollection<string> registeredToolNames,
        bool enableToolCalls,
        IReadOnlyList<string>? enabledNames)
    {
        ArgumentNullException.ThrowIfNull(registeredToolNames);

        return enableToolCalls
            && !string.IsNullOrWhiteSpace(toolName)
            && registeredToolNames.Contains(toolName, StringComparer.OrdinalIgnoreCase)
            && (enabledNames is null
                || enabledNames.Contains(toolName, StringComparer.OrdinalIgnoreCase));
    }

    internal static string CreateBlockedResult(string? toolName) => BlockedResult;
}
