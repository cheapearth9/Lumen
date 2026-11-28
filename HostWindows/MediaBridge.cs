using System.Text;
using Windows.Media.Control;

namespace Lumen;

/// Reads what is playing (YouTube in a browser, Spotify, ...) from Windows media controls and lets the iPad seek it.
public sealed class MediaBridge : IDisposable
{
    private readonly Action<byte[]> _send;
    private readonly System.Threading.Timer _timer;
    private GlobalSystemMediaTransportControlsSessionManager? _mgr;
    private int _busy;
    private bool _disposed;
    private bool _lastAvailable = true;      // forces the first "unavailable" message

    public MediaBridge(Action<byte[]> send)
    {
        _send = send;
        _timer = new System.Threading.Timer(_ => Poll(), null, 1000, 700);
    }

    private async void Poll()
    {
        if (_disposed || Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            _mgr ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var s = _mgr.GetCurrentSession();
            var tl = s?.GetTimelineProperties();
            long durTicks = tl == null ? 0 : (tl.EndTime - tl.StartTime).Ticks;
            if (s == null || tl == null || durTicks <= 0)
            {
                if (_lastAvailable) { _lastAvailable = false; Send(0, 0, 0, ""); }
                return;
            }
            var info = s.GetPlaybackInfo();
            bool playing = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            double pos = (tl.Position - tl.StartTime).TotalMilliseconds;
            if (playing) pos += Math.Max(0, (DateTimeOffset.Now - tl.LastUpdatedTime).TotalMilliseconds) * (info.PlaybackRate ?? 1.0);
            double dur = TimeSpan.FromTicks(durTicks).TotalMilliseconds;
            string title = "";
            try { title = (await s.TryGetMediaPropertiesAsync()).Title ?? ""; } catch { }
            _lastAvailable = true;
            Send(1 | (playing ? 2 : 0), (uint)Math.Clamp(pos, 0, dur), (uint)dur, title);
        }
        catch { }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    private void Send(int flags, uint pos, uint dur, string title)
    {
        var t = Encoding.UTF8.GetBytes(title.Length > 120 ? title[..120] : title);
        var b = new byte[9 + t.Length];
        b[0] = (byte)flags;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(1), pos);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(5), dur);
        t.CopyTo(b, 9);
        _send(b);
    }

    public async void Seek(uint ms)
    {
        try
        {
            var s = _mgr?.GetCurrentSession();
            if (s == null) return;
            var start = s.GetTimelineProperties().StartTime;
            await s.TryChangePlaybackPositionAsync((start + TimeSpan.FromMilliseconds(ms)).Ticks);
        }
        catch { }
    }

    public void Dispose() { _disposed = true; try { _timer.Dispose(); } catch { } }
}
