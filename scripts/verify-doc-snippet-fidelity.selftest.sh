#!/usr/bin/env bash
# Self-test for verify-doc-snippet-fidelity.sh.
#
# A fidelity gate that has stopped comparing reads exactly like a repository whose snippets all
# match. The reject cases below pin the four drifts it exists to catch — changed harness text, doc
# text that is never compiled, a marker aimed at the wrong heading, and an allowlist entry that has
# outlived its reason — and the pass cases pin the two normalisations it must NOT report as drift,
# because a gate that cries wolf on a correct harness gets deleted.
#
# Written for bash 3.2 (macOS default).
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
checker="$script_dir/verify-doc-snippet-fidelity.sh"
engine="$script_dir/doc_snippet_fidelity.py"
failures=0

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

# $1 name  $2 pass|reject  $3 doc body  $4 harness body  $5 allowlist body
run_case() {
    local name="$1" expectation="$2" doc="$3" harness="$4" allow="${5:-}"
    local repo="$work/$name"

    mkdir -p "$repo/scripts" "$repo/docs/how-to/MAF" "$repo/tests/docs/DocSnippets/Snippets"
    cp "$checker" "$repo/scripts/"
    cp "$engine" "$repo/scripts/"
    printf '%s\n' "$doc"     > "$repo/docs/how-to/MAF/example.md"
    printf '%s\n' "$harness" > "$repo/tests/docs/DocSnippets/Snippets/Example.cs"
    printf '%s\n' "$allow"   > "$repo/scripts/allow.txt"

    local status=0
    ( cd "$repo" && bash scripts/verify-doc-snippet-fidelity.sh \
        tests/docs/DocSnippets/Snippets scripts/allow.txt ) >"$repo/out.txt" 2>&1 || status=$?

    if [[ "$expectation" == "pass" && "$status" -ne 0 ]]; then
        echo "FAIL: $name should have passed but exited $status" >&2
        sed 's/^/    /' "$repo/out.txt" >&2
        failures=$((failures + 1))
    elif [[ "$expectation" == "reject" && "$status" -eq 0 ]]; then
        echo "FAIL: $name should have been rejected but passed" >&2
        sed 's/^/    /' "$repo/out.txt" >&2
        failures=$((failures + 1))
    elif [[ "$expectation" == "reject" ]] && ! grep -q . "$repo/out.txt"; then
        # Non-zero with an empty log is a crash, not a detection. Same rule as the API-contracts
        # self-test, and for the same reason: `set -euo pipefail` can abort a gate mid-flight.
        echo "FAIL: $name exited $status but printed nothing — it crashed, it did not detect" >&2
        failures=$((failures + 1))
    else
        echo "ok: $name ($expectation)"
    fi
}

DOC_ONE_BLOCK='## Setup

```csharp
builder.Services.AddTemporalClient("localhost:7233", "default");
agent.AddTool(tool);
```'

HARNESS_MATCHING='internal static class Example
{
    internal static void Configure()
    {
        // BEGIN SNIPPET docs/how-to/MAF/example.md#setup
        builder.Services.AddTemporalClient("localhost:7233", "default");
        agent.AddTool(tool);
        // END SNIPPET docs/how-to/MAF/example.md#setup
    }
}'

run_case exact-match pass "$DOC_ONE_BLOCK" "$HARNESS_MATCHING"

# The drift that motivated this gate: a doc line grows a trailing comment, the harness copy does not.
run_case changed-harness-text reject '## Setup

```csharp
builder.Services.AddTemporalClient("localhost:7233", "default");  // explicit, see note
agent.AddTool(tool);
```' "$HARNESS_MATCHING"

# The failure the previous whole-file containment check could not see at all: a line ADDED to the
# doc block that nothing compiles. Containment is one-directional; this is why matching is not.
run_case doc-line-never-compiled reject '## Setup

```csharp
builder.Services.AddTemporalClient("localhost:7233", "default");
agent.AddTool(tool);
agent.AddTool(secondTool);
```' "$HARNESS_MATCHING"

# Same file, wrong heading. Whole-file containment happily matched lines from anywhere.
run_case wrong-heading-same-file reject '## Setup

```csharp
builder.Services.AddTemporalClient("localhost:7233", "default");
agent.AddTool(tool);
```

## Other Section

Nothing here.' 'internal static class Example
{
    internal static void Configure()
    {
        // BEGIN SNIPPET docs/how-to/MAF/example.md#other-section
        builder.Services.AddTemporalClient("localhost:7233", "default");
        agent.AddTool(tool);
        // END SNIPPET docs/how-to/MAF/example.md#other-section
    }
}'

