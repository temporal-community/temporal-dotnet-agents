# Microsoft.Extensions.AI — Temporal Durable Execution Integration Analysis

## Executive Summary

Microsoft.Extensions.AI (MEAI) provides a middleware pipeline architecture for AI operations (`IChatClient`, `IEmbeddingGenerator`, `AIFunction`, etc.) that is structurally analogous to Temporal's interceptor chain-of-responsibility pattern. This creates a natural integration surface: a `DelegatingChatClient`-based middleware could transparently wrap LLM calls as Temporal activities, giving any MEAI-based application durable execution semantics — retry, timeout, and crash recovery — without requiring the Microsoft Agent Framework (MAF). This report catalogs MEAI's public extensibility points and evaluates concrete strategies for a `Temporalio.Extensions.AI` library that operates one level below the existing `Temporalio.Extensions.Agents` MAF integration.

---

## 1. MEAI Architecture Overview

MEAI follows a **pipeline middleware** pattern. The core abstraction is `IChatClient`, and middleware is composed via `ChatClientBuilder`:

```
Outermost ←────────────────────────────────────→ Innermost
┌──────────┐   ┌──────────┐   ┌──────────────┐   ┌──────────────┐
│ Logging  │ → │  OTel    │ → │  FuncInvoke  │ → │  OpenAI /    │
│ Client   │   │  Client  │   │  Client      │   │  Ollama etc. │
└──────────┘   └──────────┘   └──────────────┘   └──────────────┘
     ↑                                                    ↑
DelegatingChatClient                              IChatClient impl
```

```csharp
// Registration — middleware applied via builder pattern
services.AddChatClient(innerClient)
    .UseLogging()
    .UseOpenTelemetry()
    .UseFunctionInvocation()
    .Build();
```

Every AI modality follows this same pattern: `IChatClient`/`ChatClientBuilder`, `IEmbeddingGenerator<,>`/`EmbeddingGeneratorBuilder<,>`, `ISpeechToTextClient`/`SpeechToTextClientBuilder`, etc.

---

## 2. Public Extensibility Points Inventory

### 2.1 Core Interfaces

| Interface | Key Methods | Notes |
|-----------|------------|-------|
| `IChatClient` | `GetResponseAsync(IEnumerable<ChatMessage>, ChatOptions?, CancellationToken)` → `Task<ChatResponse>` | Primary integration target |
| | `GetStreamingResponseAsync(...)` → `IAsyncEnumerable<ChatResponseUpdate>` | Streaming variant |
| | `GetService(Type, object?)` → `object?` | Service locator for metadata/inner clients |
| `IEmbeddingGenerator<TInput, TEmbedding>` | `GenerateAsync(IEnumerable<TInput>, EmbeddingGenerationOptions?, CancellationToken)` → `Task<GeneratedEmbeddings<TEmbedding>>` | Same pattern |
| `ISpeechToTextClient` | `GetTextAsync(Stream, SpeechToTextOptions?, CancellationToken)` → `Task<SpeechToTextResponse>` | Experimental |
| `ITextToSpeechClient` | `GetAudioAsync(string, TextToSpeechOptions?, CancellationToken)` → `Task<TextToSpeechResponse>` | Experimental |
| `IImageGenerator` | `GenerateAsync(ImageGenerationRequest, ImageGenerationOptions?, CancellationToken)` → `Task<ImageGenerationResponse>` | Experimental |
| `IHostedFileClient` | `UploadAsync`, `DownloadAsync`, `GetFileInfoAsync`, `ListFilesAsync`, `DeleteAsync` | Experimental |
| `IRealtimeClient` | `CreateSessionAsync(RealtimeSessionOptions?, CancellationToken)` → `Task<IRealtimeClientSession>` | Experimental |
| `IChatReducer` | `ReduceAsync(IEnumerable<ChatMessage>, CancellationToken)` → `Task<IEnumerable<ChatMessage>>` | History management |

### 2.2 Delegating Base Classes

Each interface has a corresponding `Delegating*` base class with virtual methods that pass through to an inner instance. These are the primary subclassing points for middleware:

| Base Class | Virtual Methods | Purpose |
|-----------|----------------|---------|
| `DelegatingChatClient` | `GetResponseAsync`, `GetStreamingResponseAsync`, `GetService`, `Dispose(bool)` | Chat middleware base |
| `DelegatingEmbeddingGenerator<TInput, TEmbedding>` | `GenerateAsync`, `GetService`, `Dispose(bool)` | Embedding middleware base |
| `DelegatingSpeechToTextClient` | `GetTextAsync`, `GetStreamingTextAsync`, `GetService`, `Dispose(bool)` | STT middleware |
| `DelegatingTextToSpeechClient` | `GetAudioAsync`, `GetStreamingAudioAsync`, `GetService`, `Dispose(bool)` | TTS middleware |
| `DelegatingImageGenerator` | `GenerateAsync`, `GetService`, `Dispose(bool)` | Image gen middleware |
| `DelegatingHostedFileClient` | All CRUD methods, `GetService`, `Dispose(bool)` | File client middleware |
| `DelegatingRealtimeClient` | `CreateSessionAsync`, `GetService`, `Dispose(bool)` | Realtime middleware |
| `DelegatingAIFunction` | `InvokeCoreAsync`, `GetService`, all property overrides | Function wrapper |

### 2.3 Pipeline Builders

| Builder | Registration Extension | Key `Use()` Overloads |
|---------|----------------------|----------------------|
| `ChatClientBuilder` (sealed) | `services.AddChatClient(...)` | `Use(Func<IChatClient, IChatClient>)`, `Use(Func<IChatClient, IServiceProvider, IChatClient>)`, anonymous delegate variants |
| `EmbeddingGeneratorBuilder<TInput, TEmbedding>` | `services.AddEmbeddingGenerator<,>(...)` | Same pattern |
| `SpeechToTextClientBuilder` | — | Same pattern (experimental) |
| `TextToSpeechClientBuilder` | — | Same pattern (experimental) |
| `ImageGeneratorBuilder` | — | Same pattern (experimental) |
| `RealtimeClientBuilder` | — | Same pattern (experimental) |
| `HostedFileClientBuilder` | — | Same pattern (experimental) |

Pipeline order: factories applied in **reverse** — first `.Use()` call becomes outermost middleware.

### 2.4 Built-In Middleware

