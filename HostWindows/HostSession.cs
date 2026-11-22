using System.Runtime.InteropServices;
using Makaretu.Dns;

namespace Lumen;

/// Glue: client hello -> ffmpeg capture+encode -> network, and client input -> injected Windows input.
public sealed class HostSession : IDisposable
{
    [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);
    private const uint ES_SYSTEM_REQUIRED = 0x1, ES_DISPLAY_REQUIRED = 0x2;

    private readonly HostServer _server = new();
    private readonly InputInjector _input = new();
    private readonly UsbMux _usb = new();
    private readonly SemaphoreSlim _ctl = new(1, 1);

    private FfmpegPipeline? _ff;
    private HelloInfo? _active;
    private volatile bool _welcomed;
    private (int W, int H, int Fps) _size;
    private EncoderKind? _detected;
    private ServiceDiscovery? _sd;
    private System.Threading.Timer? _awake;
    private long _bytes;
    private int _frames;

    public int ScreenIndex { get; set; }
    public int OutputIndex { get; set; }
    public EncoderKind Encoder { get; set; } = EncoderKind.Auto;
    public bool CableDaemonReachable => _usb.DaemonReachable;
    public int CableDevices => _usb.DeviceCount;

    public event Action<string>? Status;
    public event Action<bool>? ClientChanged;

    public HostSession()
    {
        _server.Hello += h => _ = Task.Run(() => BeginAsync(h));
        _server.Input += _input.Handle;
        _server.ClientChanged += connected =>
        {
            if (!connected)
            {
                _input.ReleaseAll();
                _ = Task.Run(EndAsync);
            }
            ClientChanged?.Invoke(connected);
        };
    }

    public void Start()
    {
        _server.Start();
        _usb.WantsTunnel = () => _server.WantsTunnel;
        _usb.OnTunnel = _server.AddTunnel;
        _usb.Start();
        Advertise();
        Status?.Invoke("Waiting for a client (Wi-Fi or cable)…");
    }

    public void Stop()
    {
        _usb.Stop();
        try { _sd?.Unadvertise(); _sd?.Dispose(); } catch { }
        _sd = null;
        _server.Stop();
        _ = Task.Run(EndAsync);
        Status?.Invoke("Stopped");
    }

    public void Dispose()
    {
        Stop();
        _ff?.Dispose();
    }

    public (long Bytes, int Frames) DrainStats() => (Interlocked.Exchange(ref _bytes, 0), Interlocked.Exchange(ref _frames, 0));

    private void Advertise()
    {
        try
        {
            _sd = new ServiceDiscovery();
            var profile = new ServiceProfile(Environment.MachineName, Wire.BonjourType, (ushort)Wire.UdpPort);
            _sd.Advertise(profile);
        }
        catch (Exception ex)
        {
            Status?.Invoke("Auto-discovery unavailable (" + ex.Message + "). Connect by IP or cable.");
        }
    }

    // MARK: Session

    private async Task BeginAsync(HelloInfo h)
    {
        await _ctl.WaitAsync();
        try
        {
            if (_active == h)
            {
                // Client repeated its hello (our welcome was probably lost).
                if (_welcomed) _server.SendWelcome(new WelcomeInfo((ushort)_size.W, (ushort)_size.H, (byte)_size.Fps));
                return;
            }
            StopCapture();

            var screens = Screen.AllScreens;
            var scr = screens[Math.Clamp(ScreenIndex, 0, screens.Length - 1)];
            _input.Configure(scr.Bounds, SystemInformation.VirtualScreen);

            var (w, hh) = Fit(scr.Bounds.Width, scr.Bounds.Height, h.Width, h.Height);
            int fps = Math.Clamp((int)h.Fps, 15, 120);
            int bps = Math.Max(2, (int)h.BitrateMbps) * 1_000_000;

            var enc = Encoder;
            if (enc == EncoderKind.Auto)
            {
                Status?.Invoke("Detecting hardware encoder…");
                _detected ??= await Ffmpeg.DetectAsync();
                if (_detected == null)
                {
                    Status?.Invoke("No hardware HEVC encoder found (needs NVIDIA, AMD or Intel graphics, and ffmpeg.exe next to LumenHost.exe).");
                    return;
                }
                enc = _detected.Value;
            }

            Status?.Invoke($"Starting {Ffmpeg.Label(enc)} {w}×{hh} @ {fps} fps…");
            _size = (w, hh, fps);
            _active = h;
            _welcomed = false;

            var ff = new FfmpegPipeline();
            ff.Frame += OnFrame;
            ff.Dropped += _server.SkipFrame;
            ff.Message += m => Status?.Invoke("ffmpeg: " + m);
            ff.Start(enc, OutputIndex, w, hh, fps, bps, scale: w != scr.Bounds.Width || hh != scr.Bounds.Height);
            _ff = ff;

            _awake ??= new System.Threading.Timer(_ => SetThreadExecutionState(ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED), null, 0, 20_000);
        }
        catch (Exception ex)
        {
            _active = null;
            Status?.Invoke("Failed: " + ex.Message);
        }
        finally { _ctl.Release(); }
    }

    private async Task EndAsync()
    {
        await _ctl.WaitAsync();
        try
        {
            StopCapture();
            _active = null;
            _awake?.Dispose(); _awake = null;
            Status?.Invoke("Waiting for a client (Wi-Fi or cable)…");
        }
        finally { _ctl.Release(); }
    }

    private void StopCapture()
    {
        _welcomed = false;
        var ff = _ff;
        _ff = null;
        if (ff != null) { ff.Frame -= OnFrame; ff.Dispose(); }
    }

    private void OnFrame(byte[] avcc, bool key, List<byte[]> sets)
    {
        if (!_welcomed)
        {
            if (!key) return;                    // the client can only start on a keyframe
            _welcomed = true;
            _server.SendWelcome(new WelcomeInfo((ushort)_size.W, (ushort)_size.H, (byte)_size.Fps));
            Status?.Invoke($"Streaming {_size.W}×{_size.H} @ {_size.Fps} fps");
        }

        byte[] payload;
        if (key)
        {
            using var ms = new MemoryStream(avcc.Length + 128);
            ms.WriteByte((byte)sets.Count);
            foreach (var s in sets)
            {
                ms.WriteByte((byte)(s.Length >> 8));
                ms.WriteByte((byte)s.Length);
                ms.Write(s, 0, s.Length);
            }
            ms.Write(avcc, 0, avcc.Length);
            payload = ms.ToArray();
        }
        else payload = avcc;

        _server.SendVideo(payload, key, (uint)Environment.TickCount64);
        Interlocked.Add(ref _bytes, payload.Length);
        Interlocked.Increment(ref _frames);
    }

    /// Aspect-correct size that fits inside the client's screen (never upscales), even dimensions.
    private static (int W, int H) Fit(int dw, int dh, int maxW, int maxH)
    {
        double scale = Math.Min(1.0, Math.Min((double)maxW / Math.Max(dw, 1), (double)maxH / Math.Max(dh, 1)));
        static int Even(double v) => Math.Max(2, (int)Math.Round(v) & ~1);
        return (Even(dw * scale), Even(dh * scale));
    }
}
