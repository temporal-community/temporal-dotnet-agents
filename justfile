set windows-shell := ["pwsh.exe", "-NoLogo", "-Command"]

solution := "TemporalAgents.slnx"
configuration := "Release"
artifacts_dir := "artifacts/packages"
coverage_dir := "artifacts/coverage"
unit_tests_dir := "tests/Temporalio.Extensions.Agents.Tests"
integration_tests_dir := "tests/Temporalio.Extensions.Agents.IntegrationTests"
unit_tests_ai_dir := "tests/Temporalio.Extensions.AI.Tests"
integration_tests_ai_dir := "tests/Temporalio.Extensions.AI.IntegrationTests"

version := `dotnet tool run minver --default-pre-release-identifiers preview`

# List available recipes
default:
    @just --list

# Show project info
info:
    @echo "Solution  : {{solution}}"
    @echo "Version   : {{version}}"
    @echo "Config    : {{configuration}}"
    @echo "Artifacts : {{artifacts_dir}}"
    @echo "Coverage  : {{coverage_dir}}"
# Remove all build output
clean: clean-source clean-tests
    @echo "Clean complete."

# Clean source and sample projects (all projects in solution)
clean-source:
    dotnet clean {{solution}} --configuration {{configuration}} --nologo -v q

# Clean test output directories
clean-tests:
    dotnet clean {{unit_tests_dir}} --configuration {{configuration}} --nologo -v q
    dotnet clean {{integration_tests_dir}} --configuration {{configuration}} --nologo -v q
    dotnet clean {{unit_tests_ai_dir}} --configuration {{configuration}} --nologo -v q
    dotnet clean {{integration_tests_ai_dir}} --configuration {{configuration}} --nologo -v q

# Restore NuGet packages
restore:
    dotnet restore {{solution}}

# Build in Release (default)
build: restore
    dotnet build {{solution}} --configuration {{configuration}} --no-restore

# Build in Debug
build-debug: restore
    dotnet build {{solution}} --configuration Debug --no-restore

# Run unit tests only — Agents library (no Temporal server required)
test-unit: build
    dotnet test {{unit_tests_dir}} \
        --configuration {{configuration}} \
        --no-build \
        --logger "console;verbosity=normal"

# Run unit tests only — AI library (no Temporal server required)
test-unit-ai: build
    dotnet test {{unit_tests_ai_dir}} \
        --configuration {{configuration}} \
        --no-build \
        --logger "console;verbosity=normal"

# Run all unit tests (Agents + AI)
test-unit-all: test-unit test-unit-ai

# Run integration tests only — Agents library (requires: temporal server start-dev)
test-integration: build
    @echo "NOTE: Integration tests require a running Temporal server."
    @echo "      Start one with: temporal server start-dev --namespace default"
    dotnet test {{integration_tests_dir}} \
        --configuration {{configuration}} \
        --no-build \
        --logger "console;verbosity=normal"

# Run integration tests only — AI library (uses in-process test server)
test-integration-ai: build
    dotnet test {{integration_tests_ai_dir}} \
        --configuration {{configuration}} \
        --no-build \
        --logger "console;verbosity=normal"

# Run both unit and integration tests (all libraries)
test: test-unit-all test-integration test-integration-ai

