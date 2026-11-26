# Lumen

Low-latency screen streaming from a **Mac, Windows PC or Linux PC** to an **iPhone/iPad**, over **Wi-Fi or a USB cable**.
Mouse, keyboard and real touch input go back to the computer. Audio is streamed too, everything is encrypted, and the
iOS device can output to an external monitor. It uses its own protocol, not AirPlay and not Sunshine/Moonlight.

```
Mac host    : ScreenCaptureKit -> VideoToolbox HEVC (hardware) ─┐
Windows host: Desktop Duplication -> ffmpeg NVENC/AMF/QSV HEVC ─┤  one encrypted wire protocol
Linux host  : X11 capture -> ffmpeg VAAPI/NVENC/x265 HEVC ──────┤──────────►  iPhone / iPad
        Wi-Fi / Ethernet : UDP (Bonjour/mDNS + subnet scan)     │             AVSampleBufferDisplayLayer
        Cable            : USB tunnel via usbmuxd (TCP)         │             (hardware HEVC decode)
Input (mouse, keyboard, touch) and audio ◄──────────────────────┘             -> iPad screen AND external monitor
```

## Downloads (Releases tab)

| File | For |
|---|---|
| `Lumen-iOS.ipa` | iPhone / iPad (unsigned: install with AltStore, Sideloadly, or re-sign it yourself) |
| `Lumen-Android.apk` | Android phone / tablet (Android 7.0+), see [Android](#android) |
| `Lumen-Host-macOS.dmg` | Mac host: open it and drag **Lumen Host** onto **Applications** |
| `Lumen-Host-Setup.exe` | Windows host installer (includes ffmpeg, optional desktop shortcut, firewall rules) |
| `Lumen-Host-Windows.zip` | Windows host, portable |
| `lumenhost_*_amd64.deb` | Debian, Ubuntu, Mint, Pop!_OS … |
| `lumenhost-*.pkg.tar.zst` | Arch Linux / Manjaro (`sudo pacman -U file`) |
| `Lumen-Host-*.AppImage` | Any Linux distro, ffmpeg included |
| `Lumen-Host.flatpak` | Flatpak (best effort, see below) |

## Quick start

1. **Host**
   - **Mac:** open the `.dmg`, drag *Lumen Host* to *Applications*, right-click → Open the first time, allow
     **Screen Recording** and **Accessibility**, quit and reopen, press **Start**.
   - **Windows:** run `Lumen-Host-Setup.exe` (tick *Create a desktop shortcut* if you want one), start *Lumen Host*, press **Start**.
     The installer opens the firewall for the app. Needs a GPU with hardware HEVC encoding, or it falls back to the CPU.
   - **Linux (X11):** install the package, then run `lumenhost` in a terminal (or start *Lumen Host* from the app menu).
     It prints your IP addresses and the pairing code. Run `lumenhost --check` if something does not work.
2. **iPhone/iPad:** open Lumen. The computer appears under *Computers on your network* (Mac, Windows or Linux), or plug in the cable.
3. The first time you connect to a computer the iPad shows a **5-digit code**. Type it into the prompt on the computer (host window or
   terminal). After that the pair is remembered and connecting needs no code.

### Finding the computer
The host window shows **Your IP address**. If the computer is not listed, type that address into *Connect by IP address*.
The app lists computers with both Bonjour and a direct scan of your subnet, so it still works on networks where multicast is blocked.

### Wi-Fi / Ethernet
Same network as the host. 5 GHz Wi-Fi or wired Ethernet (USB-C Ethernet adapter on the iPad) is best.

### Cable (USB)
Plug the iPhone/iPad into the computer, tap **Trust** on the device, start the host, and keep the Lumen app open.
It connects automatically within a couple of seconds. Cable mode has no packet loss and the lowest latency.
- **Mac / Linux:** nothing to install (Linux needs `usbmuxd`, which most distros ship).
- **Windows:** needs Apple's USB driver service. Install **iTunes** or the **Apple Devices** app from the Microsoft Store.
- Cable auto-connect only starts while the iOS app is idle. If you tap *Disconnect* during a cable session, auto-connect
  pauses until you tap *Resume cable auto-connect*.
- **Android (Windows and Linux hosts):** `adb` ships with the host. Turn on **USB debugging** once (Settings > Developer options), plug in, tap **Allow**
  on the phone, and keep the Lumen app installed. The host forwards a port over the cable and connects by itself (it even opens the app if needed).
  Linux packages depend on `adb` / `android-tools`. Mac host: iPhone/iPad cable only for now.

## Touch: touchpad and touchscreen mode
Pick the mode on the connect screen, in Settings (gear icon) or in the stream menu.

| | Touchpad | Touchscreen |
|---|---|---|
| Move | drag = relative cursor | the cursor is wherever you touch |
| Left click | 1-finger tap | 1-finger tap |
| Right click | 2-finger tap | 2-finger tap |
| Scroll | 2-finger drag | 1-finger drag |
| Drag | tap, then touch and drag | long-press, then drag (Mac host) / just drag items (Windows, Linux) |

On **Windows and Linux hosts** touchscreen mode is *native*: the computer receives real multi-touch events (you see touch
feedback, apps get touch input, pinch-zoom works) instead of a mouse pretending to be a finger. macOS has no public API for
synthetic touch, so on a Mac host touchscreen mode is emulated with mouse input and the same gestures.

## Encryption and pairing
All video, audio, input and control traffic is encrypted end to end (AES-256-GCM). Pairing works like Sunshine/Apollo: the iPad shows a
5-digit code, you type it on the computer, and both sides derive a long-term secret (ECDH plus a code-confirmation step that blocks
man-in-the-middle attacks; 3 wrong codes lock pairing for a minute). Use *Forget paired devices* in the host window (or `lumenhost --unpair-all`)
to revoke everything. Only discovery packets (name and platform of the computer) are unencrypted. Details: [`docs/PROTOCOL.md`](docs/PROTOCOL.md).

## Audio
System audio is streamed to the iPad (mute from the stream menu). Mac: ScreenCaptureKit, Windows: WASAPI loopback, Linux:
PulseAudio/PipeWire default output monitor.

## Mirror or Extend
Two buttons, in the iPad app (connect screen, Settings and stream menu). The computer's window no longer has them:
- **Mirror** shows the display chosen on the host.
- **Extend** makes the iPad an additional screen. **Mac:** a virtual display is created automatically (uses a private macOS API;
  if macOS refuses, the app tells you and mirrors instead). **Windows:** uses the [SudoVDA](https://github.com/SudoMaker/SudoVDA) virtual display driver (the one Apollo uses): tick *Install the SudoVDA virtual display driver* in the installer
  (or press *Install virtual display* in the host window; it adds the driver's certificate to Windows' trusted store, like Apollo does). Lumen then creates a monitor at exactly the iPad's resolution when you press Extend
  and keeps it after you disconnect (like Sunshine/Apollo), so your windows and layout are still there and reconnecting is almost instant; it is removed when you press Stop or quit the host. Without the driver, Extend uses any extra display that already exists. **Linux:** streams a second monitor (or a dummy/virtual xrandr output) if one exists.

## Controllers
Connect an Xbox, PlayStation, Switch Pro or MFi controller to the iPhone/iPad over Bluetooth; it is forwarded to the computer as a virtual
**Xbox 360 controller** (up to 4, rumble not supported yet). **Windows** needs the ViGEmBus driver (tick it in the installer, same driver Sunshine uses).
**Linux** uses `/dev/uinput` (same group setup as the keyboard). **macOS** hosts cannot create virtual controllers. Toggle in Settings (gear icon).

## External monitor
Plug a monitor into the iPhone/iPad (USB-C to HDMI/DisplayPort). Lumen takes over the monitor and shows the stream there fullscreen at the
monitor's native resolution (up to 4K); the iPad screen then works as the touchpad/touchscreen. Connect *after* plugging the monitor in so the
resolution is negotiated for it.

## Pointer lock for games
Use an iPad with a mouse and keyboard. In the stream view open the ⋯ menu → **Capture mouse for games**, or press
**Ctrl + Option + L**. Mouse movement is sent as raw deltas, which is what games that lock the mouse to the window expect.
Press the same shortcut to release. Without a mouse, a one-finger drag acts as a trackpad / camera stick while capture is on.
On Windows and Linux, keys are injected as hardware scancodes / evdev keys so games see them like a real keyboard.

## Linux notes
- **Wayland and X11 both work.** On Wayland the desktop asks once which screen to share (screen-share portal, remembered afterwards)
  and capture goes through PipeWire + GStreamer: install `python3-gi`, `gstreamer1.0-pipewire` and `gstreamer1.0-plugins-bad` (the deb recommends them).
  `lumenhost --check` lists anything missing. X11 uses ffmpeg directly. On Wayland the screen size comes from XWayland; pass `--size WxH` if it is wrong.
- Input uses `/dev/uinput`. The packages install a udev rule and load the module; **add yourself to the `input` group**
  (`sudo usermod -aG input $USER`) and log in again. `lumenhost --check` verifies it.
- Encoders: VAAPI (Intel/AMD GPUs), NVENC (NVIDIA), or CPU x265, picked automatically; override with `--encoder vaapi|nvenc|software`.
- Run as a background service: `systemctl --user enable --now lumenhost`.
- **Flatpak** is best effort: the sandbox has no `xrandr`/`xdpyinfo` and needs `--device=all` for uinput and the GPU, so the
  deb, Arch package or AppImage are the recommended ways.
- Options: `lumenhost --help`.

## Tuning
- Frame rate and bitrate are on the connect screen. 40 Mbps at 60 fps suits 1080p/1440p on good Wi-Fi; use more over cable (the host caps at 80 Mbps on Wi-Fi, 150 on cable).
- The host log prints `stats: fps, Mbps, dropped frames` every 5 s; send it when reporting lag.
- Mac host: lost packets trigger an immediate keyframe request. Windows/Linux hosts: ffmpeg cannot emit a keyframe on demand, so it
  sends one every second and recovery after a loss takes up to 1 s (not an issue over cable).
- Windows picks the best encoder automatically (NVIDIA NVENC, AMD AMF, Intel Quick Sync, then CPU) and falls back through
  several capture methods if one fails. If something still fails, the error is shown on the iPad and in the host window;
  the log is at `%LOCALAPPDATA%\Lumen\host.log` (*Open log file* in the host window).
- Multi-monitor: choose the display in the host window. There is no "output index" to enter anymore, it is detected automatically.

## Build locally

```
# iOS client + Mac host
brew install xcodegen
xcodegen generate
open Lumen.xcodeproj

# Windows host (needs .NET 8 SDK; put ffmpeg.exe next to the built exe)
dotnet run --project HostWindows

# Linux host (needs .NET 8 SDK and ffmpeg)
dotnet run --project HostLinux -- --check
```

Pushing to `main` runs `.github/workflows/release.yml`, which builds everything and publishes a normal
(non-pre-release) GitHub Release. If the release step fails with a permissions error, enable
*Settings → Actions → General → Workflow permissions → Read and write*. A failing Linux package never blocks the other downloads.

## Layout

```
Shared/        wire protocol + crypto (Swift)     HostCommon/   protocol, crypto, server, ffmpeg pipeline (C#, shared)
Host/          macOS host                         HostWindows/  Windows host + installer
Client/        iOS / iPadOS app                   HostLinux/    Linux host
Android/       Android app (Kotlin)
packaging/     deb, Arch, AppImage, Flatpak       docs/         protocol description
```

## Android
A native Android client (`Android/`, Kotlin, plain Views) speaks the same protocol as the iOS app.

- **Install:** download `Lumen-Android.apk` from the Releases tab, open it on the device and allow "install unknown apps" for your browser/file manager.
  It is signed with a throwaway key generated by CI, so updates install over it only when they come from the same build pipeline's key (otherwise uninstall first).
- **Use:** start Lumen Host on the computer, open Lumen. Computers appear under *Computers on your network* (unicast subnet scan), or enter the IP address;
  recent computers are remembered. On first connect the Android device shows a **5-digit code**: type it on the computer.
- **Features:** HEVC video via MediaCodec (low latency), audio, Mirror/Extend, touchpad and touchscreen modes (native multi-touch on Windows/Linux hosts),
  hardware keyboard and mouse (mouse capture for games needs Android 8+, Ctrl+Alt+L), on-screen keyboard, game controllers, settings for resolution, frame rate and Wi-Fi bitrate (8-80 Mbps).
- **USB cable:** see *Cable (USB)* above (adb, Windows/Linux hosts). **Video:** HEVC, or H.264 automatically when the device has no HEVC decoder.
- **Limits:** no AirPlay/external-monitor mode.

## Known limits
- Windows and Linux hosts serve several devices at once (each *Extend* device gets its own virtual display; each *Mirror* device shows the display picked in the host window).
  The Mac host serves one device at a time.
- Video is HEVC when the device decodes it in hardware, otherwise H.264 (iPhone 5s/6, iPad Air 1/2: use the LumenLegacy app). No setting: it is chosen from the device.
- The host keeps running in the tray / menu bar when you close its window; use the tray icon to open or quit it.
- The host's mouse cursor is drawn into the video as well as the iPad pointer.
- iPhone has limited hardware-mouse support in iOS; mouse capture works best on iPadOS.
- Linux Wayland capture needs the GStreamer/PipeWire packages and is the least-tested path. Windows Extend needs the virtual display driver. Mac Extend uses a private API that Apple could change.
- The Windows installer bundles a GPL build of ffmpeg from [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds)
  (for the CPU fallback); its license text ships alongside it.
