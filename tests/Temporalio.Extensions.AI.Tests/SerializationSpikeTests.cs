using System.Text.Json;
using Temporalio.Extensions.AI;
using Xunit;

namespace Temporalio.Extensions.AI.Tests;

/// <summary>
/// Task 0.2 — Spike: source-gen registration and PatternMatchTypeJsonConverter behavior.
///
/// GROUP 1 SIGN-OFF version: uses the real types from Temporalio.Extensions.AI
/// (PatternMatchType, PatternMatchTypeJsonConverter, ApprovalScopePattern, DurableApprovalDecision)
/// and validates them via DurableAIJsonUtilities.DefaultOptions.
/// </summary>
public class SerializationSpikeTests
{
    // -------------------------------------------------------------------
    // Test cases — all using real types from Temporalio.Extensions.AI
    // -------------------------------------------------------------------

    /// <summary>
    /// Test case 1: serialize PatternMatchType.Exact via DefaultOptions → produces "Exact" (string, not 0).
    /// </summary>
    [Fact]
    public void PatternMatchType_SerializesAsString_NotInteger()
    {
        var json = JsonSerializer.Serialize(PatternMatchType.Exact, DurableAIJsonUtilities.DefaultOptions);
        // The converter produces a JSON string value "Exact"
        Assert.Equal("\"Exact\"", json);
    }

    /// <summary>
    /// Test case 2: deserialize "Glob" → PatternMatchType.Glob.
    /// Deserialize "glob" (lower-case) → PatternMatchType.Glob (case-insensitive).
    /// </summary>
    [Fact]
    public void PatternMatchType_DeserializesFromString_CaseInsensitive()
    {
        var glob = JsonSerializer.Deserialize<PatternMatchType>("\"Glob\"", DurableAIJsonUtilities.DefaultOptions);
        Assert.Equal(PatternMatchType.Glob, glob);

        var globLower = JsonSerializer.Deserialize<PatternMatchType>("\"glob\"", DurableAIJsonUtilities.DefaultOptions);
        Assert.Equal(PatternMatchType.Glob, globLower);
    }

    /// <summary>
    /// Test case 3a: integer values for PatternMatchType are rejected with JsonException.
    ///
    /// PatternMatchTypeJsonConverter is now registered with priority in options.Converters
    /// (inserted at index 0, before the global JsonStringEnumConverter from AIJsonUtilities).
    /// The allowIntegerValues:false constraint is therefore correctly enforced at the
    /// data converter boundary for both defined and undefined integer values.
    /// </summary>
    [Fact]
    public void PatternMatchType_IntegerValue_ThrowsJsonException()
    {
        // Integer 0 is rejected — PatternMatchTypeJsonConverter (allowIntegerValues:false) wins.
        const string jsonValid = """{"Type":0,"Pattern":"/tmp/foo"}""";
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<ApprovalScopePattern>(jsonValid, DurableAIJsonUtilities.DefaultOptions));

        // Integer 99 is also rejected.
        const string jsonUndefined = """{"Type":99,"Pattern":"/tmp/foo"}""";
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<ApprovalScopePattern>(jsonUndefined, DurableAIJsonUtilities.DefaultOptions));
    }

    /// <summary>
    /// Test case 3b: deserialize an ApprovalScopePattern with an unknown Type string (e.g. "Fuzzy")
    /// → JsonException thrown.
    /// </summary>
    [Fact]
    public void PatternMatchType_UnknownStringValue_ThrowsJsonException()
    {
        // Unknown string "Type" value — must fail deserialization.
        const string json = """{"Type":"Fuzzy","Pattern":"/tmp/foo"}""";
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<ApprovalScopePattern>(json, DurableAIJsonUtilities.DefaultOptions));
    }

    /// <summary>
    /// Test case 4: full ApprovalScopePattern round-trip.
    /// { Type: Regex, Pattern: "^/tmp/.*", Parameter: null } → serialize → deserialize → all fields match.
    /// </summary>
    [Fact]
    public void ApprovalScopePattern_FullRoundTrip()
    {
        var pattern = new ApprovalScopePattern
        {
            Type = PatternMatchType.Regex,
            Pattern = "^/tmp/.*",
            Parameter = null
        };

        var json = JsonSerializer.Serialize(pattern, DurableAIJsonUtilities.DefaultOptions);
        var restored = JsonSerializer.Deserialize<ApprovalScopePattern>(json, DurableAIJsonUtilities.DefaultOptions);

        Assert.NotNull(restored);
        Assert.Equal(PatternMatchType.Regex, restored.Type);
        Assert.Equal("^/tmp/.*", restored.Pattern);
        Assert.Null(restored.Parameter);
    }

    /// <summary>
    /// Test case 5 (GROUP 1 SIGN-OFF GATE): DurableApprovalDecision round-trip with new scope fields.
    /// - Scope = Always with non-null ScopePattern round-trips correctly.
    /// - Scope = ThisCallOnly (default) omits the Scope field (WhenWritingDefault).
    /// - ScopePattern = null is also omitted (WhenWritingNull).
    /// </summary>
    [Fact]
    public void DurableApprovalDecision_WithScopeFields_RoundTrips()
    {
        // Decision with explicit Always scope and pattern
        var decision = new DurableApprovalDecision
        {
            RequestId = "req-scope-001",
            Approved = true,
            Scope = ApprovalScope.Always,
            ScopePattern = new ApprovalScopePattern
            {
                Type = PatternMatchType.Glob,
                Pattern = "/tmp/*",
                Parameter = "path"
            }
        };

        var json = JsonSerializer.Serialize(decision, DurableAIJsonUtilities.DefaultOptions);
        var restored = JsonSerializer.Deserialize<DurableApprovalDecision>(json, DurableAIJsonUtilities.DefaultOptions);

        Assert.NotNull(restored);
        Assert.Equal("req-scope-001", restored.RequestId);
        Assert.True(restored.Approved);
        Assert.Equal(ApprovalScope.Always, restored.Scope);
        Assert.NotNull(restored.ScopePattern);
        Assert.Equal(PatternMatchType.Glob, restored.ScopePattern.Type);
        Assert.Equal("/tmp/*", restored.ScopePattern.Pattern);
        Assert.Equal("path", restored.ScopePattern.Parameter);
    }

    /// <summary>
    /// Scope = ThisCallOnly (default value) must be omitted from JSON (WhenWritingDefault).
    /// ScopePattern = null must also be omitted from JSON (WhenWritingNull).
    /// </summary>
    [Fact]
    public void DurableApprovalDecision_DefaultScope_OmittedFromJson()
    {
        var decision = new DurableApprovalDecision
        {
            RequestId = "req-default-001",
            Approved = true
        };

        var json = JsonSerializer.Serialize(decision, DurableAIJsonUtilities.DefaultOptions);

        // Scope field must not appear (WhenWritingDefault suppresses the 0 value)
        Assert.DoesNotContain("\"Scope\"", json);
        Assert.DoesNotContain("\"scope\"", json);

        // ScopePattern must not appear (WhenWritingNull)
        Assert.DoesNotContain("\"ScopePattern\"", json);
        Assert.DoesNotContain("\"scopePattern\"", json);

        // Deserialize and verify defaults
        var restored = JsonSerializer.Deserialize<DurableApprovalDecision>(json, DurableAIJsonUtilities.DefaultOptions);
        Assert.NotNull(restored);
        Assert.Equal(ApprovalScope.ThisCallOnly, restored.Scope);
        Assert.Null(restored.ScopePattern);
    }
}
