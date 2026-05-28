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

# Push main branch and all tags to both origin and temporal remotes.
# Refuses to run if the current branch is not main.
sync-remotes:
    #!/usr/bin/env bash
    set -euo pipefail
    current=$(git branch --show-current)
    if [ "$current" != "main" ]; then
        echo "ERROR: Not on main branch (current: $current)."
        echo "       Checkout main before syncing remotes."
        exit 1
    fi
    echo "Pushing main + tags → origin..."
    git push origin main --tags
    echo "Pushing main + tags → temporal..."
    git push temporal main --tags
    echo "✓ Both remotes synced."

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

# Run a test project writing output to a log file (NOT piped through tail).
# Wraps `dotnet test` with a `timeout` so a hang is surfaced as exit 124/143
# rather than blocking the recipe forever.
#
# Usage:
#   just test-logged tests/Temporalio.Extensions.AI.IntegrationTests
#   just test-logged tests/Temporalio.Extensions.AI.IntegrationTests 900
#
# project: test project directory (relative to repo root)
# limit:   per-process wall-clock timeout in seconds (default 600)
#
# Why: `dotnet test ... | tail -60` buffers all output until the test command
# exits. If the test hangs, you see ZERO output until you kill it. Writing to
# a file + a hard timeout gives you (a) tail -f the log in another shell,
# (b) a guaranteed exit when a test hangs. (Cypher review, 2026-05-21.)
test-logged project limit="600": build
    @if ! command -v timeout >/dev/null 2>&1; then \
        echo "ERROR: GNU coreutils 'timeout' is required. On macOS: brew install coreutils"; \
        exit 127; \
    fi
    @LOG=$$(mktemp /tmp/temporalagents-test-XXXXXX.log); \
    echo "Logging to $$LOG"; \
    echo "Watch with:  tail -f $$LOG"; \
    echo "Wall-clock cap: {{limit}}s"; \
    echo ""; \
    timeout {{limit}} dotnet test {{project}} \
        --configuration {{configuration}} \
        --no-build \
        --logger "console;verbosity=normal" \
        > "$$LOG" 2>&1; \
    EXIT=$$?; \
    echo ""; \
    if [ "$$EXIT" -eq 124 ] || [ "$$EXIT" -eq 143 ]; then \
        echo "HANG — test exceeded {{limit}}s and was killed. Log: $$LOG"; \
    else \
        echo "Test exited with status $$EXIT. Log: $$LOG"; \
    fi; \
    exit $$EXIT

