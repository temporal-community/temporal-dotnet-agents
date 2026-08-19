# Security boundary

`TemporalCommunity.Extensions.AI` and `TemporalCommunity.Extensions.Agents` run inside trusted
application and worker processes. They provide durable routing, execution, and approval state; they
do not authenticate an external caller or decide whether that caller owns an application resource.

## Required application boundary

Before a web endpoint, dashboard, MCP server, or background service reads or changes a session:

1. Authenticate the external principal.
2. Resolve an application-owned conversation, approval, or operation resource on the server.
3. Authorize the principal against that resource and the requested operation.
4. Read the opaque Temporal conversation/session/workflow identifier from trusted server-side data.
5. Only then call the library's typed client. Keep typed durable clients and raw `ITemporalClient`
   out of untrusted client code.

A conversation ID, `TemporalAgentSessionId`, workflow ID, approval request ID, tool call ID, schema
fingerprint, or idempotency key is a routing/integrity value—not a bearer credential or proof of
tenant ownership. `UseExisting` intentionally attaches to the workflow with that exact ID; it does
not authorize the attachment.

## Approval and effect boundary

An ordinary approval decides one pending workflow request. It does not establish reviewer identity
or permanently authorize an external effect. The optional MAF session-scope administration service
is broader and must be restricted to an authenticated administrative backend.

Treat approval descriptions, `ReviewData`, `Actor`, and `Reason` as untrusted durable payloads. They
may be retained in workflow history, logs, traces, or exports. Do not place secrets or unnecessary
personal data in them, and do not use them as permission evidence.

Immediately before an activity performs a write, it must re-read current authoritative tenant,
ownership, policy, and resource state. Use an idempotency strategy appropriate to the external
system. Approval and Temporal activity retries do not provide exactly-once external effects.

## Tool and schema boundary

Tool selection and toolsets control what the model may see and request. Schema fingerprints detect
drift. Neither mechanism authenticates a tool server or authorizes a business effect. For remote
tools, authenticate the transport/server, pin or explicitly allow the intended declarations, and
perform effect-time authorization in the authoritative service.

The libraries intentionally do not expose an `IConversationAccessPolicy`, identity-bearing approval
payload, HMAC workflow-ID API, or authorization middleware. Such an API would imply a security
decision without access to the application's identity and resource model.
