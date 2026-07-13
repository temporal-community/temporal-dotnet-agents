# Durable chat pipeline architecture

`TemporalCommunity.Extensions.AI` maps a conversation ID to one long-lived Temporal workflow.
`DurableChatSessionClient.SendAsync` delivers each turn through a workflow update, so the
conversation state and completed activity results survive worker restarts.

## Managed tool loop

```
caller
  -> DurableChatSessionClient.SendAsync
  -> DurableChatWorkflow [workflow update]
  -> GetChatStep [LLM activity]
  -> InvokeFunction x N [tool activities]
  -> GetChatStep [next LLM activity]
  -> final ChatResponse
```

`GetChatStep` resolves the worker's `DurableFunctionRegistry` and passes its `AIFunction` schemas
to the configured `IChatClient`. When the model returns `FunctionCallContent`, the workflow starts
one `InvokeFunction` activity per call and feeds the resulting `FunctionResultContent` messages
back to the next model step. Tool activities have their own timeout and retry options.

This is the only managed-session function loop. The public session API rejects
`ChatOptions.Tools`; registry registration is the source of truth for both the schema and the
worker implementation. A chat pipeline used by a durable session must not include
`UseFunctionInvocation()`, which would execute functions outside the workflow-owned activity loop.

## Components

| Component | Responsibility |
| --- | --- |
| `DurableChatSessionClient` | Atomically starts/reuses the workflow and sends the chat update. |
| `DurableChatWorkflow` | Stores session history and drives model/tool iterations. |
| `DurableChatActivities.GetChatStepAsync` | Resolves the `IChatClient` and registry-backed tool schemas, then makes one model call. |
| `DurableFunctionActivities.InvokeFunctionAsync` | Resolves and invokes one registered function as an activity. |
| `DurableFunctionRegistry` | Worker-local, startup-built map of stable function names to `AIFunction` implementations. |
| `DurableAIDataConverter` | Preserves MEAI polymorphic content such as function call and result messages in Temporal payloads. |

## Boundary and deployment rules

- The client and all workers need `DurableAIDataConverter.Instance`.
- Every worker on the task queue must register the same durable tool names and compatible schemas.
- Do not deploy this breaking prerelease change over in-flight executions created by the retired
  inline-loop behavior. Drain or retire those executions before deploying the new worker set.
- `AsDurable()` is a separate direct-function primitive for custom workflows. It does not alter the
  managed-session contract.
