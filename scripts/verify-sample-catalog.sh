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
