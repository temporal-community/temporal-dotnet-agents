#!/usr/bin/env bash
# Self-test for promote-public-api.sh.
#
# This script rewrites both public API baselines, so a defect in it is destructive and silent.
# One already shipped: every argument other than `--check` fell through to promotion, so the
# typo `--chek` cleared both Unshipped files while reporting success. These cases pin that the
# parser refuses anything it does not recognise, and that promotion itself moves entries the way
# the analyzer expects.
#
# Fixtures are throwaway directories; the real repository files are never touched.
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
promote="$script_dir/promote-public-api.sh"
failures=0

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

# Builds an isolated fixture repo and echoes its path.
make_fixture() {
    local name="$1" shipped="$2" unshipped="$3"
    local root="$work/$name"
    mkdir -p "$root/scripts" \
             "$root/src/TemporalCommunity.Extensions.Agents" \
             "$root/src/TemporalCommunity.Extensions.AI"
    cp "$promote" "$root/scripts/"
    for project in Agents AI; do
        printf '%s' "$shipped" > "$root/src/TemporalCommunity.Extensions.$project/PublicAPI.Shipped.txt"
        printf '%s' "$unshipped" > "$root/src/TemporalCommunity.Extensions.$project/PublicAPI.Unshipped.txt"
    done
    echo "$root"
}

check() {
    local label="$1" actual="$2" expected="$3"
    if [ "$actual" = "$expected" ]; then
        echo "ok: $label"
    else
        echo "FAIL: $label — expected '$expected', got '$actual'" >&2
        failures=$((failures + 1))
    fi
}

SHIPPED_BASE='#nullable enable
Lib.Kept() -> void
Lib.Removed() -> void
'
UNSHIPPED_BASE='#nullable enable
Lib.Added() -> void
*REMOVED*Lib.Removed() -> void
'

# ── Argument handling ────────────────────────────────────────────────────────
# Each invalid form must exit non-zero AND leave every file byte-identical.
for bad in "--chek" "-c" "check" "--check=true" "promote"; do
    root="$(make_fixture "arg$(echo "$bad" | tr -dc 'a-z')" "$SHIPPED_BASE" "$UNSHIPPED_BASE")"
    status=0
    ( cd "$root" && bash scripts/promote-public-api.sh "$bad" ) >/dev/null 2>&1 || status=$?
    check "rejects '$bad'" "$status" "2"

    # Compare files, not command substitutions — $(cat ...) strips the trailing newline and
    # would report a spurious difference on every case.
    printf '%s' "$SHIPPED_BASE" > "$work/expect_shipped"
    printf '%s' "$UNSHIPPED_BASE" > "$work/expect_unshipped"
    unchanged=yes
    for project in Agents AI; do
        cmp -s "$root/src/TemporalCommunity.Extensions.$project/PublicAPI.Unshipped.txt" "$work/expect_unshipped" || unchanged=no
        cmp -s "$root/src/TemporalCommunity.Extensions.$project/PublicAPI.Shipped.txt" "$work/expect_shipped" || unchanged=no
    done
    check "'$bad' left both baselines untouched" "$unchanged" "yes"
done

root="$(make_fixture argtoomany "$SHIPPED_BASE" "$UNSHIPPED_BASE")"
status=0
( cd "$root" && bash scripts/promote-public-api.sh --check extra ) >/dev/null 2>&1 || status=$?
check "rejects two arguments" "$status" "2"

# ── --check is read-only ─────────────────────────────────────────────────────
root="$(make_fixture checkpending "$SHIPPED_BASE" "$UNSHIPPED_BASE")"
status=0
( cd "$root" && bash scripts/promote-public-api.sh --check ) >/dev/null 2>&1 || status=$?
check "--check reports pending as failure" "$status" "1"
printf '%s' "$UNSHIPPED_BASE" > "$work/expect_unshipped"
readonly_ok=yes
cmp -s "$root/src/TemporalCommunity.Extensions.AI/PublicAPI.Unshipped.txt" "$work/expect_unshipped" || readonly_ok=no
check "--check changed nothing" "$readonly_ok" "yes"

root="$(make_fixture checkclean "$SHIPPED_BASE" '#nullable enable
')"
status=0
( cd "$root" && bash scripts/promote-public-api.sh --check ) >/dev/null 2>&1 || status=$?
check "--check passes when nothing is pending" "$status" "0"

# ── Promotion moves entries correctly ────────────────────────────────────────
root="$(make_fixture promote "$SHIPPED_BASE" "$UNSHIPPED_BASE")"
( cd "$root" && bash scripts/promote-public-api.sh ) >/dev/null 2>&1
shipped_after="$(cat "$root/src/TemporalCommunity.Extensions.Agents/PublicAPI.Shipped.txt")"

check "addition lands in Shipped" \
      "$(echo "$shipped_after" | grep -c 'Lib.Added() -> void')" "1"
check "*REMOVED* entry is deleted from Shipped" \
      "$(echo "$shipped_after" | grep -c 'Lib.Removed() -> void')" "0"
check "untouched entry survives" \
      "$(echo "$shipped_after" | grep -c 'Lib.Kept() -> void')" "1"
check "the *REMOVED* marker is not itself copied into Shipped" \
      "$(echo "$shipped_after" | grep -c '\*REMOVED\*')" "0"
printf '#nullable enable\n' > "$work/expect_header"
emptied=yes
cmp -s "$root/src/TemporalCommunity.Extensions.Agents/PublicAPI.Unshipped.txt" "$work/expect_header" || emptied=no
check "Unshipped is emptied to just the header" "$emptied" "yes"
check "Shipped keeps exactly one nullable header" \
      "$(echo "$shipped_after" | grep -c '#nullable enable')" "1"

# Promotion must be idempotent: a second run has nothing to do.
status=0
( cd "$root" && bash scripts/promote-public-api.sh --check ) >/dev/null 2>&1 || status=$?
check "--check is clean after promotion" "$status" "0"

if [ "$failures" -ne 0 ]; then
    echo "promote-public-api.sh self-test: $failures case(s) failed" >&2
    exit 1
fi

echo "promote-public-api.sh self-test: all cases passed."
