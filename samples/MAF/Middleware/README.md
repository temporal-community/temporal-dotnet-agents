# Middleware boundaries

A two-turn durable weather agent with three explicitly labeled layers:

| Layer | Where it runs | Retry/crash behavior |
|---|---|---|
| `CallerGuardAgent` around the keyed proxy | Caller process | Runs for each client invocation; not resumed after a client crash |
| `AuditingChatClient.GetResponseAsync` | Model activity | Runs per model attempt, including retries; no in-process tool loop |
| `TemporalAgentOptions.ToolMiddleware` | Tool activity | Runs after required approval, with invocation-scoped services; retries can repeat effects |

The deterministic model stand-in needs no OpenAI credentials. Start a Temporal server, then:

```bash
dotnet run --project samples/MAF/Middleware
```

Set `TEMPORAL_ADDRESS` to target another server. Search attributes are disabled.
No `--search-attribute` flags are needed for this sample.
The output includes both weather answers and `Middleware sample complete.`

Middleware must await `next` and call it at most once, preserving the supplied function, call
identity, arguments and services. Returning a serializable value without `next` is a supported
short-circuit. Transforming the returned result is also supported. Setting `context.Terminate`
ends the turn after that result, skipping the remaining calls and the next model round.

The function and agent parameters expose declarations/metadata, not bypass routes for inline
invocation. `Messages` and iteration metadata describe the initiating managed-session batch;
they are read-only. `Options` is null and streaming is unsupported.

This sample uses read-only tools. Effectful tools need downstream idempotency/reconciliation before
enabling retries; no middleware placement guarantees exactly-once effects. In a real client, persist
each RequestId with its request and resend it unchanged while its terminal outcome is retained.

See [Intercepting LLM Calls](../../../docs/how-to/MAF/llm-call-interception.md)
and [Tool Interceptors](../../../docs/how-to/MAF/tool-interceptor.md).
