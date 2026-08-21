#!/usr/bin/env bash
set -euo pipefail

version=1.8.0
cache_root=${1:?usage: prepare-temporal-cli.sh <cache-directory>}
os_name=$(uname -s)
architecture=$(uname -m)

case "$os_name/$architecture" in
  Linux/x86_64)
    platform=linux_amd64
    expected=896c6132d6d969f84c3f2382a31abd9a67a06ed3008c1a37c3573fe81d730e4a
    ;;
  Linux/aarch64|Linux/arm64)
    platform=linux_arm64
    expected=52d2d3e4f35c4ad2d45d0677eae1e1e3c7ba3c7f40a6a42d9a7f34e541c3dd57
    ;;
  Darwin/x86_64)
    platform=darwin_amd64
    expected=7ea6edf15329e8169233d3e38a0c1f6464cf84ee25140c16ff059ea4f802762e
    ;;
  Darwin/arm64)
    platform=darwin_arm64
    expected=46b4ac2b603e2b68d684da728bccd938a69acfad9c5e1a469d28d00a64e8bc9c
    ;;
  *)
    echo "Unsupported Temporal CLI test platform: $os_name/$architecture" >&2
    exit 1
    ;;
esac

archive_name="temporal_cli_${version}_${platform}.tar.gz"
archive="$cache_root/$archive_name"
url="https://github.com/temporalio/cli/releases/download/v${version}/${archive_name}"
mkdir -p "$cache_root"

if [[ ! -f "$archive" ]]; then
  curl --fail --location --proto '=https' --tlsv1.2 --output "$archive" "$url"
fi

actual=$(shasum -a 256 "$archive" | awk '{print $1}')
if [[ "$actual" != "$expected" ]]; then
  echo "Temporal CLI checksum mismatch for $archive_name." >&2
  echo "Expected: $expected" >&2
  echo "Actual:   $actual" >&2
  exit 1
fi

binary_directory="$(cd "$cache_root" && pwd)/bin"
mkdir -p "$binary_directory"
tar -xzf "$archive" -C "$binary_directory" temporal
chmod +x "$binary_directory/temporal"
printf '%s\n' "$binary_directory/temporal"
