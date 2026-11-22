using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Lumen;

public enum EncoderKind { Auto, Nvenc, Amf, Qsv }

/// Command-line building blocks and hardware-encoder detection for the bundled ffmpeg.
public static class Ffmpeg
{
    public static string ExePath
    {
        get
        {
            var local = System.IO.Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
            return File.Exists(local) ? local : "ffmpeg";
        }
    }

    public static bool Bundled => File.Exists(System.IO.Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"));

    public static string Label(EncoderKind k) => k switch
    {
        EncoderKind.Nvenc => "NVIDIA NVENC",
        EncoderKind.Amf => "AMD AMF",
        EncoderKind.Qsv => "Intel Quick Sync",
        _ => "Auto-detect"
    };

    internal static List<string> PreInput(EncoderKind k) =>
        k == EncoderKind.Qsv ? new() { "-init_hw_device", "qsv=hw", "-filter_hw_device", "hw" } : new();

    /// Filter graph: Desktop Duplication (GPU) -> system memory -> optional scale -> NV12 (-> QSV upload).
    internal static string Filter(EncoderKind k, int outputIdx, int fps, int w, int h, bool scale)
    {
        var f = $"ddagrab=output_idx={outputIdx}:framerate={fps}:draw_mouse=1,hwdownload,format=bgra";
        if (scale) f += $",scale={w}:{h}:flags=fast_bilinear";
        f += ",format=nv12";
        if (k == EncoderKind.Qsv) f += ",hwupload=extra_hw_frames=32";
        return f + "[v]";
    }

    /// Low-latency settings: no B-frames, no look-ahead, CBR, a keyframe every second.
    internal static List<string> Codec(EncoderKind k, int fps, int bps)
    {
        string rate = bps.ToString(), buf = Math.Max(bps / 10, 100_000).ToString();
        var common = new List<string> { "-b:v", rate, "-maxrate", rate, "-bufsize", buf, "-g", fps.ToString() };
        List<string> specific = k switch
        {
            EncoderKind.Nvenc => new() { "-c:v", "hevc_nvenc", "-preset", "p1", "-tune", "ull", "-rc", "cbr", "-zerolatency", "1", "-bf", "0", "-forced-idr", "1" },
            EncoderKind.Amf => new() { "-c:v", "hevc_amf", "-usage", "ultralowlatency", "-quality", "speed", "-rc", "cbr" },
            EncoderKind.Qsv => new() { "-c:v", "hevc_qsv", "-preset", "veryfast", "-async_depth", "1", "-bf", "0" },
            _ => throw new ArgumentException("Pick a concrete encoder")
        };
        specific.AddRange(common);
        return specific;
    }

    public static async Task<EncoderKind?> DetectAsync()
    {
        foreach (var k in new[] { EncoderKind.Nvenc, EncoderKind.Amf, EncoderKind.Qsv })
            if (await TestAsync(k)) return k;
        return null;
    }

    private static async Task<bool> TestAsync(EncoderKind k)
    {
        try
        {
            var psi = new ProcessStartInfo(ExePath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-nostdin" }) psi.ArgumentList.Add(a);
            foreach (var a in PreInput(k)) psi.ArgumentList.Add(a);
            foreach (var a in new[] { "-f", "lavfi", "-i", "color=c=black:s=640x360:r=30" }) psi.ArgumentList.Add(a);
            psi.ArgumentList.Add("-vf");
            psi.ArgumentList.Add(k == EncoderKind.Qsv ? "format=nv12,hwupload=extra_hw_frames=16" : "format=nv12");
            foreach (var a in new[] { "-frames:v", "10" }) psi.ArgumentList.Add(a);
            foreach (var a in Codec(k, 30, 4_000_000)) psi.ArgumentList.Add(a);
            foreach (var a in new[] { "-f", "null", "-" }) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p == null) return false;
            _ = p.StandardError.ReadToEndAsync();
            _ = p.StandardOutput.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } return false; }
            return p.ExitCode == 0;
        }
        catch { return false; }
    }
}