# Run all tests (unit + integration) with code coverage — Agents and AI libraries
test-coverage: build
    rm -rf {{coverage_dir}}
    dotnet test {{unit_tests_dir}} \
        --configuration {{configuration}} \
        --no-build \
        --collect "XPlat Code Coverage" \
        --results-directory {{coverage_dir}}/agents \
        --logger "console;verbosity=normal" \
        -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Exclude="[Temporalio.Extensions.AI]Temporalio.Extensions.AI.DurableAIJsonContext,[Temporalio.Extensions.AI]Temporalio.Extensions.AI.DurableAIJsonUtilities"
    dotnet test {{unit_tests_ai_dir}} \
        --configuration {{configuration}} \
        --no-build \
        --collect "XPlat Code Coverage" \
        --results-directory {{coverage_dir}}/ai \
        --logger "console;verbosity=normal" \
        -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Exclude="[Temporalio.Extensions.AI]Temporalio.Extensions.AI.DurableAIJsonContext,[Temporalio.Extensions.AI]Temporalio.Extensions.AI.DurableAIJsonUtilities"
    dotnet test {{integration_tests_dir}} \
        --configuration {{configuration}} \
        --no-build \
        --collect "XPlat Code Coverage" \
        --results-directory {{coverage_dir}}/agents-integration \
        --logger "console;verbosity=normal" \
        -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Exclude="[Temporalio.Extensions.AI]Temporalio.Extensions.AI.DurableAIJsonContext,[Temporalio.Extensions.AI]Temporalio.Extensions.AI.DurableAIJsonUtilities"
    dotnet test {{integration_tests_ai_dir}} \
        --configuration {{configuration}} \
        --no-build \
        --collect "XPlat Code Coverage" \
        --results-directory {{coverage_dir}}/ai-integration \
        --logger "console;verbosity=normal" \
        -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Exclude="[Temporalio.Extensions.AI]Temporalio.Extensions.AI.DurableAIJsonContext,[Temporalio.Extensions.AI]Temporalio.Extensions.AI.DurableAIJsonUtilities"

# Merge all coverage XML files into an HTML report and print line/branch summary
coverage-report: test-coverage
    dotnet tool run reportgenerator \
        -reports:"{{coverage_dir}}/**/*.cobertura.xml" \
        -targetdir:"{{coverage_dir}}/report" \
        -reporttypes:"HtmlInline_AzurePipelines;Cobertura;TextSummary"
    @cat "{{coverage_dir}}/report/Summary.txt"

# Run tests matching a filter expression (e.g. just test-filter "FullyQualifiedName~Router")
test-filter filter: build
    dotnet test {{unit_tests_dir}} \
        --configuration {{configuration}} \
        --no-build \
        --filter "{{filter}}" \
        --logger "console;verbosity=normal"

# Pack NuGet packages (Release, into artifacts/packages/)
pack: clean build
    @echo "Creating NuGet packages with version: {{ version }}"
    @dotnet pack {{solution}} \
        --configuration {{configuration}} \
        --no-build \
        --output {{artifacts_dir}}
    @echo "Packages written to {{artifacts_dir}}/"

# Publish to NuGet.org (requires NUGET_API_KEY env var)
publish-nuget: pack
    @echo "Publishing to NuGet.org..."
    @if [ -z "$${NUGET_API_KEY:-}" ]; then \
        echo "❌ NUGET_API_KEY environment variable is not set"; \
        exit 1; \
    fi
    @dotnet nuget push "{{artifacts_dir}}/*.nupkg" \
        --api-key "$NUGET_API_KEY" \
        --source "https://api.nuget.org/v3/index.json" \
        --skip-duplicate
    @echo "✓ Packages published to NuGet.org"

# Publish to GitHub Packages (requires NUGET_GITHUB_TOKEN env var)
publish-github: pack
    @echo "Publishing to GitHub Package Registry..."
    @if [ -z "$${NUGET_GITHUB_TOKEN:-}" ]; then \
        echo "❌ NUGET_GITHUB_TOKEN environment variable is not set"; \
        exit 1; \
    fi
    @dotnet nuget push "{{artifacts_dir}}/*.nupkg" \
        --api-key "$NUGET_GITHUB_TOKEN" \
        --source "https://nuget.pkg.github.com/cecilphillip/index.json" \
        --skip-duplicate
    @echo "✓ Packages published to GitHub"

# Alias: build
compile: build

# Alias: test-unit (Agents only, for backward compatibility)
verify: test-unit

# Build + all unit tests (no server required)
validate: build test-unit-all

