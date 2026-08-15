# Durable toolsets

Durable toolsets are stable, worker-owned groups of MEAI function declarations, implementations,
and durable dispatch policies. They let the stock managed workflow accept a thin client request
without requiring the client process to construct tool schemas or reference implementation types.

## Authority boundary

Worker registration is live process state and cannot be read by deterministic workflow code. A
package-owned activity therefore resolves the selected worker toolsets once for a new session and
returns a serializable manifest. Temporal records that result in workflow history. The workflow
uses the recorded manifest as the maximum session authority, carries it unchanged through
Continue-as-New, and never refreshes it from the worker registry.

A custom typed workflow may narrow its recorded baseline for one turn. It cannot add a toolset or
function absent from that baseline. The stock thin client does not select toolsets per request in
this feature. Exposure to the model is not business authorization: effecting tools must reauthorize
against current authoritative application data inside their activity.

## Registration rules

- `AddDurableTools` contributes functions to one implicit default toolset.
- `AddDurableToolset(id, ...)` registers a non-empty named toolset.
- `AddDurableToolFactory` registers invocation-scoped activation behind a stable declaration.
- Toolset IDs and visible function names use `StringComparer.Ordinal` everywhere.
- Selected toolsets are combined in configured order; members retain registration order.
- Duplicate selected IDs or visible names fail. There is no precedence or silent deduplication.
- Registrations contain implementations; durable manifests never contain implementations,
  delegates, service providers, reflection objects, or CLR types.

## Manifest compatibility

The first manifest wire format is version `1`. Missing version `0` and unsupported versions fail
non-retryably before model or tool dispatch. The reader may ignore an additive unknown JSON field,
but an authority-bearing change requires a new manifest version or an operational drain of older
sessions. Canonical fingerprint encoding, field order, and exact ordinal name semantics are part of
the versioned contract.

Changing worker registrations affects new sessions only. To adopt a changed baseline, start a new
session; runtime refresh is intentionally unsupported.
