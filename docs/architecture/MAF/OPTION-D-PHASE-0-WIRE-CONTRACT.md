# Option D: Phase 0 — Wire-Contract Prototype Specification

**Status:** Ready for Architecture Review  
**Owner:** Architecture team (Morpheus/Tank/Trinity)  
**Scope:** Define serialization contract for session-owned state  
**Estimated Duration:** 1-2 days (design + prototype + validation test)

---

## Executive Summary

This phase defines the wire contract for carrying session state (session ID, StateBag, history) across Temporal workflow boundaries (continue-as-new, activity I/O). 

**Core Decision:** Use an internal, source-gen registered snapshot DTO (`TemporalAgentSessionSnapshot`) instead of directly serializing `TemporalAgentSession` (which violates CLAUDE.md line 155 constraint).

**Outcome:** A validated round-trip contract that passes polymorphic serialization tests, ready for Phase 1 (Revised) implementation.

---

## The Wire-Contract Problem

### Current State (v0.3)

- `TemporalAIAgent` maintains instance-level `_history` and `_currentStateBag`
- Multi-session calls on same agent corrupt each other's state
- Continue-as-new loses both history and StateBag mutations

### Attempted Solution (Phase 1 — REVERTED)

- Move history to `TemporalAgentSession`
- Serialize session directly via `JsonSerializer.SerializeToElement(this, opts.GetTypeInfo(...))`

### Problem with Phase 1 Approach

- **Violates constraint:** CLAUDE.md line 155 forbids this exact pattern
- `TemporalAgentSession` is not in `AgentSessionJsonContext`
- Serialization falls back to reflection (loses source-gen guarantees)
- Silent failure risk during Temporal payload conversion

---

## Wire-Contract Design

### 1. Internal Snapshot DTO

Create `TemporalAgentSessionSnapshot` — an internal, source-gen registered type:

```csharp
// File: src/TemporalCommunity.Extensions.Agents/Session/TemporalAgentSessionSnapshot.cs
using System.Text.Json.Serialization;
using TemporalCommunity.Extensions.AI.Session;

namespace TemporalCommunity.Extensions.Agents.Session;

/// <summary>
/// Internal snapshot DTO for serializing session state across Temporal boundaries.
/// This is the wire format for session carry-forward in workflow inputs and activity I/O.
/// NOT exposed in public API; used internally by SerializeSessionCoreAsync/DeserializeSessionCoreAsync.
/// </summary>
[JsonSerializable]  // Will be registered in AgentSessionJsonContext
internal sealed class TemporalAgentSessionSnapshot
{
    /// <summary>
    /// Gets the session identifier (uniquely identifies the session).
    /// </summary>
    [JsonPropertyName("sessionId")]
    public required string SessionId { get; init; }

    /// <summary>
    /// Gets the serialized StateBag (context-provider state).
    /// Null if StateBag is empty; deserialization treats null as empty.
    /// </summary>
    [JsonPropertyName("stateBag")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? StateBag { get; init; }

    /// <summary>
    /// Gets the serialized conversation history (request/response/marker entries).
    /// Null if history is empty; deserialization treats null/missing as empty (legacy compat).
    /// </summary>
    [JsonPropertyName("history")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<DurableSessionEntry>? History { get; init; }

    // FUTURE: SessionHistoryMetadata here if/when compaction is implemented.
    // Do NOT add version field yet; defer versioning until a defined migration exists.
}
```

### 2. Round-Trip Contract

**Serialization:**
```csharp
protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
    AgentSession session,
    JsonSerializerOptions? jsonSerializerOptions = null,
    CancellationToken cancellationToken = default)
{
    if (session is not TemporalAgentSession temporalSession)
        throw new InvalidOperationException(...);

    // Build snapshot from session state
    var snapshot = new TemporalAgentSessionSnapshot
    {
        SessionId = temporalSession.SessionId.WorkflowId,
        StateBag = temporalSession.StateBag.Count > 0 
            ? temporalSession.StateBag.Serialize() 
            : null,
        History = temporalSession.History.Count > 0 
            ? temporalSession.History 
            : null,
    };

    // Serialize snapshot via source-gen context (NOT the session itself)
    var opts = jsonSerializerOptions ?? TemporalAgentJsonUtilities.DefaultOptions;
    var snapshotJson = JsonSerializer.SerializeToElement(
        snapshot, 
        opts.GetTypeInfo(typeof(TemporalAgentSessionSnapshot)));
    
    return new ValueTask<JsonElement>(snapshotJson);
}
```