# Remove stale agent worktrees under .claude/worktrees/ — SAFE EDITION.
#
# Agent worktrees are locked (`lock reason: claude agent agent-XXX`); a plain
# `git worktree remove` refuses. The naive fix is `-f -f` (force twice), but
# that ALSO overrides dirty-tree protection and silently discards uncommitted
# work in the worktree. (Cypher review, 2026-05-20.)
#
# This recipe runs TWO safety checks before removing each worktree:
#   1. Dirty-state check: `git status --porcelain` inside the worktree. If
#      non-empty → skip + warn. Catches uncommitted work.
#   2. Unique-commits check: `git merge-base --is-ancestor <worktree-HEAD> HEAD`.
#      If the worktree branch has commits NOT reachable from the current branch
#      → skip + warn. Catches the trap where an agent committed work that never
#      got merged back to the parent — `git worktree remove` would orphan those
#      commits, leaving them recoverable only via the branch ref. The original
#      recipe missed this and silently orphaned committed work on at least three
#      occasions before this check was added. (Chief review, 2026-05-27.)
#
# Only when BOTH checks pass:
#   - Unlock the worktree (locks persist after agent process dies)
#   - Remove with single `-f` (overrides the now-released lock; the second -f
#     would override dirty-tree protection which we don't want)
#   - Delete the underlying branch if it matches `worktree-agent-*` AND has no
#     commits unique to it. Prevents the orphaned-branch accumulation we saw
#     before this was added.
#
# Recipe uses the bash-shebang block style (mirrors test-individual) rather
# than the `@cmd; \` line-continuation style. The latter is Makefile-idiom and
# DOES NOT work in just — `$$VAR` is not transformed to `$VAR`, so bash sees
# literal `$$` and treats it as the shell PID, producing syntax errors when
# adjacent to `(`. (Chief review, 2026-05-27.)
cleanup-stale-worktrees:
    #!/usr/bin/env bash
    set -uo pipefail

    echo "Current worktrees:"
    git worktree list
    echo ""

    removed=0
    skipped_dirty=0
    skipped_unique=0

    for wt in $(git worktree list --porcelain | awk '/^worktree / {print $2}' | grep "/.claude/worktrees/" || true); do
        name="${wt##*/}"
        echo "── $name"
        head=$(git -C "$wt" rev-parse --short HEAD 2>/dev/null || echo "(unreachable)")

        # Check 1: uncommitted changes in the worktree
        if [ -n "$(git -C "$wt" status --porcelain 2>/dev/null)" ]; then
            echo "    HEAD: $head"
            echo "    SKIP — worktree has uncommitted changes:"
            git -C "$wt" status --porcelain | sed 's/^/      /'
            echo "    (commit / stash / push these before re-running, or remove the worktree manually)"
            skipped_dirty=$((skipped_dirty + 1))
            continue
        fi

        # Check 2: commits unique to the worktree's branch (not reachable from current HEAD)
        if ! git merge-base --is-ancestor "$head" HEAD 2>/dev/null; then
            unique_count=$(git rev-list "HEAD..$head" --count 2>/dev/null || echo "?")
            echo "    HEAD: $head"
            echo "    SKIP — worktree has $unique_count commit(s) NOT on current branch:"
            git log "HEAD..$head" --oneline 2>/dev/null | head -5 | sed 's/^/      /'
            echo "    (cherry-pick / merge these into the current branch before re-running,"
            echo "     or remove the worktree manually after verifying the commits are dispensable)"
            skipped_unique=$((skipped_unique + 1))
            continue
        fi

        # Both checks pass — safe to remove the worktree
        echo "    HEAD: $head  (clean + already merged — removing)"
        git worktree unlock "$wt" 2>/dev/null || true
        if git worktree remove -f "$wt" 2>&1 | sed 's/^/      /'; then
            removed=$((removed + 1))
        else
            echo "      FAILED — may need manual cleanup"
            continue
        fi

        # Worktree removed — clean up the orphaned branch if it matches the agent pattern
        branch="worktree-${name}"
        if git show-ref --verify --quiet "refs/heads/$branch"; then
            # Branch should have no unique commits (we just verified head was ancestor of HEAD)
            if git branch -d "$branch" 2>&1 | sed 's/^/      /'; then
                echo "      branch '$branch' deleted"
            else
                echo "      branch '$branch' kept (refused safe delete — inspect manually)"
            fi
        fi
    done

    echo ""
    echo "Pruning administrative leftovers..."
    git worktree prune -v

    echo ""
    echo "Summary: $removed removed, $skipped_dirty skipped (dirty), $skipped_unique skipped (unique commits)"
    if [ "$skipped_dirty" -gt 0 ] || [ "$skipped_unique" -gt 0 ]; then
        echo ""
        echo "Worktrees were skipped — see messages above. Run \`git worktree list\` to confirm state."
    fi

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
#   just test-individual tests/Temporalio.Extensions.AI.IntegrationTests "" 300
#
# project: test project directory (relative to repo root)
# filter:  substring matched via FullyQualifiedName~ — empty matches all
# limit:   per-test wall-clock timeout in seconds (default 180).
#          120s was too tight for MAF compaction tests (8 LLM turns each).
#
# Tested against .NET SDK 10.0.x. If a future SDK changes the `--list-tests`
# output format the awk discovery step may need updating. (Trinity review 2026-05-21.)
test-individual project filter="" limit="180": build
    #!/usr/bin/env bash
    set -uo pipefail
    if ! command -v timeout >/dev/null 2>&1; then
        echo "ERROR: GNU coreutils 'timeout' is required. On macOS: brew install coreutils"
        exit 127
    fi
    LOGDIR="artifacts/test-individual/$(date +%Y%m%d-%H%M%S)"
    mkdir -p "$LOGDIR"
    echo "Logs:  $LOGDIR"
    echo "Cap:   {{limit}}s per test"
    LIST_FILTER=""
    if [ -n "{{filter}}" ]; then
        LIST_FILTER="--filter FullyQualifiedName~{{filter}}"
    fi
    # Discover tests. --list-tests prints test method names indented after a
    # header line ("The following Tests are available:"). Strip headers
    # (anchored to start-of-line so a legitimately Microsoft.* test FQN is
    # NOT swallowed). Keep one FQN per line.
    dotnet test {{project}} \
        --configuration {{configuration}} --no-build \
        $LIST_FILTER --list-tests 2>&1 \
        | awk '/^[ \t]+[A-Za-z]/ {gsub(/^[ \t]+/,""); print}' \
        | grep -Ev "^(Test run for|Microsoft \(R\)|Copyright |The following Tests|Build |MinVer:)" \
        | sort -u > "$LOGDIR/tests.txt" || true
    COUNT=$(wc -l < "$LOGDIR/tests.txt" | tr -d ' ')
    echo "Discovered $COUNT test(s)"
    echo ""
    PASS=0; FAIL=0; HANG=0
    while IFS= read -r test; do
        [ -z "$test" ] && continue
        SHORT=$(echo "$test" | awk -F'.' '{print $NF}')
        start=$(date +%s)
        # Exact match (=) not substring (~) — `~SHORT` collides when two
        # test classes share a method name.
        timeout {{limit}} dotnet test {{project}} \
            --configuration {{configuration}} --no-build \
            --filter "FullyQualifiedName=$test" \
            --logger "console;verbosity=minimal" \
            > "$LOGDIR/$SHORT.log" 2>&1
        status=$?
        elapsed=$(($(date +%s)-start))
        if [ $status -eq 124 ] || [ $status -eq 143 ]; then
            HANG=$((HANG+1)); printf "[%4ds] HANG  %s\n" "$elapsed" "$SHORT"
        elif [ $status -eq 0 ]; then
            PASS=$((PASS+1)); printf "[%4ds] PASS  %s\n" "$elapsed" "$SHORT"
        else
            FAIL=$((FAIL+1)); printf "[%4ds] FAIL  %s (exit %d)\n" "$elapsed" "$SHORT" "$status"
        fi
    done < "$LOGDIR/tests.txt"
    echo ""
    echo "----- Summary: $PASS pass / $FAIL fail / $HANG hang -----"
    [ "$FAIL" -eq 0 ] && [ "$HANG" -eq 0 ]

