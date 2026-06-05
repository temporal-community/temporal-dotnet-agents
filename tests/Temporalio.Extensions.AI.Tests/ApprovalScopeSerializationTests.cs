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
    /// Defined numeric PatternMatchType token (e.g. "Type": 0) deserializes to the corresponding
    /// enum member (Exact) without throwing. .NET 10 source-gen does NOT enforce allowIntegerValues:false
    /// for integer tokens — this is documented behavior.
    /// </summary>
    [Fact]
    public void PatternMatchType_DefinedNumericValue_DeserializesAsEnumMember()
    {
        // Integer 0 → Exact. Per .NET 10 source-gen limitation, this does not throw.
        const string json = """{"Type":0,"Pattern":"/tmp/foo"}""";
        var result = JsonSerializer.Deserialize<ApprovalScopePattern>(json, DurableAIJsonUtilities.DefaultOptions);

        Assert.NotNull(result);
        Assert.Equal(PatternMatchType.Exact, result.Type);
    }

    /// <summary>
    /// Undefined numeric PatternMatchType token (e.g. "Type": 99) does NOT throw at the converter
    /// boundary — it deserializes as (PatternMatchType)99 and is caught later by
    /// NormalizeApprovalScopeForPersistence via Enum.IsDefined.
    /// </summary>
    [Fact]
    public void PatternMatchType_UndefinedNumericValue_PassesThroughWithoutThrow()
    {
        const string json = """{"Type":99,"Pattern":"/tmp/foo"}""";
        var result = JsonSerializer.Deserialize<ApprovalScopePattern>(json, DurableAIJsonUtilities.DefaultOptions);

        Assert.NotNull(result);
        Assert.Equal((PatternMatchType)99, result.Type);
        // Enum.IsDefined must return false for this value.
        Assert.False(Enum.IsDefined(typeof(PatternMatchType), result.Type));
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

    // ── ApprovalScope string serialization (via AIJsonUtilities global converter) ────

    /// <summary>
    /// ApprovalScope serializes as a string via AIJsonUtilities.DefaultOptions' global
    /// JsonStringEnumConverter. DurableAIJsonUtilities.DefaultOptions inherits this converter
    /// because it is constructed from AIJsonUtilities.DefaultOptions.
    /// </summary>
    [Theory]
    [InlineData(ApprovalScope.ThisCallOnly, "\"ThisCallOnly\"")]
    [InlineData(ApprovalScope.Session, "\"Session\"")]
    [InlineData(ApprovalScope.Always, "\"Always\"")]
    public void ApprovalScope_SerializesAsString(ApprovalScope scope, string expectedJson)
    {
        var json = JsonSerializer.Serialize(scope, DurableAIJsonUtilities.DefaultOptions);
        Assert.Equal(expectedJson, json);
    }

    /// <summary>
    /// ApprovalScope with string value in JSON round-trips correctly because
    /// AIJsonUtilities.DefaultOptions includes a global JsonStringEnumConverter.
    /// </summary>
    [Fact]
    public void ApprovalScope_StringValueInJson_RoundTrips()
    {
        // ApprovalScope inherits string serialization from AIJsonUtilities.DefaultOptions.
        const string json = """{"RequestId":"req-001","Approved":true,"Scope":"Session"}""";
        var restored = JsonSerializer.Deserialize<DurableApprovalDecision>(json, DurableAIJsonUtilities.DefaultOptions);
        Assert.NotNull(restored);
        Assert.Equal(ApprovalScope.Session, restored!.Scope);
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
