# Structured output

Three ways to get typed data out of an agent. They are ordered by how much the library does for
you, and the first one that fits is the right one.

| Use | When |
|---|---|
| MAF's `RunAsync<T>` | Default. The provider honours JSON-schema decoding and a malformed reply should fail loudly. |
| `RunStructuredAsync<T>` on the agent proxy | The model may fence or surround JSON with prose, or you want a corrective retry. |
| `ChatResponseFormat` + manual parse | You need the raw text, or your own parsing and error handling. |

The examples request a JSON schema. The difference is what happens to the reply afterwards. The
direct `ITemporalAgentClient` overload of `RunStructuredAsync<T>` has narrower parsing behavior,
described below.

---

## 1. MAF's `RunAsync<T>` — the default

`AIAgent.RunAsync<T>` comes from Microsoft Agent Framework, not from this library. It generates a
JSON Schema from `T`, sets it as the response format, and returns a lazily-deserialized result.

```csharp
public record WeatherReport(string City, double TemperatureC, string Summary);

var session = await agentProxy.CreateSessionAsync();

AgentResponse<WeatherReport> response = await agentProxy.RunAsync<WeatherReport>(
    [new ChatMessage(ChatRole.User, "What's the weather in Seattle?")],
    session);

WeatherReport report = response.Result;
```

Note the shape: it returns `AgentResponse<T>`, and `.Result` performs the deserialization — so a
malformed reply throws at the property access, not at the `await`.

Through a durable proxy, MAF's generated response format is carried on `RunRequest` and stored with
the request in session history. Its replay behavior and stored fields are described below.

**It does not tolerate a fenced reply.** If the model answers with ```` ```json ```` around the
object, `.Result` throws. The next option adds fence recovery and corrective retries.

---

## 2. `RunStructuredAsync<T>` — typed output with corrective retries

On the durable `AIAgent` proxy, this library's extension calls MAF's typed run underneath — so you
keep the generated schema — then adds markdown-fence/JSON extraction and an optional corrective
retry that includes the parse error.

```csharp
WeatherReport report = await agentProxy.RunStructuredAsync<WeatherReport>(
    [new ChatMessage(ChatRole.User, "What's the weather in Seattle?")],
    session);
```

The proxy overload returns `T` directly. Its correction loop retries deserialization failures; an
empty response bypasses that loop, and an agent invocation failure follows the agent's own
error/retry behavior.

**Order of operations for the proxy overload:**

1. Run the agent with the schema from `T` attached.
2. Try to read the result exactly as MAF would — this also unwraps the object MAF wraps around
   non-object `T` such as `List<Report>` or `int`.
3. If that fails, strip fences and extract a balanced JSON value, then try again.
4. If that fails and a retry remains, go back to step 1. When `IncludeErrorContext` is enabled, the
   retry input also includes the failed reply and parse error; otherwise it is a blind retry.

### Why the name is not `RunAsync<T>`

For this call shape, MAF's applicable `RunAsync<T>` instance method on `AIAgent` is selected before
a same-named extension method. An extension with that name would therefore be unreachable, and the
symptom would be a different return type. `RunStructuredAsync<T>` cannot be shadowed, and the call
site says which behaviour is in play.

### Available on the registered proxy and client

```csharp
// External caller
var proxy = services.GetTemporalAgentProxy("AnalystAgent");
var proxyAnalysis = await proxy.RunStructuredAsync<AnalysisResult>(messages, session);

// Via the client
var clientAnalysis = await client.RunStructuredAsync<AnalysisResult>(
    sessionId,
    new RunRequest(messages),
    correlationId: Guid.NewGuid().ToString("N"));
```

The `ITemporalAgentClient` overload does not call MAF's typed run or strip fences. Empty responses
also bypass its correction loop. When
`request.ResponseFormat` is unset, it generates a schema from `T`; a format already on the request
takes precedence. It deserializes the response text directly and retries parse failures, but does
not add MAF's wrapper for non-object types. Use the proxy overload when you need fence salvage or
MAF's non-object wrapper behavior. If the provider requires an object-root schema, use an object
type with the direct client overload.

---

## 3. `ChatResponseFormat` — manual control

Set the response format yourself; the library returns the raw text without fence stripping,
deserialization, or corrective retries, leaving parsing and error handling to you.

```csharp
var options = new TemporalAgentRunOptions
{
    ResponseFormat = ChatResponseFormat.ForJsonSchema<WeatherReport>()
};

var response = await agentProxy.RunAsync("What's the weather?", session, options);
var report = JsonSerializer.Deserialize<WeatherReport>(response.Text!);
```

Reach for this when you need the raw text — to log it, to inspect it before parsing, or because
your error handling differs from the retry loop above.

The response format on a run request does not carry to a later request in the same session. A
registration-level `ChatOptions.ResponseFormat`, if configured, remains a default for that agent.

---

## Configuration

```csharp
var report = await agentProxy.RunStructuredAsync<WeatherReport>(
    messages,
    session,
    new StructuredOutputOptions
    {
        MaxRetries = 3,
        IncludeErrorContext = true,
        JsonSerializerOptions = myOptions,
    });
