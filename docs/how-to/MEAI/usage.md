# Getting Started with Temporalio.Extensions.AI

> **TODO** — This guide is a stub. See `docs/todo-meai.md` for the planned content.

Topics to cover:
- Registration: `DurableAIDataConverter`, `AddChatClient`, `AddDurableAI`, `AddDurableTools`
- `AddChatClient` vs `AddKeyedChatClient` and the unkeyed alias requirement
- `DurableChatSessionClient.ChatAsync` — basic usage
- `GetHistoryAsync` — retrieving persisted history
- Per-request overrides: `WithActivityTimeout`, `WithMaxRetryAttempts`, `WithHeartbeatTimeout`
- `UseDurableReduction` — sliding context window
- For tool functions specifically, see `tool-functions.md` — covers both execution models
