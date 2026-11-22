using System.Runtime.InteropServices;

namespace Lumen;

public enum EncoderKind { Auto, Nvenc, Amf, Qsv, Software }

/// Windows specifics: Desktop Duplication / GDI capture through ffmpeg, SendInput + touch injection,
/// and a ladder of capture+encoder combinations so one failing option does not end the session.
public sealed class WindowsPlatform : IPlatformHost
{
    [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);

    private readonly InputInjector _input = new();
    private List<EncoderKind>? _available;

    public int ScreenIndex { get; set; }
    public EncoderKind Encoder { get; set; } = EncoderKind.Auto;

    public byte PlatformId => HostPlatformId.Windows;
    public byte Capabilities => (byte)((InputInjector.TouchAvailable ? WelcomeInfo.CapNativeTouch : 0) | WelcomeInfo.CapAudio);

    public IDisposable? StartAudio(Action<byte[]> packet) => new WindowsAudio(packet);

    private int? _effective;

    /// Extend = stream an additional display if one exists (e.g. a virtual display driver or an unused monitor); otherwise mirror.
    public string? ApplyMode(bool extend, int clientW, int clientH, out bool extended)
    {
        extended = false;
        _effective = null;
        if (!extend) return null;
        var all = Screen.AllScreens;
        int second = Array.FindIndex(all, s => !s.Primary);
        if (second >= 0) { _effective = second; extended = true; return null; }
        return "Extend needs a second (virtual) display. Windows cannot create one without a driver, so the screen is mirrored. Install a virtual display driver such as Virtual Display Driver, then try again.";
    }
    public string FfmpegPath => FfmpegUtil.FindExe();

    private Screen CurrentScreen()
    {
        var all = Screen.AllScreens;
        return all[Math.Clamp(_effective ?? ScreenIndex, 0, all.Length - 1)];
    }

    public (int W, int H) SourceSize { get { var b = CurrentScreen().Bounds; return (b.Width, b.Height); } }

    public void PrepareInput() => _input.Configure(CurrentScreen().Bounds, SystemInformation.VirtualScreen);
    public void HandleInput(InputEvent e) => _input.Handle(e);
    public void ReleaseInput() => _input.ReleaseAll();
    public void KeepAwake() => SetThreadExecutionState(0x1 | 0x2);   // system + display required

    public static string Label(EncoderKind k) => k switch
    {
        EncoderKind.Nvenc => "NVIDIA NVENC",
        EncoderKind.Amf => "AMD AMF",
        EncoderKind.Qsv => "Intel Quick Sync",
        EncoderKind.Software => "CPU (software x265)",
        _ => "Auto-detect"
    };

    private static string CodecName(EncoderKind k) => k switch
    {
        EncoderKind.Nvenc => "hevc_nvenc", EncoderKind.Amf => "hevc_amf", EncoderKind.Qsv => "hevc_qsv", _ => "libx265"
    };

    // MARK: Encoder detection

    private async Task<List<EncoderKind>> DetectAsync(string exe, Action<string> status)
    {
        if (_available != null) return _available;
        status("Detecting video encoders…");
        var compiled = await FfmpegUtil.ListEncodersAsync(exe);
        var kinds = new[] { EncoderKind.Nvenc, EncoderKind.Amf, EncoderKind.Qsv }.Where(k => compiled.Contains(CodecName(k))).ToList();

        var tests = kinds.Select(async k =>
        {
            var a = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };
            a.AddRange(PreInput(k));
            a.AddRange(new[] { "-f", "lavfi", "-i", "color=c=black:s=640x360:r=30", "-vf", PostFilter(k, 640, 360, false) });
            a.AddRange(new[] { "-frames:v", "10" });
            a.AddRange(Codec(k, 30, 4_000_000));
            a.AddRange(new[] { "-f", "null", "-" });
            return (k, ok: await FfmpegUtil.TestEncodeAsync(exe, a));
        }).ToList();
        var result = new List<EncoderKind>();
        foreach (var t in tests) { var (k, ok) = await t; if (ok) result.Add(k); }
        if (compiled.Contains("libx265")) result.Add(EncoderKind.Software);

