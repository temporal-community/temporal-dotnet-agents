using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Temporalio.Extensions.Agents.State;

namespace Temporalio.Extensions.Agents;

/// <summary>
/// Default <see cref="IAgentRouter"/> implementation that calls an AI model-backed
/// <see cref="AIAgent"/> to classify which registered agent should handle a request.
/// </summary>
/// <remarks>
/// The router agent receives a compact prompt listing agent names + descriptions and
/// is instructed to respond with the single best-matching agent name. The response is
/// parsed with a fuzzy match fallback to tolerate minor formatting variation.
/// <para>
/// Register a router agent via <see cref="TemporalAgentsOptions.SetRouterAgent"/>; the
/// <see cref="AIAgentRouter"/> is registered automatically when a router agent is present.
/// </para>
/// </remarks>
public sealed class AIAgentRouter(AIAgent routerAgent, ILogger<AIAgentRouter>? logger = null) : IAgentRouter
{
    /// <inheritdoc/>
    public async Task<string> RouteAsync(
        IList<ChatMessage> messages,
        IEnumerable<AgentDescriptor> agents,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(agents);

        var descriptors = agents.ToList();
        if (descriptors.Count == 0)
        {
            throw new InvalidOperationException(
                "No agent descriptors are registered. Call AddAgentDescriptor() on TemporalAgentsOptions for each routable agent.");
        }

        var agentList = string.Join("\n", descriptors.Select(a => $"- {a.Name}: {a.Description}"));
        var lastUserMessage = messages
            .LastOrDefault(m => m.Role == ChatRole.User)?.Text
            ?? messages.LastOrDefault()?.Text
            ?? string.Empty;

        var routingMessages = new List<ChatMessage>
        {
            new(ChatRole.User,
                $"Available agents:\n{agentList}\n\n" +
                $"User message: {lastUserMessage}\n\n" +
                "Respond with ONLY the agent name, nothing else.")
        };

        var session = await routerAgent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        var response = await routerAgent
            .RunAsync(routingMessages, session, null, cancellationToken)
            .ConfigureAwait(false);

        var responseText = response.Text?.Trim() ?? string.Empty;

        // Guard: if the LLM returned empty/whitespace (e.g. tool-only response), fail fast.
        if (string.IsNullOrWhiteSpace(responseText))
        {
            throw new InvalidOperationException(
                "Router agent returned an empty response. Ensure the router model is configured " +
                "to reply with a plain agent name (no tool calls).");
        }

        var validNames = descriptors
            .Select(a => a.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Exact match (most likely case when the model follows instructions).
        if (validNames.Contains(responseText))
        {
            return responseText;
        }

        // Fuzzy fallback: find all valid names contained in the response text.
        // If multiple names match (e.g. "WeatherAgent and BillingAgent could help"),
        // this is ambiguous — reject rather than silently picking one at random.
        var matches = descriptors
            .Where(d => responseText.Contains(d.Name, StringComparison.OrdinalIgnoreCase))
            .Select(d => d.Name)
            .ToList();

        if (matches.Count == 1)
        {
            logger?.LogWarning(
                "Router fuzzy-matched '{ResponseText}' to '{AgentName}'. " +
                "Consider tuning the router prompt to return exact agent names.",
                responseText, matches[0]);
            return matches[0];
        }

        if (matches.Count > 1)
        {
            throw new InvalidOperationException(
                $"Router agent response '{responseText}' is ambiguous — " +
                $"it contains multiple agent names: {string.Join(", ", matches)}. " +
                "Ensure the router returns exactly one agent name.");
        }

        throw new InvalidOperationException(
            $"Router agent returned an unrecognized agent name: '{responseText}'. " +
            $"Valid names: {string.Join(", ", validNames)}");
    }
}