# Shared preflight for sample-canary recipes: verify GNU timeout, OPENAI_API_KEY,
# and that a Temporal server is reachable. Exits non-zero with an actionable
# message rather than letting every sample fail identically with a confusing
# error. (Trinity review, 2026-05-21.)
_sample-preflight:
    @if ! command -v timeout >/dev/null 2>&1; then \
        echo "ERROR: GNU coreutils 'timeout' is required. On macOS: brew install coreutils"; \
        exit 127; \
    fi
    @if ! command -v nc >/dev/null 2>&1; then \
        echo "ERROR: 'nc' (netcat) is required for the Temporal-server reachability probe."; \
        exit 127; \
    fi
    @if [ -z "$${OPENAI_API_KEY:-}" ]; then \
        echo "ERROR: OPENAI_API_KEY is not set."; \
        echo "  Set in your shell, or:  dotnet user-secrets set OPENAI_API_KEY sk-... --project samples/MEAI/DurableChat"; \
        exit 2; \
    fi
    @if ! nc -z localhost 7233 2>/dev/null; then \
        echo "ERROR: No Temporal server listening on localhost:7233."; \
        echo "  Start one with:  temporal server start-dev --namespace default"; \
        exit 2; \
    fi

# Run all non-interactive MEAI samples end-to-end. Per-sample timeouts:
# default 90s, with overrides for samples that legitimately take longer
# (DurableEmbeddings parallel-indexes a corpus). Reports PASS / FAIL / HANG.
# Requires OPENAI_API_KEY and a running Temporal server. Skips HumanInTheLoop.
test-samples-meai: build _sample-preflight
    #!/usr/bin/env bash
    set -uo pipefail
    LOGDIR="artifacts/sample-runs/meai-$(date +%Y%m%d-%H%M%S)"
    mkdir -p "$LOGDIR"
    echo "Logs: $LOGDIR"
    PASS=0; FAIL=0; HANG=0
    # Format: name:dir:timeout_seconds. Each sample runs from its own dir so
    # Host.CreateApplicationBuilder finds appsettings.json. OpenTelemetry uses
    # DurableOpenTelemetry.csproj — still the only .csproj in that directory.
    for entry in \
        "DurableChat:samples/MEAI/DurableChat:120" \
        "DurableTools:samples/MEAI/DurableTools:90" \
        "DurableEmbeddings:samples/MEAI/DurableEmbeddings:180" \
        "CustomWorkflow:samples/MEAI/CustomWorkflow:90" \
        "OpenTelemetry:samples/MEAI/OpenTelemetry:90" ; do
        IFS=':' read -r name dir cap <<< "$entry"
        echo "═══ MEAI/$name (cap ${cap}s) ═══"
        start=$(date +%s)
        ( cd "$dir" && timeout "$cap" dotnet run --configuration {{configuration}} --no-build ) \
            > "$LOGDIR/$name.log" 2>&1
        status=$?
        elapsed=$(($(date +%s)-start))
        if [ $status -eq 124 ] || [ $status -eq 143 ]; then
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

