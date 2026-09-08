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
binary against the same fixtures. `LegacyAgentOwnedTurnLoop` reproduces the v0.3 model exactly: a
`List<DurableSessionEntry>` field plus a carried `JsonElement` StateBag that each LLM step replaces
wholesale. `SessionOwnedTurnLoop` does the same work through the session.

Only per-turn state bookkeeping is measured — no Temporal server, no model call. Those dominate real
wall-clock by orders of magnitude and would bury a genuine regression.

`SerializeSnapshot` / `DeserializeSnapshot` have **no v0.3 counterpart** (history never crossed the
wire), so their `Ratio` column is not a regression measure — it is only their cost relative to the
turn loop in the same parameter group. Read their absolute numbers.

**Environment:** BenchmarkDotNet v0.15.8, macOS 26.6.2, Apple M4 Max (14 cores), .NET SDK 10.0.201,
ShortRun job (3 warmup, 3 iterations). Ratios are stable; absolute times are indicative.

---

## Turn-loop results

| Turns | Tools/turn | Legacy (v0.3) | Session-owned (v0.4) | Ratio | Legacy alloc | Session alloc | Alloc ratio |
|------:|-----------:|--------------:|---------------------:|------:|-------------:|--------------:|------------:|
| 1     | 0          | 134.8 ns      | 707.4 ns             | 5.25× | 1.09 KB      | 3.84 KB       | 3.51×       |
| 1     | 4          | 1.08 µs       | 3.11 µs              | 2.88× | 4.00 KB      | 10.46 KB      | 2.62×       |
| 10    | 0          | 2.14 µs       | 15.52 µs             | 7.24× | 15.61 KB     | 57.24 KB      | 3.67×       |
| 10    | 4          | 11.68 µs      | 56.13 µs             | 4.81× | 44.67 KB     | 152.24 KB     | 3.41×       |
| 100   | 0          | 88.51 µs      | 245.22 µs            | 2.77× | 649.96 KB    | 1080.42 KB    | 1.66×       |
| 100   | 4          | 187.22 µs     | 810.67 µs            | 4.33× | 940.59 KB    | 2059.25 KB    | 2.19×       |

## Snapshot round-trip (new capability — no v0.3 baseline)

| Turns | Tools/turn | Serialize | Deserialize | Serialize alloc | Deserialize alloc |
|------:|-----------:|----------:|------------:|----------------:|------------------:|
| 1     | 0          | 2.86 µs   | 2.79 µs     | 3.32 KB         | 6.56 KB           |
| 10    | 0          | 20.68 µs  | 18.21 µs    | 16.56 KB        | 22.73 KB          |
| 100   | 0          | 194.98 µs | 175.86 µs   | 149.37 KB       | 184.51 KB         |

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

The ratios are the honest headline, and they are not small. But the decisive figure is the absolute
per-turn cost. At the worst measured point — a 100-turn conversation with four tools per turn —
session ownership adds **623 µs spread across 100 turns, about 6 µs per turn**. Every one of those
turns also performs at least one LLM activity round trip, which costs on the order of 100 ms. The
added bookkeeping is therefore roughly **0.006% of turn latency**, against a correctness guarantee
that did not previously exist: cross-session isolation and state that survives continue-as-new.

Allocation grows 1.7×–3.7×, peaking at 2.06 MB for the 100-turn / 4-tool case. Gen2 stayed at zero
throughout; the growth is short-lived Gen0/Gen1 traffic.

### Where the cost comes from

The legacy model kept the StateBag as an opaque `JsonElement` and replaced it with a single
assignment. Session ownership makes the inherited `AgentSession.StateBag` authoritative, so every
mutation round-trips: serialize the typed bag, merge, deserialize back. That is what keeps
`session.StateBag` readable by workflow code and by anything MAF hands the session to.

Per tool-calling iteration that is three serializations and two deserializations. Two obvious
reductions were considered and deliberately not taken:

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
