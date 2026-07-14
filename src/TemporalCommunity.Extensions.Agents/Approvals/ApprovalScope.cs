using System.Text.Json;
using System.Text.Json.Serialization;

namespace TemporalCommunity.Extensions.Agents.Approvals;

/// <summary>
/// Controls how far an approved agent-tool invocation carries forward.
/// </summary>
/// <remarks>
/// Serialized as an integer for compactness and replay safety. Do not add a
/// <see cref="JsonStringEnumConverter"/>: integer serialization is the durable agent
/// workflow contract.
/// </remarks>
[JsonConverter(typeof(ApprovalScopeJsonConverter))]
public enum ApprovalScope
{
    /// <summary>Approves this invocation only. No reusable scope record is written.</summary>
    ThisCallOnly = 0,

    /// <summary>
    /// Approves a matching tool invocation for the remainder of the current agent session.
    /// The record survives continue-as-new in the StateBag.
    /// </summary>
    Session = 1,

    /// <summary>
    /// Approves a matching tool invocation for future agent sessions through the configured
    /// approval-scope store.
    /// </summary>
    Always = 2,
}

/// <summary>Enforces integer-only serialization for <see cref="ApprovalScope"/>.</summary>
internal sealed class ApprovalScopeJsonConverter : JsonConverter<ApprovalScope>
{
    /// <inheritdoc/>
    public override ApprovalScope Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.Number)
        {
            throw new JsonException("ApprovalScope must be a JSON integer.");
        }

        return (ApprovalScope)reader.GetInt32();
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, ApprovalScope value, JsonSerializerOptions options) =>
        writer.WriteNumberValue((int)value);
}
