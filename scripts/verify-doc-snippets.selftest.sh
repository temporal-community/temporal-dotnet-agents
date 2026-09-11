#!/usr/bin/env bash
# Self-test for the two halves of the doc-snippet gate:
#
#   Part A — verify-doc-snippet-coverage.sh, the drift detector, against fixture repos.
#   Part B — tests/docs/DocSnippets itself, against a known-bad mutation.
#
# WHY PART B EXISTS AT ALL
#   The harness's entire value is that the COMPILER reads those snippet files. If they ever stopped
#   being compiled — a `Compile Remove`, a moved directory, a project dropped from the solution —
#   `just build` would keep printing "Build succeeded" and the gate would report success forever
#   while checking nothing. Part B proves the files are in the compile set by breaking one on a COPY
#   and requiring the build to fail. This is the same failure mode verify-markdown-links.selftest.sh
#   exists to prevent.
#
#   The mutation is the exact defect this gate was built for: dropping the leading name argument
#   from a factory-first AddTool call, i.e. `AddTool("x", sp => ...)` -> `AddTool(sp => ...)`, which
#   binds to no overload.
#
# Written for bash 3.2 (macOS default): no mapfile, no associative arrays.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
script_dir="$repo_root/scripts"
checker="$script_dir/verify-doc-snippet-coverage.sh"
harness_dir="$repo_root/tests/docs/DocSnippets"
failures=0

work="$(mktemp -d)"
# The build copy has to live inside the repo: the harness csproj reaches the two src projects with
# `../../../src/...`, so the copy must sit at the same depth. artifacts/ is gitignored.
build_copy_root="$repo_root/artifacts/doc-snippet-selftest"
trap 'rm -rf "$work" "$build_copy_root"' EXIT