        HostLog.Write("available encoders: " + (result.Count == 0 ? "none" : string.Join(", ", result)));
        return _available = result;
    }

    // MARK: ffmpeg argument building

    private enum Grab { DdaFull, DdaPlain, Gdi }

    private static List<string> PreInput(EncoderKind k) =>
        k == EncoderKind.Qsv ? new() { "-init_hw_device", "qsv=hw", "-filter_hw_device", "hw" } : new();

    /// Scaling, pixel format and (for Quick Sync) upload to the GPU.
    private static string PostFilter(EncoderKind k, int w, int h, bool scale)
    {
        var f = scale ? $"scale={w}:{h}:flags=fast_bilinear," : "";
        f += k == EncoderKind.Software ? "format=yuv420p" : "format=nv12";
        if (k == EncoderKind.Qsv) f += ",hwupload=extra_hw_frames=32";
        return f;
    }

    /// Low-latency settings: no B-frames, no look-ahead, CBR, a keyframe every second.
    private static List<string> Codec(EncoderKind k, int fps, int bps)
    {
        string rate = bps.ToString(), buf = Math.Max(bps / 10, 100_000).ToString();
        var common = new List<string> { "-b:v", rate, "-maxrate", rate, "-bufsize", buf, "-g", fps.ToString() };
        List<string> specific = k switch
        {
            EncoderKind.Nvenc => new() { "-c:v", "hevc_nvenc", "-preset", "p1", "-tune", "ull", "-rc", "cbr", "-zerolatency", "1", "-bf", "0", "-forced-idr", "1" },
            EncoderKind.Amf => new() { "-c:v", "hevc_amf", "-usage", "ultralowlatency", "-quality", "speed", "-rc", "cbr" },
            EncoderKind.Qsv => new() { "-c:v", "hevc_qsv", "-preset", "veryfast", "-async_depth", "1", "-bf", "0" },
            _ => new() { "-c:v", "libx265", "-preset", "ultrafast", "-tune", "zerolatency",
                         "-x265-params", $"keyint={fps}:min-keyint={fps}:bframes=0:rc-lookahead=0:scenecut=0:repeat-headers=1:log-level=error" }
        };
        specific.AddRange(common);
        return specific;
    }

    private List<string> BuildArgs(EncoderKind k, Grab grab, int adapter, int output, Rectangle bounds, int fps, int w, int h, bool scale, int bps, int port)
    {
        var a = new List<string> { "-hide_banner", "-loglevel", "warning", "-nostdin" };
        a.AddRange(PreInput(k));

        switch (grab)
        {
            case Grab.Gdi:
                a.AddRange(new[] { "-f", "gdigrab", "-framerate", fps.ToString(), "-draw_mouse", "1",
                                   "-offset_x", bounds.X.ToString(), "-offset_y", bounds.Y.ToString(),
                                   "-video_size", $"{bounds.Width}x{bounds.Height}", "-i", "desktop",
                                   "-vf", PostFilter(k, w, h, scale) });
                break;
            default:
            {
                var dd = $"ddagrab=output_idx={output}:framerate={fps}:draw_mouse=1";
                if (adapter > 0) dd += $":adapter_idx={adapter}";
                if (grab == Grab.DdaFull) dd += ":output_fmt=bgra";            // force 8-bit even on HDR displays
                a.AddRange(new[] { "-filter_complex", $"{dd},hwdownload,format=bgra,{PostFilter(k, w, h, scale)}[v]", "-map", "[v]" });
                break;
            }
        }

        a.Add("-an");
        a.AddRange(Codec(k, fps, bps));
        if (grab != Grab.DdaPlain)
        {
            a.Add("-bsf:v"); a.Add("dump_extra=freq=keyframe");               // VPS/SPS/PPS in-band with every keyframe
            a.AddRange(FfmpegUtil.OutputArgs(port));
        }
        else
        {
            // Most conservative variant: plain RTP output, nothing optional.
            a.AddRange(new[] { "-f", "rtp", "-payload_type", "96", $"rtp://127.0.0.1:{port}?pkt_size=1200" });
        }
        return a;
    }

    public async Task<List<CaptureCandidate>> BuildCandidatesAsync(int w, int h, int fps, int bps, bool needScale, Action<string> status)
    {
        var exe = FfmpegPath;
        var screen = CurrentScreen();
        var (adapter, output) = Dxgi.Find(screen.DeviceName) ?? (0, Math.Max(0, Array.IndexOf(Screen.AllScreens, screen)));

        var available = await DetectAsync(exe, status);
        var kinds = new List<EncoderKind>();
        if (Encoder == EncoderKind.Auto) kinds.AddRange(available);
        else
        {
            kinds.Add(Encoder);
            if (Encoder != EncoderKind.Software && available.Contains(EncoderKind.Software)) kinds.Add(EncoderKind.Software);
        }

        var bounds = screen.Bounds;
        var list = new List<CaptureCandidate>();
        foreach (var k in kinds)
        {
            var kind = k;
            foreach (var (grab, label) in new[] { (Grab.DdaFull, "Desktop Duplication"), (Grab.DdaPlain, "Desktop Duplication (basic)"), (Grab.Gdi, "GDI capture") })
            {
                var g = grab;
                list.Add(new CaptureCandidate
                {
                    Name = $"{Label(kind)} + {label}",
                    Args = port => BuildArgs(kind, g, adapter, output, bounds, fps, w, h, needScale, bps, port)
                });
            }
        }
        return list;
    }
}

