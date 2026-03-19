# Testing with Temporalio.Extensions.AI

> **TODO** — This guide is a stub. See `docs/todo-meai.md` for the planned content.

Topics to cover:
- When to use unit tests vs integration tests for durable AI code
- `IDurableChatSessionClient` — stubbing the session client in application-layer tests
- `WorkflowEnvironment.StartLocalAsync()` — the integration test fixture pattern
- `TestChatClient` — the `IChatClient` stub used in this library's own tests
- What NOT to test: avoid mocking `ITemporalClient` to assert SDK calls were made
