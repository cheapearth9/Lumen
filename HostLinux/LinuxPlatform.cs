using System.Diagnostics;

namespace Lumen;

public enum LinuxEncoder { Auto, Vaapi, Nvenc, Software }

/// Linux specifics: X11 capture through ffmpeg (x11grab), VAAPI / NVENC / x265 encoding, uinput input.
/// Wayland sessions are not supported by x11grab; the --check command and startup messages say so.
public sealed class LinuxPlatform : IPlatformHost
{
    private readonly UInputInjector _input = new();
    private bool _inputOpen;
    private List<LinuxEncoder>? _available;
    private (int W, int H)? _size;

    public string Display { get; set; } = Environment.GetEnvironmentVariable("DISPLAY") is { Length: > 0 } d ? d : ":0";
    public LinuxEncoder Encoder { get; set; } = LinuxEncoder.Auto;
    public string? SizeOverride { get; set; }
    public string VaapiDevice { get; set; } = "/dev/dri/renderD128";
    public string? InputError => _input.Error;

    public byte PlatformId => HostPlatformId.Linux;
    public byte Capabilities => (byte)((_inputOpen && _input.TouchAvailable ? WelcomeInfo.CapNativeTouch : 0) | WelcomeInfo.CapAudio);
    public string FfmpegPath => FfmpegUtil.FindExe();

