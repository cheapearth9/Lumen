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
    /// h264 = the client cannot decode HEVC in hardware, so produce an H.264 stream (RTP, RFC 6184) instead of H.265.
    Task<List<CaptureCandidate>> BuildCandidatesAsync(int w, int h, int fps, int bitrateBps, bool needScale, bool h264, Action<string> status);

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

    /// Start a session for one client. Platforms that can serve several clients at once (separate virtual displays and
    /// input mapping per client) override this; the default shares the single display state.
    IPlatformSession OpenSession(bool extend, int clientW, int clientH, string deviceName, out string? note)
    {
        var s = new SharedPlatformSession(this, extend, clientW, clientH);
        note = s.Note;
        return s;
    }
}

/// One client's view of the platform: its own display (a mirror of the picked screen or a virtual display of its own),
/// input mapping and capture pipelines. Disposing it ends the session (a virtual display may stay around for the next one).
public interface IPlatformSession : IDisposable
{
    bool Extended { get; }
    (int W, int H) SourceSize { get; }
    void HandleInput(InputEvent e);
    void ReleaseInput();
    Task<List<CaptureCandidate>> BuildCandidatesAsync(int w, int h, int fps, int bitrateBps, bool needScale, bool h264, Action<string> status);
    /// Raised when the captured display's position or size changed (monitor arrangement edited); capture is restarted, the client stays connected.
    event Action? LayoutChanged { add { } remove { } }
}

/// Platforms with a single display state (Linux) share it between sessions.
internal sealed class SharedPlatformSession : IPlatformSession
{
    private readonly IPlatformHost _p;
    public bool Extended { get; }
    public string? Note { get; }
    public SharedPlatformSession(IPlatformHost p, bool extend, int w, int h)
    {
        _p = p;
        Note = p.ApplyMode(extend, w, h, out bool ex);
        Extended = ex;
        p.PrepareInput();
    }
    public (int W, int H) SourceSize => _p.SourceSize;
    public void HandleInput(InputEvent e) => _p.HandleInput(e);
    public void ReleaseInput() => _p.ReleaseInput();
    public Task<List<CaptureCandidate>> BuildCandidatesAsync(int w, int h, int fps, int bps, bool needScale, bool h264, Action<string> status) =>
        _p.BuildCandidatesAsync(w, h, fps, bps, needScale, h264, status);
    public void Dispose() => _p.EndSession();
}

/// Shared glue: client hello -> ffmpeg capture+encode -> network, and client input -> platform input injection.
public sealed class HostCore : IDisposable
{
    private readonly IPlatformHost _p;
    private readonly HostServer _server = new();
    private readonly UsbMux _usb = new();
    private readonly AdbTunnel _adb = new();
    private readonly SemaphoreSlim _ctl = new(1, 1);

    /// Everything that belongs to one connected device.
    private sealed class Session
    {
        public HostClient Client = null!;
        public HelloInfo Hello;
        public bool Active;
        public IPlatformSession? Plat;
        public FfmpegPipeline? Ff;
        public IDisposable? Audio;
        public volatile bool GotKey;
        public (int W, int H, int Fps) Size;
    }

    private readonly List<Session> _sessions = new();
    private ServiceDiscovery? _sd;
    private System.Threading.Timer? _awake;
    private bool _extended;
    private bool? _modeOverride;      // set by the host's Mirror / Extend buttons
    private bool _lastClientExtend;
    private long _bytes;
    private int _frames;

    public bool CableDaemonReachable => _usb.DaemonReachable;
    public int CableDevices => _usb.DeviceCount;
    public int AndroidCableDevices => _adb.DeviceCount;
    public bool AndroidNeedsAuthorization => _adb.NeedsAuthorization;
    public bool AdbFound => _adb.AdbFound;
    public int ClientCount => _server.ClientCount;
    public string ServerName { get; }
    public PairedStore Paired => _server.Paired;
    /// A device wants to pair: ask the user to type the 5-digit code the device shows, then call request.Submit(code).
    public event Action<HostServer.PairRequest>? PairRequested { add => _server.PairRequested += value; remove => _server.PairRequested -= value; }
    public event Action? PairResolved { add => _server.PairResolved += value; remove => _server.PairResolved -= value; }

