#!/usr/bin/env bash
# Self-test for verify-markdown-links.sh.
#
# The link checker is a gate: if it silently stopped rejecting things, every "links are valid" run
# after that would be a false pass, and nothing else in the build would notice. These cases assert
# it still fails on what it is supposed to fail on, and still passes what it should pass.
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
checker="$script_dir/verify-markdown-links.sh"
failures=0

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

# The checker walks `git ls-files`, so each case needs its own throwaway repo.
run_case() {
    local name="$1" expectation="$2" content="$3" extra_name="${4:-}" extra_content="${5:-}"
    local repo="$work/$name"

    mkdir -p "$repo/scripts"
    cp "$checker" "$repo/scripts/"
    git -C "$repo" init -q
    printf '%s\n' "$content" > "$repo/doc.md"
    if [[ -n "$extra_name" ]]; then
        printf '%s\n' "$extra_content" > "$repo/$extra_name"
    fi
    git -C "$repo" add -A

    local status=0
    ( cd "$repo" && bash scripts/verify-markdown-links.sh ) >"$repo/out.txt" 2>&1 || status=$?

    if [[ "$expectation" == "pass" && "$status" -ne 0 ]]; then
        echo "FAIL: $name should have passed but exited $status" >&2
        sed 's/^/    /' "$repo/out.txt" >&2
        failures=$((failures + 1))
    elif [[ "$expectation" == "reject" && "$status" -eq 0 ]]; then
        echo "FAIL: $name should have been rejected but passed" >&2
        sed 's/^/    /' "$repo/out.txt" >&2
        failures=$((failures + 1))
    else
        echo "ok: $name ($expectation)"
    fi
}

run_case missing-file reject '# Doc

[gone](./nope.md)'

run_case existing-file pass '# Doc

[there](./other.md)' other.md '# Other'

run_case bad-fragment-same-file reject '# Doc

## Real Heading

[typo](#real-headnig)'

run_case good-fragment-same-file pass '# Doc

## Real Heading

[fine](#real-heading)'

run_case bad-fragment-cross-file reject '# Doc

[typo](./other.md#no-such-thing)' other.md '# Other

## Actual Heading'

run_case good-fragment-cross-file pass '# Doc

[fine](./other.md#actual-heading)' other.md '# Other

## Actual Heading'

# Headings vary in ways the slugger has to normalise: code spans, punctuation, bold.
run_case slug-normalisation pass '# Doc

## `IDurableToolSource` requires suppressing TA001

## Batch fan-out **and** approval: behavior

[a](#idurabletoolsource-requires-suppressing-ta001)
[b](#batch-fan-out-and-approval-behavior)'

# A `# heading` inside a fenced block is a shell comment, not an anchor.
run_case fenced-heading-is-not-an-anchor reject '# Doc

```bash
# Not A Heading
```

[nope](#not-a-heading)'

# Explicit anchors authors add by hand must resolve.
run_case explicit-html-anchor pass '# Doc

<a id="hand-written"></a>

[fine](#hand-written)'

# Fragments on non-Markdown targets are line refs, not anchors — do not reject them.
run_case line-ref-into-source pass '# Doc

[code](./Thing.cs#L42)' Thing.cs 'class Thing { }'

# ---------------------------------------------------------------------------
# Missing ripgrep must be LOUD, not a silent pass.
#
# This is not hypothetical. Neither GitHub runner image ships rg, and because every call site wraps
# it in `|| true` (so "no matches", which rg reports as exit 1, is not an error), exit 127 was
# swallowed the same way. The checker then reported "0 repository-local targets and 0 anchors
# checked" and exited 0 — a green gate that had validated nothing. Only the reject cases above
# noticed, and only because a checker that finds nothing also rejects nothing.
# ---------------------------------------------------------------------------
rg_path="$(command -v rg || true)"
if [[ -n "$rg_path" ]]; then
    repo="$work/missing-rg"
    mkdir -p "$repo/scripts"
    cp "$checker" "$repo/scripts/"
    printf '%s\n' '# Doc' '' '[broken](./nope.md)' > "$repo/example.md"
    ( cd "$repo" && git init -q && git add -A )

    # A PATH with rg's directory removed, rather than a stub that exits 127: the guard asks
    # `command -v rg`, so a stub that EXISTS would satisfy it and prove nothing.
    rg_dir="$(dirname "$rg_path")"
    clean_path="$(printf '%s' "$PATH" | tr ':' '\n' | grep -Fxv "$rg_dir" | paste -sd: -)"

    status=0
    ( cd "$repo" && env PATH="$clean_path" bash scripts/verify-markdown-links.sh ) \
        >"$repo/out.txt" 2>&1 || status=$?

    if [[ "$status" -eq 0 ]]; then
        echo "FAIL: missing-ripgrep should have failed but exited 0" >&2
        sed 's/^/    /' "$repo/out.txt" >&2
        failures=$((failures + 1))
    elif ! grep -q "ripgrep" "$repo/out.txt"; then
        echo "FAIL: missing-ripgrep exited $status but never mentioned ripgrep" >&2
        sed 's/^/    /' "$repo/out.txt" >&2
        failures=$((failures + 1))
    elif grep -q "links are valid" "$repo/out.txt"; then
        echo "FAIL: missing-ripgrep still printed a success message" >&2
        sed 's/^/    /' "$repo/out.txt" >&2
        failures=$((failures + 1))
    else
        echo "ok: missing-ripgrep (reject)"
    fi
else
    echo "skip: missing-ripgrep (rg is not installed here, so the guard cannot be exercised)"
fi

if [[ "$failures" -ne 0 ]]; then
    echo "verify-markdown-links.sh self-test: $failures case(s) failed" >&2
    exit 1
fi

echo "verify-markdown-links.sh self-test: all cases passed."