    public static bool IsWayland => string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase)
                                    && Environment.GetEnvironmentVariable("LUMEN_FORCE_X11") != "1";

    private (int X, int Y) _origin;
    private bool _extendedSecond;

    public (int W, int H) SourceSize => _size ??= DetectSize();

    public IDisposable? StartAudio(Action<byte[]> packet) => new LinuxAudio(FfmpegPath, packet);

    /// Extend = stream a second connected monitor if there is one (a virtual display needs a driver / xrandr setup we cannot rely on).
    public string? ApplyMode(bool extend, int clientW, int clientH, out bool extended)
    {
        extended = false;
        _origin = (0, 0); _extendedSecond = false; _size = null;
        if (!extend) return null;
        var text = Run("xrandr", "--display " + Display + " --listmonitors") ?? "";
        var mons = System.Text.RegularExpressions.Regex.Matches(text, @"(\d+)/\d+x(\d+)/\d+\+(\d+)\+(\d+)")
            .Select(m => (W: int.Parse(m.Groups[1].Value), H: int.Parse(m.Groups[2].Value), X: int.Parse(m.Groups[3].Value), Y: int.Parse(m.Groups[4].Value))).ToList();
        var second = mons.Where(m => m.X != 0 || m.Y != 0).Cast<(int W, int H, int X, int Y)?>().FirstOrDefault();
        if (second is { } m2)
        {
            _origin = (m2.X, m2.Y); _size = (m2.W, m2.H); _extendedSecond = true; extended = true;
            return null;
        }
        return "Extend needs a second monitor (or a dummy/virtual output) on this Linux PC, so the screen is mirrored. See the README for a virtual display setup.";
    }

    private (int, int) DetectSize()
    {
        var o = SizeOverride ?? Environment.GetEnvironmentVariable("LUMEN_SIZE");
        if (o != null)
        {
            var p = o.ToLowerInvariant().Split('x');
            if (p.Length == 2 && int.TryParse(p[0], out var w) && int.TryParse(p[1], out var h) && w > 0 && h > 0) return (w, h);
        }
        var xdpy = Run("xdpyinfo", "-display " + Display);
        var m = System.Text.RegularExpressions.Regex.Match(xdpy ?? "", @"dimensions:\s+(\d+)x(\d+)");
        if (m.Success) return (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
        var xr = Run("xrandr", "--display " + Display + " --current");
        m = System.Text.RegularExpressions.Regex.Match(xr ?? "", @"current\s+(\d+)\s*x\s*(\d+)");
        if (m.Success) return (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
        try
        {
            foreach (var f in Directory.GetFiles("/sys/class/drm", "card*-*/modes"))
            {
                var first = File.ReadLines(f).FirstOrDefault();
                var mm = System.Text.RegularExpressions.Regex.Match(first ?? "", @"(\d+)x(\d+)");
                if (mm.Success) return (int.Parse(mm.Groups[1].Value), int.Parse(mm.Groups[2].Value));
            }
        }
        catch { }
        return (1920, 1080);
    }

    private static string? Run(string exe, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using var p = Process.Start(psi);
            if (p == null) return null;
            var text = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(3000)) { try { p.Kill(); } catch { } }
            return text;
        }
        catch { return null; }
    }

    public void PrepareInput()
    {
        if (_inputOpen) return;
        _inputOpen = _input.Open();
        if (!_inputOpen) HostLog.Write("input unavailable: " + _input.Error);
    }

    public void HandleInput(InputEvent e) { if (_inputOpen) _input.Handle(e); }
    public void ReleaseInput() { if (_inputOpen) _input.ReleaseAll(); }
    public void KeepAwake() { }

    // MARK: encoders

    private static string CodecName(LinuxEncoder k) => k switch
    {
        LinuxEncoder.Vaapi => "hevc_vaapi", LinuxEncoder.Nvenc => "hevc_nvenc", _ => "libx265"
    };

    private List<string> Pre(LinuxEncoder k) =>
        k == LinuxEncoder.Vaapi ? new() { "-vaapi_device", VaapiDevice } : new();

    private static string Filter(LinuxEncoder k, int w, int h, bool scale)
    {
        var f = scale ? $"scale={w}:{h}:flags=fast_bilinear," : "";
        return k switch
        {
            LinuxEncoder.Vaapi => f + "format=nv12,hwupload",
            LinuxEncoder.Nvenc => f + "format=nv12",
            _ => f + "format=yuv420p"
        };
    }

    private static List<string> Codec(LinuxEncoder k, int fps, int bps)
    {
        string rate = bps.ToString(), buf = Math.Max(bps / 10, 100_000).ToString();
        var c = new List<string> { "-b:v", rate, "-maxrate", rate, "-bufsize", buf, "-g", fps.ToString() };
        List<string> s = k switch
        {
            LinuxEncoder.Vaapi => new() { "-c:v", "hevc_vaapi", "-bf", "0", "-async_depth", "1" },
            LinuxEncoder.Nvenc => new() { "-c:v", "hevc_nvenc", "-preset", "p1", "-tune", "ull", "-rc", "cbr", "-zerolatency", "1", "-bf", "0", "-forced-idr", "1" },
            _ => new() { "-c:v", "libx265", "-preset", "ultrafast", "-tune", "zerolatency",
                         "-x265-params", $"keyint={fps}:min-keyint={fps}:bframes=0:rc-lookahead=0:scenecut=0:repeat-headers=1:log-level=error" }
        };
        s.AddRange(c);
        return s;
    }

    private async Task<List<LinuxEncoder>> DetectAsync(string exe, Action<string> status)
    {
        if (_available != null) return _available;
        status("Detecting video encoders…");
        var compiled = await FfmpegUtil.ListEncodersAsync(exe);
        var result = new List<LinuxEncoder>();
        foreach (var k in new[] { LinuxEncoder.Nvenc, LinuxEncoder.Vaapi })
        {
            if (!compiled.Contains(CodecName(k))) continue;
            var a = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };
            a.AddRange(Pre(k));
            a.AddRange(new[] { "-f", "lavfi", "-i", "color=c=black:s=640x360:r=30", "-vf", Filter(k, 640, 360, false), "-frames:v", "10" });
            a.AddRange(Codec(k, 30, 4_000_000));
            a.AddRange(new[] { "-f", "null", "-" });
            if (await FfmpegUtil.TestEncodeAsync(exe, a)) result.Add(k);
        }
        if (compiled.Contains("libx265")) result.Add(LinuxEncoder.Software);
        HostLog.Write("available encoders: " + (result.Count == 0 ? "none" : string.Join(", ", result)));
        return _available = result;
    }

    private List<string> BuildArgs(LinuxEncoder k, int fps, int w, int h, bool scale, int bps, int port)
    {
        var (sw, sh) = SourceSize;
        var a = new List<string> { "-hide_banner", "-loglevel", "warning", "-nostdin" };
        a.AddRange(Pre(k));
        a.AddRange(new[] { "-f", "x11grab", "-framerate", fps.ToString(), "-draw_mouse", "1", "-video_size", $"{sw}x{sh}", "-i", (Display.Contains('.') ? Display : Display + ".0") + $"+{_origin.X},{_origin.Y}" });
        a.AddRange(new[] { "-vf", Filter(k, w, h, scale), "-an" });
        a.AddRange(Codec(k, fps, bps));
        a.AddRange(new[] { "-bsf:v", "dump_extra=freq=keyframe" });
        a.AddRange(FfmpegUtil.OutputArgs(port));
        return a;
    }

    public async Task<List<CaptureCandidate>> BuildCandidatesAsync(int w, int h, int fps, int bps, bool needScale, Action<string> status)
    {
        if (IsWayland)
            status("Wayland session detected: screen capture needs an X11 session (log in with 'GNOME on Xorg' / 'Plasma (X11)').");
        var available = await DetectAsync(FfmpegPath, status);
        var kinds = new List<LinuxEncoder>();
        if (Encoder == LinuxEncoder.Auto) kinds.AddRange(available);
        else
        {
            kinds.Add(Encoder);
            if (Encoder != LinuxEncoder.Software && available.Contains(LinuxEncoder.Software)) kinds.Add(LinuxEncoder.Software);
        }
        return kinds.Select(k => new CaptureCandidate
        {
            Name = k switch { LinuxEncoder.Vaapi => "VAAPI (Intel/AMD GPU)", LinuxEncoder.Nvenc => "NVIDIA NVENC", _ => "CPU (x265)" } + " + X11 capture",
            Args = port => BuildArgs(k, fps, w, h, needScale, bps, port)
        }).ToList();
    }

    /// Startup self-test for `lumenhost --check`.
    public async Task<int> CheckAsync()
    {
        int problems = 0;
        void Line(bool ok, string text) { Console.WriteLine((ok ? "  ok   " : "  FAIL ") + text); if (!ok) problems++; }

        Line(File.Exists(FfmpegPath) || Run(FfmpegPath, "-version") != null, "ffmpeg: " + FfmpegPath);
        Line(!IsWayland, IsWayland ? "session is Wayland: x11grab cannot capture it. Use an X11 session." : "X11 session (" + Display + ")");
        var (w, h) = SourceSize;
        Console.WriteLine($"  info screen size {w}x{h}");
        bool uinput = File.Exists("/dev/uinput");
        bool writable = false;
        if (uinput) { try { using var f = File.Open("/dev/uinput", FileMode.Open, FileAccess.Write); writable = true; } catch { } }
        Line(writable, writable ? "/dev/uinput is writable (mouse, keyboard and touch input work)"
            : "/dev/uinput is not writable: run 'sudo modprobe uinput' and add yourself to the 'input' group (the package does this), then log in again");
        var encoders = await DetectAsync(FfmpegPath, s => { });
        Line(encoders.Count > 0, "encoders: " + (encoders.Count == 0 ? "none (install ffmpeg with libx265, or VAAPI/NVENC drivers)" : string.Join(", ", encoders)));
        return problems == 0 ? 0 : 1;
    }

    public void Dispose() => _input.Dispose();
}