# Full local CI pipeline: clean → build → test-unit-all → pack
ci: clean build test-unit-all pack

# ---------------------------------------------------------------------------
# Process hygiene — orphan cleanup + safe logging
#
# WorkflowEnvironment.StartLocalAsync() spawns a child `temporal-sdk-dotnet`
# process per integration-test fixture. If the test host is killed (e.g. via
# `pkill -9` to recover from a hang) before DisposeAsync runs, the embedded
# server is left listening on its port indefinitely.
#
# Scoping:
#   `temporal-sdk-dotnet` is the .NET SDK's extracted CLI binary
#   (`/var/folders/.../T/temporal-sdk-dotnet-X.Y.Z`); the name is unique to
#   .NET SDK test fixtures and cannot collide with the user's installed
#   `temporal` Homebrew CLI. The `kill-orphans` recipe targets only this
#   binary by name.
#
#   `testhost.dll` and `dotnet test` are GENERIC .NET test-runner patterns.
#   Killing them unscoped would terminate tests in OTHER projects on the
#   same machine (Rider runs, background CI shells, sibling repos). The
#   `kill-test-hosts` recipe path-filters to TemporalAgents only — opt-in,
#   not bundled into `kill-orphans`. (Tank + Cypher review, 2026-05-20.)
# ---------------------------------------------------------------------------

# Show orphaned Temporal embedded servers + dotnet test hosts scoped to this repo
list-orphans:
    @echo "== temporal-sdk-dotnet processes =="
    @pgrep -af "temporal-sdk-dotnet" 2>/dev/null || echo "(none)"
    @echo ""
    @echo "== TemporalAgents testhost.dll processes =="
    @pgrep -af "testhost.dll" 2>/dev/null | grep -i "TemporalAgents" || echo "(none)"
    @echo ""
    @echo "== TemporalAgents 'dotnet test' processes =="
    @pgrep -af "dotnet test" 2>/dev/null | grep -i "TemporalAgents" | grep -v "just " || echo "(none)"

# Kill orphaned Temporal embedded servers (.NET SDK's extracted CLI binary only).
# Safe across multi-project machines — the binary name is unique to .NET SDK
# integration test fixtures. Uses SIGTERM first, then SIGKILL for stragglers.
# Does NOT touch testhost.dll or `dotnet test` — see `kill-test-hosts` for that.
kill-orphans:
    @echo "Sending SIGTERM to orphaned temporal-sdk-dotnet processes..."
    -@pkill -TERM -f "temporal-sdk-dotnet" 2>/dev/null; true
    @sleep 2
    @echo "Sending SIGKILL to any stragglers..."
    -@pkill -9 -f "temporal-sdk-dotnet" 2>/dev/null; true
    @echo ""
    @echo "Remaining temporal-sdk-dotnet processes:"
    @pgrep -af "temporal-sdk-dotnet" 2>/dev/null || echo "(none)"

# Kill TemporalAgents-scoped test hosts (opt-in; risk of cross-project blast
# without the path filter). Use this when `dotnet test` for THIS repo is hung
# and `kill-orphans` alone didn't clean up its parent processes.
kill-test-hosts:
    @echo "Killing TemporalAgents testhost.dll processes (path-scoped)..."
    -@pgrep -af "testhost.dll" 2>/dev/null | grep -i "TemporalAgents" | awk '{print $$1}' | xargs -r kill -TERM 2>/dev/null; true
    @sleep 2
    -@pgrep -af "testhost.dll" 2>/dev/null | grep -i "TemporalAgents" | awk '{print $$1}' | xargs -r kill -9 2>/dev/null; true
    @echo "Killing TemporalAgents 'dotnet test' driver processes (path-scoped)..."
    -@pgrep -af "dotnet test" 2>/dev/null | grep -i "TemporalAgents" | grep -v "just " | awk '{print $$1}' | xargs -r kill -TERM 2>/dev/null; true
    @sleep 2
    -@pgrep -af "dotnet test" 2>/dev/null | grep -i "TemporalAgents" | grep -v "just " | awk '{print $$1}' | xargs -r kill -9 2>/dev/null; true
    @echo ""
    @echo "Remaining (scoped):"
    @pgrep -af "testhost.dll|dotnet test" 2>/dev/null | grep -i "TemporalAgents" || echo "(none)"

