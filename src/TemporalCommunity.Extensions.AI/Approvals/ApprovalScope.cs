using System.Text.Json;
using System.Text.Json.Serialization;

namespace TemporalCommunity.Extensions.AI.Approvals;

/// <summary>
/// Controls how far an approval decision carries forward when a tool call is approved.
/// </summary>
/// <remarks>
/// Serialized as an integer for compactness and replay safety.
/// Do not add <c>JsonStringEnumConverter</c> — integer serialization is the wire contract.
/// <see cref="TemporalCommunity.Extensions.AI.DurableAIJsonUtilities.DefaultOptions"/> inserts
/// <see cref="ApprovalScopeJsonConverter"/> before the global <c>JsonStringEnumConverter</c>
/// inherited from <see cref="Microsoft.Extensions.AI.AIJsonUtilities.DefaultOptions"/>.
/// </remarks>
[JsonConverter(typeof(ApprovalScopeJsonConverter))]
public enum ApprovalScope
{
    /// <summary>
    /// Approve this specific invocation only (equivalent to today's per-invocation behavior).
    /// No scope record is written.
    /// </summary>
    ThisCallOnly = 0,

    /// <summary>
    /// Approve this tool/pattern for the remainder of the current session.
    /// Survives continue-as-new via StateBag. Expires when the session workflow terminates.
    /// </summary>
    Session = 1,

    /// <summary>
    /// Approve this tool/pattern for all future sessions.
    /// Stored in the agent's configured approval-scope store under a well-known key.
    /// For scope-aware tools, when approval-scope store mode is not enabled the scope degrades to
    /// <see cref="Session"/> with a warning logged when the decision is processed. For tools that
    /// are not scope-aware, reusable scopes are ignored and the decision behaves as
    /// <see cref="ThisCallOnly"/>.
    /// </summary>
    Always = 2,
}

/// <summary>
/// Custom JSON converter for <see cref="ApprovalScope"/> that enforces integer-only
/// serialization and rejects string enum values.
/// </summary>
/// <remarks>
/// <see cref="DurableAIJsonUtilities.DefaultOptions"/> inserts this converter before the global
/// <c>JsonStringEnumConverter</c> inherited from
/// <see cref="Microsoft.Extensions.AI.AIJsonUtilities.DefaultOptions"/>.
/// <see cref="ApprovalScope"/> is part of the durable wire contract; string values
/// (e.g. <c>"Session"</c>) must be rejected to prevent payload drift across worker versions.
/// </remarks>
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
    public override void Write(Utf8JsonWriter writer, ApprovalScope value, JsonSerializerOptions options)
    {
        writer.WriteNumberValue((int)value);
    }
}
