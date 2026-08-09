# DurableChat: multi-turn durable conversations

This sample runs multi-turn `DurableChatSessionClient` conversations and a managed durable-tool
loop. A conversation ID maps to a Temporal workflow; each turn is a workflow update and the
workflow retains conversation history across worker restarts.

`AddDurableTools` is the only tool registration used by a durable chat session. It supplies both
the model-visible schema and the worker-side function implementation. The sample intentionally
does not call `UseFunctionInvocation()` and never passes `ChatOptions.Tools`.

```
DurableChatSessionClient
  -> DurableChatWorkflow
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

Open `http://localhost:8233` to inspect `GetChatStep` model activities and individual
`InvokeFunction` tool activities. Configure per-tool timeouts and retry behavior through the
`AddDurableTools` callback; use `NoRetry()` for an unsafe non-idempotent operation.

The first multi-turn request also selects the built-in `"tags"` decorator with
`WithChatClientFactoryKey("tags")` and supplies two `WithChatClientTag(...)` values. The decorator
sees those values inside the activity; the OpenAI provider receives the ordinary chat options with
all Temporal-private keys removed.