# Pre-test cleanup: kill embedded-server orphans only (safe across projects).
test-clean: kill-orphans
    @echo "Environment cleaned. Safe to run integration tests."

# Run a test command writing to a log file (NOT piped through tail/grep).
# Usage: just test-logged tests/Temporalio.Extensions.AI.IntegrationTests
#
# Why: `dotnet test ... | tail -60` buffers all output until the test command
# exits. If the test hangs, you see ZERO output until you kill it. Writing to
# a file lets you `tail -f` the log in another shell to watch progress.
# Run a test project, redirecting output to a /tmp log file (NOT piped tail)
test-logged project: build
    @LOG=$$(mktemp /tmp/temporalagents-test-XXXXXX.log); \
    echo "Logging to $$LOG"; \
    echo "Watch with:  tail -f $$LOG"; \
    echo ""; \
    dotnet test {{project}} \
        --configuration {{configuration}} \
        --no-build \
        --logger "console;verbosity=normal" \
        > "$$LOG" 2>&1; \
    EXIT=$$?; \
    echo ""; \
    echo "Test exited with status $$EXIT. Log: $$LOG"; \
    exit $$EXIT

# Remove stale agent worktrees under .claude/worktrees/ — SAFE EDITION.
#
# Agent worktrees are locked (`lock reason: claude agent agent-XXX`); a plain
# `git worktree remove` refuses. The naive fix is `-f -f` (force twice), but
# that ALSO overrides dirty-tree protection and silently discards uncommitted
# work in the worktree. (Cypher review, 2026-05-20.)
#
# This recipe:
#   1. For each worktree under .claude/worktrees/, runs `git status --porcelain`
#      inside it. If non-empty → skip + warn (don't force).
#   2. For clean worktrees, uses single `-f` (sufficient to override the lock;
#      the second -f is what's dangerous).
#   3. Echoes the HEAD ref + dirty state before any removal.
cleanup-stale-worktrees:
    @echo "Current worktrees:"
    @git worktree list
    @echo ""
    @for wt in $$(git worktree list --porcelain | awk '/^worktree / {print $$2}' | grep "/.claude/worktrees/" || true); do \
        echo "── $$wt"; \
        head=$$(git -C "$$wt" rev-parse --short HEAD 2>/dev/null || echo "(unreachable)"); \
        dirty=$$(git -C "$$wt" status --porcelain 2>/dev/null); \
        if [ -n "$$dirty" ]; then \
            echo "    HEAD: $$head"; \
            echo "    SKIP — worktree has uncommitted changes:"; \
            echo "$$dirty" | sed 's/^/      /'; \
            echo "    (commit / stash / push these before re-running, or remove the worktree manually)"; \
        else \
            echo "    HEAD: $$head  (clean — removing with single -f)"; \
            git worktree remove -f "$$wt" || echo "    FAILED — may need manual cleanup"; \
        fi; \
    done
    @echo ""
    @echo "Pruning administrative leftovers..."
    @git worktree prune -v
    @echo "Done."

# ---------------------------------------------------------------------------
# Diagnostic + sample-canary recipes (trinity)
#
# See .clans/patterns/per-test-diagnostic-loop/PATTERN.md
# See .clans/patterns/sample-as-canary/PATTERN.md
# See .claude/skills/sample-canary/SKILL.md
# ---------------------------------------------------------------------------

