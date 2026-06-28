using Microsoft.Agents.AI;
using TemporalCommunity.Extensions.Agents;
using TemporalCommunity.Extensions.Agents.Approvals;
using TemporalCommunity.Extensions.AI;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.Tests;

/// <summary>
/// Task 0.1 — Spike: typed StateBag write/read round-trip.
/// Validates the critical typed StateBag write pattern used by Feature B scope persistence.
/// Uses the real ApprovalScopeRecord type (Task 1.4).
/// </summary>
public class StateBagSpikeTests
{
    // -------------------------------------------------------------------
    // Test cases — all using real types from TemporalCommunity.Extensions.Agents
    // -------------------------------------------------------------------

    /// <summary>
    /// Test case 1: write a List&lt;ApprovalScopeRecord&gt; to an AgentSessionStateBag, serialize,
    /// deserialize, read back, and assert round-trip fidelity.
    /// </summary>
    [Fact]
    public void RoundTrip_TypedSetGet_PreservesFieldValues()
    {
        var bag = new AgentSessionStateBag();
        var now = DateTimeOffset.UtcNow;

        var record = new ApprovalScopeRecord
        {
            ToolName = "write_file",
            GrantedAt = now,
            OriginatingRequestId = "req-001"
        };
        var records = new List<ApprovalScopeRecord> { record };

        // Write using typed SetValue with TemporalAgentJsonUtilities.DefaultOptions
        bag.SetValue<List<ApprovalScopeRecord>>(
            "temporal.approval_scopes.session",
            records,
            TemporalAgentJsonUtilities.DefaultOptions);

        // Serialize the bag, then deserialize it
        var serialized = bag.Serialize();
        var restored = AgentSessionStateBag.Deserialize(serialized);

        // Read back using typed TryGetValue
        var found = restored.TryGetValue<List<ApprovalScopeRecord>>(
            "temporal.approval_scopes.session",
            out var readBack,
            TemporalAgentJsonUtilities.DefaultOptions);

        Assert.True(found);
        Assert.NotNull(readBack);
        Assert.Single(readBack);
        Assert.Equal("write_file", readBack[0].ToolName);
        Assert.Equal("req-001", readBack[0].OriginatingRequestId);
        // DateTimeOffset round-trips faithfully
        Assert.Equal(now.ToUniversalTime(), readBack[0].GrantedAt.ToUniversalTime());
    }

    /// <summary>
    /// Test case 2: write twice to the same key (simulating a second approval in the same session).
    /// The second write should reflect the appended list, not just the second element alone.
    /// </summary>
    [Fact]
    public void WriteToSameKey_Twice_AppendSemanticsByCaller()
    {
        var bag = new AgentSessionStateBag();

        var record1 = new ApprovalScopeRecord
        {
            ToolName = "send_email",
            GrantedAt = DateTimeOffset.UtcNow,
            OriginatingRequestId = "req-001"
        };

        // First write
        bag.SetValue<List<ApprovalScopeRecord>>(
            "temporal.approval_scopes.session",
            new List<ApprovalScopeRecord> { record1 },
            TemporalAgentJsonUtilities.DefaultOptions);

        // Simulate append: read existing, append, write back
        var serialized1 = bag.Serialize();
        var bag2 = AgentSessionStateBag.Deserialize(serialized1);

        bag2.TryGetValue<List<ApprovalScopeRecord>>(
            "temporal.approval_scopes.session",
            out var existing,
            TemporalAgentJsonUtilities.DefaultOptions);

        existing ??= new List<ApprovalScopeRecord>();

        var record2 = new ApprovalScopeRecord
        {
            ToolName = "delete_file",
            GrantedAt = DateTimeOffset.UtcNow,
            OriginatingRequestId = "req-002"
        };
        existing.Add(record2);

        bag2.SetValue<List<ApprovalScopeRecord>>(
            "temporal.approval_scopes.session",
            existing,
            TemporalAgentJsonUtilities.DefaultOptions);

        // Final serialize/deserialize and read back
        var serialized2 = bag2.Serialize();
        var bagFinal = AgentSessionStateBag.Deserialize(serialized2);

        var found = bagFinal.TryGetValue<List<ApprovalScopeRecord>>(
            "temporal.approval_scopes.session",
            out var finalList,
            TemporalAgentJsonUtilities.DefaultOptions);

        Assert.True(found);
        Assert.NotNull(finalList);
        Assert.Equal(2, finalList.Count);
        Assert.Equal("req-001", finalList[0].OriginatingRequestId);
        Assert.Equal("req-002", finalList[1].OriginatingRequestId);
    }

    /// <summary>
    /// Test case 3: write to key A, then read key B. TryGetValue must return false for key B.
    /// </summary>
    [Fact]
    public void ReadDifferentKey_ReturnsFalse_NoCrossKeyContamination()
    {
        var bag = new AgentSessionStateBag();

        bag.SetValue<List<ApprovalScopeRecord>>(
            "temporal.approval_scopes.session",
            new List<ApprovalScopeRecord>
            {
                new() { ToolName = "tool_a", GrantedAt = DateTimeOffset.UtcNow, OriginatingRequestId = "req-a" }
            },
            TemporalAgentJsonUtilities.DefaultOptions);

        // Serialize/deserialize round-trip
        var serialized = bag.Serialize();
        var restored = AgentSessionStateBag.Deserialize(serialized);

        // Reading a DIFFERENT key must return false
        var found = restored.TryGetValue<List<ApprovalScopeRecord>>(
            "temporal.approval_scopes.always",
            out var value,
            TemporalAgentJsonUtilities.DefaultOptions);

        Assert.False(found);
        Assert.Null(value);
    }

    /// <summary>
    /// Test case 4: confirm that SetValue&lt;T&gt; uses List&lt;ApprovalScopeRecord&gt; as the type
    /// parameter — not JsonElement (which is a struct and would fail the class constraint).
    /// Validates that the typed read-back succeeds and returns a strongly-typed List&lt;T&gt;.
    /// </summary>
    [Fact]
    public void SetValue_UsesList_NotJsonElement()
    {
        var bag = new AgentSessionStateBag();
        var record = new ApprovalScopeRecord
        {
            ToolName = "write_record",
            GrantedAt = DateTimeOffset.UtcNow,
            OriginatingRequestId = "req-x"
        };

        // Write using List<T> as the type parameter (not JsonElement)
        bag.SetValue<List<ApprovalScopeRecord>>(
            "temporal.approval_scopes.session",
            new List<ApprovalScopeRecord> { record },
            TemporalAgentJsonUtilities.DefaultOptions);

        var serialized = bag.Serialize();
        var restored = AgentSessionStateBag.Deserialize(serialized);

        // Verify that TryGetValue<List<T>> succeeds — this only works if we set as List<T>
        var found = restored.TryGetValue<List<ApprovalScopeRecord>>(
            "temporal.approval_scopes.session",
            out var readBack,
            TemporalAgentJsonUtilities.DefaultOptions);

        Assert.True(found);
        Assert.NotNull(readBack);
        Assert.IsType<List<ApprovalScopeRecord>>(readBack);
        Assert.Single(readBack);
        Assert.Equal("write_record", readBack[0].ToolName);
    }
}
