namespace TemporalCommunity.Extensions.Agents.Workflows;

internal readonly record struct AgentModelContextBudget(int? MaximumTokens, int OutputTokenReserve)
{
    internal static AgentModelContextBudget Resolve(
        DurableAgentRegistration registration,
        TemporalAgentsOptions options)
    {
        var maximumTokens = registration.MaxModelContextTokens ?? options.DefaultMaxModelContextTokens;
        var outputTokenReserve = registration.ModelOutputTokenReserve ?? options.DefaultModelOutputTokenReserve;
        if (maximumTokens is <= 0
            || outputTokenReserve < 0
            || (maximumTokens is { } limit && outputTokenReserve >= limit))
        {
            throw new InvalidOperationException(
                $"The effective model-context token budget for agent '{registration.Name}' is invalid.");
        }

        return new(maximumTokens, outputTokenReserve);
    }
}
