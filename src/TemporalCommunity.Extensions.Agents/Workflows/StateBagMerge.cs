using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace TemporalCommunity.Extensions.Agents.Workflows;

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
/// <para>
/// <strong>Security — reserved approval-scope namespace deny-list.</strong> Approval-scope grants
/// live in StateBag under reserved keys (the literal session key, the prefix-reserved
/// <c>temporal.approval_scopes.*</c> namespace, and the agent's configured always-scopes store
/// key). Tool- and interceptor-supplied write-backs are <em>untrusted</em>: an unfiltered overlay
/// could let a later-index tool forge an approval grant (self-approval / privilege escalation) or
/// clobber a scope record the trusted workflow thread wrote earlier the same turn. Only the trusted
/// workflow-thread helpers (<c>WriteSessionScopeToStateBag</c> / <c>MergeAlwaysScopesIntoStateBag</c>)
/// may write those keys. The merge therefore drops any reserved key from each write-back
/// contribution <em>before</em> overlaying it (and emits a <c>LogWarning</c> as a tampering signal —
/// it is not a silent no-op). <c>current</c> is the trusted carried bag and is never filtered.
/// </para>
/// </remarks>
internal static class StateBagMerge
{
    /// <summary>
    /// Reserved StateBag key prefix for approval-scope records. Any write-back key under this
    /// prefix is dropped from tool/interceptor contributions before merge (covers the literal
    /// <c>temporal.approval_scopes.session</c> key and the default
    /// <c>temporal.approval_scopes.always</c> store key). Centralized so both the X-1 (tool) and
    /// X-2 (interceptor) merges use one deny-list. Used together with the agent's configured
    /// always-scopes store key (which may be a custom, non-prefixed key).
    /// </summary>
    internal const string ApprovalScopesReservedPrefix = "temporal.approval_scopes.";

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="key"/> is a reserved approval-scope key
    /// that untrusted tool/interceptor write-backs may never create, overwrite, or delete:
    /// any key under <see cref="ApprovalScopesReservedPrefix"/>, or the agent's configured
    /// <paramref name="alwaysScopesStoreKey"/> (which may not carry the reserved prefix).
    /// </summary>
    internal static bool IsReservedApprovalScopeKey(string key, string? alwaysScopesStoreKey) =>
        key.StartsWith(ApprovalScopesReservedPrefix, StringComparison.Ordinal)
        || (!string.IsNullOrEmpty(alwaysScopesStoreKey)
            && string.Equals(key, alwaysScopesStoreKey, StringComparison.Ordinal));

    /// <summary>
    /// Merges <paramref name="updatedBags"/> (in index order, later wins) into
    /// <paramref name="current"/>. Returns <paramref name="current"/> unchanged when no
    /// write-back carried mergeable content.
    /// </summary>
    /// <param name="current">The trusted carried StateBag. Never filtered.</param>
    /// <param name="updatedBags">
    /// Untrusted tool/interceptor write-backs, ordered by tool-call index (later index wins).
    /// </param>
    /// <param name="alwaysScopesStoreKey">
    /// The agent's configured always-scopes store key, or <see langword="null"/> when approval
    /// scopes are not configured. Reserved (along with the <see cref="ApprovalScopesReservedPrefix"/>
    /// namespace) — dropped from every contribution.
    /// </param>
    /// <param name="logger">Optional logger; a dropped reserved key is logged as a tampering signal.</param>
    internal static JsonElement? Merge(
        JsonElement? current,
        IReadOnlyList<JsonElement?> updatedBags,
        string? alwaysScopesStoreKey = null,
        ILogger? logger = null)
    {
        var merged = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        static void OverlayTrusted(Dictionary<string, JsonElement> into, JsonElement? bagElement)
        {
            if (bagElement is { ValueKind: JsonValueKind.Object } obj)
            {
                foreach (var prop in obj.EnumerateObject())
                {
                    into[prop.Name] = prop.Value.Clone();
                }
            }
        }

        // current is the trusted carried bag (written only by workflow-thread helpers) — never filtered.
        OverlayTrusted(merged, current);

        var changed = false;
        foreach (var updated in updatedBags)
        {
            if (updated is { ValueKind: JsonValueKind.Object } obj)
            {
                foreach (var prop in obj.EnumerateObject())
                {
                    // SECURITY: drop reserved approval-scope keys from untrusted write-backs
                    // BEFORE merge. A tool/interceptor may never forge or clobber an approval grant.
                    if (IsReservedApprovalScopeKey(prop.Name, alwaysScopesStoreKey))
                    {
                        logger?.LogWarning(
                            "StateBag write-back attempted to write reserved approval-scope key '{Key}'. " +
                            "Dropping the contribution (possible tampering / privilege-escalation signal). " +
                            "Only the workflow thread may write approval-scope records.",
                            prop.Name);
                        continue;
                    }

                    merged[prop.Name] = prop.Value.Clone();
                    changed = true;
                }
            }
        }

        if (!changed)
        {
            return current;
        }

        return SerializeSorted(merged);
    }

