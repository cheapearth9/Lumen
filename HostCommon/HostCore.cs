using Makaretu.Dns;

namespace Lumen;

/// What a platform (Windows, Linux) must provide to the shared host logic.
public interface IPlatformHost
{
    byte PlatformId { get; }
    /// WelcomeInfo.Cap* bits.
    byte Capabilities { get; }
    string FfmpegPath { get; }
    /// Real pixel size of the display being streamed.
    (int W, int H) SourceSize { get; }
    /// Called at the start of every session: pick up the current display geometry for input mapping.
    void PrepareInput();
    void HandleInput(InputEvent e);
    void ReleaseInput();
    /// Keep the machine from sleeping (called every 20 s while streaming).
    void KeepAwake();
    /// Ordered list of capture+encode pipelines to try for this session.
    Task<List<CaptureCandidate>> BuildCandidatesAsync(int w, int h, int fps, int bitrateBps, bool needScale, Action<string> status);

    /// Start capturing system audio; deliver 960-byte packets (240 frames, 48 kHz stereo s16le). Null if unsupported.
    IDisposable? StartAudio(Action<byte[]> packet) => null;

    /// The session ended (or is restarting): release anything ApplyMode created, e.g. a virtual display.
    void EndSession() { }

    /// Make the streamed display an extended desktop (extend = true) or the existing screen (false).
    /// Returns a note for the user when the request could not be honoured; `extended` says what is really active.
    string? ApplyMode(bool extend, int clientW, int clientH, out bool extended)
    {
        extended = false;
        return extend ? "Extend is not available on this computer, so the screen is mirrored." : null;
    }
}

/// Shared glue: client hello -> ffmpeg capture+encode -> network, and client input -> platform input injection.
public sealed class HostCore : IDisposable
{
    private readonly IPlatformHost _p;
    private readonly HostServer _server = new();
    private readonly UsbMux _usb = new();
    private readonly SemaphoreSlim _ctl = new(1, 1);

    private FfmpegPipeline? _ff;
    private HelloInfo? _active;
    private volatile bool _gotKey;
    private (int W, int H, int Fps) _size;
    private bool _lastClientExtend;
    private ServiceDiscovery? _sd;
    private System.Threading.Timer? _awake;
    private IDisposable? _audio;
    private bool _extended;
    private bool? _modeOverride;      // set by the host's Mirror / Extend buttons
    private long _bytes;
    private int _frames;

    public bool CableDaemonReachable => _usb.DaemonReachable;
    public int CableDevices => _usb.DeviceCount;
    public string ServerName { get; }
    public PairedStore Paired => _server.Paired;
    /// A device wants to pair: ask the user to type the 5-digit code the device shows, then call request.Submit(code).
    public event Action<HostServer.PairRequest>? PairRequested { add => _server.PairRequested += value; remove => _server.PairRequested -= value; }
    public event Action? PairResolved { add => _server.PairResolved += value; remove => _server.PairResolved -= value; }

    /// Host-side Mirror / Extend choice (null = follow whatever the client asked for). Restarts a running session.
    public bool? ModeOverride => _modeOverride;
    public bool Extended => _extended;
    public event Action<bool>? ModeChanged;

    public void SetMode(bool extend)
    {
        _modeOverride = extend;
        var h = _active;
        if (h != null) _ = Task.Run(() => BeginAsync(h.Value, force: true));
        else ModeChanged?.Invoke(extend);
    }

    public event Action<string>? Status;
    public event Action<bool>? ClientChanged;

    public HostCore(IPlatformHost platform, string name)
    {
        _p = platform;
        ServerName = name;
        _server.ServerName = name;
        _server.PlatformId = platform.PlatformId;
        _server.Hello += h => _ = Task.Run(() => BeginAsync(h));
        _server.Input += _p.HandleInput;
        _server.ClientChanged += connected =>
        {
            if (!connected)
            {
                _p.ReleaseInput();
                _ = Task.Run(EndAsync);
            }
            ClientChanged?.Invoke(connected);
        };
    }

    private void Say(string s) { HostLog.Write(s); Status?.Invoke(s); }

    public void Start(bool cable = true)
    {
        _server.Start();
        if (cable)
        {
            _usb.WantsTunnel = () => _server.WantsTunnel;
            _usb.OnTunnel = _server.AddTunnel;
            _usb.Start();
        }
        Advertise();
        Say("Waiting for a client (Wi-Fi or cable)…");
    }

