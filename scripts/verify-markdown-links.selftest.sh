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

if [[ "$failures" -ne 0 ]]; then
    echo "verify-markdown-links.sh self-test: $failures case(s) failed" >&2
    exit 1
fi

echo "verify-markdown-links.sh self-test: all cases passed."
