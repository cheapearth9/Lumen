#!/usr/bin/env bash
# Build Lumen-Host-<version>-<arch>.AppImage.
# Usage: build-appimage.sh <version> <x64|arm64> <path/to/lumenhost> <outdir>
set -euo pipefail
version="${1:?version}"; arch="${2:?x64|arm64}"; bin="${3:?binary}"; outdir="${4:?outdir}"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
case "$arch" in x64) aiarch=x86_64 ;; arm64) aiarch=aarch64 ;; *) echo "bad arch" >&2; exit 2 ;; esac

work="$(mktemp -d)"; trap 'rm -rf "$work"' EXIT
appdir="$work/Lumen-Host.AppDir"
install -Dm755 "$bin" "$appdir/usr/bin/lumenhost"
"$here/../linux/fetch-ffmpeg.sh" "$arch" "$appdir/usr/bin/ffmpeg"
install -m755 "$here/AppRun" "$appdir/AppRun"
install -m644 "$here/lumen-host.desktop" "$appdir/lumen-host.desktop"
install -m644 "$here/lumen-host.png" "$appdir/lumen-host.png"
cp "$here/lumen-host.png" "$appdir/.DirIcon"

tool="$work/appimagetool"
curl -fL --retry 3 -o "$tool" "https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-$(uname -m).AppImage"
chmod +x "$tool"

mkdir -p "$outdir"
out="$(cd "$outdir" && pwd)/Lumen-Host-${version}-${aiarch}.AppImage"
# APPIMAGE_EXTRACT_AND_RUN: no FUSE on CI runners. No updateinfo (not published for zsync).
ARCH="$aiarch" APPIMAGE_EXTRACT_AND_RUN=1 "$tool" --no-appstream "$appdir" "$out"
chmod +x "$out"
ls -la "$out"
