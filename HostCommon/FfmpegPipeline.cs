using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;

namespace Lumen;

/// One way of capturing + encoding the screen (for example "NVIDIA NVENC + Desktop Duplication").
/// The pipeline tries candidates in order until one produces video.
public sealed class CaptureCandidate
{
    public string Name { get; init; } = "";
    /// Builds the complete ffmpeg argument list for a given loopback RTP port.
    public Func<int, List<string>> Args { get; init; } = _ => new();
    /// Program to run instead of ffmpeg (e.g. the Wayland GStreamer helper). It must send RTP H.265 to the given loopback port.
    public string? Exe { get; init; }
    /// How long to wait for the first frame (a permission dialog may be on screen).
    public int NoFrameSeconds { get; init; } = 8;
}

public static class HostLog
{
    private static readonly object Lock = new();
    private static string? _path;

    public static string Path
    {
        get
        {
            if (_path != null) return _path;
            string dir;
            if (OperatingSystem.IsWindows())
                dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lumen");
            else
                dir = System.IO.Path.Combine(Environment.GetEnvironmentVariable("XDG_STATE_HOME") is { Length: > 0 } x ? x
                       : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state"), "lumen");
            try { Directory.CreateDirectory(dir); } catch { }
            _path = System.IO.Path.Combine(dir, "host.log");
            try { if (File.Exists(_path) && new FileInfo(_path).Length > 1_000_000) File.Delete(_path); } catch { }
            return _path;
        }
    }

    public static void Write(string line)
    {
        try { lock (Lock) File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}"); } catch { }
    }
}

public static class LocalNet
{
    /// IPv4 addresses a phone on the same network could use to reach this computer.
    public static List<(string Interface, string Address)> Ipv4()
    {
        var list = new List<(string, string)>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var s = ua.Address.ToString();
                    if (s.StartsWith("169.254.") || s.StartsWith("127.")) continue;
                    list.Add((ni.Name, s));
                }
            }
        }
        catch { }
        return list;
    }
}

public static class FfmpegUtil
{
    public static string FindExe()
    {
        var env = Environment.GetEnvironmentVariable("LUMEN_FFMPEG");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        var name = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var local = System.IO.Path.Combine(AppContext.BaseDirectory, name);
        return File.Exists(local) ? local : name;       // otherwise rely on PATH
    }

    public static bool Bundled => File.Exists(System.IO.Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg"));

    public static List<string> OutputArgs(int port) =>
        new() { "-flush_packets", "1", "-f", "rtp", "-payload_type", "96", $"rtp://127.0.0.1:{port}?pkt_size=1200" };

    /// Names of the encoders this ffmpeg build contains (hevc_nvenc, libx265, ...).
    public static async Task<HashSet<string>> ListEncodersAsync(string exe)
    {
        var set = new HashSet<string>();
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "-hide_banner", "-encoders" }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) return set;
            _ = p.StandardError.ReadToEndAsync();
            var text = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            foreach (var line in text.Split('\n'))
            {
                var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts[0].StartsWith("V")) set.Add(parts[1]);
            }
        }
        catch (Exception ex) { HostLog.Write("ffmpeg -encoders failed: " + ex.Message); }
        return set;
    }

    /// Encodes a few synthetic frames to prove an encoder really works on this machine.
    public static async Task<bool> TestEncodeAsync(string exe, IEnumerable<string> args, int timeoutSeconds = 12)
    {
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) return false;
            var err = p.StandardError.ReadToEndAsync();
            _ = p.StandardOutput.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } return false; }
            if (p.ExitCode != 0) HostLog.Write("encoder test failed: " + string.Join(' ', args.Take(12)) + " -> " + Trim(await err));
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    private static string Trim(string s) => s.Length > 300 ? s[^300..] : s;
}

/// Runs ffmpeg and receives its output as RTP on loopback. RTP gives access-unit boundaries (marker bit)
/// without waiting for the next frame, so there is no extra frame of latency. Access units are converted to
/// the AVCC layout the iOS client expects. If a candidate fails (exits, or never produces a frame) the next
/// candidate is tried automatically; if all fail, Failed is raised with the reason.
public sealed class FfmpegPipeline : IDisposable
{
    /// (AVCC access unit, isKeyframe, [VPS, SPS, PPS] when keyframe)
    public event Action<byte[], bool, List<byte[]>>? Frame;
    public event Action? Dropped;
    public event Action<string>? Message;
    public event Action<string>? Attempt;
    public event Action<string>? Failed;

