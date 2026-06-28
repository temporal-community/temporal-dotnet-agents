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