# Per-test diagnostic loop: run each matching test in its own process with a
# wall-clock timeout, report PASS / FAIL / HANG per test. Use when an
# integration test suite hangs and you cannot tell which test is responsible.
#
#   just test-individual tests/Temporalio.Extensions.AI.IntegrationTests Pattern3
#   just test-individual tests/Temporalio.Extensions.Agents.IntegrationTests ""
#
# project: test project directory (relative to repo root)
# filter:  substring matched via FullyQualifiedName~ — empty matches all
test-individual project filter="": build
    #!/usr/bin/env bash
    set -uo pipefail
    LOGDIR="artifacts/test-individual/$(date +%Y%m%d-%H%M%S)"
    mkdir -p "$LOGDIR"
    echo "Logs: $LOGDIR"
    LIST_FILTER=""
    if [ -n "{{filter}}" ]; then
        LIST_FILTER="--filter FullyQualifiedName~{{filter}}"
    fi
    # Discover tests. --list-tests prints test method names indented after a
    # header. Strip headers and noise; keep one FQN per line.
    dotnet test {{project}} \
        --configuration {{configuration}} --no-build \
        $LIST_FILTER --list-tests 2>&1 \
        | awk '/^[ \t]+[A-Za-z]/ {gsub(/^[ \t]+/,""); print}' \
        | grep -v "^Test run for\|^Microsoft\|^Copyright\|^The following Tests\|^Build" \
        | sort -u > "$LOGDIR/tests.txt" || true
    COUNT=$(wc -l < "$LOGDIR/tests.txt" | tr -d ' ')
    echo "Discovered $COUNT test(s)"
    PASS=0; FAIL=0; HANG=0
    while IFS= read -r test; do
        [ -z "$test" ] && continue
        SHORT=$(echo "$test" | awk -F'.' '{print $NF}')
        start=$(date +%s)
        timeout 120 dotnet test {{project}} \
            --configuration {{configuration}} --no-build \
            --filter "FullyQualifiedName~$SHORT" \
            --logger "console;verbosity=minimal" \
            > "$LOGDIR/$SHORT.log" 2>&1
        status=$?
        elapsed=$(($(date +%s)-start))
        if [ $status -eq 124 ]; then
            HANG=$((HANG+1)); printf "[%4ds] HANG  %s\n" "$elapsed" "$SHORT"
        elif [ $status -eq 0 ]; then
            PASS=$((PASS+1)); printf "[%4ds] PASS  %s\n" "$elapsed" "$SHORT"
        else
            FAIL=$((FAIL+1)); printf "[%4ds] FAIL  %s\n" "$elapsed" "$SHORT"
        fi
    done < "$LOGDIR/tests.txt"
    echo "----- Summary: $PASS pass / $FAIL fail / $HANG hang -----"
    [ "$FAIL" -eq 0 ] && [ "$HANG" -eq 0 ]

