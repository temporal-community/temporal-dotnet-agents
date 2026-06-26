using System.Text.Json;

namespace Temporalio.Extensions.Agents.Workflows;

/// <summary>
/// Deterministic, replay-safe merge of StateBag write-backs returned by concurrently
/// fanned-out tool and interceptor activities (X-1 / X-2 merge policy).
/// </summary>
/// <remarks>
/// <para>
/// Tool and interceptor activities fan out concurrently, so their <em>completion</em> order is
/// non-deterministic and must never drive merge order (it would break workflow replay). Callers
/// therefore supply write-backs ordered by the original tool-call index (the
/// <c>FunctionCallContent</c> order within the turn). The merge applies them in that fixed index
/// order, so <strong>the later index wins</strong> on a top-level key conflict.
/// </para>
/// <para>
/// The merge is key-level over the flat JSON object produced by
/// <c>AgentSessionStateBag.Serialize()</c>. Keys are emitted in ordinal-sorted order so the
/// resulting <see cref="JsonElement"/> carried in workflow state is byte-stable across replay.
/// Pure computation — no I/O, no awaits, no wall-clock — safe to call on the workflow thread.
/// </para>
/// </remarks>
internal static class StateBagMerge
{
    /// <summary>
    /// Merges <paramref name="updatedBags"/> (in index order, later wins) into
    /// <paramref name="current"/>. Returns <paramref name="current"/> unchanged when no
    /// write-back carried content.
    /// </summary>
    internal static JsonElement? Merge(JsonElement? current, IReadOnlyList<JsonElement?> updatedBags)
    {
        var merged = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        static void Overlay(Dictionary<string, JsonElement> into, JsonElement? bagElement)
        {
            if (bagElement is { ValueKind: JsonValueKind.Object } obj)
            {
                foreach (var prop in obj.EnumerateObject())
                {
                    into[prop.Name] = prop.Value.Clone();
                }
            }
        }

        Overlay(merged, current);

        var changed = false;
        foreach (var updated in updatedBags)
        {
            if (updated is { ValueKind: JsonValueKind.Object })
            {
                Overlay(merged, updated);
                changed = true;
            }
        }

        if (!changed)
        {
            return current;
        }

        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var key in merged.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                writer.WritePropertyName(key);
                merged[key].WriteTo(writer);
            }
            writer.WriteEndObject();
        }

        return JsonSerializer.Deserialize<JsonElement>(stream.ToArray());
    }
}
