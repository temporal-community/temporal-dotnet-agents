#!/usr/bin/env bash
# Drift detector for the compile-only doc-snippet harness in tests/docs/DocSnippets/.
#
# WHY THIS EXISTS
#   The harness compiles documented registration examples so the compiler — not a regex — decides
#   whether they bind to real overloads. But a harness only proves something about the snippets it
#   actually contains. Adding a new `AddDurableAgent(` / `AddTool(` example to a MAF how-to doc is
#   exactly the moment a fresh defect can land, and the harness would stay silent about it.
#   Existence of a harness file is not usage of it; this check converts existence into coverage.
#
#   Mirrors `just verify-sample-coverage`: a declared set, a discovered set, and a failure when the
#   discovered set grows past the declared one.
#
# THE KEY
#   A snippet is identified by `<doc-path>#<slug-of-nearest-preceding-heading>` — the same slug
#   GitHub puts on that heading, so the key doubles as a working deep link. Keys are NOT line
#   ranges: docs get edited constantly and every unrelated insertion above a snippet would shift
#   them. Harness markers carry a `(lines N-M)` suffix as a human convenience; it is informational
#   and deliberately unchecked.
#
# WHAT FAILS
#   1. A qualifying doc block whose key has no harness marker and no allowlist entry.
#   2. A heading holding N qualifying blocks with fewer than N markers (the second block under a
#      shared heading must not hide behind the first one's marker).
#   3. A harness marker whose key matches no qualifying doc block — catches a renamed heading or a
#      deleted example leaving a stale harness file behind.
#   4. An allowlist entry that matches no qualifying doc block — keeps the debt list honest.
#   5. An unbalanced BEGIN/END pair.
#   6. A csproj that removes files from the compile glob — a harness whose files stop being
#      compiled reports success forever.
#
# Written for bash 3.2 (macOS default): no mapfile, no associative arrays.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

self_name="$(basename "${BASH_SOURCE[0]}")"
harness_dir="tests/docs/DocSnippets"
harness_csproj="$harness_dir/DocSnippets.csproj"
doc_glob="docs/how-to/MAF"

# ---------------------------------------------------------------------------
# Allowlist: qualifying doc blocks deliberately NOT compiled, each with the structural reason it
# cannot be. Lives in a sibling file so the self-test can drive this script against fixture repos
# with their own (usually empty) list. Every entry is re-checked (rule 4) — an entry whose block is
# gone or no longer qualifies fails the gate, so the list cannot quietly rot into a blanket opt-out.
# Shrink it; do not grow it without a reason of that kind.
# ---------------------------------------------------------------------------
allowlist_file="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/doc-snippet-allowlist.txt"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

failures=0
fail() {
    echo "ERROR: $1" >&2
    failures=$((failures + 1))
}

