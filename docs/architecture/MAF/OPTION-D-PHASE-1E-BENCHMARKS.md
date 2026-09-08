# Option D Phase 1e — recorded benchmark results

Acceptance evidence for moving conversation history and the StateBag from the `TemporalAIAgent`
instance onto `TemporalAgentSession`.

Source: `benchmarks/TemporalCommunity.Extensions.Agents.Benchmarks/SessionOwnedStateBenchmarks.cs`

```
dotnet run --project benchmarks/TemporalCommunity.Extensions.Agents.Benchmarks -c Release \
  -- --filter "*SessionOwnedStateBenchmarks*"
```

---

## Method

The spec called for a baseline on the pre-change implementation. Rather than measure across two git
revisions — which mixes in machine, runtime, and dependency drift — both models run in the same
binary against the same fixtures. `LegacyAgentOwnedTurnLoop` reproduces the v0.3 model: a
`List<DurableSessionEntry>` field plus a carried `JsonElement` StateBag that each LLM step replaces
wholesale. `SessionOwnedTurnLoop` does the same work through the session.

Only per-turn state bookkeeping is measured — no Temporal server, no model call. Those dominate real
wall-clock by orders of magnitude and would bury a genuine regression.

### The three turn shapes, and which one is the baseline

`Shape` distinguishes two questions that an earlier revision of this document conflated:

| Shape | What it is |
|---|---|
| `NoTools` | One LLM step per turn, no tool round. |
| **`ToolsHistorical`** | Four tool calls whose StateBag write-backs are **all null**. **This is the exact v0.3 comparison.** |
| `ToolsProspective` | Four tool calls with **populated** write-backs. **Not a historical baseline** — a forward cost model. |

`ToolsHistorical` is the real one because tool StateBag write-backs were *always* null on the
sub-agent path, and still are: `InvokeAgentToolInput` carries no session ID, so the tool activity
derives its session from the orchestrating workflow's ID, fails to parse it, and never establishes a
`TemporalAgentContext`. The tool therefore returns no bag. Populated write-backs describe a world
where that gap is closed — useful for sizing that future change, misleading as a regression
measure. **Read `ToolsHistorical` when asking "did this change regress v0.3?"**

`SerializeSnapshot` / `DeserializeSnapshot` have **no v0.3 counterpart** (history never crossed the
wire), so their `Ratio` column is not a regression measure either — it is only their cost relative
to the turn loop in the same group. Read their absolute numbers.

**Environment:** BenchmarkDotNet v0.15.8, macOS 26.6.2, Apple M4 Max (14 cores), .NET SDK 10.0.201,
ShortRun job (3 warmup, 3 iterations). Ratios are stable; absolute times are indicative.

---

## Historical baseline — the regression measure

| Turns | Shape | Legacy (v0.3) | Session-owned (v0.4) | Ratio | Added per turn | Legacy alloc | Session alloc | Alloc ratio |
|------:|---|--------------:|---------------------:|------:|---------------:|-------------:|--------------:|------------:|
| 1     | NoTools         | 135.0 ns  | 756.4 ns   | 5.60× | 0.62 µs | 1.09 KB   | 3.84 KB    | 3.51× |
| 1     | ToolsHistorical | 204.5 ns  | 1.03 µs    | 5.02× | 0.82 µs | 1.48 KB   | 4.17 KB    | 2.83× |
| 10    | NoTools         | 2.18 µs   | 16.07 µs   | 7.37× | 1.39 µs | 15.61 KB  | 57.24 KB   | 3.67× |
| 10    | ToolsHistorical | 2.96 µs   | 22.72 µs   | 7.66× | 1.98 µs | 19.44 KB  | 60.52 KB   | 3.11× |
| 100   | NoTools         | 88.44 µs  | 249.73 µs  | 2.82× | 1.61 µs | 649.96 KB | 1080.42 KB | 1.66× |
| 100   | ToolsHistorical | 94.98 µs  | 279.54 µs  | 2.94× | 1.85 µs | 688.24 KB | 1113.23 KB | 1.62× |

"Added per turn" is `(session − legacy) / turns` — the marginal cost the change introduces per
conversational turn.

> The 10-turn `ToolsHistorical` row is noisy (StdDev 5.47 µs on a 22.72 µs mean). Treat its ratio as
> approximate; the 100-turn rows are the stable ones.

