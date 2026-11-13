# MAF `HarnessAgent` compatibility

`HarnessAgent` is not a drop-in for a Temporal-managed agent. The repository references
`Microsoft.Agents.AI` 1.23.0; `HarnessAgent` comes from the separate
`Microsoft.Agents.AI.Harness` package and builds its own `ChatClientAgent` from an `IChatClient`.
Adding that package does not change the managed agent's registration contract.

## Why it cannot be used directly

### The `ChatClient` slot requires an `IChatClient`

`DurableAgentBuilder.ChatClient` is a `Func<IServiceProvider, IChatClient>`, but `HarnessAgent` is an
`AIAgent`:

```csharp
// Does not compile: HarnessAgent is not an IChatClient.
agent.ChatClient = _ => new HarnessAgent(innerChatClient, options);
```

### The pipeline must wrap the supplied agent

`ConfigureAgentPipeline` can add transparent `DelegatingAIAgent` wrappers around the agent created
by the library. This compiles, but the factory discards that supplied agent and constructs an
unrelated `HarnessAgent` with its own inner `ChatClientAgent`:

```csharp
// Compiles, then fails worker startup validation.
agent.ConfigureAgentPipeline = builder =>
    builder.Use(_ => new HarnessAgent(chatClient, harnessOptions));
```

Startup validation rejects a pipeline that does not preserve the library-created inner agent.
Transparent wrappers such as MAF's `OpenTelemetryAgent` can compose when they delegate the supplied
inner agent; the pipeline lease also owns and disposes that wrapper.

## The tool loop belongs to Temporal

MAF 1.23.0's `HarnessAgent` includes `FunctionInvokingChatClient` in its chat-client pipeline. The
Temporal Agents integration separately rejects that middleware when it appears in the registered
chat-client chain; the Harness pipeline example above is rejected because it replaces the
library-created agent. Managed tool calls must reach the workflow, which dispatches each registered
tool as a separate Temporal activity. An in-process loop bypasses those per-tool activity, retry,
timeout, approval, and history boundaries.

## Reusing context providers

Many MAF context providers are available independently of `HarnessAgent`, but using one directly
does not make the full Harness profile durable. Providers may contribute instructions and messages;
provider-supplied tools are not automatically executable. Managed agents can use registered durable
tools selected from the exact function instances seeded into the provider context, through
`IDurableToolSource` or `AddContextProvider(provider, durableTools)`. See
[Context providers](./context-providers.md) for the supported selection and registration patterns.

Review providers individually for history ownership, external side effects, and process-local state.
The [bounded durable `ChatClientAgent` contract](../../architecture/MAF/bounded-durable-agent-compatibility.md)
describes the integration boundary.

## Background agents need workflow orchestration

`BackgroundAgentsProvider` starts subagent runs as process-local tasks. Its serialized session state
does not include the in-flight `Task<AgentResponse>` or subagent `AgentSession` references; after
the managed workflow restores the session for another model-step activity, those references are
unavailable. When the provider refreshes task status, a running task with no runtime reference is
marked `Lost`. The child work is not a Temporal activity or workflow. For parallel work, orchestrate
registered agents from the workflow instead; see
[Routing patterns](./routing.md#retry-identity-and-parallel-calls).

## Use durable approval for managed tools

MAF's `ToolApprovalAgent` exchanges `ToolApprovalRequestContent` and
`ToolApprovalResponseContent` with the caller and stores approval bookkeeping in the agent session.
Managed tools follow a different path: the workflow receives `FunctionCallContent` and controls
whether to dispatch the tool activity. Use `RequireApproval()`, an `IAgentToolInterceptor` returning
`DurableToolDecision.PauseForApproval(...)`, or
`TemporalAgentContext.Current.RequestApprovalAsync(...)` for that workflow-owned approval flow.
See [Human-in-the-loop patterns](./hitl-patterns.md).

## See also

- [Context providers](./context-providers.md) — registering providers and their durable tools
- [Human-in-the-loop patterns](./hitl-patterns.md) — durable approval
- [Usage](./usage.md) — agent registration
