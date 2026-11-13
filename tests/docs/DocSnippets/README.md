# DocSnippets — compile-only harness for documentation examples

A `net10.0` **library**. No `OutputType=Exe`, no entry point, no tests. Nothing here runs. The
compiler is the assertion: if a documented registration example stops binding to a real overload,
this project fails to build and `just build` — and therefore CI — goes red.

The coverage gate includes MAF registration examples and selected C# blocks in MEAI
[`usage.md`](../../../docs/how-to/MEAI/usage.md) and
[`embeddings.md`](../../../docs/how-to/MEAI/embeddings.md), plus
[`testing.md`](../../../docs/how-to/MEAI/testing.md) examples selected by the coverage gate.
The MEAI blocks compile verbatim with application configuration supplied as harness parameters.
Dependencies
use the repository pins (MEAI 10.10.0 and Temporalio 1.21.1). The shared test project supplies the
pinned environment helper and controlled activity attempt; these are test infrastructure, not
public library APIs.

## Why not just grep the docs

A regex cannot tell these two apart:

```
| `agent.RetryPolicy`  | `opts.DefaultRetryPolicy` |     <- correct: opts is TemporalAgentsOptions
1. `agent.AddTool(t, opts => opts.DefaultRetryPolicy = ...)`  <- wrong: opts is DurableToolOptions
```

Same token, four lines apart, different lambda binding. The compiler checks overload resolution and
lambda binding in the context of the real dependency versions; text matching alone cannot.

## Layout

```
Harness/        stand-ins for types the docs name but do not define (services, chat clients, …)
Snippets/<Doc>/<Section>.cs   one file per doc section
```

Each snippet file is: harness context (usings, a wrapper method supplying `builder` / `opts` /
`agent`), then the doc text verbatim between markers:

```csharp
// BEGIN SNIPPET docs/how-to/MAF/usage.md#worker-hosted-example (lines 36-74)
...
// END SNIPPET docs/how-to/MAF/usage.md#worker-hosted-example
```

The key is `<doc-path>#<heading-slug>`, which is also a working deep link. The `(lines N-M)` suffix
is a human convenience and is **not** checked — line numbers churn on every unrelated doc edit.
`BEGIN SNIPPET-PROSE` marks a snippet lifted from prose rather than a fenced block; it is held to
the heading still existing, not to a fenced block existing.

Text between the markers stays verbatim, and `scripts/verify-doc-snippet-fidelity.sh` enforces it by
comparing the marker body against the ```csharp block **under the keyed heading**, as an ordered
sequence. The check is:

- **scoped** — a line that happens to appear elsewhere in the file is not a match
- **ordered** — reordered lines are drift
- **bidirectional** — a line added to the doc block and never compiled fails, which a containment
  check cannot see at all

Two normalisations are applied to both sides: common indentation is stripped (the harness wraps doc
text in a class and a method), and *leading* `using` directives and blank lines are dropped (C#
forbids usings inside a method body, so hoisting them is forced).

Adaptations that genuinely cannot be verbatim — a prose snippet whose doc form is not valid C#, a
`[Fact]` that needs injected parameters — go in `scripts/doc-snippet-fidelity-allowlist.txt` with a
written reason. Each entry exempts that snippet from line comparison, so each is a hole in the gate;
an entry whose key no longer exists, or whose snippet has become an exact match, fails the build
rather than lingering.

Anything else the harness has to add — a `using`, a `#pragma`, a type alias — goes outside the
markers, with a comment saying why.

Without this check, compilation can prove the harness copy is valid while a changed documentation
example goes uncompiled. The scoped, ordered comparison keeps the example readers see aligned with
the code the harness builds.

## Gates

| Command | What it proves |
|---|---|
| `just build` | every snippet compiles |
| `just _verify-doc-snippets` | two self-tests, then: every qualifying doc block has a compiled counterpart, and every snippet still matches the doc block it quotes |

The same recipe also runs `scripts/verify-copyable-doc-examples.py`. It extracts the complete MAF
quickstart (including its application-defined weather service), split client, MAF agent/spy
doubles, and MEAI session-client stub directly from their Markdown fences into temporary projects.
Those builds use the repository's pinned dependency versions; quickstart provider packages come
from its documented installation commands. This catches missing abstract/interface members and
missing provider dependencies that a partial registration snippet cannot prove.

`just _verify-doc-snippets` runs `scripts/verify-doc-snippets.selftest.sh` first. That self-test
copies this project, drops the name argument from a real `AddTool` call, and **requires the build to
fail**. Without it, a harness whose files silently stopped being compiled would report success
forever — the failure mode `verify-markdown-links.selftest.sh` exists to prevent.

The outer proxy-invocation examples in MAF usage, routing, dos-and-donts, prompt caching,
agent orchestration, event-driven communication, durability, and library combinations are extracted
directly from their Markdown fences into a checked-in file by `compile_agent_invocation_examples.py`.
These blocks do not all qualify for the registration coverage scanner. Extraction keeps them
verbatim without additional handwritten copies; the harness supplies only imports, containing
types, and method parameters. This checks shared activity-option scope, the two terminal-outcome
exception imports, and preserved retry limits/backoff without executing workflows or providers.
The doc gate checks that the generated file matches the Markdown; `just build` needs no Python.

Deliberately-uncompiled blocks live in `scripts/doc-snippet-allowlist.txt`, one reason per entry.
