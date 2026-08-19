using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using TemporalCommunity.Extensions.Agents.Approvals;

namespace TemporalCommunity.Extensions.Agents.Workflows;

/// <summary>
/// Pure workflow-thread helpers for approval-scope bookkeeping.
/// All methods are free of I/O and <c>await</c> — safe to call on the Temporal workflow thread.
/// </summary>
/// <remarks>
/// Extracted from <see cref="AgentWorkflow"/> so that the logic is independently unit-testable
/// without spinning up a Temporal environment. Callers pass workflow fields as explicit
/// parameters rather than relying on captured state.
/// </remarks>
internal static class ApprovalScopeCoordinator
{
    // ── Budget guard ─────────────────────────────────────────────────────────

    /// <summary>
    /// Returns <see langword="true"/> when the given <paramref name="scopes"/> fit within
    /// both the record-count and byte-size budgets for the session-scope StateBag cache.
    /// </summary>
    /// <remarks>
    /// Deterministic: serializes using <see cref="TemporalAgentJsonUtilities.DefaultOptions"/>
    /// (no I/O). When either limit is exceeded a <c>LogWarning</c> is emitted and the method
    /// returns <see langword="false"/> — the caller skips the always-cache merge for this
    /// run but continues normally.
    /// </remarks>
    internal static bool IsWithinSessionScopeBudget(
        IReadOnlyList<ApprovalScopeRecord> scopes,
        int maxRecords,
        int maxBytes,
        string sessionId,
        ILogger logger)
    {
        if (scopes.Count > maxRecords)
        {
            logger.LogWarning(
                "[{SessionId}] Session approval grant rejected: {Count} records exceeds " +
                "MaxSessionScopeRecords ({Max}).",
                sessionId, scopes.Count, maxRecords);
            return false;
        }

        var json = System.Text.Json.JsonSerializer.Serialize(scopes, TemporalAgentJsonUtilities.DefaultOptions);
        var byteCount = System.Text.Encoding.UTF8.GetByteCount(json);
        if (byteCount > maxBytes)
        {
            logger.LogWarning(
                "[{SessionId}] Session approval grant rejected: serialized size {Bytes:N0} bytes " +
                "exceeds MaxSessionScopeBytes ({Max:N0}).",
                sessionId, byteCount, maxBytes);
            return false;
        }

        return true;
    }

    // ── Session-scope write ──────────────────────────────────────────────────

    /// <summary>
    /// Writes a session-scope <see cref="ApprovalScopeRecord"/> to the
    /// <c>temporal.approval_scopes.session</c> key in <paramref name="currentStateBag"/>.
    /// Returns the updated bag, or the original value on overflow (degraded to this-call-only).
    /// Pure workflow-thread computation — no I/O, no awaits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deduplicates by <c>(ToolName, Pattern)</c> — the latest <see cref="ApprovalScopeRecord.GrantedAt"/>
    /// wins. Bounds by <paramref name="maxAlwaysScopeCacheRecords"/> / <paramref name="maxAlwaysScopeCacheBytes"/>.
    /// On overflow the new grant is rejected and the method returns the unchanged bag.
    /// </para>
    /// </remarks>
    internal static JsonElement? WriteSessionScopeToStateBag(
        JsonElement? currentStateBag,
        string toolName,
        ApprovalScopePattern? pattern,
        bool matchAllArguments,
        string grantId,
        DateTimeOffset expiresAt,
        string? actor,
        string? reason,
        string originatingRequestId,
        DateTimeOffset grantedAt,
        int maxSessionScopeRecords,
        int maxSessionScopeBytes,
        string sessionId,
        ILogger logger)
    {
        const string sessionScopeKey = "temporal.approval_scopes.session";

        var bag = currentStateBag is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null } bagEl
            ? AgentSessionStateBag.Deserialize(bagEl)
            : new AgentSessionStateBag();

        bag.TryGetValue<List<ApprovalScopeRecord>>(
            sessionScopeKey,
            out var existing,
            TemporalAgentJsonUtilities.DefaultOptions);

        var records = existing ?? new List<ApprovalScopeRecord>();

        var newRecord = new ApprovalScopeRecord
        {
            GrantId = grantId,
            ToolName = toolName,
            Pattern = pattern,
            MatchAllArguments = matchAllArguments,
            GrantedAt = grantedAt,
            ExpiresAt = expiresAt,
            OriginatingRequestId = originatingRequestId,
            Actor = actor,
            Reason = reason,
        };

        // Dedup by (ToolName, Pattern): drop any prior record with the same identity so the
        // latest grant (this one, with the newest GrantedAt) wins. Preserves relative order of
        // surviving records, appending the new grant last.
        var newKey = BuildDedupKey(toolName, pattern);
        var deduped = new List<ApprovalScopeRecord>(records.Count + 1);
        foreach (var r in records)
        {
            if (!string.Equals(BuildDedupKey(r.ToolName, r.Pattern), newKey, StringComparison.Ordinal))
            {
                deduped.Add(r);
            }
        }
        deduped.Add(newRecord);

        // Bound the session cache. On overflow, reject the
        // new grant (degrade to this-call-only) and keep the pre-existing records untouched.
        if (!IsWithinSessionScopeBudget(
                deduped,
                maxSessionScopeRecords,
                maxSessionScopeBytes,
                sessionId,
                logger))
        {
            logger.LogWarning(
                "[{SessionId}] Session-scope grant for tool '{ToolName}' (RequestId: {RequestId}) " +
                "rejected: it would exceed MaxSessionScopeRecords/MaxSessionScopeBytes. " +
                "Degrading this approval to this-call-only; the tool still runs " +
                "but no reusable session record is persisted.",
                sessionId, toolName, originatingRequestId);
            return currentStateBag;
        }

