#!/usr/bin/env bash
# Download a static Linux ffmpeg and copy the binary to the given path.
# Usage: fetch-ffmpeg.sh <x64|arm64> <output-file>
set -euo pipefail
arch="${1:?arch (x64|arm64)}"; out="${2:?output path}"
case "$arch" in
  x64)   jv="ffmpeg-release-amd64-static.tar.xz"; btbn="ffmpeg-master-latest-linux64-gpl.tar.xz" ;;
  arm64) jv="ffmpeg-release-arm64-static.tar.xz"; btbn="ffmpeg-master-latest-linuxarm64-gpl.tar.xz" ;;
  *) echo "unknown arch $arch" >&2; exit 2 ;;
esac
tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT
fetch() { curl -fL --retry 3 --retry-delay 5 --connect-timeout 30 -o "$tmp/ff.tar.xz" "$1"; }
if fetch "https://johnvansickle.com/ffmpeg/releases/$jv"; then
  echo "using johnvansickle static build"
else
  echo "johnvansickle.com failed, falling back to BtbN" >&2
  fetch "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/$btbn"
fi
tar -xJf "$tmp/ff.tar.xz" -C "$tmp"
bin="$(find "$tmp" -type f -name ffmpeg -perm -u+x | head -n 1)"
[ -n "$bin" ] || { echo "ffmpeg binary not found in archive" >&2; exit 1; }
mkdir -p "$(dirname "$out")"
install -m 0755 "$bin" "$out"
"$out" -version | head -n 1 || true
