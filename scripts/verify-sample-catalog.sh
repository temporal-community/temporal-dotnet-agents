#!/usr/bin/env bash
# Validates the authoritative sample catalog without requiring a Temporal service,
# credentials, or a build. A sample is a tracked project root, not merely a directory:
# ignored bin/ and obj/ output must never create a catalog entry.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
catalog="$repo_root/samples/catalog.md"
expected="$(mktemp)"
declared="$(mktemp)"
trap 'rm -f "$expected" "$declared"' EXIT

if [[ ! -f "$catalog" ]]; then
    echo "ERROR: Sample catalog not found: $catalog" >&2
    exit 1
fi

while IFS= read -r project; do
    relative="${project#"$repo_root/samples/"}"
    stack="${relative%%/*}"
    remainder="${relative#*/}"
    sample="${remainder%%/*}"
    printf '%s/%s\n' "$stack" "$sample"
done < <(
    find "$repo_root/samples/MAF" "$repo_root/samples/MEAI" "$repo_root/samples/MCP" \
        -type f -name '*.csproj' ! -path '*/bin/*' ! -path '*/obj/*' -print | sort
) | sort -u > "$expected"

# Catalog paths are relative to samples/, e.g. [BasicAgent](MAF/BasicAgent/).
sed -nE 's#.*\]\(((MAF|MEAI|MCP)/[^/)]+)(/)?\).*#\1#p' "$catalog" | sort > "$declared"

if [[ -s "$declared" ]]; then
    duplicates="$(uniq -d "$declared")"
    if [[ -n "$duplicates" ]]; then
        echo "ERROR: Sample catalog contains duplicate entries:" >&2
        printf '%s\n' "$duplicates" >&2
        exit 1
    fi
fi

if ! diff -u "$expected" "$declared"; then
    echo "ERROR: samples/catalog.md must contain each tracked sample-project root exactly once." >&2
    exit 1
fi

echo "Sample catalog is valid: $(wc -l < "$expected" | tr -d ' ') tracked sample-project roots."

# Drift guard: every MAF sample registered in justfile's test-samples-maf canary loop uses
# AddDurableAgent/AddTemporalAgents, which defaults EnableSearchAttributes to true. A fresh
# `temporal server start-dev` does not auto-register the AgentName/SessionCreatedAt/TurnCount
# search attributes those agents upsert (confirmed via a live test against CLI 1.8.3 / Server
# 1.31.2 — see .clans/knowledge/maf-sample-search-attribute-doc-gap.md), so every such sample's
# README must document the --search-attribute prerequisite or a fresh contributor's first run
# fails outright. This check exists so a future canary-registered MAF sample can't silently
# reintroduce the gap that affected ~15 of the 17 agent samples until 2026-09-05.
justfile="$repo_root/justfile"
missing_docs=""
while IFS= read -r sample_name; do
    readme="$repo_root/samples/MAF/$sample_name/README.md"
    if [[ ! -f "$readme" ]]; then
        continue
    fi
    if ! grep -q -- '--search-attribute' "$readme"; then
        missing_docs="$missing_docs $sample_name"
    fi
done < <(sed -nE 's#.*"([A-Za-z0-9]+):samples/MAF/[A-Za-z0-9]+:[0-9]+".*#\1#p' "$justfile")

if [[ -n "$missing_docs" ]]; then
    echo "ERROR: these MAF samples are registered in justfile's test-samples-maf canary loop" >&2
    echo "  but their README does not document the required --search-attribute prerequisite" >&2
    echo "  (AddDurableAgent defaults EnableSearchAttributes to true):" >&2
    printf ' -%s\n' "$missing_docs" >&2
    exit 1
fi

echo "MAF canary samples all document the required search-attribute prerequisite."