    /// Host-side Mirror / Extend choice (null = follow whatever the client asked for). Restarts running sessions.
    public bool? ModeOverride => _modeOverride;
    public bool Extended => _extended;
    public event Action<bool>? ModeChanged;

    public void SetMode(bool extend)
    {
        _modeOverride = extend;
        List<Session> live;
        lock (_sessions) live = _sessions.Where(x => x.Active).ToList();
        if (live.Count == 0) { ModeChanged?.Invoke(extend); return; }
        foreach (var x in live) { var sx = x; _ = Task.Run(() => BeginAsync(sx.Client, sx.Hello, force: true)); }
    }

    public event Action<string>? Status;
    /// True when at least one device is connected (raised whenever that changes).
    public event Action<bool>? ClientChanged;

    public HostCore(IPlatformHost platform, string name)
    {
        _p = platform;
        ServerName = name;
        _server.ServerName = name;
        _server.PlatformId = platform.PlatformId;
        _server.Hello += (c, h) => _ = Task.Run(() => BeginAsync(c, h));
        _server.Input += (c, e) => { if (c.Tag is Session { Plat: { } plat }) plat.HandleInput(e); };
        _server.ClientChanged += (c, connected) =>
        {
            if (!connected) _ = Task.Run(() => EndAsync(c));
            ClientChanged?.Invoke(_server.ClientCount > 0);
        };
    }

    private void Say(string s) { HostLog.Write(s); Status?.Invoke(s); }

    public void Start(bool cable = true)
    {
        _server.Start();
        if (cable)
        {
            _usb.WantsTunnel = _server.WantsTunnel;
            _usb.OnTunnel = _server.AddTunnel;
            _usb.Start();
            _adb.WantsTunnel = _server.WantsTunnel;
            _adb.OnTunnel = _server.AddTunnel;
            _adb.Start();
        }
        Advertise();
        Say("Waiting for a client (Wi-Fi or cable)…");
    }

    public void Stop()
    {
        _usb.Stop();
        _adb.Stop();
        try { _sd?.Unadvertise(); _sd?.Dispose(); } catch { }
        _sd = null;
        _server.Stop();
        Say("Stopped");
    }