# ---------------------------------------------------------------------------
# Part A — drift detector behaviour, on throwaway git repos.
#
# The detector walks `git ls-files`, so each case needs its own repo. Each fixture gets a MAF doc,
# a harness project file, and whatever snippet files the case is about.
# ---------------------------------------------------------------------------
run_case() {
    local name="$1" expectation="$2" doc="$3" snippet="$4" allowlist="${5:-}"
    local repo="$work/$name"

    mkdir -p "$repo/scripts" "$repo/docs/how-to/MAF" "$repo/tests/docs/DocSnippets"
    cp "$checker" "$repo/scripts/"
    git -C "$repo" init -q

    printf '%s\n' "$doc" > "$repo/docs/how-to/MAF/example.md"
    printf '%s\n' '<Project Sdk="Microsoft.NET.Sdk" />' > "$repo/tests/docs/DocSnippets/DocSnippets.csproj"
    if [[ -n "$snippet" ]]; then
        printf '%s\n' "$snippet" > "$repo/tests/docs/DocSnippets/Snippet.cs"
    fi
    printf '%s\n' "$allowlist" > "$repo/scripts/doc-snippet-allowlist.txt"
    git -C "$repo" add -A

    local status=0
    ( cd "$repo" && bash scripts/verify-doc-snippet-coverage.sh ) >"$repo/out.txt" 2>&1 || status=$?

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

covered_doc='# Example

## Registration

```csharp
opts.AddDurableAgent("A", agent => { });
```'

covered_snippet='// BEGIN SNIPPET docs/how-to/MAF/example.md#registration (lines 5-7)
// END SNIPPET docs/how-to/MAF/example.md#registration'

run_case covered-block pass "$covered_doc" "$covered_snippet"

# The case this gate is FOR: a new registration example lands with no compiled counterpart.
run_case uncovered-block reject "$covered_doc" ''

# A heading rename orphans the harness file; the old key must not silently keep counting.
run_case renamed-heading reject '# Example

## Configuration

```csharp
opts.AddDurableAgent("A", agent => { });
```' "$covered_snippet"

# Two qualifying blocks under one heading: one marker must not cover both.
run_case two-blocks-one-marker reject '# Example

## Registration

```csharp
opts.AddDurableAgent("A", agent => { });
```

```csharp
agent.AddTool(tool, opts => opts.NoRetry());
```' "$covered_snippet"

run_case two-blocks-two-markers pass '# Example

## Registration

```csharp
opts.AddDurableAgent("A", agent => { });
```

```csharp
agent.AddTool(tool, opts => opts.NoRetry());
```' "$covered_snippet
$covered_snippet"

# Allowlisting is a real escape hatch…
run_case allowlisted-block pass "$covered_doc" '' 'docs/how-to/MAF/example.md#registration'

# …but a stale entry in it is itself a failure, so it cannot become a dumping ground.
run_case stale-allowlist-entry reject "$covered_doc" "$covered_snippet" 'docs/how-to/MAF/example.md#long-gone'

# A ```csharp block with no registration call is not this gate's business.
run_case unrelated-csharp-block pass '# Example

## Registration

```csharp
var x = 1;
```' ''

# `AddTool(` inside a non-csharp fence (a shell transcript, say) must not demand coverage.
run_case non-csharp-fence pass '# Example

## Registration

```bash
echo "agent.AddTool(x)"
```' ''

# Unbalanced markers mean someone edited a harness file by hand and lost track.
run_case unbalanced-markers reject "$covered_doc" '// BEGIN SNIPPET docs/how-to/MAF/example.md#registration (lines 5-7)'

# A csproj that drops files from the compile glob turns "covered" back into a lie.
compile_remove_case="$work/compile-remove"
mkdir -p "$compile_remove_case/scripts" "$compile_remove_case/docs/how-to/MAF" "$compile_remove_case/tests/docs/DocSnippets"
cp "$checker" "$compile_remove_case/scripts/"
git -C "$compile_remove_case" init -q
printf '%s\n' "$covered_doc" > "$compile_remove_case/docs/how-to/MAF/example.md"
printf '%s\n' "$covered_snippet" > "$compile_remove_case/tests/docs/DocSnippets/Snippet.cs"
: > "$compile_remove_case/scripts/doc-snippet-allowlist.txt"
cat > "$compile_remove_case/tests/docs/DocSnippets/DocSnippets.csproj" <<'CSPROJ'
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <Compile Remove="Snippet.cs" />
  </ItemGroup>
</Project>
CSPROJ
git -C "$compile_remove_case" add -A
compile_remove_status=0
( cd "$compile_remove_case" && bash scripts/verify-doc-snippet-coverage.sh ) >"$compile_remove_case/out.txt" 2>&1 || compile_remove_status=$?
if [[ "$compile_remove_status" -eq 0 ]]; then
    echo "FAIL: compile-remove should have been rejected but passed" >&2
    sed 's/^/    /' "$compile_remove_case/out.txt" >&2
    failures=$((failures + 1))
else
    echo "ok: compile-remove (reject)"
fi

# ---------------------------------------------------------------------------
# Part B — the harness really is compiled.
# ---------------------------------------------------------------------------
build_copy="$build_copy_root/DocSnippets"
rm -rf "$build_copy_root"
mkdir -p "$build_copy"
# bin/obj are excluded so the copy cannot pass on stale output from the real project.
( cd "$harness_dir" && find . -type d \( -name bin -o -name obj \) -prune -o -type f -print ) \
    | while IFS= read -r rel; do
        mkdir -p "$build_copy/$(dirname "$rel")"
        cp "$harness_dir/$rel" "$build_copy/$rel"
    done

build_copy_ok() {
    ( cd "$build_copy" && dotnet build DocSnippets.csproj -c Release --nologo -v q ) >"$work/build.txt" 2>&1
}

if ! build_copy_ok; then
    echo "FAIL: unmutated copy of the harness does not build — the self-test cannot prove anything" >&2
    tail -30 "$work/build.txt" | sed 's/^/    /' >&2
    failures=$((failures + 1))
else
    echo "ok: harness copy builds clean (baseline)"

    # Pick a real factory-first call site rather than hard-coding a filename: the mutation has to
    # land in a file the project actually compiles, whatever it is called this month.
    target="$(grep -rl 'AddTool("' "$build_copy/Snippets" | head -1)"
    if [[ -z "$target" ]]; then
        echo "FAIL: no factory-first AddTool call found to mutate — has the harness lost its seeded snippets?" >&2
        failures=$((failures + 1))
    else
        cp "$target" "$work/target.orig"
        # Drop the leading name argument: AddTool("x", sp => ...) -> AddTool(sp => ...)
        perl -0pi -e 's/AddTool\("[^"]*",\s*/AddTool(/' "$target"
        if ! diff -q "$work/target.orig" "$target" >/dev/null; then
            if build_copy_ok; then
                echo "FAIL: harness built successfully WITH a broken AddTool call — ${target#$build_copy/} is not being compiled" >&2
                failures=$((failures + 1))
            else
                echo "ok: mutated harness fails to build (${target#$build_copy/})"
            fi

            cp "$work/target.orig" "$target"
            if build_copy_ok; then
                echo "ok: reverted harness builds clean again"
            else
                echo "FAIL: reverted harness still does not build — the mutation was not cleanly undone" >&2
                tail -30 "$work/build.txt" | sed 's/^/    /' >&2
                failures=$((failures + 1))
            fi
        else
            echo "FAIL: mutation did not change $target — the pattern no longer matches" >&2
            failures=$((failures + 1))
        fi
    fi
fi

if [[ "$failures" -ne 0 ]]; then
    echo "doc-snippet self-test: $failures case(s) failed" >&2
    exit 1
fi

echo "doc-snippet self-test: all cases passed."
