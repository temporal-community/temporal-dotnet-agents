# Cross-Library Integration

`TemporalCommunity.Extensions.AI` is the lower-level durable `IChatClient` library.
`TemporalCommunity.Extensions.Agents` depends on it and adds Microsoft Agent Framework support.
The dependency direction is one-way: installing the AI package does not bring in
`Microsoft.Agents.AI`.

```
TemporalCommunity.Extensions.Agents
        → TemporalCommunity.Extensions.AI
```

## Shared contracts

The Agents library reuses these AI-library contracts rather than duplicating their wire format or
workflow mechanics:

- `DurableChatWorkflowBase<TOutput>` and `DurableChatWorkflowInput` for the common session loop.
- `DurableSessionEntry`, `DurableSessionRequest`, and `DurableSessionResponse` for persisted
  conversation history. `AgentSessionRequest` and `AgentSessionResponse` derive from the request
  and response types.
- `DurableApprovalRequest`, `DurableApprovalDecision`, `DurableApprovalResolutionResult`, and
  the inherited approval update/query handlers. MAF's optional reusable session grants remain a
  separate administrative capability so the AI package stays scope-free.
- `DurableAIDataConverter` for MEAI message and content serialization.
- Shared tool-interceptor result and decision types.

The Agents package also uses selected AI internals through its friend-assembly relationship for
retry and error classification. That is an implementation detail, not a consumer extension point.

## Separate execution models

The packages do not share a model activity:

- A managed AI session has `DurableChatWorkflow` call `GetChatStep`; it owns the model/tool loop
  and schedules each registered function as `InvokeFunction`.
- An agent session has `AgentWorkflow` call agent activities. It manages MAF session state,
  `AIContextProvider` hooks, and agent-specific tool dispatch.

Do not combine `AddDurableAI()` with `AddTemporalAgents()` to make one session implementation.
Choose the library whose public entry point matches the application: `IChatClient` and
`DurableChatSessionClient` for MEAI, or `AIAgent` and `ITemporalAgentClient` for MAF.

## Converter registration

When clients are registered through the supported hosting extensions, each library configures the
AI data converter. When connecting `ITemporalClient` manually, set
`DataConverter = DurableAIDataConverter.Instance` yourself so persisted MEAI content retains its
polymorphic type information.
