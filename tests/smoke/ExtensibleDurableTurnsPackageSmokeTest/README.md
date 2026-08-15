# Extensible durable turns packed-package smoke test

This standalone consumer is intentionally outside `TemporalAgents.slnx`. It has no project
references, central package management, repository build props/targets, or internal visibility.
It validates the freshly packed `TemporalCommunity.Extensions.AI` and
`TemporalCommunity.Extensions.Agents` artifacts through their public NuGet surface.

Run from the repository root:

```bash
just smoke-extensible-turns
```

The gate packs the current exact MinVer version, creates a temporary NuGet global-packages folder,
disables the HTTP cache, verifies local source metadata and package SHA-512 values, and runs twice:

- `net10.0`, which must select both packages' `lib/net10.0` assets;
- `net8.0`, which must select both packages' `lib/netstandard2.1` assets.

The executable starts an embedded Temporal server and uses separate client and worker service
providers. The thin client registers no tools or schemas; the worker owns a named toolset and the
custom workflow records that toolset's resolved manifest. The gate covers client-only converter
wiring, worker-owned toolsets, the public typed workflow base, approve and deny decisions,
activity retry with a fresh/disposed DI scope per attempt, sequential typed state, separate
resolver/model/tool activities, invalid dispatch rejection, and the one-turn-per-Update guard.