| Middleware | Base Class | Key Extensibility |
|-----------|-----------|-------------------|
| `FunctionInvokingChatClient` | `DelegatingChatClient` | `FunctionInvoking`/`FunctionInvoked` events; `FunctionInvocationContext` (fully mutable); `MaximumIterationsPerRequest`; `AllowConcurrentInvocation`; `AdditionalTools` |
| `CachingChatClient` (abstract) | `DelegatingChatClient` | Override `GetCacheKey`, `ReadCacheAsync`, `WriteCacheAsync`, `ReadCacheStreamingAsync`, `WriteCacheStreamingAsync`, `EnableCaching` |
| `DistributedCachingChatClient` (sealed) | `CachingChatClient` | `JsonSerializerOptions`, `CacheKeyAdditionalValues` |
| `OpenTelemetryChatClient` (sealed) | `DelegatingChatClient` | `EnableSensitiveData`; exposes `ActivitySource` via `GetService` |
| `LoggingChatClient` | `DelegatingChatClient` | `JsonSerializerOptions` |
| `ConfigureOptionsChatClient` (sealed) | `DelegatingChatClient` | `Action<ChatOptions>` callback |
| `ReducingChatClient` (sealed) | `DelegatingChatClient` | Accepts `IChatReducer` |

Built-in reducers: `MessageCountingChatReducer`, `SummarizingChatReducer` (LLM-based, has `SummarizationPrompt` property).

### 2.5 Data Types

**Chat types:**
- `ChatMessage` — `ChatRole Role`, `IList<AIContent> Contents`, `string? AuthorName`, `DateTimeOffset? CreatedAt`, `string? MessageId`, `AdditionalPropertiesDictionary? AdditionalProperties`
- `ChatResponse` — `IList<ChatMessage> Messages`, `string? ResponseId`, `string? ConversationId`, `string? ModelId`, `ChatFinishReason? FinishReason`, `UsageDetails? Usage`, `ResponseContinuationToken? ContinuationToken`
- `ChatResponseUpdate` — streaming equivalent with same shape
- `ChatOptions` — `string? ConversationId`, `string? Instructions`, `float? Temperature`, `int? MaxOutputTokens`, `IList<AITool>? Tools`, `ChatToolMode? ToolMode`, `AdditionalPropertiesDictionary? AdditionalProperties`, `virtual Clone()`
- `UsageDetails` — `long? InputTokenCount`, `OutputTokenCount`, `TotalTokenCount`, `CachedInputTokenCount`, `ReasoningTokenCount`, `Add(UsageDetails)`

**Content hierarchy** (`AIContent` base, JSON polymorphic with `$type` discriminator):
- `TextContent`, `DataContent`, `UriContent`, `ErrorContent`
- `FunctionCallContent` — `string Name`, `IDictionary<string, object?> Arguments`, `bool InformationalOnly`
- `FunctionResultContent` — function result
- `ToolApprovalRequestContent` — `string RequestId`, `ToolCallContent ToolCall`, `CreateResponse(bool approved, string? reason)`
- `ToolApprovalResponseContent` — correlated approval decision
- `UsageContent`, `TextReasoningContent`, `HostedFileContent`, `HostedVectorStoreContent`
- Custom types registerable via `AIJsonUtilities.AddAIContentType<T>(options, discriminator)`

### 2.6 Tool/Function Abstractions

| Type | Kind | Key Members |
|------|------|-------------|
| `AITool` | Abstract base | `virtual Name`, `virtual Description`, `virtual AdditionalProperties`, `virtual GetService` |
| `AIFunctionDeclaration : AITool` | Abstract | `virtual JsonSchema`, `virtual ReturnJsonSchema` |
| `AIFunction : AIFunctionDeclaration` | Abstract | `abstract InvokeCoreAsync(AIFunctionArguments, CancellationToken)`, `InvokeAsync(...)`, `AsDeclarationOnly()` |
| `DelegatingAIFunction : AIFunction` | Delegating base | All members virtual, delegates to `InnerFunction` |
| `ApprovalRequiredAIFunction : DelegatingAIFunction` | Sealed marker | Signals `FunctionInvokingChatClient` to emit `ToolApprovalRequestContent` instead of invoking directly |
| `AIFunctionFactory` | Static factory | `Create(Delegate, options?)`, `Create(MethodInfo, instance?, options?)` |
| `AIFunctionArguments` | Dictionary-like | `IServiceProvider? Services` — enables DI inside function invocations |

### 2.7 Events & Hooks

**`FunctionInvokingChatClient` events:**
- `event EventHandler<FunctionInvocationContext> FunctionInvoking` — fires before each invocation (mutable context)
- `event EventHandler<FunctionInvocationContext> FunctionInvoked` — fires after invocation

**`FunctionInvocationContext`** (fully mutable):
```csharp
public class FunctionInvocationContext {
    public AIFunction Function { get; set; }
    public AIFunctionArguments Arguments { get; set; }
    public FunctionCallContent CallContent { get; set; }
    public IList<ChatMessage> Messages { get; set; }
    public ChatOptions? Options { get; set; }
    public int Iteration { get; set; }
    public int FunctionCallIndex { get; set; }
    public int FunctionCount { get; set; }
    public bool Terminate { get; set; }
    public bool IsStreaming { get; set; }
}
```

### 2.8 Options & Metadata

- `ChatClientMetadata` — `ProviderName`, `ProviderUri`, `DefaultModelId`
- All options classes have `virtual Clone()` for thread-safe concurrent usage
- All options have `AdditionalPropertiesDictionary? AdditionalProperties` for custom metadata
- `ChatOptions.RawRepresentationFactory` — `Func<IChatClient, object?>` callback for provider-specific configuration
- `ResponseContinuationToken` (experimental) — enables polling/resumption of background responses

---

## 3. Integration Opportunity Analysis

### 3.1 Strategy A: Durable Chat Client (`DelegatingChatClient` Wrapper)

**Concept:** A `DurableChatClient : DelegatingChatClient` that wraps `GetResponseAsync` as a Temporal activity call, making every LLM invocation automatically durable.

```csharp
// Usage
services.AddChatClient(openAIClient)
    .UseDurableExecution(temporalClient, "ai-task-queue")  // ← new middleware
    .UseFunctionInvocation()
    .UseLogging()
    .Build();
```

