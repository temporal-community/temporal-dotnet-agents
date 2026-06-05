using System.Text.Json;
using Temporalio.Extensions.AI;
using Xunit;

namespace Temporalio.Extensions.AI.Tests;

/// <summary>
/// Task 8.1 — Unit tests: DTO construction, serialization round-trips.
/// Covers ApprovalScope, PatternMatchType, ApprovalScopePattern, and DurableApprovalDecision
/// with the new Scope / ScopePattern fields.
/// </summary>
public class ApprovalScopeSerializationTests
{
    // ── DurableApprovalDecision / ApprovalScope ─────────────────────────────

    /// <summary>
    /// DurableApprovalDecision serialized without Scope field → deserializes with Scope == ThisCallOnly.
    /// </summary>
    [Fact]
    public void DurableApprovalDecision_MissingScope_DeserializesAsThisCallOnly()
    {
        // JSON without the Scope field at all (legacy payload format).
        const string json = """{"RequestId":"req-001","Approved":true}""";
        var decision = JsonSerializer.Deserialize<DurableApprovalDecision>(json, DurableAIJsonUtilities.DefaultOptions);

        Assert.NotNull(decision);
        Assert.Equal(ApprovalScope.ThisCallOnly, decision.Scope);
        Assert.Null(decision.ScopePattern);
    }

    /// <summary>
    /// DurableApprovalDecision with explicit Scope = Session → round-trips correctly.
    /// </summary>
    [Fact]
    public void DurableApprovalDecision_ExplicitSessionScope_RoundTrips()
    {
        var decision = new DurableApprovalDecision
        {
            RequestId = "req-session-01",
            Approved = true,
            Scope = ApprovalScope.Session,
        };

        var json = JsonSerializer.Serialize(decision, DurableAIJsonUtilities.DefaultOptions);
        var restored = JsonSerializer.Deserialize<DurableApprovalDecision>(json, DurableAIJsonUtilities.DefaultOptions);

        Assert.NotNull(restored);
        Assert.Equal(ApprovalScope.Session, restored.Scope);
        Assert.Null(restored.ScopePattern);
    }

    /// <summary>
    /// DurableApprovalDecision with Scope = Always and ScopePattern round-trips correctly,
    /// with PatternMatchType serialized as a string in the JSON.
    /// </summary>
    [Fact]
    public void DurableApprovalDecision_ScopePatternGlob_RoundTrips()
    {
        var decision = new DurableApprovalDecision
        {
            RequestId = "req-glob-01",
            Approved = true,
            Scope = ApprovalScope.Always,
            ScopePattern = new ApprovalScopePattern
            {
                Type = PatternMatchType.Glob,
                Pattern = "/tmp/*",
                Parameter = "path",
            },
        };

        var json = JsonSerializer.Serialize(decision, DurableAIJsonUtilities.DefaultOptions);

        // Type must be serialized as string "Glob" not integer 1.
        Assert.Contains("\"Glob\"", json);
        Assert.DoesNotContain("\"Type\":1", json);

        var restored = JsonSerializer.Deserialize<DurableApprovalDecision>(json, DurableAIJsonUtilities.DefaultOptions);

        Assert.NotNull(restored);
        Assert.Equal(ApprovalScope.Always, restored.Scope);
        Assert.NotNull(restored.ScopePattern);
        Assert.Equal(PatternMatchType.Glob, restored.ScopePattern.Type);
        Assert.Equal("/tmp/*", restored.ScopePattern.Pattern);
        Assert.Equal("path", restored.ScopePattern.Parameter);
    }

    // ── PatternMatchType string enum serialization ──────────────────────────

    /// <summary>
    /// PatternMatchType serializes as "Exact", "Glob", "Regex" — not 0, 1, 2.
    /// </summary>
    [Theory]
    [InlineData(PatternMatchType.Exact, "\"Exact\"")]
    [InlineData(PatternMatchType.Glob, "\"Glob\"")]
    [InlineData(PatternMatchType.Regex, "\"Regex\"")]
    public void PatternMatchType_SerializesAsString(PatternMatchType type, string expectedJson)
    {
        var json = JsonSerializer.Serialize(type, DurableAIJsonUtilities.DefaultOptions);
        Assert.Equal(expectedJson, json);
    }