    private readonly object _sync = new();
    private List<CaptureCandidate> _cands = new();
    private string _exe = "ffmpeg";
    private int _index, _gen;
    private bool _stopped, _gotFrame;
    private Process? _proc;
    private Socket? _rtp;
    private System.Threading.Timer? _noFrame;
    private readonly Queue<string> _tail = new();
    private byte[]? _vps, _sps, _pps;

    public void Start(string exe, IReadOnlyList<CaptureCandidate> candidates)
    {
        lock (_sync) { _exe = exe; _cands = candidates.ToList(); _index = 0; _stopped = false; }
        Launch();
    }

    public void Stop()
    {
        lock (_sync) { _stopped = true; _gen++; }
        KillCurrent();
    }

    public void Dispose() => Stop();

    private void KillCurrent()
    {
        Process? p; Socket? s; System.Threading.Timer? t;
        lock (_sync) { p = _proc; s = _rtp; t = _noFrame; _proc = null; _rtp = null; _noFrame = null; }
        try { t?.Dispose(); } catch { }
        try { s?.Close(); } catch { }
        try { if (p is { HasExited: false }) p.Kill(true); } catch { }
        try { p?.Dispose(); } catch { }
    }

    private string Summary()
    {
        lock (_sync)
        {
            var lines = _tail.Where(l => !string.IsNullOrWhiteSpace(l)).TakeLast(3).ToArray();
            return lines.Length == 0 ? "no output from ffmpeg" : string.Join(" | ", lines);
        }
    }

    private void Launch()
    {
        CaptureCandidate cand;
        int gen;
        string exe;
        lock (_sync)
        {
            if (_stopped) return;
            if (_index >= _cands.Count) { gen = -1; cand = null!; exe = ""; }
            else { cand = _cands[_index]; gen = ++_gen; _gotFrame = false; _tail.Clear(); exe = cand.Exe ?? _exe; }
        }
        if (gen < 0) { Failed?.Invoke("No working capture/encoder combination was found."); return; }

        Attempt?.Invoke(cand.Name);
        HostLog.Write("trying: " + cand.Name);
        try
        {
            var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            sock.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            sock.ReceiveBufferSize = 16 * 1024 * 1024;
            int port = ((IPEndPoint)sock.LocalEndPoint!).Port;

            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true, RedirectStandardOutput = true   // ffmpeg prints an SDP to stdout for RTP output
            };
            var args = cand.Args(port);
            foreach (var a in args) psi.ArgumentList.Add(a);
            HostLog.Write("ffmpeg " + string.Join(' ', args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)));

