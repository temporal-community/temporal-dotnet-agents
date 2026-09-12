#!/usr/bin/env bash
# Self-test for verify-maf-doc-api-contracts.sh.
#
# This gate is a regex pre-filter, and a regex pre-filter that has quietly stopped matching reads
# exactly like a clean repository. The reject cases below pin what it must still catch; the pass
# cases pin the three CORRECT AddTool forms a sloppier pattern would false-positive on — a gate that
# cries wolf on valid docs gets disabled within a week, which is the same outcome as deleting it.
#
# Written for bash 3.2 (macOS default): no mapfile, no associative arrays.
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
checker="$script_dir/verify-maf-doc-api-contracts.sh"
failures=0

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

# The checker walks `git ls-files`, so each case needs its own throwaway repo.
run_case() {
    local name="$1" expectation="$2" doc="$3" src="${4:-}" factory_ratchet="${5:-}" stale_ratchet="${6:-}"
    local repo="$work/$name"

    mkdir -p "$repo/scripts" "$repo/docs/how-to/MAF" "$repo/src/Lib"
    cp "$checker" "$repo/scripts/"
    git -C "$repo" init -q

    printf '%s\n' "$doc" > "$repo/docs/how-to/MAF/example.md"
    printf '%s\n' "${src:-// nothing to see here}" > "$repo/src/Lib/Thing.cs"
    printf '%s\n' "$factory_ratchet" > "$repo/scripts/maf-doc-factory-first-ratchet.txt"
    printf '%s\n' "$stale_ratchet" > "$repo/scripts/maf-doc-stale-term-ratchet.txt"
    git -C "$repo" add -A

    local status=0
    ( cd "$repo" && bash scripts/verify-maf-doc-api-contracts.sh ) >"$repo/out.txt" 2>&1 || status=$?

    if [[ "$expectation" == "pass" && "$status" -ne 0 ]]; then
        echo "FAIL: $name should have passed but exited $status" >&2
        sed 's/^/    /' "$repo/out.txt" >&2
        failures=$((failures + 1))
    elif [[ "$expectation" == "reject" && "$status" -eq 0 ]]; then
        echo "FAIL: $name should have been rejected but passed" >&2
        sed 's/^/    /' "$repo/out.txt" >&2
        failures=$((failures + 1))
    elif [[ "$expectation" == "reject" ]] && ! grep -q . "$repo/out.txt"; then
        # A non-zero exit with an empty log is NOT a passing reject case. `set -euo pipefail` can
        # abort this gate mid-flight — a drained ratchet file did exactly that — and the result is
        # indistinguishable from a detection except that CI shows no reason. Demand the reason.
        echo "FAIL: $name exited $status but printed nothing — it crashed, it did not detect" >&2
        failures=$((failures + 1))
    else
        echo "ok: $name ($expectation)"
    fi
}

# ---------------------------------------------------------------------------
# REJECT — factory-first AddTool, in every shape it actually appears in.
# ---------------------------------------------------------------------------
run_case factory-first-single-line reject '```csharp
agent.AddTool(sp => AIFunctionFactory.Create(svc.Read, "read"));
```'

# The form the single-line version of this regex misses entirely. Most of the real defects in the
# repository are written this way, which is why `rg -U` is not optional.
run_case factory-first-multi-line reject '```csharp
agent.AddTool(
    sp => AIFunctionFactory.Create(svc.Read, "read"),
    opts => opts.NoRetry());
```'

run_case factory-first-parenthesised reject '```csharp
agent.AddTool((sp) => AIFunctionFactory.Create(svc.Read, "read"));
```'

run_case factory-first-typed-parameter reject '```csharp
agent.AddTool((IServiceProvider sp) => AIFunctionFactory.Create(svc.Read, "read"));
```'

# ---------------------------------------------------------------------------
# PASS — the three correct forms. All three are live in the docs today.
# ---------------------------------------------------------------------------
run_case correct-factory-with-name pass '```csharp
agent.AddTool("read", sp => AIFunctionFactory.Create(svc.Read, name: "read"));
```'

run_case correct-instance-with-options pass '```csharp
agent.AddTool(tool, opts => opts.NoRetry().WithTimeout(TimeSpan.FromSeconds(30)));
```'

run_case correct-factory-multi-line pass '```csharp
agent.AddTool(
    "apply_refund",
    sp => AIFunctionFactory.Create(
        sp.GetRequiredService<RefundService>().ApplyRefund,
        name: "apply_refund"),
    opts => opts.NoRetry());