**Implementation sketch:**
```csharp
public class DurableChatClient : DelegatingChatClient
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // If already inside a Temporal activity, pass through (avoid double-wrapping)
        if (ActivityExecutionContext.HasCurrent)
            return await base.GetResponseAsync(messages, options, cancellationToken);

        // If inside a Temporal workflow, execute as activity
        if (Workflow.InWorkflow)
        {
            return await Workflow.ExecuteActivityAsync(
                (DurableChatActivities a) => a.GetResponseAsync(
                    new DurableChatInput(messages.ToList(), options)),
                new() { StartToCloseTimeout = TimeSpan.FromMinutes(5) });
        }

        // External: start workflow or pass through
        return await base.GetResponseAsync(messages, options, cancellationToken);
    }
}
```

**Temporal alignment:**
- LLM calls become activities → automatic retry, timeout, heartbeat
- On worker crash, completed LLM calls replay from history (no re-invocation)
- `ChatResponse` must be serializable via Temporal's `DataConverter`

**Challenge — Streaming:**
`GetStreamingResponseAsync` returns `IAsyncEnumerable<ChatResponseUpdate>`. Temporal activities return a single result. Options:
1. **Buffer-and-replay**: Collect all updates in the activity, return as `IReadOnlyList<ChatResponseUpdate>`, yield from the middleware. Durability preserved but live streaming lost.
2. **Pass-through streaming**: Don't wrap streaming calls as activities. Only `GetResponseAsync` gets durability. Document this trade-off.
3. **Hybrid**: Use `ChatResponseUpdate.ToChatResponse()` to store the final coalesced result in history; stream live on first execution, replay as batch on recovery.

**Recommendation:** Option 3 (hybrid) — matches how `CachingChatClient` already handles `CoalesceStreamingUpdates`.

### 3.2 Strategy B: Durable Function Invocation (`DelegatingAIFunction` Wrapper)

**Concept:** Wrap individual tool/function calls as Temporal activities. Each `AIFunction.InvokeCoreAsync` becomes a separate activity, giving per-tool durability.

```csharp
public class DurableAIFunction : DelegatingAIFunction
{
    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        // Execute the function invocation as a Temporal activity
        return await Workflow.ExecuteActivityAsync(
            (DurableFunctionActivities a) => a.InvokeFunctionAsync(
                new FunctionInvocationInput(InnerFunction.Name, arguments)),
            new() { StartToCloseTimeout = TimeSpan.FromMinutes(10) });
    }
}
```

**This pairs naturally with `FunctionInvokingChatClient`**: When the LLM requests a tool call, `FunctionInvokingChatClient` invokes the `AIFunction`. If that function is wrapped in `DurableAIFunction`, the invocation becomes a Temporal activity.

**Temporal alignment:**
- Each tool call is independently retryable and has its own timeout
- Tool results are individually recorded in workflow history
- On replay, completed tool calls return cached results — the LLM is not re-queried for the same tool request

### 3.3 Strategy C: Durable Approval via `ApprovalRequiredAIFunction`

**Concept:** MEAI already has a built-in approval pattern (`ApprovalRequiredAIFunction` + `ToolApprovalRequestContent` + `ToolApprovalResponseContent`). Temporal can back this with durable `WaitConditionAsync` — exactly the same pattern as TemporalAgents' existing HITL support.

```csharp
// The FunctionInvokingChatClient detects ApprovalRequiredAIFunction
// and emits ToolApprovalRequestContent. A DurableChatClient intercepts
// this content and implements the approval gate via Temporal:

[WorkflowUpdate]
public async Task<ToolApprovalResponseContent> RequestToolApprovalAsync(
    ToolApprovalRequestContent request)
{
    _pendingApproval = request;
    await Workflow.WaitConditionAsync(() => _approvalDecision != null);
    return request.CreateResponse(_approvalDecision!.Approved, _approvalDecision.Reason);
}
```

**Key insight:** MEAI's `ToolApprovalRequestContent.CreateResponse(bool, string?)` produces a `ToolApprovalResponseContent` — this maps directly to Temporal's `[WorkflowUpdate]` return value pattern. The content types are already designed for request/response semantics.

### 3.4 Strategy D: Durable History Reduction (`IChatReducer`)

**Concept:** Implement `IChatReducer` backed by Temporal workflow state. The reducer persists conversation history in workflow state and manages context windows durably.

```csharp
public class DurableChatReducer : IChatReducer
{
    public async Task<IEnumerable<ChatMessage>> ReduceAsync(
        IEnumerable<ChatMessage> messages, CancellationToken cancellationToken)
    {
        // Store full history in Temporal workflow state (survives crashes)
        // Return reduced window for the next LLM call
        // On continue-as-new, carry forward the reduced set
    }
}
```

**Temporal alignment:**
- History survives worker crashes via workflow state
- `ContinueAsNew` transitions carry the reduced history forward
- Summarization (if using `SummarizingChatReducer`) can be wrapped as an activity

### 3.5 Strategy E: Conversation ID ↔ Workflow ID Mapping

**Concept:** MEAI's `ChatOptions.ConversationId` maps naturally to Temporal workflow IDs. A middleware can use this to route messages to existing workflow instances.

```csharp
public class DurableChatClient : DelegatingChatClient
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var workflowId = options?.ConversationId ?? Guid.NewGuid().ToString();

        // Start or signal existing workflow
        return await _temporalClient.ExecuteUpdateAsync<ChatResponse>(
            workflowId, "RunChat", new ChatInput(messages.ToList(), options));
    }
}
```

**This enables session persistence**: Same `ConversationId` → same workflow → same conversation history and state. The workflow manages the session lifecycle, TTL, and cleanup.

### 3.6 Strategy F: ChatOptions.AdditionalProperties for Temporal Metadata

**Concept:** Use the `AdditionalProperties` dictionary to carry Temporal-specific metadata through the pipeline without modifying MEAI types.

```csharp
// Set Temporal context
options.AdditionalProperties ??= new();
options.AdditionalProperties["temporal.workflow.id"] = workflowId;
options.AdditionalProperties["temporal.activity.timeout"] = TimeSpan.FromMinutes(5);
options.AdditionalProperties["temporal.retry.max"] = 3;

// Read in middleware
var timeout = options?.AdditionalProperties?
    .TryGetValue("temporal.activity.timeout", out var t) == true
    ? (TimeSpan)t : DefaultTimeout;
```

---

## 4. Comparison: MEAI-Level vs MAF-Level Integration

