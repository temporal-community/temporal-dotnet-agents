#!/usr/bin/env bash
# Verifies that harness snippet text still MATCHES the doc it claims to quote.
#
# WHY THIS EXISTS
#   tests/docs/DocSnippets/README.md promises "text between the markers stays verbatim", and the
#   whole value of the harness rests on it: a snippet that has quietly diverged from its doc proves
#   that the SNIPPET compiles, not that the DOC does. Coverage (verify-doc-snippet-coverage.sh)
#   checks that a marker exists; compilation checks that the marker's body is valid C#. Neither one
#   looks at whether the body is still the same text the reader sees.
#
#   This gap was not hypothetical. It was found by hand: a doc line grew a trailing `//` comment and
#   the harness copy did not, leaving the harness asserting something subtly different from what was
#   published. One line, invisible to both existing gates.
#
# WHAT IT COMPARES
#   Every non-blank line between BEGIN/END markers must appear, after stripping leading and trailing
#   whitespace, somewhere in the doc file the key names. Indentation is deliberately ignored — a
#   harness wraps doc text in a class and a method, so every line is shifted. Line ORDER is not
#   checked either; this is a containment check, which is enough to catch edited, dropped, or
#   silently "improved" text without fighting the wrapping.
#
# ALLOWLIST
#   Some adaptations cannot be verbatim and are legitimate. Each needs a reason in
#   scripts/doc-snippet-fidelity-allowlist.txt, as `<key>\t<exact stripped line>`.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

harness_dir="${1:-tests/docs/DocSnippets/Snippets}"
allowlist="${2:-scripts/doc-snippet-fidelity-allowlist.txt}"

python3 - "$harness_dir" "$allowlist" <<'PY'
import sys, os, re, glob

harness_dir, allowlist_path = sys.argv[1], sys.argv[2]

allow = set()
if os.path.exists(allowlist_path):
    for raw in open(allowlist_path):
        line = raw.rstrip("\n")
        if not line.strip() or line.lstrip().startswith("#"):
            continue
        if "\t" not in line:
            print(f"ERROR: malformed allowlist line (needs a TAB): {line}", file=sys.stderr)
            sys.exit(2)
        key, text = line.split("\t", 1)
        allow.add((key.strip(), text.strip()))

used, problems, checked = set(), [], 0

for path in sorted(glob.glob(os.path.join(harness_dir, "**", "*.cs"), recursive=True)):
    lines = open(path).read().split("\n")
    i = 0
    while i < len(lines):
        m = re.search(r"BEGIN SNIPPET(?:-PROSE)?\s+(\S+)", lines[i])
        if not m:
            i += 1
            continue
        key = m.group(1)
        doc_path = key.split("#")[0]
        j, body = i + 1, []
        while j < len(lines) and "END SNIPPET" not in lines[j]:
            body.append(lines[j])
            j += 1
        if j >= len(lines):
            problems.append(f"{path}: BEGIN marker for {key} has no END marker")
            break
        if not os.path.exists(doc_path):
            problems.append(f"{path}: {key} names a doc that does not exist: {doc_path}")
            i = j + 1
            continue

        doc_lines = {d.strip() for d in open(doc_path).read().split("\n")}
        checked += 1
        for line in body:
            text = line.strip()
            if not text:
                continue
            if text in doc_lines:
                continue
            if (key, text) in allow:
                used.add((key, text))
                continue
            problems.append(
                f"{path}\n       key : {key}\n       line: {text}\n"
                f"       This line is in the harness but not in {doc_path}. Re-sync the snippet with\n"
                f"       the doc, or add it to {allowlist_path} with the reason it cannot be verbatim."
            )
        i = j + 1

for key, text in sorted(allow - used):
    problems.append(
        f"stale allowlist entry — no longer needed:\n       key : {key}\n       line: {text}\n"
        f"       Remove it from {allowlist_path}; the snippet now matches the doc."
    )

for p in problems:
    print(f"ERROR: {p}", file=sys.stderr)

if problems:
    print(f"\nDoc-snippet fidelity: {len(problems)} problem(s).", file=sys.stderr)
    sys.exit(1)

print(f"Doc-snippet fidelity OK: {checked} snippet region(s) match their docs "
      f"({len(allow)} allowlisted adaptation(s)).")
PY
