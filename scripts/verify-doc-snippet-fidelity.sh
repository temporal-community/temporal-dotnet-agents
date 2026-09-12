#!/usr/bin/env bash
# Verifies that harness snippet text still MATCHES the doc block it claims to quote.
#
# WHY THIS EXISTS
#   tests/docs/DocSnippets/README.md promises "text between the markers stays verbatim", and the
#   whole value of the harness rests on it: a snippet that has quietly diverged proves that the
#   SNIPPET compiles, not that the DOC does. Coverage checks a marker exists; compilation checks the
#   marker's body is valid C#. Neither looks at whether the body is still the published text.
#
# WHAT IT COMPARES
#   The doc's fenced ```csharp block under the KEYED HEADING, against the marker body, as ordered
#   sequences after dedenting. That is deliberately strict in three ways an earlier, weaker version
#   of this script was not:
#     * scoped to the keyed heading  — a line matching some unrelated part of the file is not a match
#     * ordered                      — reordered lines are drift
#     * bidirectional                — a line ADDED to the doc block and never compiled now fails
#
# NORMALISATION (the only two liberties taken)
#   1. Common leading indentation is stripped from both sides. A harness wraps doc text in a class
#      and a method, so every line shifts; that is not drift.
#   2. Leading `using` directives and blank lines are dropped from both sides. C# forbids usings
#      inside a method body, so the harness hoists them above the markers by necessity — the README
#      documents this. Only LEADING ones are dropped, so a using in the middle of a block still
#      counts.
#
# ADAPTED SNIPPETS
#   A snippet that genuinely cannot be verbatim — prose whose doc form is not valid C#, a [Fact]
#   needing injected parameters — is listed in the allowlist as `<key><TAB><reason>`. It then falls
#   back to containment WITHIN THAT SECTION (still far stronger than the whole-file check it
#   replaces). An entry whose snippet has become exact fails the gate, so the list cannot rot.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

harness_dir="${1:-tests/docs/DocSnippets/Snippets}"
allowlist="${2:-scripts/doc-snippet-fidelity-allowlist.txt}"

python3 scripts/doc_snippet_fidelity.py "$harness_dir" "$allowlist"
