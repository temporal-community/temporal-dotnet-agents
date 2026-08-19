# Broad test-generation research

Scope: implementation of the approved Temporal AI sequencing plan across both shipped libraries,
their unit/integration/replay suites, samples, CI, benchmarks, and package smoke tests.

## Repository conventions

- SDK-style .NET repository pinned to SDK 10.0.100.
- Production libraries target `net10.0;netstandard2.1`; tests and samples target `net10.0`.
- xUnit 2.9.3 with `Microsoft.NET.Test.Sdk`; repository commands use VSTest-style `dotnet test`.
- Unit projects: `TemporalCommunity.Extensions.AI.Tests` and
  `TemporalCommunity.Extensions.Agents.Tests`.
- Integration projects: corresponding `.IntegrationTests` projects; history capture tests carry the
  `HistoryCapture` category and are excluded from normal integration runs.
- Existing replay tests and fixtures live below each unit project's `Compat` directory.
- Tests use `Method_Condition_ExpectedResult`, focused assertions, scripted providers, and the shared
  embedded Temporal environment.
- `MCP_INTEGRATION_REVIEW.md` is a pre-existing untracked file and is out of scope.

## Static pairing baseline

The required Roslyn pairing analyzer found 232 production/sample source files, 198 test files,
134 paired source files, and 98 statically unpaired files. Most unpaired entries are sample contracts
or trivial DTOs. Plan-relevant unpaired logic includes `DurableApprovalMixin`,
`DurableToolsetBuilder`, and several registration/plugin wrappers. Existing behavior tests often reach
these through integration paths despite the analyzer's namespace-based static classification.

## Acceptance checklist

1. Canonical schema/manifest failures become stable non-retryable workflow failures; successful v1
   fingerprints remain unchanged.
2. Pattern-1 and Continue-as-New histories have replay consumers and every fixture has a disposition.
3. Ordinary approval is one-call-only; raw workflow-ID control is removed; reusable session scope is
   explicit, expiring, revocable, and opt-in; Always scope is removed.
4. Workflow input initializes synchronously before the first await and duplicate/invalid approval
   failures are terminal and consistent.
5. Tool activity retries use a tool-specific bounded default on every MEAI/MAF construction path.
6. Registration APIs expose singular configured registration plus plural params/enumerable paths
   without repeated enumeration or extra declaration construction.
7. Pull requests discover all integration projects, own all replay fixtures, verify/cache the pinned
   Temporal binary, and provide a separate time-skipping test environment.
8. Security documentation and HITL/MCP samples preserve authentication, authorization, effect-time
   checks, and tenant-safe errors.
9. Ordinary MCP samples use real SDK 2.2 APIs and exercise separate durable model/tool activities.
10. Payload telemetry is low-cardinality; codec benchmarks cover compressible and incompressible
    inputs; any shipped codec is bounded, opt-in, decoder-first, and fails loudly on mismatch.
11. MCP Task work remains non-shipping research ending in an evidence-backed ADR.
12. The ordinary MCP server sample applies authorization filters before workflow creation and
    documents retention-limited workflow-ID dedupe.
13. Every public/package change builds for both library TFMs and passes packed-consumer smoke.
