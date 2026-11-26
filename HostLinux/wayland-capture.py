#!/usr/bin/env python3
# Lumen Wayland capture helper: asks the desktop (xdg-desktop-portal ScreenCast) for a monitor stream,
# then runs a GStreamer pipeline that encodes it to HEVC and sends RTP to a loopback UDP port.
# Usage: wayland-capture.py PORT WIDTH HEIGHT FPS KBPS ENCODER   (ENCODER: x265 | va | vaapi | nvenc)
import os, sys, random, subprocess
import gi
from gi.repository import Gio, GLib

port, width, height, fps, kbps, enc = sys.argv[1:7]
codec = sys.argv[7] if len(sys.argv) > 7 else "h265"      # h265 | h264 (clients without hardware HEVC)
TOKEN_FILE = os.path.join(os.environ.get("XDG_CONFIG_HOME") or os.path.expanduser("~/.config"), "lumen", "portal.token")

bus = Gio.bus_get_sync(Gio.BusType.SESSION, None)
sender = bus.get_unique_name()[1:].replace(".", "_")
loop = GLib.MainLoop()
PORTAL = ("org.freedesktop.portal.Desktop", "/org/freedesktop/portal/desktop")


def log(*a):
    print(*a, file=sys.stderr, flush=True)


def request(method, params_fn, timeout=120):
    """Calls a portal method that answers through a Request/Response signal. Returns the response dict."""
    token = "lumen%d" % random.randint(0, 1 << 30)
    path = "/org/freedesktop/portal/desktop/request/%s/%s" % (sender, token)
    result = {}

    def on_response(conn, sender_name, obj, iface, signal, params):
        result["code"], result["data"] = params.unpack()
        loop.quit()

    sub = bus.signal_subscribe(None, "org.freedesktop.portal.Request", "Response", path, None,
                               Gio.DBusSignalFlags.NONE, on_response)
    params, opts = params_fn(token)
    bus.call_sync(PORTAL[0], PORTAL[1], "org.freedesktop.portal.ScreenCast", method, params, None,
                  Gio.DBusCallFlags.NONE, -1, None)
    GLib.timeout_add_seconds(timeout, loop.quit)
    loop.run()
    bus.signal_unsubscribe(sub)
    if result.get("code") != 0:
        log("portal request %s failed or was cancelled (code %s)" % (method, result.get("code")))
        sys.exit(3)
    return result["data"]


def variant_opts(d):
    return GLib.Variant("a{sv}", d)


# 1. session
data = request("CreateSession", lambda t: (GLib.Variant("(a{sv})", (variant_opts({
    "handle_token": GLib.Variant("s", t), "session_handle_token": GLib.Variant("s", "lumen" + t)}),)), None))
session = data["session_handle"]

# 2. choose a monitor (cursor drawn into the video)
restore = None
try:
    restore = open(TOKEN_FILE).read().strip() or None
except OSError:
    pass


def select(t):
    o = {"handle_token": GLib.Variant("s", t), "types": GLib.Variant("u", 1), "multiple": GLib.Variant("b", False),
         "cursor_mode": GLib.Variant("u", 2), "persist_mode": GLib.Variant("u", 2)}
    if restore:
        o["restore_token"] = GLib.Variant("s", restore)
    return GLib.Variant("(oa{sv})", (session, o)), None


request("SelectSources", select)

# 3. start (this is where the desktop shows its "share your screen" dialog the first time)
data = request("Start", lambda t: (GLib.Variant("(osa{sv})", (session, "", {"handle_token": GLib.Variant("s", t)})), None), timeout=300)
streams = data.get("streams") or []
if not streams:
    log("the desktop did not provide a screen stream")
    sys.exit(4)
node = streams[0][0]
if data.get("restore_token"):
    try:
        os.makedirs(os.path.dirname(TOKEN_FILE), exist_ok=True)
        open(TOKEN_FILE, "w").write(data["restore_token"])
    except OSError:
        pass

# 4. PipeWire connection for that stream
reply, fds = bus.call_with_unix_fd_list_sync(
    PORTAL[0], PORTAL[1], "org.freedesktop.portal.ScreenCast", "OpenPipeWireRemote",
    GLib.Variant("(oa{sv})", (session, {})), GLib.VariantType("(h)"), Gio.DBusCallFlags.NONE, -1, None, None)
fd = fds.get(reply.unpack()[0])

enc_el = {
    "x265": "x265enc tune=zerolatency speed-preset=ultrafast bitrate=%s key-int-max=%s option-string=bframes=0:rc-lookahead=0:scenecut=0" % (kbps, fps),
    "va": "vah265enc bitrate=%s key-int-max=%s b-frames=0 target-usage=7" % (kbps, fps),
    "vaapi": "vaapih265enc bitrate=%s keyframe-period=%s max-bframes=0 rate-control=cbr" % (kbps, fps),
    "nvenc": "nvh265enc bitrate=%s gop-size=%s bframes=0 zerolatency=true preset=low-latency-hq rc-mode=cbr" % (kbps, fps),
    "x264": "x264enc tune=zerolatency speed-preset=ultrafast bitrate=%s key-int-max=%s bframes=0 byte-stream=true" % (kbps, fps),
    "h264:va": "vah264enc bitrate=%s key-int-max=%s b-frames=0 target-usage=7" % (kbps, fps),
    "h264:vaapi": "vaapih264enc bitrate=%s keyframe-period=%s max-bframes=0 rate-control=cbr" % (kbps, fps),
    "h264:nvenc": "nvh264enc bitrate=%s gop-size=%s bframes=0 zerolatency=true preset=low-latency-hq rc-mode=cbr" % (kbps, fps),
}[("h264:" + enc) if (codec == "h264" and enc != "x264") else enc]
parse = ("h264parse config-interval=-1 ! rtph264pay config-interval=-1 pt=96 mtu=1200"
         if codec == "h264" else "h265parse config-interval=-1 ! rtph265pay config-interval=-1 pt=96 mtu=1200")

pipeline = (
    "pipewiresrc fd=%d path=%s do-timestamp=true ! "
    "queue leaky=downstream max-size-buffers=2 ! videoconvert ! videoscale ! videorate ! "
    "video/x-raw,width=%s,height=%s,framerate=%s/1 ! videoconvert ! %s ! "
    "%s ! "
    "udpsink host=127.0.0.1 port=%s sync=false" % (fd, node, width, height, fps, enc_el, parse, port))
log("gst-launch-1.0 " + pipeline)
os.set_inheritable(fd, True)
os.execvp("gst-launch-1.0", ["gst-launch-1.0", "-q"] + pipeline.split(" "))