| Aspect | MEAI-Level (`Temporalio.Extensions.AI`) | MAF-Level (`Temporalio.Extensions.Agents`) |
|--------|----------------------------------------|-------------------------------------------|
| **Abstraction level** | `IChatClient` pipeline middleware | `AIAgent` session orchestration |
| **Granularity** | Per-LLM-call or per-tool-call durability | Per-agent-turn durability |
| **User surface** | `builder.UseDurableExecution()` | `builder.AddTemporalAgents()` |
| **Framework dependency** | `Microsoft.Extensions.AI` only | `Microsoft.Agents.AI` + MEAI |
| **Session management** | Via `ConversationId` ↔ workflow ID | Via `AgentWorkflow` with explicit `StateBag` |
| **HITL** | Via `ApprovalRequiredAIFunction` + Temporal updates | Via `TemporalAgentContext.RequestApprovalAsync` |
| **History management** | Via `IChatReducer` implementations | Via `AgentWorkflow._history` + continue-as-new |
| **Tool invocation** | `DurableAIFunction` wraps individual tools | Tools run inside `AgentActivities.ExecuteAgentAsync` |
| **Streaming** | Must handle `IAsyncEnumerable` explicitly | Not applicable (MAF is request/response) |
| **Composability** | Middleware — composes with logging, caching, OTel | Opinionated — owns the workflow lifecycle |
| **Target audience** | Any MEAI consumer (minimal buy-in) | Agent framework users (full agent orchestration) |
| **Adoption path** | Drop-in middleware, works with existing code | Requires restructuring around `TemporalAIAgent` |

**Key trade-off:** MEAI-level integration is more **composable and broadly applicable** — it makes any `IChatClient` pipeline durable. MAF-level integration is more **opinionated and feature-rich** — it provides session management, routing, parallel fan-out, and agent-to-agent communication out of the box.

**These are complementary, not competing.** A `Temporalio.Extensions.AI` library could serve as the foundation that `Temporalio.Extensions.Agents` builds on internally.

---

## 5. Temporal Best Practices Alignment

### Determinism Rules

| MEAI Integration Point | Temporal Concern | Mitigation |
|----------------------|-----------------|-----------|
| `DurableChatClient.GetResponseAsync` | LLM calls are non-deterministic | Wrap as activity — results replayed from history on re-execution |
| `DurableAIFunction.InvokeCoreAsync` | Tool calls may have side effects | Wrap as activity — idempotency key via `FunctionCallContent.CallId` |
| `IChatReducer.ReduceAsync` | Summarization calls LLM | Wrap summarization as activity; pure truncation is safe in workflow |
| Streaming (`IAsyncEnumerable`) | Cannot replay mid-stream | Buffer complete in activity; replay as batch |
| `ChatOptions.ConversationId` | Workflow ID must be deterministic | Use `ConversationId` as workflow ID directly (deterministic by construction) |
| `ApprovalRequiredAIFunction` | Human response timing is non-deterministic | Use `Workflow.WaitConditionAsync` — event-sourced, replay-safe |

### TaskScheduler Constraints (from Temporal .NET SDK)

- **DO NOT** use `Task.Run()` inside workflows — use `Workflow.RunTaskAsync()`
- **DO NOT** use `Task.WhenAny/All` — use `Workflow.WhenAnyAsync/AllAsync()`
- The `DurableChatClient` pipeline must only operate inside activities or outside workflows. Within a workflow, it must dispatch to activities via `Workflow.ExecuteActivityAsync`.
- The `ChatClientBuilder` pipeline itself is built once (at DI registration time) and is deterministic. Only the runtime execution of `GetResponseAsync` needs wrapping.

### Serialization

- `ChatMessage`, `ChatResponse`, `ChatOptions`, `AIContent` subclasses are all JSON-serializable via `System.Text.Json`
- `AIJsonUtilities.DefaultOptions` provides the default serializer with polymorphic `AIContent` support
- Temporal's `DataConverter` (default: JSON) can serialize these types, but a custom `IEncodingConverter` or source-generated `JsonSerializerContext` may improve performance
- `AdditionalPropertiesDictionary` uses `object?` values — may need careful handling for Temporal's payload converter

---

## 6. Proposed Library Shape

### Package: `Temporalio.Extensions.AI`

**Dependencies:**
- `Microsoft.Extensions.AI.Abstractions` (>= 10.0.0)
- `Temporalio` (>= 1.11.1)
- `Temporalio.Extensions.Hosting` (>= 1.11.1) — optional, for DI integration

### Key Types

```
Temporalio.Extensions.AI/
├── DurableChatClient.cs                    // DelegatingChatClient → wraps calls as activities
├── DurableEmbeddingGenerator.cs            // DelegatingEmbeddingGenerator<,> → same pattern
├── DurableAIFunction.cs                    // DelegatingAIFunction → wraps tool calls as activities
├── DurableChatReducer.cs                   // IChatReducer → backed by workflow state
├── DurableChatWorkflow.cs                  // [Workflow] managing conversation lifecycle
├── DurableChatActivities.cs                // [Activity] executing actual LLM/tool calls
├── DurableApprovalHandler.cs               // Bridges MEAI ToolApproval ↔ Temporal WorkflowUpdate
├── ChatClientBuilderExtensions.cs          // .UseDurableExecution() extension
├── EmbeddingGeneratorBuilderExtensions.cs  // .UseDurableExecution() extension
├── DurableExecutionOptions.cs              // Configuration (timeouts, retry, task queue)
├── TemporalChatClientMetadata.cs           // Metadata exposed via GetService()
└── AIFunctionExtensions.cs                 // .AsDurable() wrapping helper
```

### Builder API

```csharp
// Minimal integration — wrap LLM calls as activities
services.AddChatClient(openAiClient)
    .UseDurableExecution(options =>
    {
        options.TaskQueue = "ai-task-queue";
        options.ActivityTimeout = TimeSpan.FromMinutes(5);
        options.RetryPolicy = new RetryPolicy { MaximumAttempts = 3 };
    })
    .UseFunctionInvocation()
    .Build();

// Durable function invocation — each tool call is a separate activity
services.AddChatClient(openAiClient)
    .UseFunctionInvocation(client =>
    {
        client.AdditionalTools.Add(
            myFunction.AsDurable(new() { StartToCloseTimeout = TimeSpan.FromMinutes(10) }));
    })
    .UseDurableExecution()
    .Build();

// Full durable conversation — workflow-backed session
services.AddChatClient(openAiClient)
    .UseDurableExecution(options =>
    {
        options.EnableSessionManagement = true;
        options.SessionTimeToLive = TimeSpan.FromDays(7);
        options.UseWorkflowUpdate = true;  // [WorkflowUpdate] for request/response
    })
    .UseFunctionInvocation()
    .UseReduction(new DurableChatReducer())
    .Build();
```

