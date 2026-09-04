#!/usr/bin/env bash
# Verifies repository-local Markdown links. Network links and anchor fragments are
# intentionally out of scope; this gate catches moved or missing local documentation,
# samples, and source files without depending on network access.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
failures=0
checked=0

while IFS= read -r match; do
    source_file="${match%%:*}"
    markdown_link="${match#*:}"
    target="${markdown_link#](}"
    target="${target%)}"
    target="${target#<}"
    target="${target%>}"
    target="${target%%#*}"

    case "$target" in
        ''|http://*|https://*|mailto:*|tel:*|data:*)
            continue
            ;;
    esac

    checked=$((checked + 1))
    if [[ "$target" == /* ]]; then
        resolved="$target"
    else
        resolved="$(cd "$(dirname "$repo_root/$source_file")" && pwd)/$target"
    fi

    if [[ ! -e "$resolved" ]]; then
        echo "ERROR: $source_file links to missing local target: $target" >&2
        failures=$((failures + 1))
    fi
done < <(
    cd "$repo_root"
    { git ls-files '*.md'; git ls-files --others --exclude-standard '*.md'; } | sort -u | while IFS= read -r file; do
        rg --with-filename --no-heading -o '\]\(([^)#]+)(#[^)]*)?\)' "$file" || true
    done
)

if [[ "$failures" -ne 0 ]]; then
    exit 1
fi

echo "Markdown links are valid: $checked repository-local links checked."
