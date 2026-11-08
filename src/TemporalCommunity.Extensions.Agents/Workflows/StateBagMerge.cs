using System.Text.Json;

namespace TemporalCommunity.Extensions.Agents.Workflows;

internal sealed record StateBagDelta(
    JsonElement? Upserts,
    IReadOnlyList<string>? RemovedKeys);

/// <summary>
/// Deterministic, key-level StateBag merge. Callers order tool and interceptor contributions
/// by original tool-call index, not activity completion order; later indices win.
/// Keys are serialized in ordinal order to keep workflow state byte-stable across replay.
/// </summary>
internal static class StateBagMerge
{
    internal static JsonElement EmptyStateBag { get; } =
        JsonSerializer.SerializeToElement(new Dictionary<string, object?>());

    internal static StateBagDelta? CreateDelta(JsonElement? before, JsonElement? after)
    {
        var oldValues = GetProperties(before);
        var newValues = GetProperties(after);
        var upserts = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var removed = new List<string>();

        foreach (var (key, value) in newValues)
        {
            if (!oldValues.TryGetValue(key, out var previous)
                || !string.Equals(previous.GetRawText(), value.GetRawText(), StringComparison.Ordinal))
            {
                upserts[key] = value;
            }
        }

        foreach (var key in oldValues.Keys)
        {
            if (!newValues.ContainsKey(key))
            {
                removed.Add(key);
            }
        }

        if (upserts.Count == 0 && removed.Count == 0)
        {
            return null;
        }

        return new StateBagDelta(
            upserts.Count == 0 ? null : SerializeSorted(upserts),
            removed.Count == 0 ? null : removed.OrderBy(key => key, StringComparer.Ordinal).ToArray());
    }

    internal static JsonElement? ApplyDelta(JsonElement? current, StateBagDelta? delta)
    {
        if (delta is null)
        {
            return current;
        }

        var merged = GetProperties(current);
        var changed = false;

        if (delta.RemovedKeys is { Count: > 0 } removedKeys)
        {
            foreach (var key in removedKeys)
            {
                changed |= merged.Remove(key);
            }
        }

        if (delta.Upserts is { ValueKind: JsonValueKind.Object } upserts)
        {
            foreach (var property in upserts.EnumerateObject())
            {
                merged[property.Name] = property.Value.Clone();
                changed = true;
            }
        }

        return changed ? SerializeSorted(merged) : current;
    }

    internal static JsonElement? ApplyTrustedDelta(JsonElement? current, StateBagDelta? delta) =>
        ApplyDelta(current, delta);

    /// <summary>Overlays contributions in index order, preserving keys they did not touch.</summary>
    internal static JsonElement? Merge(JsonElement? current, IReadOnlyList<JsonElement?> updatedBags)
    {
        var merged = GetProperties(current);
        var changed = false;
        foreach (var updated in updatedBags)
        {
            if (updated is { ValueKind: JsonValueKind.Object } obj)
            {
                foreach (var property in obj.EnumerateObject())
                {
                    merged[property.Name] = property.Value.Clone();
                    changed = true;
                }
            }
        }

        return changed ? SerializeSorted(merged) : current;
    }

    /// <summary>
    /// Overlays activity output without replacing carried keys omitted by a hash-gated step.
    /// Use key-level deltas when removals must propagate.
    /// </summary>
    internal static JsonElement? OverlayTrustedStateBag(JsonElement? current, JsonElement? updated)
    {
        if (updated is not { ValueKind: JsonValueKind.Object } updatedObj)
        {
            return current;
        }

        if (current is not { ValueKind: JsonValueKind.Object })
        {
            return updated;
        }

        var merged = GetProperties(current);
        foreach (var property in updatedObj.EnumerateObject())
        {
            merged[property.Name] = property.Value.Clone();
        }

        return SerializeSorted(merged);
    }

    private static JsonElement SerializeSorted(Dictionary<string, JsonElement> properties)
    {
        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var key in properties.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                writer.WritePropertyName(key);
                properties[key].WriteTo(writer);
            }
            writer.WriteEndObject();
        }

        return JsonSerializer.Deserialize<JsonElement>(stream.ToArray());
    }

    private static Dictionary<string, JsonElement> GetProperties(JsonElement? bag)
    {
        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (bag is { ValueKind: JsonValueKind.Object } objectBag)
        {
            foreach (var property in objectBag.EnumerateObject())
            {
                properties[property.Name] = property.Value.Clone();
            }
        }

        return properties;
    }
}
