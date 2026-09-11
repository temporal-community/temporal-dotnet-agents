#!/usr/bin/env bash
# Fast pre-filter for two MAF documentation defects that a compiler cannot reach.
#
# SCOPE — deliberately narrow. This script keeps only the two patterns that survived mutation
# testing. Two earlier candidates were dropped on purpose and should not come back:
#   * `DurableToolOptions.DefaultRetryPolicy` — matched nothing anywhere in the repo, so it would
#     have shipped green forever while proving nothing. The real instance of that defect lives in
#     PROSE and is caught by the compiled harness instead
#     (tests/docs/DocSnippets/Snippets/Usage/InheritancePerAgentVsWorkerLevel.cs).
#   * contradictory retry claims across documents — 4 of 6 semantics-preserving rewrites escaped the
#     pattern. Semantic cross-document properties are not regex-expressible; a check that misses
#     two thirds of its target is worse than none, because it reads as coverage.
#
# THE REAL GATE IS THE COMPILER. tests/docs/DocSnippets/ compiles the documented registration
# examples, and `just verify-doc-snippets` enforces that every one of them stays covered. This
# script is the cheap first pass, not the proof.
#
# Written for bash 3.2 (macOS default): no mapfile, no associative arrays.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

ratchet_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
factory_ratchet="$ratchet_dir/maf-doc-factory-first-ratchet.txt"
stale_ratchet="$ratchet_dir/maf-doc-stale-term-ratchet.txt"
failures=0
notices=0

fail() {
    echo "ERROR: $1" >&2
    failures=$((failures + 1))
}

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

# Ratchet files are `<path> <allowed-count>`, `#` comments and blank lines ignored. A path that is
# absent allows zero. The point of a count rather than a line reference is that counts do not churn
# when unrelated edits move text around.
ratchet_allowed() {
    local file="$1" path="$2"
    [[ -f "$file" ]] || { echo 0; return; }
    # `|| true` is load-bearing. A fully-drained ratchet file is all comments, so grep selects
    # nothing and exits 1; under `set -euo pipefail` that propagates out of the command
    # substitution at the call site and kills the script BEFORE fail() can print. The gate then
    # exits non-zero with an empty log — right answer, no reason, and indistinguishable from a
    # crash. Draining a ratchet to zero must not disarm the check it guards.
    { grep -v '^[[:space:]]*#' "$file" || true; } \
        | awk -v p="$path" 'NF && $1 == p { print $2; found = 1 } END { if (!found) print 0 }' | head -1
}

# Emits a NOTICE for every ratchet entry now looser than reality, so the lists shrink instead of
# quietly becoming permanent exemptions.
ratchet_slack_notices() {
    local file="$1" counts="$2"
    [[ -f "$file" ]] || return 0
    while read -r path allowed _rest; do
        [[ -n "${path:-}" ]] || continue
        local actual
        actual="$(awk -F: -v p="$path" '$1 == p { print $NF }' "$counts" | head -1)"
        actual="${actual:-0}"
        if [[ "$actual" -lt "$allowed" ]]; then
            echo "NOTICE: $path is down to $actual site(s) but $(basename "$file") still allows $allowed. Lower it." >&2
            notices=$((notices + 1))
        fi
    done < <(grep -v '^[[:space:]]*#' "$file" | awk 'NF')
}

# ---------------------------------------------------------------------------
# Check 1 — renamed internals must not survive in prose or comments.
#
# Scoped through `git ls-files` across docs/ AND src/, the way verify-markdown-links.sh does it. A
# bare `grep -r` here pulls in bin/obj XML documentation output, which contains copies of every
# comment in the source and turns this check into permanent noise.
# ---------------------------------------------------------------------------
stale_terms='CachedDurableAgent|ComposeDurableAgent|ResolveDurableAgent'

git ls-files 'docs/*' 'src/*' > "$work/tracked.txt"
if [[ ! -s "$work/tracked.txt" ]]; then
    fail "no tracked files under docs/ or src/ — this gate is looking at the wrong tree"
    exit 1
fi

: > "$work/stale-counts.txt"
# xargs over the tracked list, not a recursive walk: untracked scratch files and build output are
# not the repository's prose.
tr '\n' '\0' < "$work/tracked.txt" \
    | xargs -0 rg --no-messages -H --count-matches -e "$stale_terms" \
    > "$work/stale-counts.txt" 2>/dev/null || true

while IFS= read -r line; do
    [[ -n "$line" ]] || continue
    path="${line%:*}"
    count="${line##*:}"
    allowed="$(ratchet_allowed "$stale_ratchet" "$path")"
    if [[ "$count" -gt "$allowed" ]]; then
        fail "$path mentions a renamed internal ($count occurrence(s), ratchet allows $allowed).
       CachedDurableAgent / ComposeDurableAgent / ResolveDurableAgent no longer exist — update the
       prose or comment to the current name. Only raise the count in $(basename "$stale_ratchet") for a
       pre-existing site you are not fixing right now."
    fi
done < "$work/stale-counts.txt"

ratchet_slack_notices "$stale_ratchet" "$work/stale-counts.txt"

# ---------------------------------------------------------------------------
# Check 2 — factory-first `AddTool`, as a pre-filter.
#
# `rg -U` (multiline) is mandatory. The single-line form of this pattern has roughly one-third
# recall: it misses `AddTool(\n    sp => ...`, which is how the majority of the real defects are
# actually written, and it also misses `(sp) =>` and typed parameters.
#
# The alternation matches a lambda as the FIRST argument. It must NOT match the two correct forms:
#   AddTool("name", sp => ...)   — first token is a string literal
#   AddTool(tool, opts => ...)   — identifier is followed by a comma, not by `=>`
# ---------------------------------------------------------------------------
factory_first_pattern='AddTool\(\s*(?:\([^)]*\)|[A-Za-z_][A-Za-z0-9_]*)\s*=>'

git ls-files 'docs/*.md' > "$work/docs.txt"
: > "$work/factory-hits.txt"
if [[ -s "$work/docs.txt" ]]; then
    tr '\n' '\0' < "$work/docs.txt" \
        | xargs -0 rg --no-messages -H -U --count-matches -e "$factory_first_pattern" \
        > "$work/factory-hits.txt" 2>/dev/null || true
fi

while IFS= read -r line; do
    [[ -n "$line" ]] || continue
    path="${line%:*}"
    count="${line##*:}"
    allowed="$(ratchet_allowed "$factory_ratchet" "$path")"

    if [[ "$count" -gt "$allowed" ]]; then
        fail "$path has $count factory-first AddTool call(s) but only $allowed are on the ratchet.
       AddTool's factory overload takes the NAME FIRST:
           agent.AddTool(\"my_tool\", sp => AIFunctionFactory.Create(..., name: \"my_tool\"), ...)
       Fix the example, or — only if it is a pre-existing known defect — raise the count in
       $(basename "$factory_ratchet") and say why. Counts there are meant to go down."
    fi
done < "$work/factory-hits.txt"

ratchet_slack_notices "$factory_ratchet" "$work/factory-hits.txt"

if [[ "$failures" -ne 0 ]]; then
    echo "" >&2
    echo "MAF doc API contracts: $failures problem(s)." >&2
    exit 1
fi

echo "MAF doc API contracts OK: no stale internal names; no new factory-first AddTool sites ($notices ratchet notice(s))."