```

| Property | Default | Behaviour |
|---|---|---|
| `MaxRetries` | `2` | Parse-failure retries after the initial call; the default allows up to 3 calls. |
| `IncludeErrorContext` | `true` | Adds the failed reply and correction prompt to the retry input. Each retry submits the accumulated input as a new run on the same session, so history grows by that full input. Turning this off makes retries blind repeats. |
| `JsonSerializerOptions` | `null` → web defaults | camelCase, case-insensitive. |

### The JSON defaults are deliberate

The default is `JsonSerializerDefaults.Web`, which accepts conventional camelCase output and is
case-insensitive.

Do not substitute `JsonSerializerOptions.Default` here. It is PascalCase and case-**sensitive**,
and against `{"city":"Seattle"}` it does not throw — it returns a `WeatherReport` with every
member defaulted. No exception means no retry, so the caller gets a silently empty object rather
than an error. Caller-supplied options replace the default entirely, so this is a real hazard if
you pass `JsonSerializerOptions.Default` explicitly.

The correction loop checks deserialization, not all schema or domain constraints. Valid JSON with
missing or defaulted properties may deserialize successfully and will not trigger a retry; validate
required business invariants separately.

---

## Pitfalls

### Prose containing a brace before the JSON

Fence stripping falls back to finding the first balanced `{` or `[`, so a brace earlier in the
reply wins over the payload:

````text
Use the {city} placeholder.
```json
{"city": "Seattle"}
```
````

That extracts `{city}` and fails to deserialize; when a retry remains, it consumes one. Telling the
agent to answer with JSON and nothing else avoids that recovery.

### Non-object `T`

For `List<Report>`, `int`, and other non-object types, pinned MAF wraps the schema in an object
envelope and `AIAgent.RunAsync<T>` unwraps the result. The durable proxy overload of
`RunStructuredAsync<T>` uses that MAF path. The direct `ITemporalAgentClient` overload sends the
schema from `T` without MAF's wrapper and deserializes the returned text directly; option 3 also
does not add or remove a wrapper.

### Large or polymorphic schemas

`System.Text.Json` will not deserialize an abstract base or interface without polymorphic
configuration — configure `[JsonDerivedType]` or a converter. If a model struggles with a deeply
nested schema, simplify the type before increasing `MaxRetries`.

---

## How the response format is stored

For MAF agents, `AgentSessionRequest` stores the response kind and JSON schema with the request:

- `ResponseType` — `"json"` or `"text"`
- `ResponseSchema` — the schema as a `JsonElement` when the format is `ChatResponseFormatJson`

Only JSON versus text is recorded. For a JSON request, the workflow reconstructs a response format
from the stored schema (or `ChatResponseFormat.Json` if the schema is absent); non-JSON format
details and other response-format metadata are not preserved.
Temporal replay reuses completed activity results, but this
does not make model output deterministic: a retried or newly executed model activity can return a
different response. `ChatMessage`/`AIContent` polymorphism survives via `DurableAIDataConverter`.

The MEAI library has its own `SendStructuredAsync<T>` API on `IDurableChatSessionClient`; see
[schema-bound structured output in the MEAI guide](../MEAI/usage.md#send-schema-bound-structured-output).

### Correlation IDs

`RunStructuredAsync<T>` takes `correlationId` as a first-class parameter rather than routing it
through `TemporalAgentRunOptions`:

```csharp
var report = await proxy.RunStructuredAsync<WeatherReport>(
    messages, session, correlationId: "request-abc-123");
```

For the durable `AIAgent` proxy, omitting it lets the proxy generate an ID; a supplied ID is reused
across that overload's correction retries. The direct `ITemporalAgentClient` overload uses its
`correlationId` argument or `request.CorrelationId` for the first call. `RunRequest` does not
generate a correlation ID, and the workflow rejects a missing one, so provide it explicitly (use
`Workflow.NewGuid()` in workflow code and `Guid.NewGuid()` outside). Later client-overload retries
receive fresh correlation and request IDs.

---

## References

- `src/TemporalCommunity.Extensions.Agents/StructuredOutputExtensions.cs`
- `src/TemporalCommunity.Extensions.Agents/StructuredOutputOptions.cs`
- `src/TemporalCommunity.Extensions.Agents/MarkdownCodeFenceHelper.cs`
- `tests/TemporalCommunity.Extensions.Agents.Tests/StructuredOutputExtensionsTests.cs`
- `tests/TemporalCommunity.Extensions.Agents.Tests/MarkdownCodeFenceHelperTests.cs` — 13 edge cases
- [Usage guide](./usage.md)
