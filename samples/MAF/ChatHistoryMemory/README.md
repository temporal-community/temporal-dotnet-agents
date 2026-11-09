# Chat-history memory

This sample uses MAF's `ChatHistoryMemoryProvider` through the approved
`TemporalAgentOptions.UseChatHistoryMemory` API. It uses a process-local vector store and a small,
deterministic token-hash embedding generator so the example does not need a separate embedding
service. These embeddings are for demonstration only; use a production embedding model and durable
vector store for real workloads.

By default, the provider retrieves relevant messages automatically before model invocation
(`BeforeAIInvoke`). To demonstrate model-directed recall instead, run with `--on-demand`; this
registers MAF's `Search` function and dispatches its invocation as a Temporal tool activity. The
scope initializer is called once for a session, in a dedicated activity, and binds storage and
recall to the session and sample application. It must be safe to repeat because the activity can
retry before its result is recorded. Authenticate and authorize each session request before
creating or joining its workflow; do not treat a caller-supplied session ID as identity or
authorization. Authorization revocation is not rechecked during an in-flight turn.

The default scope, when no initializer is supplied, binds both storage and recall to the registered
agent and current Temporal session. If collections are shared across applications or Temporal
namespaces, provide a trusted initializer that adds an application or user boundary. Cross-session
recall is disabled by default; enabling it requires explicit `allowCrossSessionRecall: true` and a
bounded application or user scope.

## Run

From the repository root, start a Temporal service in a separate terminal with the required
search attributes:

```sh
temporal server start-dev --namespace default \
  --search-attribute AgentName=Keyword \
  --search-attribute SessionCreatedAt=Datetime \
  --search-attribute TurnCount=Int
```

Configure credentials, then select Development so Generic Host loads the project's user secrets.
The copied `appsettings.json` supplies the OpenAI endpoint independently of the working directory:

```sh
dotnet user-secrets set "OPENAI_API_KEY" "..." --project samples/MAF/ChatHistoryMemory
dotnet user-secrets set "OPENAI_API_BASE_URL" "https://api.openai.com/v1" --project samples/MAF/ChatHistoryMemory
DOTNET_ENVIRONMENT=Development dotnet run --project samples/MAF/ChatHistoryMemory/ChatHistoryMemory.csproj
```

For on-demand function-calling recall:

```sh
DOTNET_ENVIRONMENT=Development dotnet run --project samples/MAF/ChatHistoryMemory/ChatHistoryMemory.csproj -- --on-demand
```

In the default mode, the provider searches before each model invocation. In on-demand mode, the
model can call `Search` when it needs earlier conversation details; the function is routed through
the durable tool activity path and uses the initialized session scope.

The vector store is in memory and is lost when the process exits. For a useful demonstration,
provide a durable fact in the first turn and ask about it in the second.