```'

# Neighbouring forms that must not trip it either.
run_case correct-plain-instance pass '```csharp
agent.AddTool(lookupOrderTool);
agent.AddTools(reads);
agent.AddTool(byName["delete_inventory"], policy => policy.NoRetry());
```'

run_case correct-interceptor-registration pass '```csharp
agent.AddToolInterceptor(sp => new OrderPolicyInterceptor(sp.GetRequiredService<OrderPolicyService>()));
```'

# ---------------------------------------------------------------------------
# Ratchet behaviour.
# ---------------------------------------------------------------------------
run_case factory-first-within-ratchet pass '```csharp
agent.AddTool(sp => AIFunctionFactory.Create(svc.Read, "read"));
```' '' 'docs/how-to/MAF/example.md 1'

# A ratchet drained to zero is a file of nothing but comments. `grep -v '^#'` then selects no
# lines and exits 1, which under `set -euo pipefail` aborted the whole gate before it could report
# anything. Finishing the cleanup must not disarm the check that guards it.
run_case drained-ratchet-still-rejects reject '```csharp
agent.AddTool(sp => AIFunctionFactory.Create(svc.Read, "read"));
```' '' '# every site fixed; this list is intentionally empty
# numbers may only go down'

run_case drained-ratchet-passes-clean-docs pass '```csharp
agent.AddTool("read", sp => AIFunctionFactory.Create(svc.Read, name: "read"));
```' '' '# every site fixed; this list is intentionally empty'

run_case factory-first-exceeds-ratchet reject '```csharp
agent.AddTool(sp => AIFunctionFactory.Create(svc.Read, "read"));
agent.AddTool(sp => AIFunctionFactory.Create(svc.Write, "write"));
```' '' 'docs/how-to/MAF/example.md 1'

# ---------------------------------------------------------------------------
# Ratchet ENFORCEMENT. "Numbers may only go down" used to be a comment in a text file: slack
# printed a NOTICE and the build stayed green, so a fixed defect left its allowance behind and the
# allowance then covered the next regression. These cases pin that it is now a rule.
# ---------------------------------------------------------------------------
run_case ratchet-slack-fails reject '```csharp
agent.AddTool(sp => AIFunctionFactory.Create(svc.Read, "read"));
```' '' 'docs/how-to/MAF/example.md 2'

run_case ratchet-slack-with-zero-actual-fails reject '```csharp
agent.AddTool("read", sp => AIFunctionFactory.Create(svc.Read, name: "read"));
```' '' 'docs/how-to/MAF/example.md 1'

run_case ratchet-exact-passes pass '```csharp
agent.AddTool(sp => AIFunctionFactory.Create(svc.Read, "read"));
```' '' 'docs/how-to/MAF/example.md 1'

run_case ratchet-non-numeric-count-fails reject '```csharp
agent.AddTool(sp => AIFunctionFactory.Create(svc.Read, "read"));
```' '' 'docs/how-to/MAF/example.md many'

run_case ratchet-missing-count-fails reject '```csharp
agent.AddTool(sp => AIFunctionFactory.Create(svc.Read, "read"));
```' '' 'docs/how-to/MAF/example.md'

run_case ratchet-trailing-junk-fails reject '```csharp
agent.AddTool(sp => AIFunctionFactory.Create(svc.Read, "read"));
```' '' 'docs/how-to/MAF/example.md 1 because reasons'

# Only the first line ever took effect, so the second was silent cover for a raised allowance.
run_case ratchet-duplicate-path-fails reject '```csharp
agent.AddTool(sp => AIFunctionFactory.Create(svc.Read, "read"));
agent.AddTool(sp => AIFunctionFactory.Create(svc.Write, "write"));
```' '' 'docs/how-to/MAF/example.md 1
docs/how-to/MAF/example.md 2'

# ---------------------------------------------------------------------------
# REJECT / PASS — stale internal names, across docs AND src.
# ---------------------------------------------------------------------------
run_case stale-term-in-src reject '# Doc' '// Record for audit logging at ComposeDurableAgent time.'

run_case stale-term-in-doc reject 'The worker calls ResolveDurableAgent before dispatch.'

run_case stale-term-within-ratchet pass '# Doc' '// Record for audit logging at ComposeDurableAgent time.' '' 'src/Lib/Thing.cs 1'

run_case no-stale-terms pass '# Doc

The worker resolves the registered agent before dispatch.' '// Nothing renamed here.'

if [[ "$failures" -ne 0 ]]; then
    echo "verify-maf-doc-api-contracts.sh self-test: $failures case(s) failed" >&2
    exit 1
fi

echo "verify-maf-doc-api-contracts.sh self-test: all cases passed."
