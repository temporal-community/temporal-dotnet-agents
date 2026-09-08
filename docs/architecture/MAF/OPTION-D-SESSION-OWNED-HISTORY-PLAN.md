# Option D: Session-Owned Durable State — Implementation Plan

**Date:** September 2026  
**Scope:** TemporalCommunity.Extensions.Agents v0.3+  
**Status:** Production-Ready Design

---

## Overview

Option D relocates agent history persistence from agent-wide dictionaries (current) to session-owned storage within `TemporalAgentSession`. This eliminates multi-session conflicts, makes session boundaries explicit, and aligns with MAF's session contract. The customer workflow explicitly carries sessions across continue-as-new boundaries—no magic, full transparency.

**Key Design Principles**
- Session ID (not agent name) is the isolation key
- History is internal to session, source-gen serializable
- No invisible magic; customer workflows own carry-forward
- Deterministic replay guaranteed via session-owned state
- Backwards compatible with old sessions (migration path)

---

## 1. Changes to TemporalAgentSession

### 1.1 Current State (v0.3)

File: `/src/TemporalCommunity.Extensions.Agents/Session/TemporalAgentSession.cs`

```csharp
public sealed class TemporalAgentSession : AgentSession
{
    public TemporalAgentSession(TemporalAgentSessionId sessionId)
    {
        this.SessionId = sessionId;
    }

    [JsonConstructor]
    internal TemporalAgentSession(TemporalAgentSessionId sessionId, AgentSessionStateBag stateBag) 
        : base(stateBag)
    {
        this.SessionId = sessionId;
    }

    [JsonInclude]
    [JsonPropertyName("sessionId")]
    public TemporalAgentSessionId SessionId { get; }

    internal JsonElement Serialize(JsonSerializerOptions? jsonSerializerOptions = null) { ... }
    internal static TemporalAgentSession Deserialize(JsonElement serializedSession, ...) { ... }
    // ... rest omitted
}
```

**Issues:**
- No history storage → `TemporalAIAgent` maintains `_history` field
- Multi-session scenarios cause cross-session contamination
- Session serialization doesn't round-trip history

### 1.2 New Structure

Add two internal fields to store serialized history entries:

```csharp
public sealed class TemporalAgentSession : AgentSession
{
    // Serialized history entries (request/response pairs + markers).
    // Populated by TemporalAIAgent.RunCore and restored via Deserialize.
    // Internal: customers see history via public accessor methods only.
    internal IReadOnlyList<DurableSessionEntry> History { get; private set; } = [];

    // Session-owned metadata for history compaction and diagnostics.
    // Tracks the last compaction marker and entry count for size guards.
    internal SessionHistoryMetadata? HistoryMetadata { get; set; }

    public TemporalAgentSession(TemporalAgentSessionId sessionId)
    {
        this.SessionId = sessionId;
    }

    [JsonConstructor]
    internal TemporalAgentSession(
        TemporalAgentSessionId sessionId,
        AgentSessionStateBag stateBag,
        IReadOnlyList<DurableSessionEntry>? history = null,
        SessionHistoryMetadata? historyMetadata = null) 
        : base(stateBag)
    {
        this.SessionId = sessionId;
        this.History = history ?? [];
        this.HistoryMetadata = historyMetadata;
    }

    [JsonInclude]
    [JsonPropertyName("sessionId")]
    public TemporalAgentSessionId SessionId { get; }

    [JsonInclude]
    [JsonPropertyName("history")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    internal IReadOnlyList<DurableSessionEntry>? SerializedHistory
    {
        get => this.History.Count > 0 ? this.History : null;
        init => this.History = value ?? [];
    }

    [JsonInclude]
    [JsonPropertyName("historyMetadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    internal SessionHistoryMetadata? HistoryMetadata { get; set; }

    // ─────────────── Public API for history access ──────────────

    /// <summary>
    /// Returns a read-only snapshot of the session's conversation history.
    /// Does not include implementation details (e.g., compaction markers).
    /// </summary>
    public IReadOnlyList<DurableSessionEntry> GetHistory() => this.History;

    /// <summary>
    /// Returns the count of history entries (for diagnostics).
    /// </summary>
    public int HistoryEntryCount => this.History.Count;

    // ─────────────── Internal mutation API ──────────────

    /// <summary>
    /// Appends an entry to the session's history. Called by TemporalAIAgent.RunCore
    /// after each agent turn. Not exposed publicly (internal).
    /// </summary>
    internal void AppendHistoryEntry(DurableSessionEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var mutableHistory = new List<DurableSessionEntry>(this.History) { entry };
        this.History = mutableHistory.AsReadOnly();
    }

    /// <summary>
    /// Replaces the session's history (for compaction or migration scenarios).
    /// Called by TemporalAIAgent when applying history reduction.
    /// </summary>
    internal void SetHistory(IReadOnlyList<DurableSessionEntry> newHistory)
    {
        ArgumentNullException.ThrowIfNull(newHistory);
        this.History = newHistory;
    }

    internal JsonElement Serialize(JsonSerializerOptions? jsonSerializerOptions = null)
    {
        var opts = jsonSerializerOptions ?? TemporalAgentJsonUtilities.DefaultOptions;
        return JsonSerializer.SerializeToElement(this, opts.GetTypeInfo(typeof(TemporalAgentSession)));
    }

    internal static TemporalAgentSession Deserialize(JsonElement serializedSession, JsonSerializerOptions? jsonSerializerOptions = null)
    {
        if (!serializedSession.TryGetProperty("sessionId", out JsonElement sessionIdElement) ||
            sessionIdElement.ValueKind != JsonValueKind.String)
        {
            throw new JsonException("Invalid or missing sessionId property.");
        }

        string sessionIdString = sessionIdElement.GetString() ?? throw new JsonException("sessionId property is null.");
        TemporalAgentSessionId sessionId = TemporalAgentSessionId.Parse(sessionIdString);

        AgentSessionStateBag stateBag = serializedSession.TryGetProperty("stateBag", out JsonElement stateBagElement)
            ? AgentSessionStateBag.Deserialize(stateBagElement)
            : new AgentSessionStateBag();

        // NEW: Restore history entries from serialized form.
        IReadOnlyList<DurableSessionEntry>? history = null;
        if (serializedSession.TryGetProperty("history", out JsonElement historyElement) &&
            historyElement.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            history = JsonSerializer.Deserialize<IReadOnlyList<DurableSessionEntry>>(
                historyElement,
                jsonSerializerOptions ?? TemporalAgentJsonUtilities.DefaultOptions)
                ?? [];
        }

        // NEW: Restore history metadata.
        SessionHistoryMetadata? historyMetadata = null;
        if (serializedSession.TryGetProperty("historyMetadata", out JsonElement metadataElement) &&
            metadataElement.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            historyMetadata = JsonSerializer.Deserialize<SessionHistoryMetadata>(
                metadataElement,
                jsonSerializerOptions ?? TemporalAgentJsonUtilities.DefaultOptions);
        }

        return new TemporalAgentSession(sessionId, stateBag, history, historyMetadata);
    }

    /// <inheritdoc/>
    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        if (serviceType == typeof(TemporalAgentSessionId))
        {
            return this.SessionId;
        }

        return base.GetService(serviceType, serviceKey);
    }

    /// <inheritdoc/>
    public override string ToString() => this.SessionId.WorkflowId;
}
```

### 1.3 New Type: SessionHistoryMetadata

File: `/src/TemporalCommunity.Extensions.Agents/Session/SessionHistoryMetadata.cs` (new)

```csharp
using System.Text.Json.Serialization;
using TemporalCommunity.Extensions.AI.Session;

namespace TemporalCommunity.Extensions.Agents.Session;

/// <summary>
/// Internal metadata tracking session history state (entry count, last compaction, etc.).
/// Carried forward across continue-as-new to guide history reduction decisions.
/// </summary>
internal sealed class SessionHistoryMetadata
{
    /// <summary>Gets the timestamp of the last compaction marker entry.</summary>
    [JsonPropertyName("lastCompactionTime")]
    public DateTimeOffset? LastCompactionTime { get; init; }

    /// <summary>Gets the correlation ID of the compaction request.</summary>
    [JsonPropertyName("lastCompactionCorrelationId")]
    public string? LastCompactionCorrelationId { get; init; }

    /// <summary>
    /// Gets the total entry count at the time of last compaction.
    /// Used to detect when compaction threshold is reached again.
    /// </summary>
    [JsonPropertyName("entriesAtLastCompaction")]
    public int? EntriesAtLastCompaction { get; init; }
}
```

### 1.4 Source-Gen Registration

Update `AgentSessionJsonContext`:

```csharp
[JsonSourceGenerationOptions(WriteIndented = false)]
// ... existing registrations ...
[JsonSerializable(typeof(SessionHistoryMetadata))]
[JsonSerializable(typeof(IReadOnlyList<DurableSessionEntry>))]
internal sealed partial class AgentSessionJsonContext : JsonSerializerContext;
```

---

## 2. Changes to TemporalAIAgent

File: `/src/TemporalCommunity.Extensions.Agents/TemporalAIAgent.cs`

### 2.1 Remove Instance Fields

**Delete:**
```csharp
private readonly List<DurableSessionEntry> _history = [];  // NOW IN SESSION
```

**Keep:**
```csharp
private readonly string _agentName;
private readonly ActivityOptions _activityOptions;
private int _requestCount;
private JsonElement? _currentStateBag;
private bool _settingsResolved;
// ... tool/interceptor cached configs
```

### 2.2 Update `RunCoreAsync`

Modify the key history-recording points:

**Before (lines 143-144):**
```csharp
_history.Add(AgentSessionRequest.FromRunRequest(request, Workflow.UtcNow));
_requestCount++;
```

**After:**
```csharp
session.AppendHistoryEntry(AgentSessionRequest.FromRunRequest(request, Workflow.UtcNow));
_requestCount++;
```

**Before (lines 154-158):**
```csharp
var accumulated = new List<ChatMessage>();
foreach (var entry in _history)
{
    foreach (var m in entry.Messages)
        accumulated.Add(m);
}
```

**After:**
```csharp
var accumulated = new List<ChatMessage>();
var sessionHistory = session is TemporalAgentSession ts 
    ? ts.History 
    : [];
foreach (var entry in sessionHistory)
{
    foreach (var m in entry.Messages)
        accumulated.Add(m);
}
```

**Before (lines 247-248):**
```csharp
_history.Add(AgentSessionResponse.FromAgentResponse(
    request.CorrelationId!, response, Workflow.UtcNow));
```

**After:**
```csharp
session.AppendHistoryEntry(AgentSessionResponse.FromAgentResponse(
    request.CorrelationId!, response, Workflow.UtcNow));
```

**Before (lines 446-447):**
```csharp
_history.Add(AgentSessionResponse.FromAgentResponse(
    request.CorrelationId!, iterCapResponse, Workflow.UtcNow));
```

**After:**
```csharp
session.AppendHistoryEntry(AgentSessionResponse.FromAgentResponse(
    request.CorrelationId!, iterCapResponse, Workflow.UtcNow));
```

### 2.3 Validate Session at Entry

At the start of `RunCoreAsync`, after null-coalescing session:

```csharp
protected override async Task<AgentResponse> RunCoreAsync(...)
{
    if (!Workflow.InWorkflow) 
        throw new InvalidOperationException(
            "TemporalAIAgent must be used inside a Temporal workflow. Use TemporalAIAgentProxy for external-context invocation.");

    session ??= await CreateSessionAsync(cancellationToken).ConfigureAwait(true);

    // NEW: Validate session type and agent name match.
    if (session is not TemporalAgentSession temporalSession)
    {
        throw new InvalidOperationException(
            $"TemporalAIAgent requires a TemporalAgentSession, but got '{session.GetType().Name}'.");
    }

    // Validate agent name matches to catch cross-agent reuse.
    if (!string.Equals(temporalSession.SessionId.AgentName, _agentName, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException(
            $"Session agent name '{temporalSession.SessionId.AgentName}' does not match " +
            $"agent name '{_agentName}'. Sessions must be created for and used with the same agent.");
    }

    // ─────────────── continue with rest of RunCoreAsync ──────────────
}
```

---

## Implementation Sequence

| Phase | Task | Days | Deliverable |
|-------|------|------|-------------|
| 1 | Session history storage + types | 2-3 | SessionHistoryMetadata, TemporalAgentSession changes |
| 2 | Migrate TemporalAIAgent to session | 2-3 | Remove `_history`, add validation |
| 3 | Test suite (unit + integration) | 2-3 | 50+ tests (isolation, replay, backwards compat) |
| 4 | Docs + migration guide | 1-2 | CLAUDE.md, usage.md, migration guide |
| 5 | Release & publish | 0.5-1 | v0.4.0 tag, NuGet publish |
| **Total** | | **8-12 days** | **2-3 weeks (1 engineer)** |

---

See the full plan at `/docs/architecture/MAF/OPTION-D-SESSION-OWNED-HISTORY-PLAN.md` for complete details including testing strategy, risk assessment, success criteria, and migration examples.