### DI Registration

```csharp
// Register Temporal worker with durable AI activities
services.AddHostedTemporalWorker("localhost:7233", "default", "ai-task-queue")
    .AddDurableAI()  // registers DurableChatWorkflow + DurableChatActivities
    .ConfigureOptions(opts => opts.MaxConcurrentActivities = 20);
```

---

## 7. Cross-Reference to Temporal SDK Extension Points

The [Temporal .NET SDK Integration Review](./temporal-sdk-dotnet-integration-review.md) identified 7 extension point categories. Here is how each pairs with MEAI:

| Temporal Extension Point | MEAI Integration Surface | Usage |
|-------------------------|------------------------|-------|
| **Activity definitions** | `DurableChatActivities` wraps `IChatClient.GetResponseAsync` and `AIFunction.InvokeCoreAsync` | Core — every LLM/tool call becomes an activity |
| **Worker interceptors** (`IWorkerInterceptor`) | `FunctionInvocationContext` events; `ChatOptions.AdditionalProperties` | Propagate Temporal context (workflow ID, run ID) into MEAI pipeline metadata |
| **Client interceptors** (`IClientInterceptor`) | `DurableChatClient.GetService(typeof(ActivitySource))` | OTel span linking between Temporal traces and MEAI OTel traces |
| **Payload converter/codec** (`IPayloadCodec`) | `AIJsonUtilities.DefaultOptions`; `ChatResponse` serialization | Serialize `ChatResponse`/`ChatMessage` as Temporal payloads; optional encryption via codec |
| **SimplePlugin** | `ChatClientBuilderExtensions.UseDurableExecution()` | Bundle all Temporal integration (interceptors, activities, codec) into one `UseX` call |
| **DI hosting** (`AddHostedTemporalWorker`) | `services.AddChatClient(...).UseDurableExecution()` | Compose MEAI DI registration with Temporal worker registration |
| **Custom metrics** (`ICustomMetricMeter`) | MEAI `OpenTelemetryChatClient` metrics | Bridge MEAI's `gen_ai.client.*` metrics with Temporal's custom meter for unified dashboards |

---

## 8. Detailed Type Mapping: MEAI ↔ Temporal Concepts

| MEAI Concept | Temporal Equivalent | Notes |
|-------------|-------------------|-------|
| `IChatClient.GetResponseAsync` | Activity execution | Single LLM call = single activity |
| `ChatOptions.ConversationId` | Workflow ID | Natural 1:1 mapping for session management |
| `ChatResponse` | Activity result payload | Serialized into workflow history |
| `IAsyncEnumerable<ChatResponseUpdate>` | Activity result (buffered) | Must coalesce for durability |
| `AIFunction.InvokeCoreAsync` | Activity execution | Tool call = activity |
| `FunctionInvocationContext` | Activity execution context | Carries call metadata |
| `ApprovalRequiredAIFunction` | `[WorkflowUpdate]` + `WaitConditionAsync` | HITL approval gate |
| `ToolApprovalRequestContent` | Approval request (workflow state) | Pending approval stored in workflow |
| `ToolApprovalResponseContent` | Approval decision (update result) | Human decision completes the gate |
| `IChatReducer` | Workflow state + `ContinueAsNew` | History management across workflow transitions |
| `DelegatingChatClient` pipeline | Temporal interceptor chain | Both are chain-of-responsibility |
| `ChatClientBuilder.Use()` | `TemporalWorkerOptions.Interceptors` | Pipeline registration |
| `GetService(Type)` | Workflow/Activity context access | Service locator pattern |
| `ChatOptions.AdditionalProperties` | Temporal `Headers` | Metadata propagation across boundaries |
| `UsageDetails` | Activity span attributes / OTel metrics | Token counting in telemetry |

---

## 9. Open Questions & Risks

### Design Questions

1. **Where does the workflow live?** Should the `DurableChatClient` start a new workflow per `GetResponseAsync` call, or should it assume it's already running inside a workflow? The former is simpler but creates many short-lived workflows; the latter requires the caller to manage workflow lifecycle.

2. **Activity granularity:** Should the entire `GetResponseAsync` → function invocation loop be one activity, or should the LLM call and each tool call be separate activities? Separate activities give finer replay granularity but add latency (each activity dispatch is a round-trip to the Temporal server).

3. **Streaming trade-off:** Losing real-time streaming is a significant UX degradation. Is there a way to stream from the activity to the workflow caller while still recording the final result? Temporal doesn't support streaming activity results today. A possible workaround: use signals for intermediate updates + activity for the final result.

4. **Pipeline position:** Should `UseDurableExecution()` be outermost (wrap everything, including function invocation) or innermost (wrap only the LLM call, let function invocation happen in-workflow)? This affects what's durable and what's not.

5. **Multi-modality:** Should `DurableEmbeddingGenerator`, `DurableSpeechToTextClient`, etc. be first-class or start with chat only? The pattern is identical, but scope affects initial library size.

### Technical Risks

1. **Serialization compatibility:** `ChatMessage.Contents` contains polymorphic `AIContent` objects. Temporal's default JSON converter may not handle the `$type` discriminator correctly without configuration. May need a custom `IEncodingConverter` that uses `AIJsonUtilities.DefaultOptions`.

2. **ChatOptions mutation:** MEAI documents that middleware may mutate `ChatOptions`. When deserializing in an activity, the original mutations from upstream middleware won't be present. The durable middleware must snapshot the fully-configured options before dispatching.

3. **`IServiceProvider` in activities:** `AIFunctionArguments.Services` provides DI access inside function invocations. In a Temporal activity, the `IServiceProvider` is the worker's scoped provider, not the caller's. Service availability may differ.

4. **Circular composition:** If `FunctionInvokingChatClient` is in the pipeline and calls a `DurableAIFunction`, which internally calls back to the pipeline — need to avoid infinite recursion. The `ActivityExecutionContext.HasCurrent` guard prevents double-wrapping, but the function's inner logic needs the real (non-durable) client.

