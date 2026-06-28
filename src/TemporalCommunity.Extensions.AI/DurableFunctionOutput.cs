namespace TemporalCommunity.Extensions.AI;

/// <summary>
/// Serializable output from the durable function invocation activity.
/// </summary>
internal sealed class DurableFunctionOutput
{
    /// <summary>
    /// The result returned by the <see cref="Microsoft.Extensions.AI.AIFunction"/>.
    /// </summary>
    /// <remarks>
    /// <strong>Boundary type (S-X-6, accepted limitation).</strong> Because this is declared
    /// <c>object?</c>, the value crosses the activity→workflow boundary as a
    /// <see cref="System.Text.Json.JsonElement"/> after deserialization — the original domain CLR
    /// type is <em>not</em> rehydrated. The workflow embeds this <see cref="System.Text.Json.JsonElement"/>
    /// directly into <see cref="Microsoft.Extensions.AI.FunctionResultContent.Result"/>
    /// (see <c>DurableChatWorkflow</c> FunctionResultContent construction). Downstream consumers
    /// reading tool results from history therefore observe a <see cref="System.Text.Json.JsonElement"/>,
    /// not the tool's return type. This is intentional: rehydrating domain types would require
    /// carrying type metadata across the boundary and would break replay of histories serialized
    /// before such a change. Consumers that need a typed value should deserialize the
    /// <see cref="System.Text.Json.JsonElement"/> explicitly.
    /// </remarks>
    public object? Result { get; init; }
}
