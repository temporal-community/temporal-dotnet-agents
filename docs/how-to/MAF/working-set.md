# Working set provider

`WorkingSetContextProvider` is the one `AIContextProvider` that ships with
`TemporalCommunity.Extensions.Agents` — no extra package reference.

It keeps a coding-style agent oriented on which files are currently in play, without the user
re-stating them. On every LLM step it derives the file paths from the conversation itself, injects a
compact note, and publishes the same list to the session's `StateBag`.

```csharp
opts.AddDurableAgent("CodingAgent", agent =>
{
    agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
    agent.AddContextProvider(new WorkingSetContextProvider());
});
```

It has no DI dependencies and no mutable per-session state, so the instance overload is safe here.
For the general rules on registering providers, see [context-providers.md](./context-providers.md).

---

## What it extracts

It scans **assistant and tool messages only** — user messages are ignored, so a path the user merely
mentions does not enter the working set until the agent acts on it.

Within those messages it looks at three content types:

| Content | What is scanned |
|---|---|
| `TextContent` | The message text |
| `FunctionCallContent` | Each argument **value** — the higher-signal source, since it is a path the model explicitly named |
| `FunctionResultContent` | The result value |

For the two function content types, a value is scanned **only when it is a string or a JSON string**.
A path buried inside a structured JSON object is not found.

Two heuristics identify a path in text:

1. **The first line inside a code fence** — the common ```` ```lang ```` / path convention.
2. **Path-shaped tokens** — a token containing `/` or `\` **and** ending in a recognized extension.

Both conditions are required for the second heuristic, so `Program.cs` alone is not picked up but
`src/Program.cs` is. Recognized extensions cover mainstream languages (`cs`, `py`, `ts`, `js`, `go`,
`rs`, `java`, `kt`, `rb`, `php`, `c`/`cpp`/`h`, `swift`, `dart`, `ex`, `hs`, `lua`, `r`, `sql`,
shell), config and data (`yaml`, `json`, `xml`, `toml`, `ini`, `env`), docs (`md`, `txt`), and
MSBuild files (`csproj`, `sln`, `slnx`, `props`, `targets`).

Paths are deduplicated case-insensitively and kept in **most-recently-seen order** — seeing a path
again moves it to the end rather than adding a duplicate. When the list exceeds `MaxPaths`, the
oldest entries are dropped.

---

## What the model sees

```
## Working set
Recently referenced files/paths in this session:
- src/Auth/AuthService.cs
- src/Data/UserRepository.cs
```

Injected as a system message on each step. With `SilentMode = true` the note is suppressed entirely
and no tokens are added, while the `StateBag` entry is still published.

---

## Configuration

| Property | Behaviour |
|---|---|
| `MaxPaths` | Default `20`. Most-recent paths win when the window overflows. `0` disables tracking. A negative value throws `ArgumentOutOfRangeException` rather than silently behaving like `0` — a silent no-op would leave you with an empty working set and no reason why. |
| `SilentMode` | Default `false`. When `true`, suppresses the injected note but still writes the `StateBag` entry. Use it to piggyback tracking without paying for the tokens. |

---

## Reading the working set elsewhere

The provider publishes to `AgentSessionStateBag["temporal.working_set"]`, exposed as the public
constant `WorkingSetContextProvider.StateBagKey`, as **comma-separated text**.

This is the supported way for another provider or a tool to consume the working set. It matters
because a provider cannot see the messages another provider injected — the `StateBag` is the channel
that works, and the only one that survives a worker restart.

```csharp
if (context.Session is TemporalAgentSession session
    && session.StateBag.TryGetValue(
        WorkingSetContextProvider.StateBagKey,
        out string? csv,
        JsonSerializerOptions.Default)
    && !string.IsNullOrEmpty(csv))
{
    var paths = csv.Split(',');
    // ...
}
```

`StateBag` values are reference types only, which is why this is a string rather than a list.

**Treat the key as a recomputed mirror, not a persistence contract.** It holds whatever paths appear
in the currently retained history — recomputed from scratch on every step, not accumulated. It is
not a judgement that a file is still relevant. When a step extracts nothing the key is **removed**
rather than left holding a stale list, so a reader can trust that what is there is in scope.

If the session is not a `TemporalAgentSession`, or the write fails, the provider logs at Debug and
continues — the injected note still works. It never fails the step.

---

## Boundaries

**History is the workflow's.** Do not pair this with a provider-owned history store; provider-owned
external persistence has no atomic idempotent retry contract in this library.

**Continue-as-new.** `AgentWorkflow` carries the `StateBag` across its own continue-as-new, so the
working set survives. A custom orchestrating workflow driving `TemporalAIAgent` must serialize and
carry its `TemporalAgentSession` itself.

---

## See also

- [context-providers.md](./context-providers.md) — writing and registering providers
- [prompt-caching.md](./prompt-caching.md) — history and token optimization
- [`samples/MAF/WorkingSet`](../../../samples/MAF/WorkingSet/) — a four-turn code assistant that
  builds a working set from mock file reads, with a second provider reading the published key