5. **`ContinueAsNew` history limits:** Long conversations accumulate large `ChatMessage` lists in workflow state. Need `IChatReducer`-based compaction before `ContinueAsNew` transitions, or the workflow history will grow unbounded.

6. **Non-determinism in options:** `ChatOptions.RawRepresentationFactory` is a `Func<IChatClient, object?>` — lambdas cannot be serialized. This property must be excluded from activity inputs and re-attached on the worker side.

### Adoption Considerations

- **Minimal buy-in:** `builder.UseDurableExecution()` — one line to add durability to any MEAI pipeline. Much lower barrier than restructuring around MAF agents.
- **Incremental adoption:** Start with durable LLM calls only; add durable function invocation and session management later.
- **Compatibility:** Must work alongside existing `Temporalio.Extensions.Agents` — shared workers, shared task queues, unified OTel.

---

## 10. Relationship with `Temporalio.Extensions.Agents`

The proposed `Temporalio.Extensions.AI` library would operate one architectural layer below the existing `Temporalio.Extensions.Agents` project in this repository. Understanding how these two libraries relate — and could evolve together — is critical for avoiding duplication and ensuring a coherent developer experience.

### 10.1 The Current Architecture Stack

Today, the stack looks like this:

```
┌─────────────────────────────────────────────────────┐
│              Application Code                       │
│  (samples/BasicAgent, samples/MultiAgentRouting)    │
├─────────────────────────────────────────────────────┤
│        Temporalio.Extensions.Agents                 │  ← This project
│  AgentWorkflow, AgentActivities, TemporalAIAgent,   │
│  DefaultTemporalAgentClient, IAgentRouter, HITL     │
├─────────────────────────────────────────────────────┤
│        Microsoft.Agents.AI (MAF)                    │
│  AIAgent, AgentSession, AgentResponse,              │
│  AgentSessionStateBag, DelegatingAIAgent            │
├─────────────────────────────────────────────────────┤
│        Microsoft.Extensions.AI (MEAI)               │
│  IChatClient, ChatMessage, ChatResponse,            │
│  AIFunction, FunctionInvokingChatClient             │
├─────────────────────────────────────────────────────┤
│        Temporal .NET SDK                            │
│  Workflows, Activities, Workers, Interceptors       │
└─────────────────────────────────────────────────────┘
```

With the proposed library, a new layer is inserted:

```
┌─────────────────────────────────────────────────────┐
│              Application Code                       │
├──────────────────────┬──────────────────────────────┤
│  Temporalio.         │  Direct MEAI consumers       │
│  Extensions.Agents   │  (no MAF dependency)         │
├──────────────────────┴──────────────────────────────┤
│        Temporalio.Extensions.AI   ← NEW             │
│  DurableChatClient, DurableAIFunction,              │
│  DurableChatWorkflow, DurableApprovalHandler        │
├─────────────────────────────────────────────────────┤
│        Microsoft.Agents.AI (MAF)  │  MEAI           │
│        (optional above this line) │  (required)     │
├─────────────────────────────────────────────────────┤
│        Temporal .NET SDK                            │
└─────────────────────────────────────────────────────┘
```

### 10.2 What Currently Lives in Agents That Could Move Down

Several concerns in `Temporalio.Extensions.Agents` are not MAF-specific — they operate on MEAI primitives and could be extracted into `Temporalio.Extensions.AI`:

| Current Location | Concern | MEAI-Level Equivalent |
|-----------------|---------|----------------------|
| `AgentWorkflow._history` (list of `TemporalAgentStateEntry`) | Conversation history persistence | `DurableChatWorkflow` managing `List<ChatMessage>` directly — no custom `TemporalAgentStateMessage` serialization layer needed since MEAI types are already JSON-serializable via `AIJsonUtilities.DefaultOptions` |
| `AgentActivities.ExecuteAgentAsync` | Wraps agent call as activity, heartbeats on streaming chunks, emits OTel span | `DurableChatActivities.GetResponseAsync` wrapping `IChatClient.GetResponseAsync` as activity — same pattern, no MAF `AgentResponse` wrapper |
| `AgentWorkflow` HITL handlers (`RequestApprovalAsync`, `SubmitApprovalAsync`, `GetPendingApproval`) | Approval gate via `[WorkflowUpdate]` + `WaitConditionAsync` | `DurableApprovalHandler` bridging `ToolApprovalRequestContent`/`ToolApprovalResponseContent` ↔ Temporal updates — same `WaitConditionAsync` pattern, but using MEAI's built-in approval types instead of custom `ApprovalRequest`/`ApprovalDecision`/`ApprovalTicket` |
| `AgentWorkflow` continue-as-new with `_currentStateBag` | Session state carried across workflow transitions | `DurableChatWorkflow` carrying `ChatMessage[]` history + reducer state across `ContinueAsNew` |
| `TemporalAgentTelemetry` spans (`agent.client.send`, `agent.turn`) | OTel instrumentation | `DurableChatClient` could emit `gen_ai.client.*` spans that compose with MEAI's `OpenTelemetryChatClient` |
| `DefaultTemporalAgentClient.RunAgentAsync` | Starts/updates workflow for a chat turn | `DurableChatClient.GetResponseAsync` sending `[WorkflowUpdate]` |
| `TemporalAgentSession` + `TemporalAgentSessionId` | Session ID encoding (`ta-{name}-{key}`) | `ChatOptions.ConversationId` → workflow ID mapping |

### 10.3 What Must Stay in Agents

These concerns are inherently MAF-level and have no MEAI equivalent:

| Concern | Why It's MAF-Specific |
|---------|----------------------|
| `TemporalAIAgent` / `TemporalAIAgentProxy` (subclassing `AIAgent`) | MAF's `AIAgent` abstraction — sessions, `CreateSessionCoreAsync`, `RunCoreAsync` |
| `AgentWorkflowWrapper` (applies `RunRequest` overrides) | `ChatClientAgentRunOptions`, response format filtering, tool name filtering — MAF's agent run customization |
| `AgentSessionStateBag` persistence | MAF's `AgentSessionStateBag.Serialize()` / `FromStateBag()` pattern for `AIContextProvider` state (e.g., Mem0) |
| `IAgentRouter` / `AIModelAgentRouter` | Multi-agent routing by name+description — agent-level concept, not chat-level |
| `TemporalWorkflowExtensions.GetAgent()` / `ExecuteAgentsInParallelAsync()` | Workflow-level sub-agent orchestration — operates on named agents, not raw `IChatClient` |
| `AgentJobWorkflow` / `ScheduleActivities` / `ScheduleRegistrationService` | Scheduling named agent runs — agent lifecycle management |
| Fire-and-forget signal pattern (`RunAgentFireAndForgetAsync`) | Agent-level operational pattern |

