#!/usr/bin/env bash
# Build lumenhost_<version>_<arch>.deb from a published single-file binary.
# Usage: build-deb.sh <version> <amd64|arm64> <path/to/lumenhost> <outdir>
set -euo pipefail
version="${1:?version}"; arch="${2:?deb arch}"; bin="${3:?binary}"; outdir="${4:?outdir}"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
files="$here/files"
icon="$repo/HostLinux/packaging/lumen.png"
[ -f "$bin" ] || { echo "binary not found: $bin" >&2; exit 1; }
[ -f "$icon" ] || { echo "icon not found: $icon" >&2; exit 1; }

root="$(mktemp -d)"; trap 'rm -rf "$root"' EXIT
install -Dm0755 "$bin" "$root/usr/bin/lumenhost"
install -Dm0644 "$files/lumen-host.desktop" "$root/usr/share/applications/lumen-host.desktop"
install -Dm0644 "$icon" "$root/usr/share/icons/hicolor/512x512/apps/lumen-host.png"
install -Dm0644 "$files/lumenhost.service" "$root/usr/lib/systemd/user/lumenhost.service"
install -Dm0644 "$files/lumen.conf" "$root/etc/modules-load.d/lumen.conf"
install -Dm0644 "$files/60-lumen-uinput.rules" "$root/etc/udev/rules.d/60-lumen-uinput.rules"

mkdir -p "$root/DEBIAN"
installed_size="$(du -sk --exclude=DEBIAN "$root" | cut -f1)"
cat > "$root/DEBIAN/control" <<CTL
Package: lumenhost
Version: ${version}
Section: utils
Priority: optional
Architecture: ${arch}
Installed-Size: ${installed_size}
Maintainer: Lumen <noreply@users.noreply.github.com>
Depends: libc6, libstdc++6, ffmpeg, x11-utils, x11-xserver-utils, python3, python3-gi, gir1.2-gstreamer-1.0, gstreamer1.0-tools, gstreamer1.0-pipewire, gstreamer1.0-plugins-base, gstreamer1.0-plugins-good, gstreamer1.0-plugins-bad, gstreamer1.0-vaapi
Recommends: pulseaudio-utils | pipewire-pulse
Homepage: https://github.com/cheapearth9/Lumen
Description: Lumen Host - stream this screen to an iPhone or iPad
 Lumen Host captures the X11 screen with ffmpeg and streams it to the
 Lumen app over USB or the local network, with remote mouse and keyboard
 input via /dev/uinput. Works on Wayland (screen-share portal) and X11.
CTL
echo "/etc/modules-load.d/lumen.conf" > "$root/DEBIAN/conffiles"
echo "/etc/udev/rules.d/60-lumen-uinput.rules" >> "$root/DEBIAN/conffiles"

cat > "$root/DEBIAN/postinst" <<'PST'
#!/bin/sh
set -e
if [ "$1" = "configure" ]; then
    getent group input >/dev/null 2>&1 || groupadd -r input || true
    modprobe uinput >/dev/null 2>&1 || true
    if command -v udevadm >/dev/null 2>&1; then
        udevadm control --reload >/dev/null 2>&1 || true
        udevadm trigger >/dev/null 2>&1 || true
    fi
    # Let the stream through the firewall (UDP 47900) when one is active.
    if command -v ufw >/dev/null 2>&1 && ufw status 2>/dev/null | grep -q "Status: active"; then
        ufw allow 47900/udp >/dev/null 2>&1 || true
    fi
    if command -v firewall-cmd >/dev/null 2>&1 && firewall-cmd --state >/dev/null 2>&1; then
        firewall-cmd --permanent --add-port=47900/udp >/dev/null 2>&1 || true
        firewall-cmd --reload >/dev/null 2>&1 || true
    fi
    added=""
    if [ -n "${SUDO_USER:-}" ] && [ "$SUDO_USER" != "root" ] && id "$SUDO_USER" >/dev/null 2>&1; then
        if usermod -aG input "$SUDO_USER" >/dev/null 2>&1; then added="$SUDO_USER"; fi
    fi
    echo ""
    echo "Lumen Host: remote input needs access to /dev/uinput (group 'input')."
    if [ -n "$added" ]; then
        echo "  Added '$added' to group 'input'. Log out and back in for it to take effect."
    else
        echo "  Add yourself:  sudo usermod -aG input \$USER   (then log out and in again)"
    fi
    echo "  Run:           lumenhost"
    echo "  Autostart:     systemctl --user enable --now lumenhost"
    echo ""
fi
exit 0
PST
cat > "$root/DEBIAN/postrm" <<'PRM'
#!/bin/sh
set -e
if [ "$1" = "remove" ] || [ "$1" = "purge" ]; then
    if command -v udevadm >/dev/null 2>&1; then
        udevadm control --reload >/dev/null 2>&1 || true
    fi
fi
exit 0
PRM
chmod 0755 "$root/DEBIAN/postinst" "$root/DEBIAN/postrm"

mkdir -p "$outdir"
out="$outdir/lumenhost_${version}_${arch}.deb"
dpkg-deb --root-owner-group -Zxz --build "$root" "$out"
dpkg-deb --info "$out"
dpkg-deb --contents "$out"
echo "built $out"
