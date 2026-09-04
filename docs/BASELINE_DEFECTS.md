# Durable history migration note

## Request/response boundary preservation

Before this remediation, the default history fallback used a raw `TakeLast(MaxEntryCount / 2)`
suffix whenever a durable chat session continued as new without a `HistoryReducerKey`. A completed
chat turn is stored as adjacent `DurableSessionRequest` and `DurableSessionResponse` entries with
the same correlation ID. If the selected suffix size was odd, the retained suffix could begin with
the response half of a turn and omit its request.

The affected configuration is the no-reducer fallback with completed request/response history and
an odd `floor(MaxEntryCount / 2)` target. Examples include `MaxEntryCount` 1, 2, 3, 6, 7, 10, and
11. A configured keyed reducer is not changed by this fix; it owns its own reduction semantics.

The revised fallback omits that leading response when it can prove the immediately preceding,
omitted entry is its matching request. The resulting carried history remains below the continue-as-
new threshold and begins only at a complete request/response boundary. Existing persisted runs
cannot have omitted requests reconstructed by an upgrade. Before rollout, operators should inspect
sessions that use the no-reducer fallback and depend on complete conversation context; configure a
deterministic keyed reducer where the product needs a different retention policy.

## Converter validation

The worker now validates the DI `ITemporalClient.Options.DataConverter` before processing durable
AI work. This closes the manual-client path that previously allowed an incompatible converter to
silently lose polymorphic AI content, durable session-entry type information, or embedding wrapper
metadata. Custom converters remain supported when their payload converter preserves those durable
contracts. Payload codecs remain caller-owned and must be deployed to every client, worker,
replayer, and operational reader that accesses encoded histories.

## Workflow streaming

`DurableChatClient.GetStreamingResponseAsync` no longer turns a workflow streaming request into a
buffered synthetic response. Its async iterator throws `NotSupportedException` when advanced in a
workflow because Temporal activities return one serialized result. Workflow code must use
`GetResponseAsync`; external and activity callers retain real provider streaming.