# Run all non-interactive MAF samples end-to-end. Per-sample timeouts override
# the 90s default where needed (Compaction walks 8 turns with summarization;
# ConfigurableAgent has multi-agent handoff). Skips HumanInTheLoop (interactive)
# and SplitWorkerClient (two processes — run manually).
test-samples-maf: build _sample-preflight
    #!/usr/bin/env bash
    set -uo pipefail
    LOGDIR="artifacts/sample-runs/maf-$(date +%Y%m%d-%H%M%S)"
    mkdir -p "$LOGDIR"
    echo "Logs: $LOGDIR"
    PASS=0; FAIL=0; HANG=0
    for entry in \
        "BasicAgent:samples/MAF/BasicAgent:90" \
        "WorkflowOrchestration:samples/MAF/WorkflowOrchestration:90" \
        "EvaluatorOptimizer:samples/MAF/EvaluatorOptimizer:120" \
        "MultiAgentRouting:samples/MAF/MultiAgentRouting:90" \
        "WorkflowRouting:samples/MAF/WorkflowRouting:120" \
        "AmbientAgent:samples/MAF/AmbientAgent:90" \
        "ConfigurableAgent:samples/MAF/ConfigurableAgent:150" \
        "ExternalHistoryStore:samples/MAF/ExternalHistoryStore:120" \
        "PerToolActivities:samples/MAF/PerToolActivities:90" \
        "Compaction:samples/MAF/Compaction:180" ; do
        IFS=':' read -r name dir cap <<< "$entry"
        echo "═══ MAF/$name (cap ${cap}s) ═══"
        start=$(date +%s)
        ( cd "$dir" && timeout "$cap" dotnet run --configuration {{configuration}} --no-build ) \
            > "$LOGDIR/$name.log" 2>&1
        status=$?
        elapsed=$(($(date +%s)-start))
        if [ $status -eq 124 ] || [ $status -eq 143 ]; then
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

# Helper: drift detection for sample-canary recipes. Diffs `ls samples/{MEAI,MAF}`
# against the hardcoded recipe lists; fails if a new sample directory appears
# uncategorized (so adding a sample without updating test-samples-* surfaces).
#
# Intentional exclusions (interactive or multi-process — must run manually):
#   - samples/MEAI/HumanInTheLoop  (interactive Console.ReadLine)
#   - samples/MAF/HumanInTheLoop   (interactive Console.ReadLine)
#   - samples/MAF/SplitWorkerClient (two-process — Worker + Client)
verify-sample-coverage:
    #!/usr/bin/env bash
    set -uo pipefail
    EXIT=0
    declared_meai=$(awk '/^test-samples-meai:/,/^test-samples-maf:/' justfile \
        | grep -oE 'samples/MEAI/[A-Za-z]+' | sort -u)
    actual_meai=$(find samples/MEAI -mindepth 1 -maxdepth 1 -type d \
        -not -name 'bin' -not -name 'obj' \
        -not -name 'HumanInTheLoop' \
        | sort -u)
    missing_meai=$(comm -23 <(echo "$actual_meai") <(echo "$declared_meai"))
    if [ -n "$missing_meai" ]; then
        echo "WARN: MEAI samples missing from test-samples-meai:"
        echo "$missing_meai" | sed 's/^/  /'
        EXIT=1
    fi
    declared_maf=$(awk '/^test-samples-maf:/,/^test-samples:/' justfile \
        | grep -oE 'samples/MAF/[A-Za-z]+' | sort -u)
    actual_maf=$(find samples/MAF -mindepth 1 -maxdepth 1 -type d \
        -not -name 'bin' -not -name 'obj' \
        -not -name 'HumanInTheLoop' \
        -not -name 'SplitWorkerClient' \
        | sort -u)
    missing_maf=$(comm -23 <(echo "$actual_maf") <(echo "$declared_maf"))
    if [ -n "$missing_maf" ]; then
        echo "WARN: MAF samples missing from test-samples-maf:"
        echo "$missing_maf" | sed 's/^/  /'
        EXIT=1
    fi
    if [ $EXIT -eq 0 ]; then
        echo "OK: sample-canary recipes cover all sample directories (modulo documented interactive/multi-process exclusions)."
    fi
    exit $EXIT

# Remove timestamped artifact directories from prior diagnostic / sample-canary
# runs. Safe across-the-board; logs are not load-bearing.
clean-test-artifacts:
    rm -rf artifacts/test-individual artifacts/sample-runs
    @echo "Removed artifacts/test-individual and artifacts/sample-runs."