### 10.4 Refactoring Path: Agents on Top of Extensions.AI

The most compelling long-term architecture is for `Temporalio.Extensions.Agents` to **use** `Temporalio.Extensions.AI` internally, rather than reimplementing the same Temporal patterns at the MAF level. Here's what that refactoring would look like:

**Phase 1 — Extract the durable chat primitives:**

```csharp
// Before (AgentActivities today):
[Activity]
public async Task<ExecuteAgentResult> ExecuteAgentAsync(ExecuteAgentInput input)
{
    var agent = ResolveAgent(input.AgentName);
    var session = TemporalAgentSession.FromStateBag(sessionId, input.SerializedStateBag);
    // ... rebuild ChatMessage history from custom state entries ...
    // ... call agent.RunAsync() ...
    // ... serialize StateBag, wrap in ExecuteAgentResult ...
}

// After (AgentActivities delegates to DurableChatActivities):
[Activity]
public async Task<ExecuteAgentResult> ExecuteAgentAsync(ExecuteAgentInput input)
{
    var agent = ResolveAgent(input.AgentName);
    var session = TemporalAgentSession.FromStateBag(sessionId, input.SerializedStateBag);

    // Use MEAI-level durable chat for the actual LLM call
    var chatClient = agent.GetService<IChatClient>();
    var response = await chatClient.GetResponseAsync(input.Messages, input.Options);

    // MAF-specific: wrap response, serialize StateBag
    return new ExecuteAgentResult(AgentResponse.From(response), session.SerializeStateBag());
}
```

**Phase 2 — Replace custom history serialization:**

The current project maintains a parallel type hierarchy (`TemporalAgentStateEntry` → `TemporalAgentStateRequest` / `TemporalAgentStateResponse` / `TemporalAgentStateMessage`) with source-generated JSON contexts for serializing conversation history into workflow state. With `Temporalio.Extensions.AI`, this could be simplified:

```csharp
// Before: Custom state entry types + source-gen JSON context
List<TemporalAgentStateEntry> _history;  // in AgentWorkflow

// After: Use MEAI's ChatMessage directly (already JSON-serializable)
List<ChatMessage> _history;  // DurableChatWorkflow manages this
// AgentWorkflow wraps DurableChatWorkflow, adds MAF-specific StateBag on top
```

This eliminates the `State/TemporalAgentState*.cs` files and the `TemporalAgentStateJsonContext` source generator — MEAI's `AIJsonUtilities.DefaultOptions` handles polymorphic `AIContent` serialization natively.

**Phase 3 — Unify HITL:**

Today, `Temporalio.Extensions.Agents` defines its own approval types (`ApprovalRequest`, `ApprovalDecision`, `ApprovalTicket`). MEAI already defines `ToolApprovalRequestContent` and `ToolApprovalResponseContent` with correlated `RequestId`. The MEAI types are richer (they carry the `ToolCallContent` that needs approval) and are already understood by `FunctionInvokingChatClient`.

```csharp
// Before (Agents-level HITL):
var ticket = await TemporalAgentContext.Current.RequestApprovalAsync(
    new ApprovalRequest { Action = "Delete records", Details = "Irreversible." });

// After (backed by MEAI-level durable approval):
// FunctionInvokingChatClient detects ApprovalRequiredAIFunction,
// emits ToolApprovalRequestContent, DurableApprovalHandler
// intercepts it and implements the Temporal WaitConditionAsync gate.
// The agent tool doesn't need to call any Temporal-specific API.
```

This means HITL becomes transparent to tool authors — they just mark their `AIFunction` as `ApprovalRequiredAIFunction` and the durable approval gate is handled by the pipeline.

### 10.5 Coexistence Strategy

During migration, both libraries must coexist on the same worker. This is straightforward because:

1. **Shared task queue:** Both `AgentWorkflow` and `DurableChatWorkflow` can run on the same task queue, registered via the same `AddHostedTemporalWorker` call.

2. **Shared `ITemporalClient`:** Both `DefaultTemporalAgentClient` and `DurableChatClient` resolve the same `ITemporalClient` from DI.

3. **Non-overlapping workflow IDs:** `Temporalio.Extensions.Agents` uses `ta-{agentName}-{key}` workflow IDs. `Temporalio.Extensions.AI` would use `chat-{conversationId}` or a configurable prefix. No collision.

4. **Unified OTel:** Register all activity sources together:
   ```csharp
   Sdk.CreateTracerProviderBuilder()
       .AddSource(
           TracingInterceptor.ClientSource.Name,
           TracingInterceptor.WorkflowsSource.Name,
           TracingInterceptor.ActivitiesSource.Name,
           TemporalAgentTelemetry.ActivitySourceName,     // "Temporalio.Extensions.Agents"
           DurableChatTelemetry.ActivitySourceName)        // "Temporalio.Extensions.AI"
       .Build();
   ```

5. **DI registration composes:**
   ```csharp
   services.AddHostedTemporalWorker("localhost:7233", "default", "ai-queue")
       // MEAI-level durability (new)
       .AddDurableAI()
       // MAF-level agent orchestration (existing)
       .AddTemporalAgents(opts =>
       {
           opts.AddAIAgent(weatherAgent);
           opts.AddAIAgent(billingAgent);
           opts.SetRouterAgent(routerAgent);
       });
   ```

### 10.6 When to Use Which Library