    /// <summary>
    /// Overlays the <em>trusted</em> <paramref name="updated"/> StateBag on top of the trusted
    /// carried <paramref name="current"/> bag (updated wins on a top-level key conflict), preserving
    /// every carried key <paramref name="updated"/> did not touch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unlike <see cref="Merge"/>, this overlay is <strong>unfiltered</strong> — it applies no
    /// reserved approval-scope deny-list. It is the correct policy for context-provider StateBag
    /// output returned by the LLM-step activity: context providers are developer-registered and
    /// trusted-tier (same trust as the workflow thread), so they may legitimately carry any key
    /// forward. Do <strong>not</strong> use this for tool/interceptor write-backs — those are
    /// untrusted and must go through <see cref="Merge"/>.
    /// </para>
    /// <para>
    /// This exists specifically so that a turn ending on a hash-gated LLM step (which returns a
    /// null or subset bag) does not <em>replace</em> — and thereby wipe — cross-turn StateBag state
    /// the workflow thread wrote between activities (e.g. approval-scope records from
    /// <c>WriteSessionScopeToStateBag</c>, or <c>temporal.working_set</c> from a context provider).
    /// </para>
    /// <para>
    /// Pure and deterministic — no I/O, no awaits, no wall-clock — safe on the workflow thread.
    /// Keys are emitted in ordinal-sorted order (matching <see cref="Merge"/>) so the resulting
    /// <see cref="JsonElement"/> is byte-stable across replay. This matters because the result feeds
    /// the FNV-1a content hash in <c>GetStateBagForDispatch</c> and is carried in workflow state.
    /// </para>
    /// </remarks>
    /// <param name="current">The trusted carried StateBag, or <see langword="null"/>.</param>
    /// <param name="updated">
    /// The trusted StateBag output from the LLM-step activity (context-provider mutations), or
    /// <see langword="null"/> when the activity produced no bag (e.g. hash-gated null dispatch).
    /// </param>
    /// <returns>
    /// The overlaid bag. Returns <paramref name="current"/> unchanged when <paramref name="updated"/>
    /// is null; returns <paramref name="updated"/> when <paramref name="current"/> is null.
    /// </returns>
    internal static JsonElement? OverlayTrustedStateBag(JsonElement? current, JsonElement? updated)
    {
        // Activity returned nothing (hash-gated / empty step) — keep the carried bag intact.
        if (updated is not { ValueKind: JsonValueKind.Object } updatedObj)
        {
            return current;
        }

        // Nothing carried yet — the activity's bag is the whole bag.
        if (current is not { ValueKind: JsonValueKind.Object } currentObj)
        {
            return updated;
        }

        var merged = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        // Start from the carried bag, then overlay the activity's keys (activity wins per-key).
        foreach (var prop in currentObj.EnumerateObject())
        {
            merged[prop.Name] = prop.Value.Clone();
        }
        foreach (var prop in updatedObj.EnumerateObject())
        {
            merged[prop.Name] = prop.Value.Clone();
        }

        return SerializeSorted(merged);
    }

    /// <summary>
    /// Restores application-, provider-, interceptor-, and tool-owned StateBag entries to their
    /// pre-turn values after a failed turn, while preserving approval-scope records committed by
    /// independent approval updates during that turn.
    /// </summary>
    /// <remarks>
    /// Both inputs are trusted workflow state. This operation is deterministic and replay-safe.
    /// It intentionally preserves only the reserved approval-scope namespace (including a custom
    /// always-scope store key) from <paramref name="afterFailure"/>; every other key comes from
    /// <paramref name="beforeTurn"/>.
    /// </remarks>
    internal static JsonElement? RestoreTurnOwnedState(
        JsonElement? beforeTurn,
        JsonElement? afterFailure,
        string? alwaysScopesStoreKey)
    {
        var restored = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        if (beforeTurn is { ValueKind: JsonValueKind.Object } beforeObj)
        {
            foreach (var prop in beforeObj.EnumerateObject())
            {
                restored[prop.Name] = prop.Value.Clone();
            }
        }

        if (afterFailure is { ValueKind: JsonValueKind.Object } failedObj)
        {
            foreach (var prop in failedObj.EnumerateObject())
            {
                if (IsReservedApprovalScopeKey(prop.Name, alwaysScopesStoreKey))
                {
                    restored[prop.Name] = prop.Value.Clone();
                }
            }
        }

        return restored.Count == 0 ? null : SerializeSorted(restored);
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
}