/// Maps a Windows display (\\.\DISPLAYn) to the DXGI adapter/output index that ffmpeg's ddagrab expects.
internal static class Dxgi
{
    [DllImport("dxgi.dll")] private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_OUTPUT_DESC
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public RECT DesktopCoordinates;
        public int AttachedToDesktop;
        public int Rotation;
        public IntPtr Monitor;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int EnumFn(IntPtr self, uint index, out IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int OutputDescFn(IntPtr self, out DXGI_OUTPUT_DESC desc);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint ReleaseFn(IntPtr self);

    private static IntPtr Slot(IntPtr obj, int index) => Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), index * IntPtr.Size);
    private static void Release(IntPtr obj) { if (obj != IntPtr.Zero) Marshal.GetDelegateForFunctionPointer<ReleaseFn>(Slot(obj, 2))(obj); }

    public static (int Adapter, int Output)? Find(string deviceName)
    {
        IntPtr factory = IntPtr.Zero;
        try
        {
            var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");   // IDXGIFactory1
            if (CreateDXGIFactory1(ref iid, out factory) < 0 || factory == IntPtr.Zero) return null;
            var enumAdapters = Marshal.GetDelegateForFunctionPointer<EnumFn>(Slot(factory, 12));   // IDXGIFactory1::EnumAdapters1

            for (uint a = 0; a < 16; a++)
            {
                if (enumAdapters(factory, a, out var adapter) < 0) break;
                try
                {
                    var enumOutputs = Marshal.GetDelegateForFunctionPointer<EnumFn>(Slot(adapter, 7));   // IDXGIAdapter::EnumOutputs
                    for (uint o = 0; o < 16; o++)
                    {
                        if (enumOutputs(adapter, o, out var output) < 0) break;
                        try
                        {
                            var getDesc = Marshal.GetDelegateForFunctionPointer<OutputDescFn>(Slot(output, 7));   // IDXGIOutput::GetDesc
                            if (getDesc(output, out var desc) >= 0 &&
                                string.Equals(desc.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
                                return ((int)a, (int)o);
                        }
                        finally { Release(output); }
                    }
                }
                finally { Release(adapter); }
            }
        }
        catch (Exception ex) { HostLog.Write("DXGI lookup failed: " + ex.Message); }
        finally { Release(factory); }
        return null;
    }
}
