# Structured output

Three ways to get typed data out of an agent. They are ordered by how much the library does for
you, and the first one that fits is the right one.

| Use | When |
|---|---|
| MAF's `RunAsync<T>` | Default. The provider honours JSON-schema decoding and a malformed reply should fail loudly. |
| `RunStructuredAsync<T>` | The model wraps JSON in code fences or adds prose, or you want an automatic corrective retry. |
| `ChatResponseFormat` + manual parse | You need the raw text, or your own parsing and error handling. |

All three send a schema to the model. The difference is what happens to the reply afterwards.

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

This works through the durable pipeline unchanged. The response format is carried on `RunRequest`,
persisted into the session history, and reapplied on replay.

**It does not tolerate a fenced reply.** If the model answers with ```` ```json ```` around the
object, `.Result` throws. That is the entire reason the next option exists.

---

## 2. `RunStructuredAsync<T>` — fence-tolerant, with corrective retries

This library's addition. It calls MAF's typed run underneath — so you keep the generated schema —
then adds two things: markdown-fence stripping, and a retry that shows the model its own parse
error.

```csharp
WeatherReport report = await agentProxy.RunStructuredAsync<WeatherReport>(
    [new ChatMessage(ChatRole.User, "What's the weather in Seattle?")],
    session);
```

It returns `T` directly, and throws at the `await` if every attempt fails.

**Order of operations per attempt:**

1. Run the agent with the schema from `T` attached.
2. Try to read the result exactly as MAF would — this also unwraps the object MAF wraps around
   non-object `T` such as `List<Report>` or `int`.
3. If that fails, strip markdown fences and try again.
4. If that fails and a retry remains, append the failed reply plus the parse error to the
   conversation and go back to step 1.

### Why the name is not `RunAsync<T>`

MAF declares `RunAsync<T>` as an *instance* method on `AIAgent`, and an instance method always wins
over an extension method. An extension with that name is unreachable from the call shape most
people write, and the only symptom is a quietly different return type. `RunStructuredAsync<T>`
cannot be shadowed, and the call site says which behaviour is in play.

### Available on all three receivers

```csharp
// Inside a workflow
var agent = WorkflowAgents.GetTemporalAgent("AnalystAgent");
var analysis = await agent.RunStructuredAsync<AnalysisResult>(messages, session);

// External caller
var proxy = services.GetTemporalAgentProxy("AnalystAgent");
var analysis = await proxy.RunStructuredAsync<AnalysisResult>(messages, session);

// Via the client
var analysis = await client.RunStructuredAsync<AnalysisResult>(sessionId, new RunRequest(messages));
```

---

## 3. `ChatResponseFormat` — manual control

Set the response format yourself and parse the text however you like. Nothing is stripped,
deserialized, or retried.

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

`ResponseFormat` is **per request**. Setting it on one call does not carry to the next call in the
same session.

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
| `MaxRetries` | `2` | Retries after the initial call, so 3 attempts total. Each retry adds two messages to the conversation. |
| `IncludeErrorContext` | `true` | Appends the failed reply and the parse error so the model can self-correct. Turning it off makes retries blind repeats. |
| `JsonSerializerOptions` | `null` → web defaults | camelCase, case-insensitive. |

### The JSON defaults are deliberate

The default is `JsonSerializerDefaults.Web` because that is what models emit.

Do not substitute `JsonSerializerOptions.Default` here. It is PascalCase and case-**sensitive**,
and against `{"city":"Seattle"}` it does not throw — it returns a `WeatherReport` with every
member defaulted. No exception means no retry, so the caller gets a silently empty object rather
than an error. Caller-supplied options replace the default entirely, so this is a real hazard if
you pass `JsonSerializerOptions.Default` explicitly.

---

## Pitfalls

### Prose containing a brace before the JSON

Fence stripping falls back to finding the first balanced `{` or `[`, so a brace earlier in the
reply wins over the payload:

```text
Use the {city} placeholder.
```json
{"city": "Seattle"}
```
```

That extracts `{city}`, fails to deserialize, and costs a retry. Telling the agent to answer with
JSON and nothing else is cheaper than paying for the recovery.

### Non-object `T`

For `List<Report>`, `int`, and other non-object types, MAF wraps the schema in an envelope so the
payload satisfies providers that require a root object. Both `RunAsync<T>` and
`RunStructuredAsync<T>` unwrap it for you. Hand-parsing the raw text with option 3 does not.

### Large or polymorphic schemas

`System.Text.Json` will not deserialize an abstract base or interface without a discriminator —
configure `[JsonDerivedType]` or a converter. Deeply nested schemas raise the odds of a malformed
first attempt; flattening the type usually beats raising `MaxRetries`.

---

## How the response format is stored

`ChatResponseFormat` is serialized into the conversation history on `AgentSessionRequest`, the
MAF-specific subclass of `DurableSessionRequest`:

- `ResponseType` — `"json"` or `"text"`
- `ResponseSchema` — the schema as a `JsonElement`, for `ChatResponseFormatJson`

Both fields are MAF-side only; the AI library has no structured-output analogue today. On replay
the same format and messages are sent to the model, so the run stays deterministic.
`ChatMessage`/`AIContent` polymorphism survives via `DurableAIDataConverter`.

### Correlation IDs

`RunStructuredAsync<T>` takes `correlationId` as a first-class parameter rather than routing it
through `TemporalAgentRunOptions`:

```csharp
var report = await proxy.RunStructuredAsync<WeatherReport>(
    messages, session, correlationId: "request-abc-123");
```

Omit it and one is generated — `Workflow.NewGuid()` inside a workflow, `Guid.NewGuid()` outside.
Each retry attempt gets a fresh ID on the client overload.

---

## References

- `src/TemporalCommunity.Extensions.Agents/StructuredOutputExtensions.cs`
- `src/TemporalCommunity.Extensions.Agents/StructuredOutputOptions.cs`
- `src/TemporalCommunity.Extensions.Agents/MarkdownCodeFenceHelper.cs`
- `tests/TemporalCommunity.Extensions.Agents.Tests/StructuredOutputExtensionsTests.cs`
- `tests/TemporalCommunity.Extensions.Agents.Tests/MarkdownCodeFenceHelperTests.cs` — 13 edge cases
- [Usage guide](./usage.md)

_Last updated: 2026-09-10_