**Deserialization:**
```csharp
protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
    JsonElement serializedState,
    JsonSerializerOptions? jsonSerializerOptions = null,
    CancellationToken cancellationToken = default)
{
    var opts = jsonSerializerOptions ?? TemporalAgentJsonUtilities.DefaultOptions;
    
    // Deserialize snapshot from source-gen context
    var snapshot = JsonSerializer.Deserialize<TemporalAgentSessionSnapshot>(
        serializedState, 
        opts.GetTypeInfo(typeof(TemporalAgentSessionSnapshot)))
        ?? throw new JsonException("Invalid session snapshot");

    // Restore session from snapshot
    var sessionId = TemporalAgentSessionId.Parse(snapshot.SessionId);
    var stateBag = snapshot.StateBag is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) }
        ? AgentSessionStateBag.Deserialize(snapshot.StateBag.Value)
        : new AgentSessionStateBag();

    var session = new TemporalAgentSession(sessionId, stateBag);
    
    // Restore history (will move to session in Phase 1)
    if (snapshot.History?.Count > 0)
    {
        foreach (var entry in snapshot.History)
            session.AppendHistoryEntry(entry); // Phase 1: internal mutation API
    }

    return new ValueTask<AgentSession>(session);
}
```

### 3. Source-Gen Registration

Update `AgentSessionJsonContext.cs`:

```csharp
[JsonSourceGenerationOptions(WriteIndented = false)]
// ... existing registrations ...
[JsonSerializable(typeof(TemporalAgentSessionSnapshot))]  // NEW
// ... rest
internal sealed partial class AgentSessionJsonContext : JsonSerializerContext;
```

---

## Wire-Contract Test Gates

Define these tests **before implementation** to validate the contract:

### Gate 1: Source-Gen Resolver-Origin Test

**Purpose:** Verify snapshot DTO is source-gen registered and resolvable.

```csharp
[Fact]
public void SessionSnapshot_SourceGenResolver()
{
    var typeInfo = TemporalAgentJsonUtilities.DefaultOptions
        .GetTypeInfo(typeof(TemporalAgentSessionSnapshot));
    
    Assert.NotNull(typeInfo);
    // Verify it came from AgentSessionJsonContext, not reflection
    Assert.Equal(nameof(AgentSessionJsonContext), 
        typeInfo.Origin.Name);
}
```

### Gate 2: Polymorphic Payload Round-Trip

**Purpose:** Verify full request/response/tool-content serialization via TemporalAgentDataConverter.

```csharp
[Fact]
public async Task SessionSnapshot_TemporalAgentDataConverter_RoundTrip()
{
    var snapshot = new TemporalAgentSessionSnapshot
    {
        SessionId = "ta-agent-abc123",
        StateBag = JsonDocument.Parse("""{ "key": "value" }""").RootElement,
        History = new[]
        {
            new AgentSessionRequest 
            { 
                CorrelationId = "req1",
                CreatedAt = DateTimeOffset.UtcNow,
                Messages = new[] { new ChatMessage(ChatRole.User, "Hello") }
            },
            new AgentSessionResponse
            {
                CorrelationId = "req1",
                CreatedAt = DateTimeOffset.UtcNow,
                Messages = new[] { new ChatMessage(ChatRole.Assistant, "Hi") }
            }
        }
    };

    // Serialize via TemporalAgentDataConverter
    var payload = TemporalAgentDataConverter.Instance.ToPayload(snapshot);
    
    // Deserialize back
    var restored = TemporalAgentDataConverter.Instance
        .FromPayload<TemporalAgentSessionSnapshot>(payload);

    Assert.NotNull(restored);
    Assert.Equal(snapshot.SessionId, restored.SessionId);
    Assert.Equal(2, restored.History?.Count);
}
```

### Gate 3: Legacy Payload Compatibility

**Purpose:** Verify old sessions (v0.2 without history) deserialize correctly.

```csharp
[Fact]
public void SessionSnapshot_LegacyPayload_NoHistory()
{
    // Simulate v0.2 session without history field
    var legacyJson = JsonDocument.Parse("""
    {
        "sessionId": "ta-agent-key",
        "stateBag": { "context": "data" }
        // Note: no "history" field
    }
    """).RootElement;

    var snapshot = JsonSerializer.Deserialize<TemporalAgentSessionSnapshot>(
        legacyJson, 
        TemporalAgentJsonUtilities.DefaultOptions);

    Assert.NotNull(snapshot);
    Assert.Equal("ta-agent-key", snapshot.SessionId);
    Assert.Null(snapshot.History); // Treated as empty
}
```

### Gate 4: Empty State Optimization

