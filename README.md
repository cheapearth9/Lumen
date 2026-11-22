# Lumen

Low-latency screen streaming from a **Mac or Windows PC** to an **iPhone/iPad**, over **Wi-Fi or a USB cable**, with
mouse + keyboard input, pointer lock for games, and output to an external monitor plugged into the
iOS device. It uses its own protocol, not AirPlay and not Sunshine/Moonlight.

```
Mac host   : ScreenCaptureKit -> VideoToolbox HEVC (hardware) ─┐
Windows host: Desktop Duplication -> ffmpeg NVENC/AMF/QSV HEVC ─┤  one wire protocol
                                                                ├──────────►  iPhone / iPad
        Wi-Fi / Ethernet : UDP (Bonjour / mDNS discovery)       │             AVSampleBufferDisplayLayer
        Cable            : USB tunnel via usbmuxd (TCP)         │             (hardware HEVC decode)
                                                                │             -> iPad screen AND external monitor
Input (mouse, keyboard, touch) goes back the same way ◄─────────┘
```

## Downloads (Releases tab)

| File | For |
|---|---|
| `Lumen-iOS.ipa` | iPhone / iPad (unsigned: install with AltStore, Sideloadly, or re-sign it yourself) |
| `Lumen-Host-macOS.dmg` | Mac host |
| `Lumen-Host-Windows.zip` | Windows host (includes `ffmpeg.exe`) |

## Quick start

1. **Host**
   - **Mac:** drag `LumenHost.app` to Applications, right-click → Open the first time, allow **Screen Recording** and
     **Accessibility**, quit and reopen, press **Start**.
   - **Windows:** unzip, run `LumenHost.exe`, pick the display, press **Start**. Allow it through Windows Firewall when asked
     (private networks). Needs an NVIDIA, AMD or Intel GPU with hardware HEVC encoding.
2. **iPhone/iPad:** open Lumen. The host appears under *Macs on your network* (Wi-Fi), or just plug in the cable.

### Wi-Fi / Ethernet
Same network as the host. 5 GHz Wi-Fi or wired Ethernet (USB-C Ethernet adapter on the iPad) is best.
If discovery fails, type the host's IP address in *Connect by IP address*.

### Cable (USB)
Plug the iPhone/iPad into the computer, tap **Trust** on the device, start the host, and keep the Lumen app open.
It connects automatically within a couple of seconds. Cable mode has no packet loss and the lowest latency.
- **Mac:** nothing to install.
- **Windows:** needs Apple's USB driver service. Install **iTunes** or the **Apple Devices** app from the Microsoft Store.
- Cable auto-connect only starts while the iOS app is idle. If you tap *Disconnect* during a cable session, auto-connect
  pauses until you tap *Resume cable auto-connect*.

## External monitor
Plug a monitor into the iPhone/iPad (USB-C to HDMI/DisplayPort). Lumen takes over the monitor as an extended display and
renders the stream there at the monitor's native resolution (up to 4K). Connect *after* plugging the monitor in so the
resolution is negotiated for it.

## Games
Use an iPad with a mouse and keyboard. In the stream view open the ⋯ menu → **Capture mouse for games**, or press
**Ctrl + Option + L**. Mouse movement is sent as raw deltas, which is what Minecraft and Roblox mouse-lock expect. Press the
same shortcut to release. Without a mouse, a one-finger drag acts as a trackpad / camera stick while capture is on.
On Windows, keys are injected as hardware scancodes so games see them like a real keyboard.

## Extended desktop (second screen)
Lumen streams a display that already exists on the host. To get an *extra* desktop instead of a mirror, create a virtual
display: [DeskPad](https://github.com/Stengo/DeskPad) or BetterDisplay on Mac, a virtual display driver on Windows, then
choose it in the host's display picker.

## Tuning
- Frame rate and bitrate are on the connect screen. 40 Mbps at 60 fps suits 1080p/1440p on good Wi-Fi; use more over cable.
- Mac host: lost packets trigger an immediate keyframe request. Windows host: ffmpeg cannot emit a keyframe on demand, so it
  sends one every second and recovery after a loss takes up to 1 s (not an issue over cable).
- If the mouse Y axis or scroll direction feels inverted, change `invertY` / `scrollScale` in `Client/InputCapture.swift`.
- Windows multi-monitor: if the wrong screen is captured, change *Capture output index* in the host window.

## Build locally

```
# iOS client + Mac host
brew install xcodegen
xcodegen generate
open Lumen.xcodeproj

# Windows host (needs .NET 8 SDK; put ffmpeg.exe next to the built exe)
dotnet run --project HostWindows
```

Pushing to `main` runs `.github/workflows/release.yml`, which builds all three apps and publishes a normal
(non-pre-release) GitHub Release with `Lumen-iOS.ipa`, `Lumen-Host-macOS.dmg` and `Lumen-Host-Windows.zip`.
If the release step fails with a permissions error, enable *Settings → Actions → General → Workflow permissions →
Read and write*.

## Layout

```
Shared/         wire protocol (Swift)             HostWindows/Protocol.cs    same protocol (C#)
Host/           macOS host                        HostWindows/               Windows host
Client/         iOS / iPadOS app
```

## Known limits
- One client at a time; video only (no audio yet); no encryption, so use it on a trusted network or over the cable.
- The host's mouse cursor is drawn into the video as well as the iPad pointer.
- iPhone has limited hardware-mouse support in iOS; mouse capture works best on iPadOS.
- The Windows host bundles an LGPL build of ffmpeg from [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds);
  its license text ships alongside it.