run_case missing-heading reject "$DOC_ONE_BLOCK" 'internal static class Example
{
    internal static void Configure()
    {
        // BEGIN SNIPPET docs/how-to/MAF/example.md#renamed-away
        agent.AddTool(tool);
        // END SNIPPET docs/how-to/MAF/example.md#renamed-away
    }
}'

# Two blocks under one heading: each marker must claim its own, so two markers quoting the SAME
# block leaves the other block uncompiled.
run_case two-blocks-two-markers pass '## Setup

```csharp
agent.AddTool(first);
```

```csharp
agent.AddTool(second);
```' 'internal static class Example
{
    internal static void A()
    {
        // BEGIN SNIPPET docs/how-to/MAF/example.md#setup
        agent.AddTool(first);
        // END SNIPPET docs/how-to/MAF/example.md#setup
    }

    internal static void B()
    {
        // BEGIN SNIPPET docs/how-to/MAF/example.md#setup
        agent.AddTool(second);
        // END SNIPPET docs/how-to/MAF/example.md#setup
    }
}'

run_case two-markers-same-block reject '## Setup

```csharp
agent.AddTool(first);
```

```csharp
agent.AddTool(second);
```' 'internal static class Example
{
    internal static void A()
    {
        // BEGIN SNIPPET docs/how-to/MAF/example.md#setup
        agent.AddTool(first);
        // END SNIPPET docs/how-to/MAF/example.md#setup
    }

    internal static void B()
    {
        // BEGIN SNIPPET docs/how-to/MAF/example.md#setup
        agent.AddTool(first);
        // END SNIPPET docs/how-to/MAF/example.md#setup
    }
}'

# ── Normalisations that must NOT be reported as drift ────────────────────────
# C# forbids `using` inside a method body, so the harness hoists them above the markers.
run_case hoisted-usings pass '## Setup

```csharp
using Microsoft.Extensions.AI;   // needed for AIFunctionFactory
using Temporalio.Client;

agent.AddTool(tool);
```' 'internal static class Example
{
    internal static void Configure()
    {
        // BEGIN SNIPPET docs/how-to/MAF/example.md#setup
        agent.AddTool(tool);
        // END SNIPPET docs/how-to/MAF/example.md#setup
    }
}'

# A #pragma written at column 0 inside an indented harness body must not break the dedent.
run_case pragma-at-column-zero pass '## Setup

```csharp
#pragma warning disable MEAI001
agent.AddTool(tool);
#pragma warning restore MEAI001
```' 'internal static class Example
{
    internal static void Configure()
    {
#pragma warning disable MEAI001
        // BEGIN SNIPPET docs/how-to/MAF/example.md#setup
#pragma warning disable MEAI001
        agent.AddTool(tool);
#pragma warning restore MEAI001
        // END SNIPPET docs/how-to/MAF/example.md#setup
#pragma warning restore MEAI001
    }
}'

# ── Allowlist hygiene ────────────────────────────────────────────────────────
run_case allowlisted-adaptation pass '## Setup

```csharp
agent.AddTool(t, opts => opts.RetryPolicy = ...);
```' 'internal static class Example
{
    internal static void Configure()
    {
        // BEGIN SNIPPET docs/how-to/MAF/example.md#setup
        agent.AddTool(t, opts => opts.RetryPolicy = policy);
        // END SNIPPET docs/how-to/MAF/example.md#setup
    }
}' "$(printf 'docs/how-to/MAF/example.md#setup\tthe doc writes ... which does not compile')"

run_case stale-allowlist-key-gone reject "$DOC_ONE_BLOCK" "$HARNESS_MATCHING" \
    "$(printf 'docs/how-to/MAF/example.md#no-such-key\tobsolete reason')"

# An exemption whose snippet has become an exact match is dead weight, and dead weight is a hole
# left open for the next real drift.
run_case stale-allowlist-now-exact reject "$DOC_ONE_BLOCK" "$HARNESS_MATCHING" \
    "$(printf 'docs/how-to/MAF/example.md#setup\tno longer true')"

run_case malformed-allowlist-line reject "$DOC_ONE_BLOCK" "$HARNESS_MATCHING" \
    'docs/how-to/MAF/example.md#setup no tab here'

if [[ "$failures" -ne 0 ]]; then
    echo "verify-doc-snippet-fidelity.sh self-test: $failures case(s) failed" >&2
    exit 1
fi

echo "verify-doc-snippet-fidelity.sh self-test: all cases passed."