/// Runs ffmpeg (Desktop Duplication -> hardware HEVC) and receives the result as RTP on loopback.
/// RTP gives us access-unit boundaries (marker bit) without waiting for the next frame, so there is no
/// extra frame of latency. Access units are converted to the AVCC layout the iOS client expects.
public sealed class FfmpegPipeline : IDisposable
{
    /// (AVCC access unit, isKeyframe, [VPS, SPS, PPS] when keyframe)
    public event Action<byte[], bool, List<byte[]>>? Frame;
    public event Action? Dropped;
    public event Action<string>? Message;

    private Process? _proc;
    private Socket? _rtp;
    private volatile bool _stop;
    private byte[]? _vps, _sps, _pps;

    public void Start(EncoderKind enc, int outputIdx, int w, int h, int fps, int bitrateBps, bool scale)
    {
        _stop = false;
        var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        sock.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        sock.ReceiveBufferSize = 16 * 1024 * 1024;
        int port = ((IPEndPoint)sock.LocalEndPoint!).Port;
        _rtp = sock;

        var psi = new ProcessStartInfo(Ffmpeg.ExePath)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true   // ffmpeg prints an SDP to stdout for RTP output
        };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "warning", "-nostdin" }) psi.ArgumentList.Add(a);
        foreach (var a in Ffmpeg.PreInput(enc)) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("-filter_complex"); psi.ArgumentList.Add(Ffmpeg.Filter(enc, outputIdx, fps, w, h, scale));
        psi.ArgumentList.Add("-map"); psi.ArgumentList.Add("[v]");
        psi.ArgumentList.Add("-an");
        foreach (var a in Ffmpeg.Codec(enc, fps, bitrateBps)) psi.ArgumentList.Add(a);
        // Make sure VPS/SPS/PPS travel in-band with every keyframe.
        psi.ArgumentList.Add("-bsf:v"); psi.ArgumentList.Add("dump_extra=freq=keyframe");
        foreach (var a in new[] { "-flush_packets", "1", "-f", "rtp", "-payload_type", "96", $"rtp://127.0.0.1:{port}?pkt_size=1200" })
            psi.ArgumentList.Add(a);

        var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start ffmpeg");
        p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Message?.Invoke(e.Data); };
        p.OutputDataReceived += (_, _) => { };
        p.BeginErrorReadLine();
        p.BeginOutputReadLine();
        p.EnableRaisingEvents = true;
        p.Exited += (_, _) => { if (!_stop) Message?.Invoke("ffmpeg stopped unexpectedly"); };
        _proc = p;

        new Thread(RxLoop) { IsBackground = true, Name = "lumen-rtp", Priority = ThreadPriority.AboveNormal }.Start();
    }

    public void Stop()
    {
        _stop = true;
        try { _rtp?.Close(); } catch { }
        _rtp = null;
        try { if (_proc is { HasExited: false }) _proc.Kill(true); } catch { }
        try { _proc?.Dispose(); } catch { }
        _proc = null;
    }

    public void Dispose() => Stop();

    // MARK: RTP (RFC 7798) depacketizer

    private void RxLoop()
    {
        var sock = _rtp;
        if (sock == null) return;
        var buf = new byte[2048];
        var nals = new List<byte[]>();
        MemoryStream? fu = null;
        ushort expect = 0;
        bool haveSeq = false, damaged = false;

        while (!_stop)
        {
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
                else Emit(nals);
                nals.Clear();
                fu = null;
                damaged = false;
            }
        }
    }

    private void Emit(List<byte[]> nals)
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
        if (avcc.Length == 0) return;

        var sets = new List<byte[]>();
        if (key)
        {
            if (_vps == null || _sps == null || _pps == null) { Dropped?.Invoke(); return; }
            sets.Add(_vps); sets.Add(_sps); sets.Add(_pps);
        }
        Frame?.Invoke(avcc.ToArray(), key, sets);
    }
}