# Emits one `key` line per qualifying fenced block: a ```csharp block whose body contains
# `AddDurableAgent(` or `AddTool(`. Headings inside fences are skipped, matching the slugger in
# verify-markdown-links.sh — `# comment` in a shell example is not a section heading.
scan_docs() {
    awk '
        function slug(line) {
            sub(/^#+[[:space:]]+/, "", line)
            sub(/[[:space:]]+#+[[:space:]]*$/, "", line)
            gsub(/`/, "", line)
            gsub(/\*\*|__|\*/, "", line)
            line = tolower(line)
            gsub(/[^a-z0-9 _-]/, "", line)
            gsub(/ /, "-", line)
            return line
        }
        FNR == 1 { infence = 0; heading = "" }
        /^[[:space:]]*(```|~~~)/ {
            if (!infence) {
                infence = 1
                lang = $0
                sub(/^[[:space:]]*(```|~~~)/, "", lang)
                body = ""
                next
            }
            infence = 0
            if (lang ~ /^csharp/ && (body ~ /AddDurableAgent\(/ || body ~ /AddTool\(/)) {
                print FILENAME "#" (heading == "" ? "-no-heading-" : heading)
            }
            next
        }
        infence { body = body "\n" $0; next }
        /^#{1,6}[[:space:]]/ { heading = slug($0) }
    ' "$@"
}

# Every heading anchor in the docs, as `<file>#<slug>`. BEGIN SNIPPET-PROSE keys point at a heading
# rather than a fenced block, so this is what they are validated against.
scan_headings() {
    awk '
        function slug(line) {
            sub(/^#+[[:space:]]+/, "", line)
            sub(/[[:space:]]+#+[[:space:]]*$/, "", line)
            gsub(/`/, "", line)
            gsub(/\*\*|__|\*/, "", line)
            line = tolower(line)
            gsub(/[^a-z0-9 _-]/, "", line)
            gsub(/ /, "-", line)
            return line
        }
        FNR == 1 { infence = 0 }
        /^[[:space:]]*(```|~~~)/ { infence = !infence; next }
        infence { next }
        /^#{1,6}[[:space:]]/ { print FILENAME "#" slug($0) }
    ' "$@"
}

# `git ls-files` rather than a bare glob: an untracked scratch copy of a doc must not create
# phantom coverage requirements, and bin/obj never enters the picture.
# Filtered to files that still exist on disk. `git ls-files` reads the INDEX, so a doc deleted in
# the working tree but not yet staged is still listed — and awk then dies on the missing file,
# taking the whole gate down over someone else's half-finished edit. CI always checks out a commit,
# where index and tree agree, so skipping absent files loses no coverage there.
# `if` rather than `[[ -f "$f" ]] && printf`: a bare && whose test fails on the LAST iteration
# leaves the loop's exit status at 1, and `set -e` then kills the gate with an empty log — the same
# silent-failure shape that once disarmed the ratchet check in verify-maf-doc-api-contracts.sh.
while IFS= read -r f; do
    if [[ -f "$f" ]]; then
        printf '%s\n' "$f"
    fi
done < <(git ls-files "$doc_glob/*.md") > "$work/doc-files.txt"
if [[ ! -s "$work/doc-files.txt" ]]; then
    fail "no tracked Markdown files found under $doc_glob — the doc layout moved and this gate is now blind"
    exit 1
fi

# shellcheck disable=SC2046  # deliberate word splitting: one path per line, no spaces in repo paths
scan_docs $(cat "$work/doc-files.txt") | sort > "$work/doc-keys.txt"

if [[ ! -f "$harness_csproj" ]]; then
    fail "$harness_csproj is missing — the doc-snippet harness is gone, so this gate proves nothing"
    exit 1
fi

# The SDK's default glob compiles every .cs under the project directory. A `Compile Remove` or
# `EnableDefaultCompileItems=false` would let a snippet file sit there looking like coverage while
# the compiler never opens it. Refuse that shape outright.
if grep -Eq '<Compile[[:space:]]+Remove|EnableDefaultCompileItems' "$harness_csproj"; then
    fail "$harness_csproj excludes files from the compile glob; every snippet file must be compiled"
fi

{ git ls-files "$harness_dir/*.cs"; git ls-files --others --exclude-standard "$harness_dir/*.cs"; } \
    | sort -u > "$work/harness-files.txt"

: > "$work/marker-keys.txt"
: > "$work/prose-keys.txt"
while IFS= read -r file; do
    [[ -n "$file" && -f "$file" ]] || continue

    begins=$(grep -c 'BEGIN SNIPPET' "$file" || true)
    ends=$(grep -c 'END SNIPPET' "$file" || true)
    if [[ "$begins" -ne "$ends" ]]; then
        fail "$file has $begins BEGIN SNIPPET marker(s) but $ends END SNIPPET marker(s)"
    fi

    # Two patterns, not one. A single `BEGIN SNIPPET[[:space:]]` pattern silently skips every
    # BEGIN SNIPPET-PROSE line (what follows "SNIPPET" there is "-PROSE", not whitespace), so prose
    # keys were never extracted and a renamed prose heading passed unnoticed.
    sed -n 's|.*BEGIN SNIPPET[[:space:]]\{1,\}\([^[:space:]]\{1,\}\).*|\1|p' "$file" >> "$work/marker-keys.txt"
    sed -n 's|.*BEGIN SNIPPET-PROSE[[:space:]]\{1,\}\([^[:space:]]\{1,\}\).*|\1|p' "$file" >> "$work/prose-keys.txt"
done < "$work/harness-files.txt"
sort -o "$work/marker-keys.txt" "$work/marker-keys.txt"

sort -o "$work/prose-keys.txt" "$work/prose-keys.txt"

# ---------------------------------------------------------------------------
# Rule 7 — a BEGIN SNIPPET-PROSE key must name a heading that still exists.
#
# A prose snippet quotes a claim made in sentences, so there is no fenced block to hold it to. What
# it CAN be held to is its heading: rename or delete that heading and the marker is pointing at
# nothing, which is exactly the rot the fenced-block rules catch for everyone else.
# ---------------------------------------------------------------------------
# shellcheck disable=SC2046  # deliberate word splitting: one path per line, no spaces in repo paths
scan_headings $(cat "$work/doc-files.txt") | sort -u > "$work/heading-keys.txt"

while IFS= read -r key; do
    [[ -n "$key" ]] || continue
    if ! grep -qxF "$key" "$work/heading-keys.txt"; then
        fail "prose marker '$key' names a heading that does not exist.
       BEGIN SNIPPET-PROSE is held to its heading rather than to a fenced block, so a renamed or
       deleted heading orphans it. Update the marker, or delete the harness file if the claim is gone."
    fi
done < "$work/prose-keys.txt"

# Allowlist keys contain a literal '#' themselves, so only WHOLE-LINE comments are stripped.
: > "$work/allow-keys.txt"
if [[ -f "$allowlist_file" ]]; then
    grep -v '^[[:space:]]*#' "$allowlist_file" | awk 'NF' | sort > "$work/allow-keys.txt"
fi

# ---------------------------------------------------------------------------
# Rules, evaluated as three sorted streams so bash 3.2 needs no associative arrays.
# ---------------------------------------------------------------------------
{
    awk '{ print "DOC\t" $0 }' "$work/doc-keys.txt"
    awk '{ print "MARK\t" $0 }' "$work/marker-keys.txt"
    awk '{ print "ALLOW\t" $0 }' "$work/allow-keys.txt"
} > "$work/stream.txt"

awk -F'\t' -v harness_dir="$harness_dir" -v self_name="$self_name" '
    $1 == "DOC"   { if (!($2 in doc)) doc_keys++; doc[$2]++;   next }
    $1 == "MARK"  { mark[$2]++;  next }
    $1 == "ALLOW" { allow[$2]++; next }
    END {
        for (key in doc) {
            if (key in allow) { allowed_used++; continue }
            want = doc[key]
            have = (key in mark) ? mark[key] : 0
            if (have == 0) {
                printf "MISSING\t%s\t%d\n", key, want
            } else if (have < want) {
                printf "SHORT\t%s\t%d\t%d\n", key, want, have
            } else {
                covered++
            }
        }
        for (key in mark) {
            if (!(key in doc)) printf "STALE_MARKER\t%s\n", key
        }
        for (key in allow) {
            if (!(key in doc)) printf "STALE_ALLOW\t%s\n", key
        }
        printf "SUMMARY\t%d\t%d\t%d\n", doc_keys, covered, allowed_used
    }
' "$work/stream.txt" > "$work/verdict.txt"

while IFS=$'\t' read -r kind a b c; do
    case "$kind" in
        MISSING)
            fail "$a registers an agent or a tool but has no harness snippet in $harness_dir/.
       Wrap the example in a new file there:
           // BEGIN SNIPPET $a (lines N-M)
           // END SNIPPET $a
       …or, if it structurally cannot compile, add it to scripts/doc-snippet-allowlist.txt with the reason."
            ;;
        SHORT)
            fail "$a holds $b qualifying code block(s) but only $c harness marker(s) — the extra block is uncovered"
            ;;
        STALE_MARKER)
            fail "harness marker '$a' matches no qualifying doc block — the heading was renamed or the example was removed. Update or delete the harness file."
            ;;
        STALE_ALLOW)
            fail "allowlist entry '$a' matches no qualifying doc block — remove the stale entry from doc-snippet-allowlist.txt."
            ;;
        SUMMARY)
            total_keys="$a"; covered_keys="$b"; allowed_keys="$c"
            ;;
    esac
done < "$work/verdict.txt"

if [[ "$failures" -ne 0 ]]; then
    echo "" >&2
    echo "doc-snippet coverage: $failures problem(s)." >&2
    exit 1
fi

marker_total=$(awk 'NF' "$work/marker-keys.txt" | wc -l | tr -d ' ')
echo "Doc-snippet coverage OK: ${total_keys:-0} qualifying key(s) under $doc_glob — ${covered_keys:-0} compiled in the harness, ${allowed_keys:-0} allowlisted, ${marker_total} marker(s) total."
