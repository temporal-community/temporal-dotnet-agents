# Test quality analysis

## Assertion strength

The added tests assert externally meaningful outcomes rather than implementation constants:

- workflow failures are non-retryable and leave the workflow healthy;
- no provider/tool/workflow activity is invoked on fail-closed paths;
- replay consumes real checked-in histories, including Continue-as-New;
- approvals cannot silently expand authority and reusable grants expire/revoke;
- retry policies are bounded on every construction path;
- enumerable APIs enumerate once, preserve order and identity, and reject invalid entries;
- ordinary MCP calls cross real in-process transports and schedule separate Temporal activities;
- codec tests validate exact metadata/payload round trips and corrupt/unknown/over-limit rejection;
- MCP Task research uses real negotiated protocol calls but proves no production assembly dependency;
- workflow-backed MCP HTTP tests prove authorization precedes workflow creation and exercise actual
  Temporal terminal states.

## Boundary and negative coverage

The suite covers null/empty/whitespace values, duplicate and conflicting requests, unknown numeric
wire values, retry exhaustion, cancellation, timeout, termination, malformed/corrupt payloads,
compressed-size limits, concurrent requests, tenant separation, retention-ledger fallback, and both
target package assets.

## Pseudo-mutation review

Critical tests would fail if any of these regressions were introduced:

- schema/configuration exceptions escape as ordinary workflow-task failures;
- a checked-in history loses its replay owner;
- ordinary approval restores session/global scope;
- approval initialization or body-path revalidation is removed;
- tool retry defaults return to unlimited;
- an `IEnumerable<AIFunction>` is enumerated more than once;
- policy-sensitive pinned MCP lookup becomes permissive;
- codec decode returns an empty/default payload or accepts an unsupported encoding;
- authorization moves after workflow creation;
- `start_or_join` reports success without obtaining the workflow result;
- caller cancellation automatically cancels accepted durable work.

No elapsed-time assertion or benchmark threshold was added to the regular test suite. Performance
claims use deterministic payload-size assertions and BenchmarkDotNet evidence instead.
