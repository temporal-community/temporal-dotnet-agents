# Option D: Phase 0 — Wire-Contract Prototype Specification

**Status:** Ready for Architecture Review  
**Owner:** Architecture team (Morpheus/Tank/Trinity)  
**Scope:** Define serialization contract for session-owned state  
**Estimated Duration:** 1-2 days (design + prototype + validation test)

---

## Executive Summary

This phase defines the wire contract for serializing session state (session ID, StateBag, history) across explicit persistence and continue-as-new boundaries. It does not replace the existing activity inputs, which continue to carry accumulated messages and serialized StateBag values independently.

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
/// This is the wire format for session persistence and carry-forward in workflow inputs.
/// NOT exposed in public API; used internally by SerializeSessionCoreAsync/DeserializeSessionCoreAsync.
/// </summary>
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
        SessionId = temporalSession.SessionId.ToString(),  // Preserves agentName-key format
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
    Assert.Same(AgentSessionJsonContext.Default, typeInfo.OriginatingResolver);
}
```

### Gate 2: Polymorphic Payload Round-Trip

**Purpose:** Verify full request/response/tool-content serialization via TemporalAgentDataConverter.

```csharp
[Fact]
public void SessionSnapshot_TemporalAgentDataConverter_RoundTrip()
{
    var stateBag = new AgentSessionStateBag();
    stateBag.SetValue("key", "value");
    var snapshot = new TemporalAgentSessionSnapshot
    {
        SessionId = "ta-agent-abc123",
        StateBag = stateBag.Serialize(),
        History = new[]
        {
            new AgentSessionRequest 
            { 
                CorrelationId = "req1",
                CreatedAt = DateTimeOffset.UtcNow,
                Messages = new[]
                {
                    new ChatMessage(ChatRole.User, "Hello"),
                    new ChatMessage(ChatRole.Assistant, new FunctionCallContent(
                        "call-1", "weather", new Dictionary<string, object?> { ["city"] = "Seattle" }))
                }
            },
            new AgentSessionResponse
            {
                CorrelationId = "req1",
                CreatedAt = DateTimeOffset.UtcNow,
                Messages = new[]
                {
                    new ChatMessage(ChatRole.Tool, new FunctionResultContent("call-1", "sunny"))
                }
            }
        }
    };

    // Serialize via TemporalAgentDataConverter
    var converter = TemporalAgentDataConverter.Instance.PayloadConverter;
    var payload = converter.ToPayload(snapshot);
    
    // Deserialize back
    var restored = (TemporalAgentSessionSnapshot)converter.ToValue(
        payload, typeof(TemporalAgentSessionSnapshot))!;

    Assert.NotNull(restored);
    Assert.Equal(snapshot.SessionId, restored.SessionId);
    Assert.Equal(2, restored.History?.Count);
    Assert.IsType<AgentSessionRequest>(restored.History![0]);
    Assert.IsType<AgentSessionResponse>(restored.History[1]);
    Assert.IsType<FunctionCallContent>(restored.History[0].Messages[1].Contents.Single());
    Assert.IsType<FunctionResultContent>(restored.History[1].Messages[0].Contents.Single());
    var restoredBag = AgentSessionStateBag.Deserialize(restored.StateBag!.Value);
    Assert.True(restoredBag.TryGetValue<string>("key", out var value));
    Assert.Equal("value", value);
}
```

### Gate 3: Legacy Snapshot Compatibility

**Purpose:** Verify legacy session payloads without a `history` field deserialize correctly.

```csharp
[Fact]
public void SessionSnapshot_LegacyPayload_NoHistory()
{
    var stateBag = new AgentSessionStateBag();
    stateBag.SetValue("context", "data");
    var serializedStateBag = stateBag.Serialize().GetRawText();

    // Legacy payload shape: no history field.
    var legacyJson = JsonDocument.Parse($$"""
    {
        "sessionId": "ta-agent-key",
        "stateBag": {{serializedStateBag}}
    }
    """).RootElement;

    var snapshot = JsonSerializer.Deserialize<TemporalAgentSessionSnapshot>(
        legacyJson,
        TemporalAgentJsonUtilities.DefaultOptions);

    Assert.NotNull(snapshot);
    Assert.Equal("ta-agent-key", snapshot.SessionId);
    Assert.Null(snapshot.History);
    Assert.True(
        AgentSessionStateBag.Deserialize(snapshot.StateBag!.Value)
            .TryGetValue<string>("context", out var value));
    Assert.Equal("data", value);
}
```

**Validates:** Missing `history` is backward compatible at the persisted wire boundary. Existing
snapshots decode with no history and preserve their StateBag. Gate 5 exercises that compatibility
through the public agent serialization boundary once session-owned history exists.

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

### Gate 5: Agent Serialization Boundary (Phase 1)

**Phase boundary:** Define this test in Phase 0. It becomes executable when Phase 1 adds
session-owned history (`AppendHistoryEntry` and `History`) and makes
`TemporalAIAgent.SerializeSessionAsync` / `DeserializeSessionAsync` use the snapshot contract.
The Phase 1 test suite must also pass a legacy snapshot with no `history` field through
`DeserializeSessionAsync`, proving the compatibility established by Gate 3 at the public boundary.

**Purpose:** Verify the public MAF session serialization boundary, not only the snapshot DTO.

```csharp
[Fact]
public async Task SessionSnapshot_FreshAgent_RestoresTypedStateAndHistory()
{
    var sourceAgent = new TemporalAIAgent("agent");
    var sourceSession = new TemporalAgentSession(new TemporalAgentSessionId("agent", "key"));
    sourceSession.StateBag.SetValue("key", "value");
    sourceSession.AppendHistoryEntry(new AgentSessionRequest
    {
        CorrelationId = "req-1",
        CreatedAt = DateTimeOffset.UtcNow,
        Messages = [new ChatMessage(ChatRole.User, "Hello")],
    });

    var serialized = await sourceAgent.SerializeSessionAsync(sourceSession);
    var restored = await new TemporalAIAgent("agent").DeserializeSessionAsync(serialized);
    var session = Assert.IsType<TemporalAgentSession>(restored);

    Assert.True(session.StateBag.TryGetValue<string>("key", out var value));
    Assert.Equal("value", value);
    Assert.Single(session.History);
    Assert.IsType<AgentSessionRequest>(session.History[0]);
}
```

### StateBag Update Contract

`TemporalAgentSession.StateBag` is the sole StateBag model. The session exposes an internal
operation that accepts a serialized StateBag snapshot and replaces its in-memory StateBag only
after the applicable deterministic merge has completed.

- After every LLM-step activity, overlay the trusted `UpdatedStateBag` onto the session snapshot
  with `StateBagMerge.OverlayTrustedStateBag` before dispatching the next LLM step.
- After concurrent tool and interceptor activities finish, merge their write-backs once in original
  tool-call index order with `StateBagMerge.Merge`, then apply the merged result to the session.
- Never apply a tool or interceptor write-back in activity completion order, and do not defer either
  update category until the turn ends.

### Concurrent-Use Contract (Phase 1)

The Phase 1 integration suite must start two overlapping `RunAsync` calls using the same live
`TemporalAgentSession` and assert that the second call fails immediately with the documented
`InvalidOperationException`. The test must separately demonstrate that distinct sessions can run
independently. This protects the contract from regressing into silent serialization or shared-state
corruption.

---

## Customer Workflow Pattern (Post-Design)

Once wire contract is validated, workflows will use it like this:

```csharp
[Workflow("MyOrchestrationWorkflow")]
public class MyOrchestrationWorkflow
{
    [WorkflowRun]
    public async Task RunAsync(MyWorkflowInput input)
    {
        var agent = WorkflowAgents.GetTemporalAgent("MyAgent");

        // Restore or create session
        TemporalAgentSession session;
        if (input.SerializedSession is { } serializedSession)
        {
            var restored = await agent.DeserializeSessionAsync(
                serializedSession,
                cancellationToken: Workflow.CancellationToken);
            session = restored as TemporalAgentSession
                ?? throw new InvalidOperationException("The serialized session is not a TemporalAgentSession.");
        }
        else
        {
            session = (TemporalAgentSession)await agent.CreateSessionAsync(Workflow.CancellationToken);
        }

        // Use session
        var response = await agent.RunAsync("request", session, cancellationToken: Workflow.CancellationToken);

        // Carry forward on continue-as-new (explicit)
        if (Workflow.ContinueAsNewSuggested)
        {
            var serialized = await agent.SerializeSessionAsync(
                session,
                cancellationToken: Workflow.CancellationToken);
            
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

The worker that executes this custom workflow must use `TemporalAgentDataConverter` (or an
equivalent converter created by `TemporalAgentDataConverter.CreateDataConverter`) so MAF session
entry polymorphism is preserved in workflow history.

---

## Design Checklist

- [ ] `TemporalAgentSessionSnapshot` DTO designed and approved
- [ ] Source-gen registration in `AgentSessionJsonContext` approved
- [ ] Round-trip contract (serialize/deserialize logic) approved
- [ ] Legacy payload compatibility strategy approved (treat missing history as empty)
- [ ] Gates 1–4 defined and passing in the wire-contract prototype
- [ ] Gate 5 specified, then passing with the Phase 1 session-owned-history implementation
- [ ] Concurrent-use rejection semantics documented and tested
- [ ] StateBag mutation flow documented and tested against LLM-step overlay plus tool/interceptor merge
- [ ] Performance characteristics understood (snapshot serialization overhead)

---

## Next Steps After Approval

1. **Implementation** (Phase 1 team)
   - Implement `TemporalAgentSessionSnapshot` exactly as designed
   - Add round-trip methods to `TemporalAIAgent`
   - Implement Gates 1–4 in the wire-contract prototype and Gate 5 with session-owned history
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

## Decisions Carried into Implementation

1. **Concurrent use:** Reject overlapping `RunAsync()` calls on the same live session with a
   clear `InvalidOperationException`. Parallel conversations require distinct session objects.

2. **StateBag timing:** Apply trusted LLM-step output after each LLM activity. Merge concurrent
   tool/interceptor output after its full fan-out completes in original tool-call index order. Both
   updates are applied before the next dispatch, never at turn completion.

3. **History truncation:** Keep complete history in this scope. Do not introduce an automatic
   threshold or compaction policy; durable history reduction remains separate work.

4. **Versioning:** Do not add a version field. A missing `history` property is the supported legacy
   shape; add a version only with a defined migration behavior.

---

## Approval Signoff

**Ready for review by:** Morpheus, Tank, Trinity  
**Review checklist:**
- [ ] Wire-contract design is sound
- [ ] Snapshot DTO structure is correct
- [ ] Test gates are comprehensive
- [ ] Customer workflow pattern is acceptable
- [ ] Concurrent-use rejection contract confirmed
- [ ] StateBag update contract confirmed
- [ ] Ready to proceed to Phase 1 (Revised) implementation

**Approved by:** ________________  
**Date:** ________________
