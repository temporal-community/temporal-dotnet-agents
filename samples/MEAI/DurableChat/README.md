# DurableChat: multi-turn durable conversations

This sample runs multi-turn `DurableChatSessionClient` conversations and a managed durable-tool
loop. A conversation ID maps to a Temporal workflow; each turn is a workflow update and the
workflow retains conversation history across worker restarts.

`AddDurableTools` creates the worker's implicit default toolset. The client sends only its
conversation ID, messages, and chat options; it does not construct or serialize tool schemas. The
workflow resolves the worker-owned toolset once, before the first model call, and Temporal records
that versioned manifest for replay and Continue-as-New. The sample intentionally does not call
`UseFunctionInvocation()` and never passes `ChatOptions.Tools`.

```
DurableChatSessionClient
  -> DurableChatWorkflow
  -> ResolveDurableToolsets activity (once per session)
  -> GetChatStep activity
  -> InvokeFunction activity (when requested)
  -> GetChatStep activity
```

## Run

Start Temporal Service 1.31.0 or newer. For local development:

```bash
temporal server start-dev
```

Configure the OpenAI-compatible endpoint and key:

```bash
dotnet user-secrets set "OPENAI_API_KEY" "sk-..." --project samples/MEAI/DurableChat
dotnet user-secrets set "OPENAI_API_BASE_URL" "https://api.openai.com/v1" --project samples/MEAI/DurableChat
```

Then run:

```bash
dotnet run --project samples/MEAI/DurableChat/DurableChat.csproj
```

Open `http://localhost:8233` to inspect the one-time resolver, `GetChatStep` model activities, and individual
`InvokeFunction` tool activities. Configure per-tool timeouts and retry behavior through the
`AddDurableTools` callback; use `NoRetry()` for an unsafe non-idempotent operation.

Changing the worker registration affects new sessions only. A running session continues to use its
recorded manifest, including after Continue-as-New.

The first multi-turn request also supplies two `WithChatClientTag(...)` values. The durable model
activity applies them directly to its current span; the OpenAI provider receives the ordinary chat
options with all Temporal-private keys removed. No keyed wrapper registration is required.
