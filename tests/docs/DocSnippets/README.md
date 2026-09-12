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
in the repository does. When this harness was written, zero factory-first defects existed under
`samples/` and nine existed under `docs/` — the only structural difference being that samples are
compiled. This project closes that gap for the docs.

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
sequence. Three properties matter, and an earlier, weaker version of this check had none of them:

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

Without this check, compilation only proves the *snippet* is valid, not that the *doc* is: a doc
line that grows a trailing comment while the harness copy does not leaves the harness quietly
asserting something the reader never sees. That exact drift happened once and is why the gate
exists.

## Gates

| Command | What it proves |
|---|---|
| `just build` | every snippet compiles |
| `just verify-doc-snippets` | two self-tests, then: every qualifying doc block has a compiled counterpart, and every snippet still matches the doc block it quotes |

`just verify-doc-snippets` runs `scripts/verify-doc-snippets.selftest.sh` first. That self-test
copies this project, drops the name argument from a real `AddTool` call, and **requires the build to
fail**. Without it, a harness whose files silently stopped being compiled would report success
forever — the failure mode `verify-markdown-links.selftest.sh` exists to prevent.

Deliberately-uncompiled blocks live in `scripts/doc-snippet-allowlist.txt`, one reason per entry.

## Doc defects this harness carries corrections for

**None — the list is empty.** Every snippet below the markers now matches its doc verbatim.

That is the steady state, not the finished state. When this harness first ran it carried corrections
for nine factory-first `AddTool` sites, a `DurableToolOptions.DefaultRetryPolicy` that never existed,
a missing `.AsIChatClient()`, an unmentioned `[Experimental("MEAI001")]` suppression, two absent
`using` directives, and an example type colliding with `Microsoft.Extensions.AI.LoggingChatClient`.
All of those are fixed in the docs.

When a new one appears, write the snippet in the **correct** form so this project stays green, name
the defect in the file's header comment, and add a row here. Removing the row is the signal that the
doc fix landed.

Also worth knowing: the older defects were found by *compiling*, not by reading. Three of them —
the missing `using` in the `TemporalAgentContext` example, the absent `.AsIChatClient()`, and the
`MEAI001` suppression — are invisible to any regex and were only ever going to surface here.
