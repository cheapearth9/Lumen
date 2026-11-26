#!/usr/bin/env bash
# Download Google's Android platform-tools (Linux) and copy the adb binary to the given path.
# Usage: fetch-adb.sh <output-file>
set -euo pipefail
out="${1:?output file}"
tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT
curl -fL --retry 3 -o "$tmp/pt.zip" "https://dl.google.com/android/repository/platform-tools-latest-linux.zip"
unzip -q "$tmp/pt.zip" -d "$tmp"
install -Dm755 "$tmp/platform-tools/adb" "$out"