    /// <summary>
    /// Defined numeric PatternMatchType token (e.g. "Type": 0) throws JsonException.
    /// PatternMatchTypeJsonConverter is now registered with priority over the global
    /// JsonStringEnumConverter, so allowIntegerValues:false is correctly enforced.
    /// </summary>
    [Fact]
    public void PatternMatchType_DefinedNumericValue_ThrowsJsonException()
    {
        // Integer 0 is rejected — PatternMatchTypeJsonConverter (allowIntegerValues:false) wins.
        const string json = """{"Type":0,"Pattern":"/tmp/foo"}""";
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<ApprovalScopePattern>(json, DurableAIJsonUtilities.DefaultOptions));
    }

    /// <summary>
    /// Undefined numeric PatternMatchType token (e.g. "Type": 99) throws JsonException.
    /// PatternMatchTypeJsonConverter is now registered with priority over the global
    /// JsonStringEnumConverter, so allowIntegerValues:false is correctly enforced.
    /// </summary>
    [Fact]
    public void PatternMatchType_UndefinedNumericValue_ThrowsJsonException()
    {
        // Integer 99 is rejected — PatternMatchTypeJsonConverter (allowIntegerValues:false) wins.
        const string json = """{"Type":99,"Pattern":"/tmp/foo"}""";
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<ApprovalScopePattern>(json, DurableAIJsonUtilities.DefaultOptions));
    }

    /// <summary>
    /// Unknown PatternMatchType string (e.g. "Fuzzy") → JsonException thrown.
    /// The converter correctly rejects unknown string values.
    /// </summary>
    [Fact]
    public void PatternMatchType_UnknownStringValue_ThrowsJsonException()
    {
        const string json = """{"Type":"Fuzzy","Pattern":"/tmp/foo"}""";
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<ApprovalScopePattern>(json, DurableAIJsonUtilities.DefaultOptions));
    }

    // ── ApprovalScope integer serialization (enforced by ApprovalScopeJsonConverter) ──

    /// <summary>
    /// ApprovalScope serializes as an integer via ApprovalScopeJsonConverter, which overrides the
    /// global JsonStringEnumConverter present in AIJsonUtilities.DefaultOptions.
    /// </summary>
    [Theory]
    [InlineData(ApprovalScope.ThisCallOnly, "0")]
    [InlineData(ApprovalScope.Session, "1")]
    [InlineData(ApprovalScope.Always, "2")]
    public void ApprovalScope_SerializesAsInteger(ApprovalScope scope, string expectedJson)
    {
        var json = JsonSerializer.Serialize(scope, DurableAIJsonUtilities.DefaultOptions);
        Assert.Equal(expectedJson, json);
    }

    /// <summary>
    /// ApprovalScope wire contract is integer-only; string values must be rejected.
    /// </summary>
    [Fact]
    public void ApprovalScope_StringValueInJson_ThrowsJsonException()
    {
        // ApprovalScope wire contract is integer-only; string values must be rejected.
        const string json = """{"RequestId":"req-001","Approved":true,"Scope":"Session"}""";
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<DurableApprovalDecision>(json, DurableAIJsonUtilities.DefaultOptions));
    }

    // ── WhenWritingDefault / WhenWritingNull omission ───────────────────────

    /// <summary>
    /// Scope = ThisCallOnly (default value = 0) must be omitted from JSON via WhenWritingDefault.
    /// ScopePattern = null must be omitted from JSON via WhenWritingNull.
    /// </summary>
    [Fact]
    public void DurableApprovalDecision_DefaultScope_OmittedFromJson()
    {
        var decision = new DurableApprovalDecision
        {
            RequestId = "req-default",
            Approved = true,
        };

        var json = JsonSerializer.Serialize(decision, DurableAIJsonUtilities.DefaultOptions);

        Assert.DoesNotContain("\"Scope\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"ScopePattern\"", json, StringComparison.OrdinalIgnoreCase);

        var restored = JsonSerializer.Deserialize<DurableApprovalDecision>(json, DurableAIJsonUtilities.DefaultOptions);
        Assert.NotNull(restored);
        Assert.Equal(ApprovalScope.ThisCallOnly, restored.Scope);
        Assert.Null(restored.ScopePattern);
    }
}
