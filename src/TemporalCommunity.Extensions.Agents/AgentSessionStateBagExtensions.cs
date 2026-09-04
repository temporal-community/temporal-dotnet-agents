using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;

namespace TemporalCommunity.Extensions.Agents;

/// <summary>
/// Provides durable-serialization helpers for <see cref="AgentSessionStateBag"/>.
/// </summary>
public static class AgentSessionStateBagExtensions
{
    /// <summary>
    /// Gets the number of UTF-8 bytes the StateBag contributes to durable agent payloads.
    /// </summary>
    /// <remarks>
    /// An empty bag returns zero because durable agent workflows omit it rather than carrying an
    /// empty JSON object. The result uses the same JSON serialization and UTF-8 measurement as
    /// the continue-as-new StateBag size warning.
    /// </remarks>
    /// <param name="stateBag">The StateBag to measure.</param>
    /// <returns>The durable serialized StateBag size in UTF-8 bytes.</returns>
    public static int GetDurableSerializedUtf8ByteCount(this AgentSessionStateBag stateBag)
    {
        ArgumentNullException.ThrowIfNull(stateBag);

        return stateBag.Count == 0 ? 0 : GetDurableSerializedUtf8ByteCount(stateBag.Serialize());
    }

    internal static int GetDurableSerializedUtf8ByteCount(JsonElement serializedStateBag) =>
        Encoding.UTF8.GetByteCount(serializedStateBag.GetRawText());
}