            var p = Process.Start(psi) ?? throw new InvalidOperationException("could not start ffmpeg");
            p.ErrorDataReceived += (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data)) return;
                lock (_sync) { _tail.Enqueue(e.Data); while (_tail.Count > 15) _tail.Dequeue(); }
                HostLog.Write("ffmpeg: " + e.Data);
                Message?.Invoke(e.Data);
            };
            p.OutputDataReceived += (_, _) => { };
            p.BeginErrorReadLine();
            p.BeginOutputReadLine();

            lock (_sync)
            {
                if (_stopped || gen != _gen) { try { p.Kill(true); } catch { } sock.Close(); return; }
                _proc = p; _rtp = sock;
                _noFrame = new System.Threading.Timer(_ => Advance(gen, $"no video within {cand.NoFrameSeconds} seconds"), null, cand.NoFrameSeconds * 1000, Timeout.Infinite);
            }
            p.EnableRaisingEvents = true;
            p.Exited += (_, _) =>
            {
                int code = -1;
                try { code = p.ExitCode; } catch { }
                Advance(gen, "ffmpeg exited with code " + code, exited: true);
            };
            new Thread(() => RxLoop(sock, gen)) { IsBackground = true, Name = "lumen-rtp", Priority = ThreadPriority.AboveNormal }.Start();
        }
        catch (Exception ex)
        {
            HostLog.Write("launch error: " + ex.Message);
            Advance(gen, "could not start ffmpeg: " + ex.Message);
        }
    }

    /// Current candidate failed (or died). Try the next one, or report the failure.
    private void Advance(int gen, string reason, bool exited = false)
    {
        bool wasStreaming;
        lock (_sync)
        {
            if (_stopped || gen != _gen) return;
            wasStreaming = _gotFrame;
            _gen++;   // invalidate callbacks from the failed attempt
        }
        KillCurrent();
        Task.Run(async () =>
        {
            await Task.Delay(300);        // let the last stderr lines arrive
            var why = Summary();
            HostLog.Write($"candidate failed ({reason}): {why}");
            if (wasStreaming)
            {
                Failed?.Invoke($"ffmpeg stopped while streaming ({reason}): {why}");
                return;
            }
            bool more;
            lock (_sync) { _index++; more = !_stopped && _index < _cands.Count; }
            if (more) Launch();
            else if (!_stopped) Failed?.Invoke($"Could not start capture ({reason}): {why}");
        });
    }

    // MARK: RTP (RFC 7798) depacketizer

    private void RxLoop(Socket sock, int gen)
    {
        var buf = new byte[2048];
        var nals = new List<byte[]>();
        MemoryStream? fu = null;
        ushort expect = 0;
        bool haveSeq = false, damaged = false;

        while (true)
        {
            lock (_sync) { if (_stopped || gen != _gen) break; }
            int n;
            try { n = sock.Receive(buf); }
            catch { break; }
            if (n < 15) continue;

            bool marker = (buf[1] & 0x80) != 0;
            ushort seq = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(2));
            if (haveSeq && seq != expect) { damaged = true; fu = null; }
            expect = (ushort)(seq + 1);
            haveSeq = true;

            int off = 12 + (buf[0] & 0x0F) * 4;                          // fixed header + CSRCs
            if ((buf[0] & 0x10) != 0 && off + 4 <= n)                    // header extension
                off += 4 + BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(off + 2)) * 4;
            if (off + 3 > n) { damaged = true; }
            else
            {
                int type = (buf[off] >> 1) & 0x3F;
                if (type == 48)                                           // aggregation packet
                {
                    int p = off + 2;
                    while (p + 2 <= n)
                    {
                        int size = (buf[p] << 8) | buf[p + 1];
                        p += 2;
                        if (size <= 0 || p + size > n) { damaged = true; break; }
                        nals.Add(buf.AsSpan(p, size).ToArray());
                        p += size;
                    }
                }
                else if (type == 49)                                      // fragmentation unit
                {
                    byte fuh = buf[off + 2];
                    bool start = (fuh & 0x80) != 0, end = (fuh & 0x40) != 0;
                    int fuType = fuh & 0x3F;
                    if (start)
                    {
                        fu = new MemoryStream();
                        fu.WriteByte((byte)((buf[off] & 0x81) | (fuType << 1)));   // rebuild the NAL header
                        fu.WriteByte(buf[off + 1]);
                    }
                    if (fu == null) damaged = true;
                    else
                    {
                        fu.Write(buf, off + 3, n - off - 3);
                        if (end) { nals.Add(fu.ToArray()); fu = null; }
                    }
                }
                else
                {
                    nals.Add(buf.AsSpan(off, n - off).ToArray());         // single NAL unit packet
                }
            }

            if (marker)                                                   // last packet of the access unit
            {
                if (damaged || fu != null) Dropped?.Invoke();
                else if (Emit(nals, gen)) { }
                nals.Clear();
                fu = null;
                damaged = false;
            }
        }
    }

    private bool Emit(List<byte[]> nals, int gen)
    {
        var avcc = new MemoryStream();
        bool key = false;
        Span<byte> len = stackalloc byte[4];
        foreach (var nal in nals)
        {
            if (nal.Length < 3) continue;
            int t = (nal[0] >> 1) & 0x3F;
            switch (t)
            {
                case 32: _vps = nal; continue;
                case 33: _sps = nal; continue;
                case 34: _pps = nal; continue;
                case 35: continue;                                        // access unit delimiter
            }
            if (t >= 16 && t <= 23) key = true;                           // IRAP picture
            BinaryPrimitives.WriteUInt32BigEndian(len, (uint)nal.Length);
            avcc.Write(len);
            avcc.Write(nal, 0, nal.Length);
        }
        if (avcc.Length == 0) return false;

        var sets = new List<byte[]>();
        if (key)
        {
            if (_vps == null || _sps == null || _pps == null) { Dropped?.Invoke(); return false; }
            sets.Add(_vps); sets.Add(_sps); sets.Add(_pps);
        }

        lock (_sync)
        {
            if (_stopped || gen != _gen) return false;
            if (!_gotFrame) { _gotFrame = true; try { _noFrame?.Dispose(); } catch { } _noFrame = null; }
        }
        Frame?.Invoke(avcc.ToArray(), key, sets);
        return true;
    }
}
