# DurableChat: multi-turn durable conversations

This sample runs multi-turn `DurableChatSessionClient` conversations and a managed durable-tool
loop. A conversation ID maps to a Temporal workflow; each turn is a workflow update and the
workflow retains conversation history across worker restarts.

The program demonstrates the concise co-hosted `AddDurableAI()` registration. Separate client and
worker deployments use `AddDurableAIClient(taskQueue)` and `AddDurableAIWorker()` respectively;
see [split client and worker hosting](../../../docs/how-to/MEAI/usage.md#split-client-and-worker-hosting).

This sample demonstrates the **current** API, not the
[durable-chat redesign target](../../../docs/how-to/MEAI/usage.md#durable-chat-redesign-target-contracts).
The string overload accepts one user message directly, while the message overload accepts MEAI
`ChatMessage` history. Its application conversation ID is the durable identity; provider
`ChatOptions.ConversationId`, `RawRepresentationFactory`, and `ContinuationToken` are rejected
because they cannot safely resume across durable activity boundaries. Responses retain usage,
finish reason, provider response/model IDs, and serializable provider metadata. Its correlation ID
is not a deduplication key. The first demo turn supplies a caller-known request ID separately from
correlation metadata; persist that ID before submission when the caller must retry an ambiguous
RPC. Phase 2.1 records the ID and canonical input fingerprint. Phase 2.2 lets a matching ID and
fingerprint join an active turn or return its terminal outcome; different IDs are `Busy`, and
changed input under the same ID is `Conflict`. The bounded terminal ledger (128 outcomes / 1 MiB by
default) and the session epoch are carried through Continue-as-New. An evicted or oversized outcome
is outside the deduplication window, so a later retry can execute again; the ledger does not make
external tool effects exactly-once. If `SendAsync` fails after generating a request ID, its
`DurableChatRequestException.RequestId` exposes that ID; callers needing recovery across process
failure should persist their own ID before submission. Phase 0 documented pinned reducer and replay
prerequisites. Phase 1.0 removed the durable embedding wrapper; safe interrupted-turn recovery and
independent context/history budgets are still pending.

Two `AddDurableToolset` registrations provide weather and service-status capabilities.
`DefaultToolsetIds` composes them in a stable order as the stock workflow's worker-owned baseline.
The client sends only its conversation ID, messages, and chat options; it does not construct or
serialize tool schemas. The workflow resolves the baseline once, before the first model call, and
Temporal records that versioned declaration-and-policy manifest for replay and Continue-as-New.
The session input also captures the configured chat-client profile key; managed sessions cannot
switch profiles between turns. Provider clients, credentials, and executable tool services remain
in worker DI and are resolved only when their activities execute. The recorded schema and policy
do not freeze the implementation registered behind a stable worker-side activation key. The sample intentionally
does not call `UseFunctionInvocation()` and never passes `ChatOptions.Tools`.

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

These samples use the Generic Host, which loads user secrets only in the Development
environment. Set `DOTNET_ENVIRONMENT` for the run so the credentials configured above are loaded.

```bash
DOTNET_ENVIRONMENT=Development dotnet run --project samples/MEAI/DurableChat/DurableChat.csproj
```

PowerShell:

```powershell
$env:DOTNET_ENVIRONMENT = "Development"
dotnet run --project samples/MEAI/DurableChat/DurableChat.csproj
```

Open `http://localhost:8233` to inspect the one-time resolver, `GetChatStep` model activities, and individual
`InvokeFunction` tool activities. Configure per-tool timeouts and retry behavior through the
toolset member callback; use `NoRetry()` for an unsafe non-idempotent operation.

The selected toolset manifest and profile key are carried through Continue-as-New, so changing
toolset selection or client profile configuration affects newly created sessions only. Provider
and tool implementations behind the recorded keys are still resolved at activity time, so
deployments should preserve their expected behavior.

Worker startup validates the configured default chat-client route, default toolset IDs, and
cross-toolset function-name collisions. The chat-client check confirms the profile is registered
without constructing the provider. If the model nevertheless returns an unknown or non-enabled
function name, the workflow supplies a safe blocked result and schedules no interceptor, approval,
or tool activity.

The first multi-turn request also supplies two `WithChatClientTag(...)` values. The durable model
activity applies them directly to its current span; the OpenAI provider receives the ordinary chat
options with all Temporal-private keys removed. No keyed wrapper registration is required.
