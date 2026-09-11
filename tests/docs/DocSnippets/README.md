# DocSnippets — compile-only harness for the MAF how-to docs

A `net10.0` **library**. No `OutputType=Exe`, no entry point, no tests. Nothing here runs. The
compiler is the assertion: if a documented registration example stops binding to a real overload,
this project fails to build and `just build` — and therefore CI — goes red.

## Why not just grep the docs

A regex cannot tell these two apart:

```
| `agent.RetryPolicy`  | `opts.DefaultRetryPolicy` |     <- correct: opts is TemporalAgentsOptions
1. `agent.AddTool(t, opts => opts.DefaultRetryPolicy = ...)`  <- wrong: opts is DurableToolOptions
```

Same token, four lines apart, different lambda binding. The compiler separates them; nothing else
in the repository does. Zero factory-first defects exist under `samples/` and nine exist under
`docs/` — the difference is that samples are compiled.

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

Text between the markers stays verbatim. Anything the harness has to add — a `using`, a `#pragma`,
a type alias — goes outside them, with a comment saying why.

## Gates

| Command | What it proves |
|---|---|
| `just build` | every snippet compiles |
| `just verify-doc-snippets` | self-test, then: every qualifying doc block has a compiled counterpart |
| `just verify-maf-doc-api-contracts` | self-test, then: fast regex pre-filter (stale names, factory-first `AddTool`) |

`just verify-doc-snippets` runs `scripts/verify-doc-snippets.selftest.sh` first. That self-test
copies this project, drops the name argument from a real `AddTool` call, and **requires the build to
fail**. Without it, a harness whose files silently stopped being compiled would report success
forever — the failure mode `verify-markdown-links.selftest.sh` exists to prevent.

Deliberately-uncompiled blocks live in `scripts/doc-snippet-allowlist.txt`, one reason per entry.

## Doc defects this harness currently carries corrections for

The snippets below are written in the **correct** form, so this project is green, while the docs
are not yet. Each file names its defect in a header comment. Removing an entry here is the signal
that a doc fix landed.

| Doc | Defect |
|---|---|
| `quickstart.md` §1 | `AddTool(sp => …)` — the factory overload takes the **name first** |
| `usage.md` §Worker-hosted example | same, three sites |
| `tool-interceptor.md` §RequireApproval / §Registration / §Per-tool opt-out / §Interceptor activity timeout | same, five sites |
| `usage.md` §Inheritance (prose) | `opts.DefaultRetryPolicy` on `DurableToolOptions`; the property is `RetryPolicy` |
| `usage.md` §Reducing the LLM Context Window | `GetChatClient(…).AsBuilder()` — `AsBuilder` is an `IChatClient` extension, so `.AsIChatClient()` is missing |
| `usage.md` §Reducing the LLM Context Window | `MessageCountingChatReducer` is `[Experimental("MEAI001")]`; the doc never says the reader must suppress it |
| `observability.md` §Setup, `usage.md` §Setup | the shown `using` list omits `Microsoft.Agents.AI` (for `pipeline.UseOpenTelemetry`) and `OpenTelemetry` (for `Sdk`) |
| `dos-and-donts.md`, `llm-call-interception.md` | the example decorator is named `LoggingChatClient`, which collides with `Microsoft.Extensions.AI.LoggingChatClient` — worth renaming in the docs |
