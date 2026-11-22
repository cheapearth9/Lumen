using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Lumen;

/// System audio (WASAPI loopback of the default output) converted to 48 kHz stereo s16le, 960-byte packets.
internal sealed class WindowsAudio : IDisposable
{
    private WasapiLoopbackCapture? _cap;
    private readonly Action<byte[]> _packet;
    private readonly List<byte> _out = new();
    private double _pos;          // fractional read position for resampling
    private float _lastL, _lastR;

    public WindowsAudio(Action<byte[]> packet)
    {
        _packet = packet;
        try
        {
            _cap = new WasapiLoopbackCapture();
            _cap.DataAvailable += OnData;
            _cap.StartRecording();
        }
        catch (Exception ex) { HostLog.Write("audio unavailable: " + ex.Message); _cap = null; }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var fmt = _cap!.WaveFormat;
        int ch = fmt.Channels, bytesPer = fmt.BitsPerSample / 8;
        bool isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat ||
                       (fmt.Encoding == WaveFormatEncoding.Extensible && fmt.BitsPerSample == 32);
        int frames = e.BytesRecorded / (bytesPer * ch);
        if (frames == 0) return;

        float[] l = new float[frames], r = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            float Get(int c)
            {
                int o = (i * ch + c) * bytesPer;
                return isFloat ? BitConverter.ToSingle(e.Buffer, o)
                     : bytesPer == 2 ? BitConverter.ToInt16(e.Buffer, o) / 32768f
                     : bytesPer == 4 ? BitConverter.ToInt32(e.Buffer, o) / 2147483648f : 0f;
            }
            l[i] = Get(0);
            r[i] = ch > 1 ? Get(1) : l[i];
        }

        double step = fmt.SampleRate / 48000.0;
        // linear resampling; _pos is relative to the start of this buffer (carry over the last sample)
        while (_pos < frames)
        {
            int i0 = (int)Math.Floor(_pos);
            double f = _pos - i0;
            float a0 = i0 == 0 && _pos < 1 && f >= 0 ? l[0] : l[Math.Min(i0, frames - 1)];
            float b0 = r[Math.Min(i0, frames - 1)];
            float a1 = l[Math.Min(i0 + 1, frames - 1)], b1 = r[Math.Min(i0 + 1, frames - 1)];
            Add((float)(a0 + (a1 - a0) * f)); Add((float)(b0 + (b1 - b0) * f));
            _pos += step;
        }
        _pos -= frames;

        while (_out.Count >= 960)
        {
            _packet(_out.GetRange(0, 960).ToArray());
            _out.RemoveRange(0, 960);
        }
    }

    private void Add(float v)
    {
        short s = (short)Math.Clamp((int)(v * 32767f), -32768, 32767);
        _out.Add((byte)s); _out.Add((byte)(s >> 8));
    }

    public void Dispose()
    {
        try { _cap?.StopRecording(); _cap?.Dispose(); } catch { }
        _cap = null;
    }
}