    public void Dispose()
    {
        Stop();
        List<Session> all;
        lock (_sessions) { all = _sessions.ToList(); _sessions.Clear(); }
        foreach (var x in all) StopSession(x);
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

    // MARK: Sessions

    private Session SessionOf(HostClient c)
    {
        lock (_sessions)
        {
            if (c.Tag is Session s) return s;
            s = new Session { Client = c };
            c.Tag = s;
            _sessions.Add(s);
            return s;
        }
    }

    private async Task BeginAsync(HostClient client, HelloInfo h, bool force = false)
    {
        await _ctl.WaitAsync();
        var sess = SessionOf(client);
        try
        {
            // The same device came back over another link (Wi-Fi -> cable or the reverse): end its old session first so
            // its display is reused instead of a second one being added.
            List<Session> old;
            lock (_sessions) old = _sessions.Where(x => !ReferenceEquals(x, sess) && x.Client.KeyId != null && x.Client.KeyId == client.KeyId).ToList();
            foreach (var d in old)
            {
                lock (_sessions) _sessions.Remove(d);
                d.Client.Tag = null;
                d.Active = false;
                try { d.Plat?.ReleaseInput(); } catch { }
                StopSession(d);
            }

            if (!force && sess.Active && sess.Hello == h && sess.Plat != null)
            {
                // Client repeated its hello (our welcome was probably lost).
                SendWelcome(sess, sess.Plat.SourceSize);
                return;
            }
            // A client that changes its extend flag overrides the host's button (last writer wins).
            if (!force && sess.Active && sess.Hello.WantsExtend != h.WantsExtend) _modeOverride = null;
            if (!force && !sess.Active && _lastClientExtend != h.WantsExtend && _sessions.Count(x => x.Active) == 0) _modeOverride = null;
            _lastClientExtend = h.WantsExtend;
            StopSession(sess);

            bool wantExtend = _modeOverride ?? h.WantsExtend;
            var plat = _p.OpenSession(wantExtend, h.Width, h.Height, Paired.NameFor(client.KeyId) ?? "Lumen", out var note);
            sess.Plat = plat;
            plat.LayoutChanged += () =>
            {
                if (!ReferenceEquals(sess.Plat, plat) || !sess.Active || !TryAutoRestart(client)) return;
                Say("Monitor layout changed - restarting capture.");
                var hello = sess.Hello;
                _ = Task.Run(async () => { if (!ReferenceEquals(client.Tag, sess)) return; await BeginAsync(client, hello, force: true); });
            };
            _extended = plat.Extended;
            if (note != null) { Say(note); _server.SendStatus(client, note, error: false); }
            ModeChanged?.Invoke(_extended);

            var src = plat.SourceSize;
            var (w, hh) = Fit(src.W, src.H, h.Width, h.Height);
            int fps = Math.Clamp((int)h.Fps, 15, 120);
            // Wi-Fi cannot carry more than about 80 Mbps of UDP without loss (each lost packet freezes the picture until the next keyframe).
            int mbps = Math.Max(2, (int)h.BitrateMbps);
            int cap = client.IsCable ? 150 : 80;
            if (mbps > cap) { Say($"Bitrate limited to {cap} Mbps for this connection type."); mbps = cap; }
            int bps = mbps * 1_000_000;
            sess.Size = (w, hh, fps);
            sess.Hello = h;
            sess.Active = true;
            sess.GotKey = false;

            // Answer right away so the client knows we are alive while encoders are probed and ffmpeg starts.
            SendWelcome(sess, src);
            Say($"Starting {w}×{hh} @ {fps} fps ({(h.WantsH264 ? "H.264" : "HEVC")})…");

            var candidates = await plat.BuildCandidatesAsync(w, hh, fps, bps, needScale: w != src.W || hh != src.H, h.WantsH264, Say);
            if (candidates.Count == 0)
            {
                Fail(sess, "No usable video encoder found. Install ffmpeg with a hardware (NVIDIA / AMD / Intel) or software " + (h.WantsH264 ? "H.264" : "HEVC") + " encoder.");
                return;
            }

            var ff = new FfmpegPipeline();
            ff.Frame += (data, key, sets) => OnFrame(sess, data, key, sets);
            ff.Dropped += () => _server.SkipFrame(client);
            ff.Attempt += name => Say("Starting: " + name);
            ff.Failed += msg =>
            {
                if (!ReferenceEquals(sess.Ff, ff)) return;
                // Capture that was working and then died (monitor moved / resolution or layout change, GPU mode switch):
                // start it again instead of dropping the client. Give up after 3 restarts within a minute.
                if (sess.GotKey && sess.Active && TryAutoRestart(client))
                {
                    Say("Capture stopped (" + msg + ") - restarting.");
                    var hello = sess.Hello;
                    _ = Task.Run(async () => { await Task.Delay(500); if (!ReferenceEquals(client.Tag, sess)) return; await BeginAsync(client, hello, force: true); });
                    return;
                }
                Fail(sess, msg);
            };
            EnsureTimers();
            sess.Ff = ff;
            ff.Start(_p.FfmpegPath, candidates, h.WantsH264);

            if (h.WantsAudio && (_p.Capabilities & WelcomeInfo.CapAudio) != 0)
                sess.Audio = _p.StartAudio(pcm => _server.SendAudio(client, pcm));
        }
        catch (Exception ex)
        {
            Fail(sess, "Failed: " + ex.Message);
        }
        finally { _ctl.Release(); }
    }

    private void SendWelcome(Session s, (int W, int H) src) =>
        _server.SendWelcome(s.Client, new WelcomeInfo((ushort)s.Size.W, (ushort)s.Size.H, (byte)s.Size.Fps, _p.Capabilities,
                                                      (ushort)Math.Min(src.W, 65535), (ushort)Math.Min(src.H, 65535), s.Plat is { Extended: true } ? (byte)1 : (byte)0));

    private readonly Dictionary<HostClient, List<long>> _restarts = new();

    private bool TryAutoRestart(HostClient c)
    {
        lock (_restarts)
        {
            if (!_restarts.TryGetValue(c, out var l)) _restarts[c] = l = new List<long>();
            long now = Environment.TickCount64;
            l.RemoveAll(t => now - t > 60_000);
            if (l.Count >= 3) return false;
            l.Add(now);
            return true;
        }
    }

    private void Fail(Session s, string message)
    {
        Say(message);
        _server.SendStatus(s.Client, message, error: true);
        StopSession(s);
        s.Active = false;
    }

    private async Task EndAsync(HostClient client)
    {
        await _ctl.WaitAsync();
        try
        {
            Session? s;
            lock (_sessions) { s = client.Tag as Session; if (s != null) _sessions.Remove(s); }
            client.Tag = null;
            if (s != null) { s.Active = false; try { s.Plat?.ReleaseInput(); } catch { } StopSession(s); }
            bool none;
            lock (_sessions) none = _sessions.Count == 0;
            if (none)
            {
                _awake?.Dispose(); _awake = null;
                _statLog?.Dispose(); _statLog = null;
            }
            if (_server.ClientCount == 0) Say("Waiting for a client (Wi-Fi or cable)…");
        }
        finally { _ctl.Release(); }
    }

    /// Stops capture, audio and the platform session (the virtual display itself may stay for the next connection).
    private void StopSession(Session s)
    {
        s.GotKey = false;
        try { s.Audio?.Dispose(); } catch { }
        s.Audio = null;
        var plat = s.Plat; s.Plat = null;
        try { plat?.Dispose(); } catch { }
        var ff = s.Ff; s.Ff = null;
        if (ff != null) ff.Dispose();
    }

    private void EnsureTimers()
    {
        _awake ??= new System.Threading.Timer(_ => _p.KeepAwake(), null, 0, 20_000);
        _statLog ??= new System.Threading.Timer(_ =>
        {
            long b = Interlocked.Exchange(ref _logBytes, 0); int f = Interlocked.Exchange(ref _logFrames, 0);
            long drops = _server.DroppedFrames;
            HostLog.Write($"stats: {f / 5.0:0.0} fps, {b * 8 / 5e6:0.0} Mbps encoded, dropped {drops - _logDropPrev} frames (cable backlog), {_server.ClientCount} client(s)");
            _logDropPrev = drops;
        }, null, 5000, 5000);
    }

    private System.Threading.Timer? _statLog;
    private long _logBytes; private int _logFrames; private long _logDropPrev;

    private void OnFrame(Session s, byte[] avcc, bool key, List<byte[]> sets)
    {
        Interlocked.Add(ref _logBytes, avcc.Length); Interlocked.Increment(ref _logFrames);
        if (!s.GotKey)
        {
            if (!key) return;                    // the client can only start on a keyframe
            s.GotKey = true;
            Say($"Streaming {s.Size.W}×{s.Size.H} @ {s.Size.Fps} fps");
        }

        byte[] payload;
        if (key)
        {
            using var ms = new MemoryStream(avcc.Length + 128);
            ms.WriteByte((byte)sets.Count);
            foreach (var set in sets)
            {
                ms.WriteByte((byte)(set.Length >> 8));
                ms.WriteByte((byte)set.Length);
                ms.Write(set, 0, set.Length);
            }
            ms.Write(avcc, 0, avcc.Length);
            payload = ms.ToArray();
        }
        else payload = avcc;

        _server.SendVideo(s.Client, payload, key, (uint)Environment.TickCount64);
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
