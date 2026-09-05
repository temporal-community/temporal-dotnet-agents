# Using TemporalCommunity.Extensions.AI

`TemporalCommunity.Extensions.AI` makes a MEAI `IChatClient` (and `IEmbeddingGenerator`) durable
with Temporal. It offers a few genuinely different ways to do this, and this doc is the deeper
how-to reference for all of them:

- **Built-in managed session** — a stock Temporal workflow owns an entire multi-turn conversation
  (history, tool dispatch, continue-as-new). You call `SendAsync`/`GetHistoryAsync`.
- **Custom workflow with the package's session machinery** — you own the `[Workflow]` class, but
  subclass `DurableChatWorkflowBase<TOutput>` or `DurableToolWorkflowBase<TRequestData, TTurnState>`
  to keep the built-in history, HITL, and continue-as-new plumbing while controlling your own turn
  activity or returning domain-specific per-turn output. This is the recommended starting point for
  "I own a custom workflow and want durable LLM calls in it" — see
  [custom workflow output](custom-workflow-output.md) and the runnable
  `samples/MEAI/CustomWorkflow` sample.
- **Custom workflow, no session machinery** — you own the workflow *and* want none of the session
  machinery above. Write a small hand-rolled Activity (constructor-injected `IChatClient`, one
  `[Activity]` method) for the LLM call — no durable-adapter ceremony required. For a standalone
  tool call inside that same workflow, `AIFunction.AsDurable()` remains available as the one
  low-level primitive that makes a single tool invocation durable.

## Built-in managed session

Register a `ChatClient`, a durable tool, and the worker in one place. This is close to the
package README's quick start but shows a couple of production-shaped defaults (bounded activity
timeout, session TTL, tool-call cap, and a per-tool override) together in one block:

```csharp
builder.Services.AddTemporalClient("localhost:7233", "default");
builder.Services.AddChatClient(innerChatClient);

var weatherTool = AIFunctionFactory.Create(
    (string city) => weather.GetCurrent(city),
    name: "get_weather",
    description: "Gets current weather for a city.");

builder.Services
    .AddHostedTemporalWorker("durable-chat")
    .AddDurableAI(options =>
    {
        options.ActivityTimeout = TimeSpan.FromMinutes(5);
        options.SessionTimeToLive = TimeSpan.FromHours(24);
        options.MaxToolCallsPerTurn = 10;
    })
    .AddDurableTool(weatherTool, tool => tool.WithTimeout(TimeSpan.FromSeconds(30)));
```

