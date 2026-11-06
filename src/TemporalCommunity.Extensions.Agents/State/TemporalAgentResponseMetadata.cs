using System.Text.Json;
using Microsoft.Agents.AI;

namespace TemporalCommunity.Extensions.Agents.State;

internal static class TemporalAgentResponseMetadata
{
    internal const string StateBagKey = "temporal.agent.stateBag";
    internal const string StateBagRevisionKey = "temporal.agent.stateBagRevision";

    internal static void SetStateBag(AgentResponse response, JsonElement? stateBag, long revision)
    {
        response.AdditionalProperties ??= [];
        response.AdditionalProperties[StateBagKey] = stateBag ?? Workflows.StateBagMerge.EmptyStateBag;
        response.AdditionalProperties[StateBagRevisionKey] = revision;
    }

    internal static bool TryGetStateBag(
        AgentResponse response,
        out JsonElement? stateBag,
        out long revision)
    {
        stateBag = null;
        revision = 0;
        if (response.AdditionalProperties is not { } properties
            || !properties.TryGetValue(StateBagKey, out var stateValue)
            || !properties.TryGetValue(StateBagRevisionKey, out var revisionValue)
            || !TryReadRevision(revisionValue, out revision))
        {
            return false;
        }

        stateBag = stateValue switch
        {
            JsonElement element => element,
            JsonDocument document => document.RootElement.Clone(),
            _ => null,
        };
        return stateBag is { ValueKind: JsonValueKind.Object };
    }

    private static bool TryReadRevision(object? value, out long revision)
    {
        switch (value)
        {
            case long longValue:
                revision = longValue;
                return true;
            case int intValue:
                revision = intValue;
                return true;
            case JsonElement { ValueKind: JsonValueKind.Number } element:
                return element.TryGetInt64(out revision);
            default:
                revision = 0;
                return false;
        }
    }
}
