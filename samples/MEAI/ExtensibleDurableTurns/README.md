# Extensible durable turns

This domain-neutral sample demonstrates `DurableToolWorkflowBase<TRequestData, TTurnState>`:

- one `GetChatStep` activity per model iteration;
- one `InvokeFunction` activity per real tool call;
- an unchanged ordinary .NET function registered with `AddDurableTools`;
- two invocation-scoped functions that receive request data and current turn state without adding
  either value to their model schema;
- activity-attempt DI scopes containing a scoped authorization service and an
  `IHttpClientFactory`-created client, disposed after every attempt;
- sequential state replacement, where the second tool observes the first tool's completed state;
- a workflow-owned approval wait before the first stateful tool is dispatched;
- an existing MEAI `DelegatingAIFunction` that checks an authoritative service and ignores a forged
  authorization-like state flag;
- a typed `DurableTurnResult<ProcessingState>`;
- activity idempotency keys stored in application-owned receipts; and
- an injected post-write activity failure whose retry is deduplicated by a fake external sink.

Start a local Temporal development server, then run:

```bash
dotnet run --project samples/MEAI/ExtensibleDurableTurns/ExtensibleDurableTurns.csproj
```

The scripted chat client makes the sample deterministic and requires no model API key. The sample
polls the workflow's approval query and auto-approves the first stateful tool. Inspect the workflow
in Temporal Web to see the model activity, the durable approval wait, and three separate tool
activities.
Each stateful tool intentionally fails after its first external write. Its retry receives the same
activity-scoped key, so the sink records one write. The retry receives a new DI scope even though
its Temporal activity identity remains stable. DI-scoped objects are ordinary process-local
dependencies; they are not durable turn state and are never serialized into workflow history.

The sample creates its Temporal client manually, so it sets `DurableAIDataConverter.Instance`
explicitly. A client registered through `AddTemporalClient` is configured automatically by
`AddDurableChatWorkflowInputFactory` unless the application supplied a custom converter.

For split processes, an implementation-only worker can avoid a duplicate DI client:

```csharp
services
    .AddHostedTemporalWorker(address, temporalNamespace, taskQueue)
    .AddDurableAI(options => options.RegisterDefaultWorkflow = false)
    .AddWorkflow<ContextualTurnWorkflow>();
```

The workflow-starting process still registers or constructs the client it uses to start the typed
workflow. The executable sample remains a single-process demonstration.

`RequestData` and `TTurnState` are application-supplied history payloads, not authorization proof.
The decorator uses them only to locate subject/resource and obtains the current decision from the
authoritative service immediately before each stateful function.

The `Turn` Update calls `RunDurableTurnAsync` exactly once. The specialized base rejects a second
managed turn from the same Update before it can schedule another model or tool activity. Use a new
Update ID for each turn; Continue-as-New starts a new run and resets the run-local guard.

For application-owned single-activity turns instead, see
[`samples/MEAI/CustomWorkflow`](../CustomWorkflow/README.md). That sample uses the lower-level
`DurableChatWorkflowBase<TOutput>` and intentionally owns its own orchestration.
