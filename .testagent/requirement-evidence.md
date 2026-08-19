# Requirement-to-evidence matrix

| Requirement | Implementation evidence | Verification evidence |
|---|---|---|
| Stable schema failures | `736dc42` | canonicalizer and workflow-boundary unit tests |
| Replay ownership | `f02a75f` | disposition catalog plus all AI replay tests |
| Bounded approval authority | `25bffc0` | one-call, grant expiry/revocation, authorization tests |
| Safe initialization | `ed02583` | early/concurrent Update and healthy-follow-up tests |
| Bounded retries | `ff76fce` | MEAI/MAF construction-path tests and replay |
| Idiomatic tool registration | `655617e` | params/enumerable/one-shot/null/order/identity tests and packed consumer |
| Authoritative CI | `453e718` | workflow syntax/discovery/cache/history ownership tests |
| Security boundary | `c9934e6` | documentation contract tests and sample assertions |
| Ordinary MCP client tools | `12f549d` | real in-process MEAI/MAF transport and replay tests |
| Compression evidence | `2d4c3a9` | deterministic size tests and BenchmarkDotNet report |
| Bounded payload codec | `75c9475` | round-trip, threshold, version, corruption, size, history, and packed tests |
| MCP Task research only | `ec0c4e8` | in-parent/detached-child lifecycle and no-production-reference tests |
| Secure ordinary MCP server | `ec5ef77` | nine authenticated HTTP/Temporal integration scenarios, passed twice |

Final regression: 571 AI unit + 616 Agents unit + 93 AI integration + 100 Agents integration =
1,380 passing tests, with history generators excluded from normal integration execution.
