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

# Validates a ratchet file's SHAPE and FAILS on any entry looser than reality.
#
# This used to print a NOTICE and carry on, which made "numbers may only go down" a comment rather
# than a rule: a fixed defect left its allowance behind, and the allowance then silently covered the
# next regression. Slack is now a build failure with the exact number to write. Malformed and
# duplicate entries fail too — an unparseable count was previously compared as an empty string, and
# a duplicated path silently resolved to whichever line came first.
# Counts ERE matches of $1 in file $2, printing a bare integer (0 when there are none).
#
# The `|| true` is not decoration. grep reports "no matches" as exit 1, which under
# `set -euo pipefail` propagates out of the enclosing command substitution and kills the script
# before it can report anything — a non-zero exit with an empty log, indistinguishable from a crash.
# That shape has now bitten this repository four times in three different scripts, so the
# convention lives in one function rather than at each call site.
#
# `-o | wc -l` counts MATCHES, not matching lines: `grep -c` would read two stale names on one line
# as one, and the ratchet compares match counts. `-I` skips binary files.
count_matches() {
    local pattern="$1" file="$2"
    { grep -I -E -o -e "$pattern" "$file" 2>/dev/null || true; } | wc -l | tr -d '[:space:]'
}

# SHAPE ONLY, and it must run BEFORE anything compares these values. `[[ "$count" -gt "$allowed" ]]`
# evaluates $allowed arithmetically, so a non-numeric count is dereferenced as a variable name and
# blows up under `set -u` inside the very check that was supposed to catch it. The self-test case
# `ratchet-non-numeric-count-fails` pins this ordering.
ratchet_validate_shape() {
    local file="$1"
    [[ -f "$file" ]] || return 0

    local seen="$work/ratchet-seen-$(basename "$file")"
    : > "$seen"

    while read -r path allowed extra; do
        [[ -n "${path:-}" ]] || continue

        if [[ -n "${extra:-}" ]]; then
            fail "$(basename "$file"): entry for '$path' has trailing junk ('$extra').
       The format is exactly '<path> <count>'."
            continue
        fi

        if ! [[ "${allowed:-}" =~ ^[0-9]+$ ]]; then
            fail "$(basename "$file"): entry for '$path' has a non-numeric count ('${allowed:-<missing>}').
       The format is exactly '<path> <count>'."
            continue
        fi

        if grep -qxF "$path" "$seen"; then
            fail "$(basename "$file"): '$path' is listed more than once.
       Only the first line took effect, so a later, larger allowance was silently ignored."
            continue
        fi
        printf '%s\n' "$path" >> "$seen"
    done < <(grep -v '^[[:space:]]*#' "$file" | awk 'NF')
}

