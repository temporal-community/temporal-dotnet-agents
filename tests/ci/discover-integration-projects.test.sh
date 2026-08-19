#!/usr/bin/env bash
set -euo pipefail

repository_root=$(cd "$(dirname "$0")/../.." && pwd -P)
fixture=$(mktemp -d "${TMPDIR:-/tmp}/integration-discovery.XXXXXX")
trap 'rm -rf -- "$fixture"' EXIT

mkdir -p "$fixture/tests/Zeta.IntegrationTests" "$fixture/tests/Alpha.IntegrationTests"
touch "$fixture/tests/Zeta.IntegrationTests/Zeta.IntegrationTests.csproj"
touch "$fixture/tests/Alpha.IntegrationTests/Alpha.IntegrationTests.csproj"

actual=$(
  "$repository_root/.github/scripts/discover-integration-projects.sh" "$fixture"
)
expected='[{"name":"Alpha.IntegrationTests","project":"tests/Alpha.IntegrationTests/Alpha.IntegrationTests.csproj"},{"name":"Zeta.IntegrationTests","project":"tests/Zeta.IntegrationTests/Zeta.IntegrationTests.csproj"}]'

if [[ "$actual" != "$expected" ]]; then
  echo "Integration discovery did not return the expected stable matrix." >&2
  echo "Expected: $expected" >&2
  echo "Actual:   $actual" >&2
  exit 1
fi
