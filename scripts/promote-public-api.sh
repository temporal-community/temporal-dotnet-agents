#!/usr/bin/env bash
# Promotes PublicAPI.Unshipped.txt into PublicAPI.Shipped.txt for both libraries.
#
# Run this immediately AFTER a release is published. Until it runs, Shipped.txt does not describe
# what consumers actually have, and the RS0016/RS0017 analyzer gate silently protects nothing —
# every public member reads as "not yet shipped", so removing one raises no error.
#
# That is not hypothetical. Both Shipped.txt files sat empty through releases 0.8.0 to 0.14.2
# while 40+ public types shipped, so the gate was inert across that whole range and two breaking
# removals landed without being recorded.
#
# `--check` verifies promotion is not pending without modifying anything; use it in CI.
#
# Written for bash 3.2 (macOS default): no mapfile, no associative arrays.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
check_only=0
[ "${1:-}" = "--check" ] && check_only=1

projects="src/TemporalCommunity.Extensions.Agents src/TemporalCommunity.Extensions.AI"
pending=0

for project in $projects; do
    unshipped="$repo_root/$project/PublicAPI.Unshipped.txt"
    shipped="$repo_root/$project/PublicAPI.Shipped.txt"

    if [ ! -f "$unshipped" ] || [ ! -f "$shipped" ]; then
        echo "ERROR: missing public API files under $project" >&2
        exit 1
    fi

    # Entries only; `#nullable enable` is a per-file header, not an API member.
    entry_count=$(grep -cvE '^#nullable enable$|^$' "$unshipped" || true)

    if [ "$entry_count" -eq 0 ]; then
        echo "ok: $project has nothing pending promotion."
        continue
    fi

    pending=1

    if [ "$check_only" -eq 1 ]; then
        echo "PENDING: $project has $entry_count unpromoted public API entries." >&2
        grep -vE '^#nullable enable$|^$' "$unshipped" | head -5 | sed 's/^/    /' >&2
        [ "$entry_count" -gt 5 ] && echo "    … and $((entry_count - 5)) more" >&2
        continue
    fi

    work="$(mktemp -d)"
    trap 'rm -rf "$work"' EXIT

    # A *REMOVED* entry deletes its target from Shipped rather than being appended to it.
    grep -vE '^#nullable enable$|^$' "$unshipped" | grep '^\*REMOVED\*' \
        | sed 's/^\*REMOVED\*//' | sort > "$work/removed.txt" || true
    grep -vE '^#nullable enable$|^$' "$unshipped" | grep -v '^\*REMOVED\*' \
        | sort > "$work/added.txt" || true
    grep -vE '^#nullable enable$|^$' "$shipped" | sort > "$work/current.txt" || true

    # Shipped minus the recorded removals, plus the new additions.
    comm -23 "$work/current.txt" "$work/removed.txt" > "$work/kept.txt"
    cat "$work/kept.txt" "$work/added.txt" | grep -v '^$' | sort -u > "$work/merged.txt"

    {
        echo "#nullable enable"
        cat "$work/merged.txt"
    } > "$shipped"

    echo "#nullable enable" > "$unshipped"

    echo "promoted $project: +$(wc -l < "$work/added.txt" | tr -d ' ') added, -$(wc -l < "$work/removed.txt" | tr -d ' ') removed, $(wc -l < "$work/merged.txt" | tr -d ' ') shipped total."
    rm -rf "$work"
    trap - EXIT
done

if [ "$check_only" -eq 1 ] && [ "$pending" -eq 1 ]; then
    echo "" >&2
    echo "Run 'just promote-public-api' after publishing, then commit the result." >&2
    exit 1
fi