# Fails on any entry looser than reality. Slack used to print a NOTICE and leave the build green,
# which made "numbers may only go down" a comment rather than a rule: a fixed defect left its
# allowance behind, and that allowance then silently covered the next regression.
ratchet_enforce_tight() {
    local file="$1" counts="$2"
    [[ -f "$file" ]] || return 0
    while read -r path allowed _extra; do
        [[ -n "${path:-}" ]] || continue
        [[ "${allowed:-}" =~ ^[0-9]+$ ]] || continue   # shape already reported by ratchet_validate_shape
        local actual
        actual="$(awk -F: -v p="$path" '$1 == p { print $NF }' "$counts" | head -1)"
        actual="${actual:-0}"
        if [[ "$actual" -lt "$allowed" ]]; then
            fail "$(basename "$file"): '$path' is down to $actual site(s) but the ratchet still allows $allowed.
       Lower it to $actual, or delete the line if $actual is 0. An allowance left above reality is
       cover for the next regression — this is what 'numbers may only go down' means."
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
# Shape first, and fail closed: every later check compares these numbers arithmetically.
ratchet_validate_shape "$stale_ratchet"
ratchet_validate_shape "$factory_ratchet"
if [[ "$failures" -ne 0 ]]; then
    echo "" >&2
    echo "MAF doc API contracts: $failures malformed ratchet entr(y/ies); fix them before the gate can run." >&2
    exit 1
fi

stale_terms='CachedDurableAgent|ComposeDurableAgent|ResolveDurableAgent'

git ls-files 'docs/*' 'src/*' > "$work/tracked.txt"
if [[ ! -s "$work/tracked.txt" ]]; then
    fail "no tracked files under docs/ or src/ — this gate is looking at the wrong tree"
    exit 1
fi

: > "$work/stale-counts.txt"
# Iterate the tracked list, not a recursive walk: untracked scratch files and build output are not
# the repository's prose.
#
# `grep -Eo | wc -l` rather than `grep -c`, because the downstream ratchet compares MATCH counts and
# `grep -c` counts matching LINES — two stale names on one line would read as one. This is what the
# previous `rg --count-matches` reported, and the `-I` skips binaries the way `--no-messages` did.
while IFS= read -r f; do
    if [[ -f "$f" ]]; then
        n="$(count_matches "$stale_terms" "$f")"
        if [[ "$n" -gt 0 ]]; then
            printf '%s:%s\n' "$f" "$n" >> "$work/stale-counts.txt"
        fi
    fi
done < "$work/tracked.txt"

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

ratchet_enforce_tight "$stale_ratchet" "$work/stale-counts.txt"

# ---------------------------------------------------------------------------
# Check 2 — factory-first `AddTool`, as a pre-filter.
#
# MULTILINE MATCHING IS MANDATORY, and that is the whole reason this is awk rather than grep. A
# line-at-a-time pattern has roughly one-third the recall: it misses
#
#     agent.AddTool(
#         sp => AIFunctionFactory.Create(...),
#
# which is how the majority of the real defects are actually written. POSIX awk gives multiline
# matching on both runners with no dependency beyond what the workflow already installs — the
# earlier `rg -U` form was convenience, and it cost a CI outage when neither GitHub image shipped
# ripgrep and every `|| true` call site turned its absence into a silent pass.
#
# The alternation matches a lambda as the FIRST argument. It must NOT match the correct forms:
#   AddTool("name", sp => ...)      — first token is a string literal
#   AddTool(tool, opts => ...)      — identifier is followed by a comma, not by `=>`
#   AddTool(map["k"], p => ...)     — identifier is followed by an indexer
#   AddToolInterceptor(sp => ...)   — `AddTool` is not followed by `(`
# The self-test pins every one of those, and pinned them for the rg implementation too, so the two
# engines are held to the same cases.
#
# Notation differs from PCRE in exactly two ways: POSIX ERE has no `(?:...)`, so the group is
# capturing, and no `\s`, so it is `[[:space:]]` — which matches newlines in the accumulated
# buffer, which is what makes this multiline.
# ---------------------------------------------------------------------------
count_factory_first() {
    awk '
        { buf = buf $0 "\n" }
        END {
            n = 0
            while (match(buf, /AddTool\([[:space:]]*(\([^)]*\)|[A-Za-z_][A-Za-z0-9_]*)[[:space:]]*=>/)) {
                n++
                buf = substr(buf, RSTART + RLENGTH)
            }
            if (n > 0) printf "%s:%d\n", FILENAME, n
        }
    ' "$1"
}

git ls-files 'docs/*.md' > "$work/docs.txt"
: > "$work/factory-hits.txt"
while IFS= read -r f; do
    if [[ -f "$f" ]]; then
        count_factory_first "$f" >> "$work/factory-hits.txt"
    fi
done < "$work/docs.txt"

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

ratchet_enforce_tight "$factory_ratchet" "$work/factory-hits.txt"

if [[ "$failures" -ne 0 ]]; then
    echo "" >&2
    echo "MAF doc API contracts: $failures problem(s)." >&2
    exit 1
fi

echo "MAF doc API contracts OK: no stale internal names, no factory-first AddTool sites, and every ratchet entry is well-formed and tight."