**Purpose:** Verify null/empty fields are omitted from JSON (payload size).

```csharp
[Fact]
public void SessionSnapshot_EmptyState_OmitFields()
{
    var snapshot = new TemporalAgentSessionSnapshot
    {
        SessionId = "ta-agent-key",
        StateBag = null,      // Omitted
        History = null,       // Omitted
    };

    var json = JsonSerializer.SerializeToElement(
        snapshot, 
        TemporalAgentJsonUtilities.DefaultOptions);

    // Should only have sessionId, not stateBag or history
    Assert.True(json.TryGetProperty("sessionId", out _));
    Assert.False(json.TryGetProperty("stateBag", out _));
    Assert.False(json.TryGetProperty("history", out _));
}
```

---

## Customer Workflow Pattern (Post-Design)

Once wire contract is validated, workflows will use it like this:

```csharp
[Workflow("MyOrchestrationWorkflow")]
public class MyOrchestrationWorkflow
{
    private TemporalAgentSession? _session;

    [WorkflowRun]
    public async Task RunAsync(MyWorkflowInput input)
    {
        // Restore or create session
        if (input.SerializedSession is not null)
        {
            _session = await agent.DeserializeSessionAsync(
                input.SerializedSession, 
                cancellationToken);
        }
        else
        {
            _session = await agent.CreateSessionAsync(cancellationToken);
        }

        // Use session
        var response = await agent.RunAsync("request", _session);

        // Carry forward on continue-as-new (explicit)
        if (Workflow.ContinueAsNewSuggested)
        {
            var serialized = await agent.SerializeSessionAsync(
                _session, 
                cancellationToken);
            
            throw Workflow.CreateContinueAsNewException(
                (MyOrchestrationWorkflow w) => w.RunAsync(
                    new MyWorkflowInput { SerializedSession = serialized }));
        }
    }
}

public record MyWorkflowInput
{
    public JsonElement? SerializedSession { get; init; }
}
```

---

## Design Checklist

- [ ] `TemporalAgentSessionSnapshot` DTO designed and approved
- [ ] Source-gen registration in `AgentSessionJsonContext` approved
- [ ] Round-trip contract (serialize/deserialize logic) approved
- [ ] Legacy payload compatibility strategy approved (treat missing history as empty)
- [ ] All 4 test gates defined and passing
- [ ] Concurrent-use semantics defined (overlapping calls: reject or serialize?)
- [ ] StateBag mutation flow documented (how updates flow from activities back to session)
- [ ] Performance characteristics understood (snapshot serialization overhead)

---

## Next Steps After Approval

1. **Implementation** (Phase 1 team)
   - Implement `TemporalAgentSessionSnapshot` exactly as designed
   - Add round-trip methods to `TemporalAIAgent`
   - Implement all 4 test gates
   - Run benchmarks at 1, 10, 100-turn scales

2. **Validation** (Phase 1 team)
   - Verify no regressions in existing workflows
   - Test with polymorphic content (tool calls, function results)
   - Verify continue-as-new carry-forward works end-to-end

3. **Documentation** (Phase 1 team)
   - Update CLAUDE.md with session ownership rules
   - Document carry-forward pattern in usage guide
   - Add migration notes for v0.3 → v0.4

---

## Open Questions for Architecture Team

1. **Concurrent-use semantics:** Should overlapping `RunAsync()` calls on same live session:
   - **A) Reject immediately** with `InvalidOperationException` (recommended)?
   - **B) Serialize internally** (queue calls, process sequentially)?
   - **C) Allow (undefined behavior)** (NOT recommended)?

2. **StateBag update flow:** Should session apply StateBag updates from activities:
   - **A) On every activity completion** (current proxy behavior)?
   - **B) On turn completion** (after all tool calls)?
   - **C) Configurable per agent**?

3. **History truncation:** Should snapshot omit very old history entries:
   - **A) No (keep full history always)**?
   - **B) Yes, with configurable threshold** (Phase 2+ work)?

4. **Version field:** Add version to snapshot for future migrations?
   - **A) Not yet (defer until needed)**?
   - **B) Yes, default to 1** (safer for evolution)?

---

## Approval Signoff

**Ready for review by:** Morpheus, Tank, Trinity  
**Review checklist:**
- [ ] Wire-contract design is sound
- [ ] Snapshot DTO structure is correct
- [ ] Test gates are comprehensive
- [ ] Customer workflow pattern is acceptable
- [ ] Concurrent-use semantics chosen
- [ ] StateBag update strategy defined
- [ ] Ready to proceed to Phase 1 (Revised) implementation

**Approved by:** ________________  
**Date:** ________________
