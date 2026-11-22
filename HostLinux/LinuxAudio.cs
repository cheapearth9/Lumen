using System.Diagnostics;

namespace Lumen;

/// System audio via a second ffmpeg process reading the PulseAudio / PipeWire default monitor source.
internal sealed class LinuxAudio : IDisposable
{
    private readonly Process? _proc;
    private volatile bool _stop;

    public LinuxAudio(string ffmpeg, Action<byte[]> packet)
    {
        try
        {
            var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-fflags", "nobuffer", "-flags", "low_delay",
                                      "-f", "pulse", "-i", "@DEFAULT_MONITOR@", "-ac", "2", "-ar", "48000", "-f", "s16le", "-" })
                psi.ArgumentList.Add(a);
            _proc = Process.Start(psi);
            if (_proc == null) return;
            _proc.ErrorDataReceived += (_, _) => { };
            new Thread(() =>
            {
                var s = _proc.StandardOutput.BaseStream;
                var buf = new byte[960];
                try
                {
                    while (!_stop)
                    {
                        int got = 0;
                        while (got < 960)
                        {
                            int n = s.Read(buf, got, 960 - got);
                            if (n <= 0) return;
                            got += n;
                        }
                        packet((byte[])buf.Clone());
                    }
                }
                catch { }
            }) { IsBackground = true, Name = "lumen-audio" }.Start();
            _ = Task.Run(() => { try { var e = _proc.StandardError.ReadToEnd(); if (e.Length > 0) HostLog.Write("audio ffmpeg: " + e.Trim()); } catch { } });
        }
        catch (Exception ex) { HostLog.Write("audio unavailable: " + ex.Message); }
    }

    public void Dispose()
    {
        _stop = true;
        try { if (_proc is { HasExited: false }) _proc.Kill(true); } catch { }
        try { _proc?.Dispose(); } catch { }
    }
}
