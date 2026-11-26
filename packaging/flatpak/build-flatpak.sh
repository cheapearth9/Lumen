#!/usr/bin/env bash
# Build Lumen-Host.flatpak (x86_64). Needs flatpak and flatpak-builder installed.
# Usage: build-flatpak.sh <path/to/lumenhost> <outdir>
set -euo pipefail
bin="${1:?binary}"; outdir="${2:?outdir}"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
mkdir -p "$outdir"; outdir="$(cd "$outdir" && pwd)"
app=io.github.cheapearth9.Lumen

rm -rf "$here/payload"; mkdir -p "$here/payload"
install -m755 "$bin" "$here/payload/lumenhost"
"$repo/packaging/linux/fetch-ffmpeg.sh" x64 "$here/payload/ffmpeg"
"$repo/packaging/linux/fetch-adb.sh" "$here/payload/adb"
cp "$repo/packaging/linux/files/lumen-host.desktop" "$here/payload/"
cp "$repo/HostLinux/packaging/lumen.png" "$here/payload/"

flatpak remote-add --user --if-not-exists flathub https://dl.flathub.org/repo/flathub.flatpakrepo
cd "$here"
flatpak-builder --user --force-clean --disable-rofiles-fuse --install-deps-from=flathub \
  --repo="$here/repo" "$here/build-dir" "$here/$app.yml"
flatpak build-bundle "$here/repo" "$outdir/Lumen-Host.flatpak" "$app"
ls -la "$outdir"