    public void Stop()
    {
        _usb.Stop();
        try { _sd?.Unadvertise(); _sd?.Dispose(); } catch { }
        _sd = null;
        _server.Stop();
        _ = Task.Run(EndAsync);
        Say("Stopped");
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
            var profile = new ServiceProfile(ServerName.Replace('.', '-'), Wire.BonjourType, (ushort)Wire.UdpPort);
            _sd.Advertise(profile);
            _sd.Announce(profile);
        }
        catch (Exception ex)
        {
            HostLog.Write("mDNS advertise failed: " + ex.Message);
        }
    }

    // MARK: Session

    private async Task BeginAsync(HelloInfo h, bool force = false)
    {
        await _ctl.WaitAsync();
        try
        {
            var src = _p.SourceSize;
            if (!force && _active == h)
            {
                // Client repeated its hello (our welcome was probably lost).
                SendWelcome(src);
                return;
            }
            // A client that changes its extend flag overrides the host's button (last writer wins).
            if (!force && _active is { } prev && prev.WantsExtend != h.WantsExtend) _modeOverride = null;
            if (!force && _active == null && _lastClientExtend != h.WantsExtend) _modeOverride = null;
            _lastClientExtend = h.WantsExtend;
            StopCapture();

            bool wantExtend = _modeOverride ?? h.WantsExtend;
            var note = _p.ApplyMode(wantExtend, h.Width, h.Height, out _extended);
            if (note != null) { Say(note); _server.SendStatus(note, error: false); }
            ModeChanged?.Invoke(_extended);

            _p.PrepareInput();
            src = _p.SourceSize;
            var (w, hh) = Fit(src.W, src.H, h.Width, h.Height);
            int fps = Math.Clamp((int)h.Fps, 15, 120);
            // Wi-Fi cannot carry more than about 80 Mbps of UDP without loss (each lost packet freezes the picture until the next keyframe).
            int mbps = Math.Max(2, (int)h.BitrateMbps);
            int cap = _server.ClientIsCable ? 150 : 80;
            if (mbps > cap) { Say($"Bitrate limited to {cap} Mbps for this connection type."); mbps = cap; }
            int bps = mbps * 1_000_000;
            _size = (w, hh, fps);
            _active = h;
            _gotKey = false;

            // Answer right away so the client knows we are alive while encoders are probed and ffmpeg starts.
            SendWelcome(src);
            Say($"Starting {w}×{hh} @ {fps} fps…");

            var candidates = await _p.BuildCandidatesAsync(w, hh, fps, bps, needScale: w != src.W || hh != src.H, Say);
            if (candidates.Count == 0)
            {
                Fail("No usable video encoder found. Install ffmpeg with a hardware (NVIDIA / AMD / Intel) or software HEVC encoder.");
                return;
            }

            var ff = new FfmpegPipeline();
            ff.Frame += OnFrame;
            ff.Dropped += _server.SkipFrame;
            ff.Attempt += name => Say("Starting: " + name);
            ff.Failed += msg => Fail(msg);
            StartStatLog();
            ff.Start(_p.FfmpegPath, candidates);
            _ff = ff;

            if (h.WantsAudio && (_p.Capabilities & WelcomeInfo.CapAudio) != 0)
                _audio = _p.StartAudio(_server.SendAudio);

            _awake ??= new System.Threading.Timer(_ => _p.KeepAwake(), null, 0, 20_000);
        }
        catch (Exception ex)
        {
            Fail("Failed: " + ex.Message);
        }
        finally { _ctl.Release(); }
    }

    private void SendWelcome((int W, int H) src) =>
        _server.SendWelcome(new WelcomeInfo((ushort)_size.W, (ushort)_size.H, (byte)_size.Fps, _p.Capabilities,
                                            (ushort)Math.Min(src.W, 65535), (ushort)Math.Min(src.H, 65535), _extended ? (byte)1 : (byte)0));

    private void Fail(string message)
    {
        Say(message);
        _server.SendStatus(message, error: true);
        StopCapture();
        _active = null;
    }

    private async Task EndAsync()
    {
        await _ctl.WaitAsync();
        try
        {
            StopCapture();
            _active = null;
            _awake?.Dispose(); _awake = null;
            Say("Waiting for a client (Wi-Fi or cable)…");
        }
        finally { _ctl.Release(); }
    }

    private void StopCapture()
    {
        _gotKey = false;
        try { _audio?.Dispose(); } catch { }
        _audio = null;
        try { _p.EndSession(); } catch { }
        _statLog?.Dispose(); _statLog = null;
        var ff = _ff;
        _ff = null;
        if (ff != null) { ff.Frame -= OnFrame; ff.Dispose(); }
    }

    private System.Threading.Timer? _statLog;
    private long _logBytes; private int _logFrames; private long _logKeys; private long _logDropPrev;

    private void StartStatLog()
    {
        _statLog?.Dispose();
        _statLog = new System.Threading.Timer(_ =>
        {
            long b = Interlocked.Exchange(ref _logBytes, 0); int f = Interlocked.Exchange(ref _logFrames, 0);
            long drops = _server.DroppedFrames;
            HostLog.Write($"stats: {f / 5.0:0.0} fps, {b * 8 / 5e6:0.0} Mbps encoded, dropped {drops - _logDropPrev} frames (cable backlog)");
            _logDropPrev = drops;
        }, null, 5000, 5000);
    }

    private void OnFrame(byte[] avcc, bool key, List<byte[]> sets)
    {
        Interlocked.Add(ref _logBytes, avcc.Length); Interlocked.Increment(ref _logFrames);
        if (!_gotKey)
        {
            if (!key) return;                    // the client can only start on a keyframe
            _gotKey = true;
            Say($"Streaming {_size.W}×{_size.H} @ {_size.Fps} fps");
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
    public static (int W, int H) Fit(int dw, int dh, int maxW, int maxH)
    {
        double scale = Math.Min(1.0, Math.Min((double)maxW / Math.Max(dw, 1), (double)maxH / Math.Max(dh, 1)));
        static int Even(double v) => Math.Max(2, (int)Math.Round(v) & ~1);
        return (Even(dw * scale), Even(dh * scale));
    }
}
