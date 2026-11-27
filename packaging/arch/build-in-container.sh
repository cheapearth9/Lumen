#!/usr/bin/env bash
# Build the Arch package inside an archlinux container.
# Usage: build-in-container.sh <version> <path/to/lumenhost (x64)> <outdir>
set -euo pipefail
version="${1:?version}"; bin="${2:?binary}"; outdir="${3:?outdir}"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
bin="$(readlink -f "$bin")"
mkdir -p "$outdir"; outdir="$(readlink -f "$outdir")"

stage="$(mktemp -d)"; trap 'rm -rf "$stage"' EXIT
cp "$here/PKGBUILD" "$here/lumenhost.install" "$stage/"
cp "$repo/packaging/linux/files/"* "$stage/"
cp "$repo/HostLinux/packaging/lumen.png" "$stage/lumen.png"
tmp="$(mktemp -d)"; cp "$bin" "$tmp/lumenhost"
tar -C "$tmp" -czf "$stage/lumenhost-${version}-linux-x64.tar.gz" lumenhost
rm -rf "$tmp"
chmod -R a+rX "$stage"

# Docker Hub's CDN occasionally resets the connection mid-pull; retry a few times before giving up.
for attempt in 1 2 3 4 5; do
  docker pull archlinux:latest && break
  echo "docker pull failed (attempt $attempt), retrying..." >&2
  [ "$attempt" = 5 ] && exit 1
  sleep $((attempt * 15))
done

docker run --rm -e LUMEN_PKGVER="$version" -v "$stage":/in:ro -v "$outdir":/out archlinux:latest bash -euxc '
  pacman -Syu --noconfirm --needed base-devel ffmpeg xorg-xdpyinfo xorg-xrandr
  useradd -m builder
  mkdir /home/builder/pkg /home/builder/out && cp /in/* /home/builder/pkg/ && chown -R builder /home/builder
  su builder -c "cd /home/builder/pkg && LUMEN_PKGVER=$LUMEN_PKGVER PKGDEST=/home/builder/out makepkg -f -d --noconfirm"
  cp /home/builder/out/*.pkg.tar.zst /out/
'
ls -la "$outdir"