| Scenario | Recommended Library | Reason |
|----------|-------------------|--------|
| "I have an `IChatClient` and want crash-resilient LLM calls" | `Temporalio.Extensions.AI` | Minimal buy-in, no MAF dependency |
| "I want durable tool invocation with automatic retries" | `Temporalio.Extensions.AI` | `DurableAIFunction` wraps any `AIFunction` |
| "I need multi-agent routing with named agents" | `Temporalio.Extensions.Agents` | `IAgentRouter`, agent descriptors, `RouteAsync` |
| "I need agent-to-agent orchestration inside a workflow" | `Temporalio.Extensions.Agents` | `GetAgent()`, `ExecuteAgentsInParallelAsync` |
| "I want HITL approval on tool calls (transparent to tool code)" | `Temporalio.Extensions.AI` | `ApprovalRequiredAIFunction` + pipeline handler |
| "I want HITL approval with custom business logic in agent tools" | `Temporalio.Extensions.Agents` | `TemporalAgentContext.RequestApprovalAsync` |
| "I need session state persistence (StateBag, Mem0)" | `Temporalio.Extensions.Agents` | MAF's `AgentSessionStateBag` + `AIContextProvider` |
| "I want scheduled/deferred agent runs" | `Temporalio.Extensions.Agents` | `ScheduleAgentAsync`, `AgentJobWorkflow` |
| "I'm building a framework-agnostic AI service" | `Temporalio.Extensions.AI` | No MAF dependency, works with any MEAI consumer |

### 10.7 Package Dependency Graph

```
Temporalio.Extensions.Agents (existing)
├── Microsoft.Agents.AI
│   └── Microsoft.Extensions.AI.Abstractions
├── Temporalio.Extensions.AI (proposed, NEW dependency)
│   ├── Microsoft.Extensions.AI.Abstractions
│   ├── Temporalio
│   └── Temporalio.Extensions.Hosting
├── Temporalio
├── Temporalio.Extensions.Hosting
└── Temporalio.Extensions.OpenTelemetry
```

`Temporalio.Extensions.AI` would **not** depend on `Microsoft.Agents.AI` — it only depends on `Microsoft.Extensions.AI.Abstractions` and `Temporalio`. This means it can be used independently by any MEAI consumer (Semantic Kernel, direct OpenAI client, custom implementations) without pulling in the full MAF stack.

`Temporalio.Extensions.Agents` would gain an **optional** dependency on `Temporalio.Extensions.AI` to reuse the durable chat primitives internally. This dependency could be introduced incrementally — the Agents library could start by using the shared workflow/activity patterns while keeping its own MAF-specific wrappers.

### 10.8 Migration Risk Assessment

| Risk | Severity | Mitigation |
|------|----------|-----------|
| Breaking changes in `AgentWorkflow` state serialization | High | Version the workflow type (`DurableChatWorkflow` is a new workflow type, not a modification of `AgentWorkflow`). Existing `AgentWorkflow` instances continue running on the old code path. |
| `TemporalAgentStateJsonContext` removal | Medium | Only remove after all active `AgentWorkflow` instances have completed or been migrated. Use Temporal's workflow versioning (`Workflow.Patched`) if needed. |
| HITL API change (custom types → MEAI types) | Medium | Keep `TemporalAgentContext.RequestApprovalAsync` as a convenience wrapper that internally uses `ToolApprovalRequestContent`. The external API (`GetPendingApprovalAsync`, `SubmitApprovalAsync`) can accept both formats. |
| `ChatOptions.ConversationId` collision with `TemporalAgentSessionId` | Low | Use different workflow ID prefixes (`ta-` for agents, `chat-` for MEAI). Document the convention. |
| Activity name collision | Low | `DurableChatActivities` methods will have different activity names than `AgentActivities.ExecuteAgentAsync`. No overlap. |

---

## Appendix A: Source File Locations

### Microsoft.Extensions.AI.Abstractions
| File | Purpose |
|------|---------|
| `ChatCompletion/IChatClient.cs` | Core chat interface |
| `ChatCompletion/DelegatingChatClient.cs` | Middleware base class |
| `ChatCompletion/ChatMessage.cs` | Message data type |
| `ChatCompletion/ChatResponse.cs` | Response data type |
| `ChatCompletion/ChatOptions.cs` | Per-request configuration |
| `ChatCompletion/UsageDetails.cs` | Token counting |
| `Functions/AIFunction.cs` | Invocable function abstraction |
| `Functions/DelegatingAIFunction.cs` | Function middleware base |
| `Functions/ApprovalRequiredAIFunction.cs` | HITL marker |
| `Functions/AIFunctionFactory.cs` | Reflection-based function creation |
| `Contents/ToolApprovalRequestContent.cs` | Approval request content type |
| `Contents/ToolApprovalResponseContent.cs` | Approval response content type |
| `ChatReduction/IChatReducer.cs` | History reduction interface |
| `Embeddings/IEmbeddingGenerator.cs` | Embedding interface |
| `Embeddings/DelegatingEmbeddingGenerator.cs` | Embedding middleware base |

### Microsoft.Extensions.AI (Implementation)
| File | Purpose |
|------|---------|
| `ChatCompletion/ChatClientBuilder.cs` | Pipeline builder |
| `ChatCompletion/FunctionInvokingChatClient.cs` | Auto tool invocation middleware |
| `ChatCompletion/FunctionInvocationContext.cs` | Mutable invocation context |
| `ChatCompletion/CachingChatClient.cs` | Abstract caching base |
| `ChatCompletion/DistributedCachingChatClient.cs` | IDistributedCache implementation |
| `ChatCompletion/OpenTelemetryChatClient.cs` | OTel instrumentation |
| `ChatCompletion/LoggingChatClient.cs` | Logging middleware |
| `ChatCompletion/ConfigureOptionsChatClient.cs` | Options injection |
| `ChatCompletion/ReducingChatClient.cs` | History reduction middleware |
| `ChatReduction/MessageCountingChatReducer.cs` | Count-based reducer |
| `ChatReduction/SummarizingChatReducer.cs` | LLM-based summarizer |

### Temporal .NET SDK (from [Integration Review](./temporal-sdk-dotnet-integration-review.md))
| Extension Point | File |
|----------------|------|
| `IClientInterceptor` | `src/Temporalio/Client/Interceptors/IClientInterceptor.cs` |
| `IWorkerInterceptor` | `src/Temporalio/Worker/Interceptors/IWorkerInterceptor.cs` |
| `IPayloadCodec` | `src/Temporalio/Converters/IPayloadCodec.cs` |
| `SimplePlugin` | `src/Temporalio/Common/SimplePlugin.cs` |
| `TracingInterceptor` (reference impl) | `src/Temporalio.Extensions.OpenTelemetry/TracingInterceptor.cs` |
| DI hosting | `src/Temporalio.Extensions.Hosting/TemporalHostingServiceCollectionExtensions.cs` |

---

*Report generated 2026-03-18. Based on source analysis of `Microsoft.Extensions.AI` (dotnet/extensions) and `Temporal .NET SDK` (temporalio/sdk-dotnet).*