# Run all non-interactive MEAI samples end-to-end. Each sample gets a 90-second
# budget; per-sample stdout/stderr lands in artifacts/sample-runs/. Reports
# PASS / FAIL / HANG. Requires OPENAI_API_KEY and a running temporal server
# (temporal server start-dev). Skips HumanInTheLoop (interactive).
test-samples-meai: build
    #!/usr/bin/env bash
    set -uo pipefail
    LOGDIR="artifacts/sample-runs/meai-$(date +%Y%m%d-%H%M%S)"
    mkdir -p "$LOGDIR"
    echo "Logs: $LOGDIR"
    PASS=0; FAIL=0; HANG=0
    # Each sample is run from its own directory so Host.CreateApplicationBuilder
    # finds appsettings.json. `dotnet run` picks the only .csproj in the dir
    # (OpenTelemetry uses DurableOpenTelemetry.csproj — still the only one).
    for entry in \
        "DurableChat:samples/MEAI/DurableChat" \
        "DurableTools:samples/MEAI/DurableTools" \
        "DurableEmbeddings:samples/MEAI/DurableEmbeddings" \
        "CustomWorkflow:samples/MEAI/CustomWorkflow" \
        "OpenTelemetry:samples/MEAI/OpenTelemetry" ; do
        name="${entry%%:*}"
        dir="${entry##*:}"
        echo "═══ MEAI/$name ═══"
        start=$(date +%s)
        ( cd "$dir" && timeout 90 dotnet run --configuration {{configuration}} --no-build ) \
            > "$LOGDIR/$name.log" 2>&1
        status=$?
        elapsed=$(($(date +%s)-start))
        if [ $status -eq 124 ]; then
            HANG=$((HANG+1)); printf "[%4ds] HANG  MEAI/%s\n" "$elapsed" "$name"
        elif [ $status -eq 0 ]; then
            PASS=$((PASS+1)); printf "[%4ds] PASS  MEAI/%s\n" "$elapsed" "$name"
        else
            FAIL=$((FAIL+1)); printf "[%4ds] FAIL  MEAI/%s (exit %d)\n" "$elapsed" "$name" "$status"
        fi
    done
    echo "Skipped (interactive): MEAI/HumanInTheLoop — run manually."
    echo "----- MEAI Summary: $PASS pass / $FAIL fail / $HANG hang -----"
    [ "$FAIL" -eq 0 ] && [ "$HANG" -eq 0 ]

# Run all non-interactive MAF samples end-to-end. Skips HumanInTheLoop
# (interactive) and SplitWorkerClient (two processes — run manually). Each
# sample gets a 90-second budget. Requires OPENAI_API_KEY and a running
# temporal server.
test-samples-maf: build
    #!/usr/bin/env bash
    set -uo pipefail
    LOGDIR="artifacts/sample-runs/maf-$(date +%Y%m%d-%H%M%S)"
    mkdir -p "$LOGDIR"
    echo "Logs: $LOGDIR"
    PASS=0; FAIL=0; HANG=0
    for entry in \
        "BasicAgent:samples/MAF/BasicAgent" \
        "WorkflowOrchestration:samples/MAF/WorkflowOrchestration" \
        "EvaluatorOptimizer:samples/MAF/EvaluatorOptimizer" \
        "MultiAgentRouting:samples/MAF/MultiAgentRouting" \
        "WorkflowRouting:samples/MAF/WorkflowRouting" \
        "AmbientAgent:samples/MAF/AmbientAgent" \
        "ConfigurableAgent:samples/MAF/ConfigurableAgent" \
        "ExternalHistoryStore:samples/MAF/ExternalHistoryStore" \
        "PerToolActivities:samples/MAF/PerToolActivities" \
        "Compaction:samples/MAF/Compaction" ; do
        name="${entry%%:*}"
        dir="${entry##*:}"
        echo "═══ MAF/$name ═══"
        start=$(date +%s)
        ( cd "$dir" && timeout 90 dotnet run --configuration {{configuration}} --no-build ) \
            > "$LOGDIR/$name.log" 2>&1
        status=$?
        elapsed=$(($(date +%s)-start))
        if [ $status -eq 124 ]; then
            HANG=$((HANG+1)); printf "[%4ds] HANG  MAF/%s\n" "$elapsed" "$name"
        elif [ $status -eq 0 ]; then
            PASS=$((PASS+1)); printf "[%4ds] PASS  MAF/%s\n" "$elapsed" "$name"
        else
            FAIL=$((FAIL+1)); printf "[%4ds] FAIL  MAF/%s (exit %d)\n" "$elapsed" "$name" "$status"
        fi
    done
    echo "Skipped (interactive):    MAF/HumanInTheLoop — run manually."
    echo "Skipped (two-process):    MAF/SplitWorkerClient — run Worker then Client."
    echo "----- MAF Summary: $PASS pass / $FAIL fail / $HANG hang -----"
    [ "$FAIL" -eq 0 ] && [ "$HANG" -eq 0 ]

# Run the full sample canary (MEAI + MAF non-interactive).
test-samples: test-samples-meai test-samples-maf