        bag.SetValue<List<ApprovalScopeRecord>>(
            sessionScopeKey,
            deduped,
            TemporalAgentJsonUtilities.DefaultOptions);

        var updatedBag = bag.Serialize();

        logger.LogInformation(
            "[{SessionId}] Session-scope record written for tool '{ToolName}' " +
            "(RequestId: {RequestId}).",
            sessionId, toolName, originatingRequestId);

        return updatedBag;
    }

    internal static (JsonElement? StateBag, bool Removed) RevokeSessionScopeFromStateBag(
        JsonElement? currentStateBag,
        string grantId)
    {
        const string sessionScopeKey = "temporal.approval_scopes.session";
        if (currentStateBag is not { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null } bagElement)
        {
            return (currentStateBag, false);
        }

        var bag = AgentSessionStateBag.Deserialize(bagElement);
        bag.TryGetValue<List<ApprovalScopeRecord>>(
            sessionScopeKey,
            out var existing,
            TemporalAgentJsonUtilities.DefaultOptions);
        if (existing is null)
        {
            return (currentStateBag, false);
        }

        var retained = existing
            .Where(record => !string.Equals(record.GrantId, grantId, StringComparison.Ordinal))
            .ToList();
        if (retained.Count == existing.Count)
        {
            return (currentStateBag, false);
        }

        bag.SetValue(sessionScopeKey, retained, TemporalAgentJsonUtilities.DefaultOptions);
        return (bag.Serialize(), true);
    }

    // ── Scope normalization ──────────────────────────────────────────────────

    /// <summary>
    /// Pure, static normalization logic.
    /// Returns the effective <see cref="ApprovalScope"/> and an optional warning reason string
    /// when the scope is degraded to <see cref="ApprovalScope.ThisCallOnly"/>.
    /// </summary>
    /// <remarks>
    /// Does not log or access workflow context. The instance method
    /// <c>NormalizeApprovalScopeForPersistence</c> in <see cref="AgentWorkflow"/> delegates
    /// here and handles logging.
    /// </remarks>
    internal static (ApprovalScope Scope, string? DegradationReason) EvaluateScopeNormalization(
        DurableAgentApprovalDecision decision)
    {
        var scope = decision.Scope;

        // Undefined integer value (e.g. Scope = 99)
        if (!Enum.IsDefined(typeof(ApprovalScope), scope))
        {
            return (ApprovalScope.ThisCallOnly,
                $"Undefined ApprovalScope value {(int)scope}.");
        }

        if (scope == ApprovalScope.ThisCallOnly)
            return (ApprovalScope.ThisCallOnly, null);

        if (string.IsNullOrWhiteSpace(decision.GrantId) || decision.ExpiresAt is null)
        {
            return (ApprovalScope.ThisCallOnly,
                "Session scope requires a grant ID and expiry from the administrative API.");
        }

        if ((decision.ScopePattern is null) == !decision.MatchAllArguments)
        {
            return (ApprovalScope.ThisCallOnly,
                "Session scope requires exactly one of an argument pattern or explicit match-all intent.");
        }

        // Session scope: validate the explicit pattern when present.
        var pattern = decision.ScopePattern;
        if (pattern is null)
        {
            // Match-all intent was validated above.
            return (scope, null);
        }

        // Pattern string must not be null, empty, or whitespace.
        if (string.IsNullOrWhiteSpace(pattern.Pattern))
        {
            return (ApprovalScope.ThisCallOnly,
                $"ApprovalScope {scope} has an empty or whitespace-only Pattern.");
        }

        // Parameter must be null (wildcard) or non-whitespace.
        if (pattern.Parameter is not null && string.IsNullOrWhiteSpace(pattern.Parameter))
        {
            return (ApprovalScope.ThisCallOnly,
                $"ApprovalScope {scope} has a whitespace-only Parameter.");
        }

        // Defense-in-depth for non-standard deserializer paths; normal Temporal payloads reject
        // numeric PatternMatchType values at the converter boundary.
        if (!Enum.IsDefined(typeof(PatternMatchType), pattern.Type))
        {
            return (ApprovalScope.ThisCallOnly,
                $"ApprovalScope {scope} has an undefined PatternMatchType value {(int)pattern.Type}.");
        }

        // For Regex: pattern must also compile.
        if (pattern.Type == PatternMatchType.Regex)
        {
            try
            {
                _ = new Regex(pattern.Pattern, RegexOptions.None, TimeSpan.FromMilliseconds(100));
            }
            catch (ArgumentException)
            {
                return (ApprovalScope.ThisCallOnly,
                    $"ApprovalScope {scope} has an invalid Regex pattern '{pattern.Pattern}'.");
            }
        }

        return (scope, null);
    }

    // ── Dedup key ────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a stable, deterministic dedup key for a session-scope record from its tool name
    /// and optional argument pattern. A <see langword="null"/> pattern (match-any) collapses to
    /// a distinct sentinel so it does not collide with concrete patterns.
    /// </summary>
    internal static string BuildDedupKey(string toolName, ApprovalScopePattern? pattern)
    {
        if (pattern is null)
        {
            return toolName + " *";
        }

        return string.Concat(
            toolName, " ",
            ((int)pattern.Type).ToString(System.Globalization.CultureInfo.InvariantCulture), " ",
            pattern.Parameter ?? "*", " ",
            pattern.Pattern);
    }

}
