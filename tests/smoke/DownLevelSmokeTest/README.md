# Down-Level Runtime Gate (`netstandard2.1` asset)

Standalone smoke test that proves the **`netstandard2.1` assets** of
`TemporalCommunity.Extensions.AI` and `.Agents` actually **RUN** on a down-level
runtime — not just that they compile. A green `just build` proves API
compatibility only; this gate proves Temporal's native core + the workflow
sandbox + activity dispatch + JSON polymorphism execute on the ns2.1 leg.

It consumes the **packed `.nupkg`** from a local feed (`artifacts/packages`, via
the scoped `nuget.config` here) — **not** a `ProjectReference` — so `restore` and
nearest-TFM **asset selection** are part of the test. It is deliberately
**outside `TemporalAgents.slnx`** so `just build` never pulls it in.

The test drives one durable chat turn + one durable tool call and one durable-agent
turn end-to-end against an **embedded** Temporal dev server
(`WorkflowEnvironment.StartLocalAsync()` — no external `temporal server start-dev`). It uses
inline scripted `IChatClient` instances, so it validates both package paths on ns2.1 with no
live LLM / no `OPENAI_API_KEY` required. It fails before server startup unless **both** loaded
library assemblies report `netstandard2.1`. Search attributes are disabled for the agent flow
because the self-contained dev server has no custom search-attribute mappings. Exit code `0` =
PASS, non-zero = FAIL.

---

## Prerequisites

1. **Pack first** — the local feed must contain the packages under test:
   ```bash
   just pack           # writes artifacts/packages/*.nupkg
   ```
2. **Pass the package version explicitly** (MinVer derives it from git; check the
   `just pack` output line "Creating NuGet packages with version: X.Y.Z-preview.N"):
   ```bash
   PACKED_VERSION=X.Y.Z-preview.N dotnet run -c Debug
   ```

---

## Running the gate

### Option A — genuine `.NET Core 3.1` (the real gate; requires provisioning)

The project targets `netcoreapp3.1`, so NuGet selects `lib/netstandard2.1` from
our packages and `lib/netcoreapp3.1` from `Temporalio`.

```bash
cd tests/smoke/DownLevelSmokeTest
PACKED_VERSION=X.Y.Z-preview.N dotnet run -c Debug  # requires Microsoft.NETCore.App 3.1
```

**This cannot run on the current dev box.** See "Runtime availability" below —
.NET Core 3.1 for **arm64 macOS was never released** (arm64 macOS support began
at .NET 6), and only .NET 8 + .NET 10 runtimes are installed here. Provision 3.1
on a supported host/CI container (below) to execute the real gate.

### Option B — `net8.0` fallback proxy (weaker, but runnable locally)

`net8.0` cannot consume the `lib/net10.0` asset (higher major), so NuGet falls
back to `lib/netstandard2.1` — the **same** ns2.1 assembly, running on an
installed runtime. This is a weaker proxy than genuine 3.1 (it does not prove
3.1's older BCL surface), but it *does* prove the ns2.1 compiled assembly loads
and executes the full durable path.

```bash
just smoke-downlevel-proxy
```

The program prints the loaded library's `compiled TargetFramework`; on this
proxy it reads `.NETStandard,Version=v2.1`, confirming the ns2.1 asset (not
net10.0) is the one running.

---

## Runtime availability (as validated 2026-07-09)

`dotnet --list-runtimes` / `--list-sdks` on the dev box:

- Runtimes: `Microsoft.NETCore.App` 8.0.28, 10.0.0, 10.0.1, 10.0.5 — **no 3.1**.
- SDKs: 8.0.422, 10.0.100/101/201 — **no 3.1**.

Attempting `dotnet bin/Debug/netcoreapp3.1/DownLevelSmokeTest.dll` fails with
`You must install or update .NET ... Framework: 'Microsoft.NETCore.App', version
'3.1.0' (arm64)`. .NET Core 3.1 is EOL (Dec 2022) and has **no arm64 macOS
build**, so genuine 3.1 cannot be provisioned natively on this machine.

**Verdict: the genuine .NET Core 3.1 gate passed in the Docker container**
(`mcr.microsoft.com/dotnet/sdk:3.1`) on arm64 macOS. It loaded both packaged
`netstandard2.1` assets and completed the durable chat/tool and durable-agent paths.
The `net8.0` proxy remains a fast local check; the container is the authoritative gate.

---

## Provisioning genuine .NET Core 3.1 to close the gate

Pick one; all keep the dev box untouched (no global SDK install):

1. **Pinned CI container (recommended).** The publish workflow runs the smoke project in
   `mcr.microsoft.com/dotnet/sdk:3.1` after packing and before NuGet authentication or upload.
   A failure blocks publication. This is the canonical gate.

2. **Local `dotnet-install` of the 3.1 runtime, side-by-side** (on a supported
   RID — i.e. not arm64 macOS):
   ```bash
   curl -sSL https://dot.net/v1/dotnet-install.sh | \
     bash -s -- --runtime dotnet --channel 3.1 --install-dir "$HOME/.dotnet-runtimes/3.1"
   DOTNET_ROOT="$HOME/.dotnet-runtimes/3.1" \
     "$HOME/.dotnet-runtimes/3.1/dotnet" bin/Debug/netcoreapp3.1/DownLevelSmokeTest.dll
   ```
   (On arm64 macOS this fails — 3.1 has no arm64-osx runtime. Use option 1 or 3.)

3. **Rosetta / x64 emulation on macOS** — install the 3.1 **x64** runtime and run
   the app under x64. Viable but slower and less representative than a CI
   container; documented only as a last resort.

---

The gate is green iff the process exits `0` and prints `=== GATE RESULT: PASS ===`.

---

## Files

- `DownLevelSmokeTest.csproj` — `netcoreapp3.1` app; `PackageReference`s to the
  two packed libraries; repo-wide props disabled.
- `Directory.Build.props` / `.targets` — **empty**; stop MSBuild walking up to the
  repo root (which would inject MinVer + central package management).
- `nuget.config` — scoped local feed (`../../../artifacts/packages`) + nuget.org.
- `Program.cs` — the durable chat + tool smoke test with pass/fail assertions.