## Prospective model — cost if tool write-backs are ever enabled on this path

Not a regression measure. Included to size the missing-session-ID fix before anyone attempts it.

| Turns | Legacy shape-matched | Session-owned | Ratio | Added per turn | Alloc ratio |
|------:|---------------------:|--------------:|------:|---------------:|------------:|
| 1     | 1.09 µs    | 3.12 µs   | 2.85× | 2.02 µs | 2.62× |
| 10    | 11.84 µs   | 56.25 µs  | 4.75× | 4.44 µs | 3.41× |
| 100   | 189.98 µs  | 783.66 µs | 4.12× | 5.94 µs | 2.19× |

Enabling tool write-backs roughly triples the marginal per-turn cost (1.85 µs → 5.94 µs at 100
turns). Still small in absolute terms, but worth knowing before committing to it.

## Snapshot round-trip (new capability — no v0.3 baseline)

| Turns | Serialize | Deserialize | Serialize alloc | Deserialize alloc |
|------:|----------:|------------:|----------------:|------------------:|
| 1     | 2.99 µs   | 2.85 µs     | 3.32 KB         | 6.56 KB           |
| 10    | 21.32 µs  | 18.85 µs    | 16.56 KB        | 22.73 KB          |
| 100   | 190.53 µs | 178.51 µs   | 149.37 KB       | 184.51 KB         |

## Serialized snapshot size

Measured on the same fixture (one user message and one assistant message per turn, plus a
populated StateBag):

| Turns | History entries | Snapshot bytes | Bytes/turn |
|------:|----------------:|---------------:|-----------:|
| 1     | 2               | 879            | 879        |
| 10    | 20              | 7,602          | 760        |
| 100   | 200             | 75,192         | 751        |

Size is linear in turns at roughly **750 bytes per turn** for this message size. Real conversations
with longer messages and tool payloads will be larger.

---

## Verdict

**Accepted.** The change ships.

Against the historical baseline, session ownership adds **about 1.9 µs per turn** at 100 turns with
a tool round (94.98 µs → 279.54 µs across the whole conversation). Every one of those turns also
performs at least one LLM activity round trip costing on the order of 100 ms, so the added
bookkeeping is roughly **0.002% of turn latency** — in exchange for cross-session isolation and
state that survives continue-as-new, neither of which existed before.

The ratios are larger than that framing suggests and are worth stating plainly: 2.8×–7.7× on the
bookkeeping itself, peaking at short conversations where the legacy baseline is only a few hundred
nanoseconds. Allocation grows 1.6×–3.7×, peaking at 1.11 MB for a 100-turn conversation. Gen2 stayed
at zero throughout; the growth is short-lived Gen0/Gen1 traffic.

### Where the cost comes from

The legacy model kept the StateBag as an opaque `JsonElement` and replaced it with a single
assignment. Session ownership makes the inherited `AgentSession.StateBag` authoritative, so every
mutation round-trips: serialize the typed bag, merge, deserialize back. That is what keeps
`session.StateBag` readable by workflow code and by anything MAF hands the session to.

Two reductions were considered and deliberately not taken:

- **Caching the serialized form on the session.** User code can call `session.StateBag.SetValue(...)`
  directly, and the session cannot observe that, so any cache would go stale and serve wrong state.
  Trading a correctness invariant for microseconds is the wrong trade in exactly the component this
  whole effort exists to make correct.
- **Passing the already-serialized bag into the merge.** Saves roughly one serialization in three,
  but introduces a "the caller must pass a bag that is still current" invariant with no way to
  enforce it. Cheap now, a latent replay bug later.

If profiling ever shows this on a real critical path, the right fix is to make
`AgentSessionStateBag` mutation observable, not to bolt on a cache.

### Operational limit worth knowing

At ~750 bytes per turn, a carried session reaches Temporal's default 256 KB payload **warning**
threshold at roughly 350 turns, and the 2 MB **error** threshold at roughly 2,800 turns. History is
uncompacted by design in v0.4. Long-running conversations that continue-as-new should either bound
their turn count per generation or wait for compaction (deferred to a later phase).

Note that this is separate from the existing 64 KB `CarriedStateBag` size guard, which is unchanged.