`AddTemporalClient` auto-wires `DurableAIDataConverter.Instance` so durable AI payloads round-trip
correctly. `AddDurableAI()` validates the registered client can preserve those payloads before the
worker starts — a manually connected client (`TemporalClient.ConnectAsync`) with an incompatible
converter fails fast at startup instead of corrupting data silently later; see
[worker setup](#worker-setup-with-a-manual-client) below if you connect manually.

- ❌ Don't call `UseFunctionInvocation()` on the `IChatClient` used by a managed session.
- ❌ Don't pass `ChatOptions.Tools` to `SendAsync`.

The workflow owns tool selection and dispatch, and both are rejected. For the full
tool-registration surface (`AddDurableTools`, `AddDurableToolset`, `AddDurableToolFactory`) see
[tool functions](tool-functions.md) and the
[managed-session tool rules](managed-session-tool-rules.md).

### Worker setup with a manual client

If you connect the Temporal client manually instead of using `AddTemporalClient`, set the durable
converter explicitly:

```csharp
var client = await TemporalClient.ConnectAsync(new TemporalClientConnectOptions("localhost:7233")
{
    DataConverter = DurableAIDataConverter.Instance,
});

builder.Services.AddSingleton<ITemporalClient>(client);
builder.Services.AddChatClient(innerChatClient);

builder.Services
    .AddHostedTemporalWorker("durable-chat")
    .AddDurableAI(options => options.SessionTimeToLive = TimeSpan.FromHours(24));
```

`DurableAIDataConverter.Instance` remains uncompressed. For large histories, an optional bounded
gzip codec is available — see [payload codecs](payload-codecs.md).

### Sharing a worker with non-AI workflows

Temporal configures a data converter per client or worker, not per workflow. Calling
`AddDurableAI()` therefore applies `DurableAIDataConverter.Instance` to every workflow and activity
served by that worker's client, including ordinary application workflows registered on the same
worker.

Every client that starts, signals, queries, or reads results from those workflows must use a
compatible converter. This matters most if you connect a client manually instead of using
`AddTemporalClient` (which applies the converter automatically), because DI option configuration
cannot reach a client built outside DI:

```csharp
var options = ClientEnvConfig.LoadClientConnectOptions();
options.DataConverter = DurableAIDataConverter.Instance;
var client = await TemporalClient.ConnectAsync(options);
```

An incompatible client can deserialize a payload into an otherwise valid application object whose
nested members are null or default values; that mismatch need not throw at deserialization time.
The Temporal service does not retain converter identity, so this library cannot diagnose a converter
chosen independently by another process. A custom converter is preserved by the registration APIs,
but it must remain compatible with the MEAI and application payloads used by every participating
client and worker.

For a keyed client, register it with `AddKeyedChatClient` and set
`DurableExecutionOptions.DefaultChatClientKey`.

### Send a turn and read history

```csharp
var sessionClient = services.GetRequiredService<DurableChatSessionClient>();

var response = await sessionClient.SendAsync(
    "customer-42",
    [new ChatMessage(ChatRole.User, "What is the weather in Seattle?")]);

Console.WriteLine(response.Text);

var history = await sessionClient.GetHistoryAsync("customer-42");
```

`SendAsync` returns a `DurableSessionResponse` (per-turn `Usage`, `FinishReason`, and completion
reason included); `GetHistoryAsync` returns the full `IReadOnlyList<DurableSessionEntry>`. Tool
calls are recorded as `TemporalCommunity.Extensions.AI.InvokeFunction` activities. Streaming is not
supported for durable sessions — there is no `GetStreamingResponseAsync` equivalent on
`DurableChatSessionClient`. See [tool functions](tool-functions.md) for the complete tool
registration and dispatch contract, and [HITL patterns](hitl-patterns.md) for pausing a turn on
tool approval.

## Durable tool calls with `AIFunction.AsDurable()`

> **Most applications that own a custom workflow and want durable LLM calls should start with
> [custom workflow output](custom-workflow-output.md) instead.** `DurableChatWorkflowBase<TOutput>`
> and `DurableToolWorkflowBase<TRequestData, TTurnState>` are backed by a full runnable sample
> (`samples/MEAI/CustomWorkflow`) and give you history, HITL, and continue-as-new for free. If you
> deliberately want none of that session machinery — a workflow that needs to make one or two
> individual LLM/tool calls durable and nothing more — write a small hand-rolled Activity for the
> LLM call (constructor-injected `IChatClient`, one `[Activity]` method, no durable-adapter
> ceremony — see `samples/MEAI/CustomWorkflow`'s `ShoppingActivities` for the reference shape) and
> use `AIFunction.AsDurable()` below for the tool call. The full pattern (Activity + `AsDurable()`
> combined) is a runnable sample: `samples/MEAI/DirectAdapters`.

Wrap a known `AIFunction` with `AsDurable()` to make a single tool call durable from workflow code
that already writes its own Activity for the LLM call — independently of the managed session's
tool contract. It passes through unchanged outside a workflow, and dispatches as a Temporal
activity when called from inside one:

```csharp
[Workflow]
public sealed class ResearchWorkflow
{
    [WorkflowRun]
    public async Task<string> RunAsync(ResearchRequest request)
    {
        // Look up a fact with a durable tool call — dispatches to DurableFunctionActivities and
        // resolves "get_current_weather" from the worker's AddDurableTool/AddDurableTools registry.
        // AsDurable() always runs on this workflow's own task queue, so its worker must be the
        // same one registering the tool.
        var weatherTool = AIFunctionFactory.Create(
            (string city) => "unreachable stub — only invoked outside a workflow",
            name: "get_current_weather",
            description: "Returns the current weather conditions for a given city.")
            .AsDurable();

        var weather = await weatherTool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["city"] = request.City }));

        // Feed the tool result into a durable LLM call via a hand-written Activity —
        // ResearchActivities.SummarizeWeatherAsync is constructor-injected with the real,
        // worker-side IChatClient and dispatched as a standard Temporal activity.
        var activityOptions = new ActivityOptions { StartToCloseTimeout = TimeSpan.FromMinutes(2) };
        return await Workflow.ExecuteActivityAsync(
            (ResearchActivities a) => a.SummarizeWeatherAsync(request.City, weather?.ToString() ?? string.Empty),
            activityOptions);
    }
}
```

Register the real provider-side `IChatClient` (constructor-injected into your Activity class),
`AddDurableAI()`, the durable tool, the Activity class (via `AddSingletonActivities<T>`), and
`ResearchWorkflow` itself on the worker — workflow classes receive no application DI services, so
the stub lambda above must never perform provider I/O; only the worker-side registrations are ever
actually invoked. See `samples/MEAI/DirectAdapters` for the complete, runnable wiring.

A few contract points worth knowing up front:

- `AsDurable()` function activities always run on the calling workflow's own task queue,
  regardless of `DurableExecutionOptions.TaskQueue` (which routes the managed session and
  direct chat/embedding adapters instead) — its worker must be the same one that registered the
  tool via `AddDurableTools`/`AddDurableTool`. See
  [the direct-adapter task-queue boundary](../../architecture/MEAI/durable-chat-pipeline.md#direct-adapter-task-queue-boundary)
  for the full routing model.
- A hand-written Activity for the LLM call gives you full control over timeouts, heartbeating
  (for long tool-invocation loops), and retry policy via standard `ActivityOptions` — no
  `ChatOptions` extension-method translation layer to reason about.

## Which pattern should I use?

| | Built-in managed session | Custom workflow, package session machinery | Custom workflow, no session machinery |
|---|---|---|---|
| Who owns the workflow class | The stock `DurableChatWorkflow` | Your own `[Workflow]`, subclassing `DurableChatWorkflowBase<TOutput>` / `DurableToolWorkflowBase<TRequestData, TTurnState>` | Your own `[Workflow]`, from scratch |
| History, continue-as-new, HITL | Handled for you | Inherited from the base class | Not provided — you build it yourself if you need it |
| Entry point | `DurableChatSessionClient.SendAsync` | `WorkflowHandle.ExecuteUpdateAsync` against your own `[WorkflowUpdate]` | `Workflow.ExecuteActivityAsync` against your own hand-written Activity, plus `AIFunction.AsDurable()` for tool calls |
| Sample-backed | Yes (`samples/MEAI/DurableChat`) | Yes (`samples/MEAI/CustomWorkflow`) | Yes (`samples/MEAI/DirectAdapters`) |
| Multiple LLM calls with custom control flow between them | No | Yes — you write `ExecuteTurnAsync` / the tool loop | Yes — that's the point |
| Best for | A standalone multi-turn chat/agent surface where the stock loop's behavior is exactly what you want | Owning a custom workflow but still wanting the package's history/HITL/continue-as-new machinery, plus typed per-turn output or custom turn orchestration | A workflow that needs one or two individual durable LLM/tool calls and nothing else — no session machinery at all |

Concrete signals:

- **Use the managed session** if your unit of work is "a conversation" and you don't need
  orchestration logic around individual LLM calls — the stock workflow's history management, tool
  dispatch, and continue-as-new policy already do what you need.
- **Use `DurableChatWorkflowBase<TOutput>` or `DurableToolWorkflowBase<TRequestData, TTurnState>`**
  — the **primary recommendation for anyone who owns a custom workflow and wants durable LLM
  calls** — if you want the managed session's history/HITL/continue-as-new plumbing but need to
  return domain-specific per-turn output, schedule your own turn activity, or reuse the package's
  typed tool loop. Backed by the runnable `samples/MEAI/CustomWorkflow` sample. See
  [custom workflow output](custom-workflow-output.md).
- **Write a hand-rolled Activity, plus `AIFunction.AsDurable()` for tool calls,** only when you
  deliberately want *none* of the session machinery above — a workflow you're already writing from
  scratch that needs to make one or a few individual LLM/tool calls durable, with no built-in
  history, turn tracking, or HITL support. This is a narrower-scope, lower-level approach than the
  two options above. Backed by the runnable `samples/MEAI/DirectAdapters` sample.

These are separate APIs: registering a custom workflow does not change the managed-session tool
contract, and a managed session's registered tools are not visible to a from-scratch workflow
(which uses `AIFunction.AsDurable()` against the same worker-side registry instead).

For the design reasoning behind recommending pattern 3 over constructing a durable chat/embedding
adapter directly inside workflow code, see
[the direct-adapter anti-pattern record](../../architecture/MEAI/direct-adapter-anti-pattern.md).

## Where to go next

**How-to guides**

- [Tool functions](tool-functions.md) — the three durable tool registration levels
  (`AddDurableTools`/`AddDurableToolset`/`AddDurableToolFactory`) and invocation-scoped tools
- [MCP tools](mcp-tools.md) — registering remote MCP client tools through the same durable surface
- [Managed-session tool rules](managed-session-tool-rules.md) — the tool rules
  specific to managed sessions (thin client, worker-resolved manifest)
- [HITL patterns](hitl-patterns.md) — pausing a tool call for human approval
- [Payload codecs](payload-codecs.md) — optional bounded gzip compression for durable payloads
- [Embeddings](embeddings.md) — the `IEmbeddingGenerator` equivalent of `UseDurableExecution()`
- [Custom workflow output](custom-workflow-output.md) — subclassing `DurableChatWorkflowBase<TOutput>`
  or `DurableToolWorkflowBase<TRequestData, TTurnState>` for structured per-turn output
- [Observability](observability.md) — the `ActivitySource`/`Meter`/`ILogger` surface
- [Testing](testing.md) — unit-testing durable AI code vs. integration-testing against a real server

**Architecture & concepts**

- [Durable chat pipeline](../../architecture/MEAI/durable-chat-pipeline.md) — the managed-session
  pipeline and the direct-adapter task-queue routing model
- [Durable toolsets](../../architecture/MEAI/durable-toolsets.md) — worker-owned toolset
  authority and manifest design
- [Cross-library integration](../../architecture/MEAI/cross-library-integration.md) — using this
  library alongside `TemporalCommunity.Extensions.Agents`
- [Extensible durable turns](../../architecture/MEAI/extensible-durable-turns.md) — receiver and
  scope lifetime rules for `AddDurableToolFactory<THandler>`
- [Durable approvals](../../concepts/durable-approvals.md) — the generic per-request approval
  lifecycle and retry outcomes
- [Security boundary](../../security.md) — normative authentication/authorization rules for
  externally reachable session and approval endpoints
