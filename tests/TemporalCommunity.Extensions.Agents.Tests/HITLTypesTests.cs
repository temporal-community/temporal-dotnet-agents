using System.Text.Json;
using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.Agents.Approvals;
using TemporalCommunity.Extensions.AI;
using TemporalCommunity.Extensions.AI.Approvals;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.Tests;

/// <summary>
/// Tests that the HITL approval types serialize/deserialize correctly.
/// Requests use the shared MEAI type; scoped agent decisions use the MAF-owned type.
/// </summary>
public class HITLTypesTests
{
    [Fact]
    public void DurableApprovalRequest_RequiresExplicitRequestId()
    {
        // required keyword means we must supply RequestId at construction time.
        var request = new DurableApprovalRequest { RequestId = "req-123" };
        Assert.Equal("req-123", request.RequestId);
    }

    [Fact]
    public void DurableApprovalRequest_RoundTripsViaJson()
    {
        var original = new DurableApprovalRequest
        {
            RequestId = "req-123",
            FunctionName = "send_email",
            CallId = "call-abc",
            Description = "Send email to alice@example.com"
        };

        var json = JsonSerializer.Serialize(original, AIJsonUtilities.DefaultOptions);
        var deserialized = JsonSerializer.Deserialize<DurableApprovalRequest>(json, AIJsonUtilities.DefaultOptions);

        Assert.NotNull(deserialized);
        Assert.Equal("req-123", deserialized.RequestId);
        Assert.Equal("send_email", deserialized.FunctionName);
        Assert.Equal("call-abc", deserialized.CallId);
        Assert.Equal("Send email to alice@example.com", deserialized.Description);
    }

    [Fact]
    public void DurableApprovalDecision_RoundTripsViaJson()
    {
        var decision = new DurableApprovalDecision
        {
            RequestId = "req-123",
            Approved = true,
            Reason = "Looks good."
        };

        var json = JsonSerializer.Serialize(decision, AIJsonUtilities.DefaultOptions);
        var deserialized = JsonSerializer.Deserialize<DurableApprovalDecision>(json, AIJsonUtilities.DefaultOptions);

        Assert.NotNull(deserialized);
        Assert.Equal("req-123", deserialized.RequestId);
        Assert.True(deserialized.Approved);
        Assert.Equal("Looks good.", deserialized.Reason);
    }

    [Fact]
    public void DurableApprovalDecision_NullOptionalFields_SerializeCorrectly()
    {
        var decision = new DurableApprovalDecision { RequestId = "req-789", Approved = false };
        var json = JsonSerializer.Serialize(decision, AIJsonUtilities.DefaultOptions);
        var deserialized = JsonSerializer.Deserialize<DurableApprovalDecision>(json, AIJsonUtilities.DefaultOptions);

        Assert.Null(deserialized?.Reason);
    }

    [Fact]
    public void DurableApprovalRequest_NullOptionalFields_SerializeCorrectly()
    {
        var request = new DurableApprovalRequest { RequestId = "req-456" };
        var json = JsonSerializer.Serialize(request, AIJsonUtilities.DefaultOptions);
        var deserialized = JsonSerializer.Deserialize<DurableApprovalRequest>(json, AIJsonUtilities.DefaultOptions);

        Assert.NotNull(deserialized);
        Assert.Null(deserialized.FunctionName);
        Assert.Null(deserialized.CallId);
        Assert.Null(deserialized.Description);
    }

    // ── Feature B: scope-specific round-trip cases ──────────────────────────

    /// <summary>
    /// DurableAgentApprovalDecision with Scope = Session round-trips via agent JSON options.
    /// </summary>
    [Fact]
    public void DurableAgentApprovalDecision_SessionScope_RoundTrips()
    {
        var decision = new DurableAgentApprovalDecision
        {
            RequestId = "req-scope-session",
            Approved = true,
            Scope = ApprovalScope.Session,
        };

        var json = JsonSerializer.Serialize(decision, TemporalAgentJsonUtilities.DefaultOptions);
        var restored = JsonSerializer.Deserialize<DurableAgentApprovalDecision>(json, TemporalAgentJsonUtilities.DefaultOptions);

        Assert.NotNull(restored);
        Assert.Equal(ApprovalScope.Session, restored.Scope);
        Assert.Null(restored.ScopePattern);
    }

    /// <summary>
    /// DurableAgentApprovalDecision with ScopePattern containing PatternMatchType serialized as string.
    /// </summary>
    [Fact]
    public void DurableAgentApprovalDecision_ScopePattern_TypeIsStringInJson()
    {
        var decision = new DurableAgentApprovalDecision
        {
            RequestId = "req-scope-pattern",
            Approved = true,
            Scope = ApprovalScope.Session,
            GrantId = "grant-1",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            ScopePattern = new ApprovalScopePattern
            {
                Type = PatternMatchType.Glob,
                Pattern = "/tmp/*",
                Parameter = "path",
            },
        };

        var json = JsonSerializer.Serialize(decision, TemporalAgentJsonUtilities.DefaultOptions);

        // Type must serialize as string "Glob", not integer 1.
        Assert.Contains("\"Glob\"", json);

        var restored = JsonSerializer.Deserialize<DurableAgentApprovalDecision>(json, TemporalAgentJsonUtilities.DefaultOptions);
        Assert.NotNull(restored);
        Assert.Equal(PatternMatchType.Glob, restored.ScopePattern?.Type);
        Assert.Equal("/tmp/*", restored.ScopePattern?.Pattern);
    }

    /// <summary>
    /// DurableAgentApprovalDecision with default Scope (ThisCallOnly) omits both Scope and ScopePattern from JSON.
    /// </summary>
    [Fact]
    public void DurableAgentApprovalDecision_DefaultScope_FieldsOmittedFromJson()
    {
        var decision = new DurableAgentApprovalDecision { RequestId = "req-def", Approved = true };
        var json = JsonSerializer.Serialize(decision, TemporalAgentJsonUtilities.DefaultOptions);

        Assert.DoesNotContain("\"Scope\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"ScopePattern\"", json, StringComparison.OrdinalIgnoreCase);
    }
}
