#!/usr/bin/env bash
set -euo pipefail

if [ "$#" -ne 2 ]; then
  echo "usage: run-smoke.sh <packed-version> <package-directory>" >&2
  exit 2
fi

packed_version=$1
package_directory=$(cd "$2" && pwd -P)
project_directory=$(cd "$(dirname "$0")" && pwd -P)
project="$project_directory/ExtensibleDurableTurnsPackageSmokeTest.csproj"
packages_cache=$(mktemp -d "${TMPDIR:-/tmp}/temporal-ai-packed-smoke.XXXXXX")

cleanup() {
  if [ -n "${packages_cache:-}" ] && [ -d "$packages_cache" ]; then
    rm -rf -- "$packages_cache"
  fi
}
trap cleanup EXIT

verify_package() {
  package_id=$1
  package_id_lower=$(printf '%s' "$package_id" | tr '[:upper:]' '[:lower:]')
  restored_directory="$packages_cache/$package_id_lower/$packed_version"
  packed_file="$package_directory/$package_id.$packed_version.nupkg"

  test -f "$packed_file"
  test -d "$restored_directory"

  metadata_source=$(jq -r '.source' "$restored_directory/.nupkg.metadata")
  if [ "$metadata_source" != "$package_directory" ]; then
    echo "ERROR: $package_id restored from '$metadata_source', expected '$package_directory'." >&2
    exit 1
  fi

  expected_hash=$(openssl dgst -sha512 -binary "$packed_file" | openssl base64 -A)
  restored_hash=$(tr -d '\r\n' < "$restored_directory/$package_id_lower.$packed_version.nupkg.sha512")
  if [ "$expected_hash" != "$restored_hash" ]; then
    echo "ERROR: $package_id restored package hash does not match the fresh nupkg." >&2
    exit 1
  fi
}

run_mode() {
  target_framework=$1
  mode=$2
  expected_asset=$3

  NUGET_PACKAGES="$packages_cache" dotnet restore "$project" \
    -p:PackedVersion="$packed_version" \
    -p:SmokeTargetFramework="$target_framework" \
    --force \
    --no-http-cache \
    --source "$package_directory" \
    --source https://api.nuget.org/v3/index.json

  assets="$project_directory/obj/project.assets.json"
  for package_id in TemporalCommunity.Extensions.AI TemporalCommunity.Extensions.Agents; do
    verify_package "$package_id"
    if ! jq -e \
      --arg package "$package_id/$packed_version" \
      --arg asset "lib/$expected_asset/" \
      '[.targets[][$package].compile | keys[] | startswith($asset)] | any' \
      "$assets" >/dev/null; then
      echo "ERROR: $package_id did not select lib/$expected_asset for $target_framework." >&2
      exit 1
    fi
  done

  NUGET_PACKAGES="$packages_cache" dotnet run \
    --project "$project" \
    --configuration Release \
    --no-restore \
    -p:PackedVersion="$packed_version" \
    -p:SmokeTargetFramework="$target_framework" \
    -- "$mode"
}

run_mode net10.0 net10 net10.0
run_mode net8.0 netstandard netstandard2.1
