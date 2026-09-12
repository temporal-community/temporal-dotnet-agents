#!/usr/bin/env bash
# Verifies repository-local Markdown links: that the target file exists, and — when the target is
# itself Markdown — that any `#fragment` matches a heading or explicit anchor in it. Network links
# are out of scope; so are fragments on non-Markdown targets (`#L42` line refs into source).
#
# A file-existence check alone passes a link that points at a heading which was renamed or never
# existed, which is the failure a rename actually produces.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
failures=0

# ---------------------------------------------------------------------------
# Hard dependency: ripgrep.
#
# Without this guard a missing `rg` is SILENT. Every call site wraps it in `|| true` (so that "no
# matches", which rg reports as exit 1, is not treated as an error), and `|| true` swallows exit 127
# just as happily. The result is zero hits, zero failures, and a confident success message about
# having checked nothing — this gate reported "0 repository-local targets and 0 anchors checked" on
# both CI runners for exactly this reason. Fail loudly instead; a checker that cannot run must not
# look like a checker that found nothing wrong.
# ---------------------------------------------------------------------------
if ! command -v rg >/dev/null 2>&1; then
    echo "ERROR: ripgrep (rg) is required by $(basename "${BASH_SOURCE[0]}") and is not installed." >&2
    echo "       macOS: brew install ripgrep    Debian/Ubuntu: sudo apt-get install -y ripgrep" >&2
    exit 2
fi

checked=0
anchors_checked=0

# Emits the GitHub-style anchor slugs for a Markdown file, one per line.
# Headings inside fenced code blocks are skipped — `# not a heading` in a shell example is not one.
anchor_slugs() {
    awk '
        /^[[:space:]]*(```|~~~)/ { fence = !fence; next }
        fence { next }
        /^#{1,6}[[:space:]]/ {
            line = $0
            sub(/^#+[[:space:]]+/, "", line)
            sub(/[[:space:]]+#+[[:space:]]*$/, "", line)       # closing ### form
            gsub(/`/, "", line)                                 # code spans
            gsub(/\*\*|__|\*/, "", line)                        # bold / italic
            while (match(line, /\[[^]]*\]\([^)]*\)/)) {         # [text](url) -> text
                inner = substr(line, RSTART + 1, RLENGTH - 1)
                sub(/\]\(.*/, "", inner)
                line = substr(line, 1, RSTART - 1) inner substr(line, RSTART + RLENGTH)
            }
            slug = tolower(line)
            gsub(/[^a-z0-9 _-]/, "", slug)
            gsub(/ /, "-", slug)
            if (slug == "") next
            n = seen[slug]++                                    # GitHub suffixes duplicates -1, -2
            print (n == 0) ? slug : slug "-" n
        }
        # Explicit anchors authors add by hand.
        {
            rest = $0
            while (match(rest, /<a[[:space:]][^>]*(id|name)="[^"]*"/)) {
                frag = substr(rest, RSTART, RLENGTH)
                sub(/.*(id|name)="/, "", frag)
                sub(/".*/, "", frag)
                if (frag != "") print frag
                rest = substr(rest, RSTART + RLENGTH)
            }
        }
    ' "$1"
}

while IFS= read -r match; do
    source_file="${match%%:*}"
    markdown_link="${match#*:}"
    target="${markdown_link#](}"
    target="${target%)}"
    target="${target#<}"
    target="${target%>}"

    fragment=""
    case "$target" in
        *#*) fragment="${target#*#}" ;;
    esac
    target="${target%%#*}"

    case "$target" in
        http://*|https://*|mailto:*|tel:*|data:*)
            continue
            ;;
        '')
            # A same-page link: `](#some-heading)`. Resolve the fragment against this file.
            if [[ -z "$fragment" ]]; then
                continue
            fi
            resolved="$repo_root/$source_file"
            ;;
        /*)
            resolved="$target"
            ;;
        *)
            resolved="$(cd "$(dirname "$repo_root/$source_file")" && pwd)/$target"
            ;;
    esac

    if [[ -n "${target}" ]]; then
        checked=$((checked + 1))
        if [[ ! -e "$resolved" ]]; then
            echo "ERROR: $source_file links to missing local target: $target" >&2
            failures=$((failures + 1))
            continue
        fi
    fi

    # Fragments are only meaningful against Markdown; `#L42` into a .cs file is a line ref.
    if [[ -z "$fragment" || "$resolved" != *.md || ! -f "$resolved" ]]; then
        continue
    fi

    anchors_checked=$((anchors_checked + 1))
    if ! anchor_slugs "$resolved" | grep -qxF -- "$fragment"; then
        echo "ERROR: $source_file links to '${target:-$source_file}#$fragment', but no such heading or anchor exists" >&2
        failures=$((failures + 1))
    fi
done < <(
    cd "$repo_root"
    { git ls-files '*.md'; git ls-files --others --exclude-standard '*.md'; } | sort -u | while IFS= read -r file; do
        rg --with-filename --no-heading -o '\]\(([^)#]*)(#[^)]*)?\)' "$file" || true
    done
)

if [[ "$failures" -ne 0 ]]; then
    exit 1
fi

echo "Markdown links are valid: $checked repository-local targets and $anchors_checked anchors checked."
