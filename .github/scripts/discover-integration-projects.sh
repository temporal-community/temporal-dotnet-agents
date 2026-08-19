#!/usr/bin/env bash
set -euo pipefail

repository_root=${1:-.}

python3 - "$repository_root" <<'PY'
import json
import pathlib
import sys

root = pathlib.Path(sys.argv[1]).resolve()
projects = sorted(root.glob("tests/*IntegrationTests/*.csproj"))
matrix = []
for project in projects:
    relative = project.relative_to(root).as_posix()
    matrix.append({"name": project.stem, "project": relative})

if not matrix:
    raise SystemExit("No integration-test projects were discovered.")

print(json.dumps(matrix, separators=(",", ":")))
PY
