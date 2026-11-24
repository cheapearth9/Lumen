using System.Buffers.Binary;

namespace Lumen;

// Wire protocol shared with Shared/Protocol.swift. Keep both in sync.

public enum PacketType : byte { Hello = 1, Welcome = 2, Video = 3, Input = 4, KeyframeRequest = 5, Ping = 6, Bye = 7, Status = 8, Discover = 9, Announce = 10, Handshake = 11, Audio = 12, Pair = 13 }

public static class HostPlatformId { public const byte Mac = 1, Windows = 2, Linux = 3; }

public struct PacketHeader
{
    public PacketType Type;
    public byte Flags;
    public uint Id;
    public ushort FragIndex;
    public ushort FragCount;
    public uint TimestampMs;
}

public static class Wire
{
    public const ushort Magic = 0x4C4D;           // "LM"
    public const int HeaderSize = 16;
    public const int MaxPayload = 1150;     // sealed (encrypted) datagrams stay under a 1280 byte MTU
    public const int UdpPort = 47900;
    public const int CablePort = 47901;
    public const string BonjourType = "_lumen._udp";

    public static byte[] Encode(PacketHeader h, ReadOnlySpan<byte> payload)
    {
        var b = new byte[HeaderSize + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(0), Magic);
        b[2] = (byte)h.Type;
        b[3] = h.Flags;
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), h.Id);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(8), h.FragIndex);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(10), h.FragCount == 0 ? (ushort)1 : h.FragCount);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(12), h.TimestampMs);
        payload.CopyTo(b.AsSpan(HeaderSize));
        return b;
    }

    public static byte[] Simple(PacketType type, uint id = 0, ReadOnlySpan<byte> payload = default) =>
        Encode(new PacketHeader { Type = type, Id = id, FragCount = 1 }, payload);

    public static bool TryDecode(byte[] d, out PacketHeader h, out ReadOnlyMemory<byte> payload)
    {
        h = default; payload = default;
        if (d.Length < HeaderSize) return false;
        if (BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(0)) != Magic) return false;
        if (d[2] < 1 || d[2] > 13) return false;
        h.Type = (PacketType)d[2];
        h.Flags = d[3];
        h.Id = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(4));
        h.FragIndex = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(8));
        h.FragCount = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(10));
        h.TimestampMs = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(12));
        if (h.FragCount < 1 || h.FragIndex >= h.FragCount) return false;
        payload = new ReadOnlyMemory<byte>(d, HeaderSize, d.Length - HeaderSize);
        return true;
    }

    public static List<byte[]> Fragment(PacketType type, byte flags, uint id, uint ts, byte[] payload)
    {
        int count = Math.Max(1, (payload.Length + MaxPayload - 1) / MaxPayload);
        var list = new List<byte[]>(count);
        for (int i = 0; i < count; i++)
        {
            int s = i * MaxPayload;
            int len = Math.Min(MaxPayload, payload.Length - s);
            var h = new PacketHeader { Type = type, Flags = flags, Id = id, FragIndex = (ushort)i, FragCount = (ushort)count, TimestampMs = ts };
            list.Add(Encode(h, payload.AsSpan(s, Math.Max(0, len))));
        }
        return list;
    }
}

public readonly record struct HelloInfo(ushort Width, ushort Height, byte Fps, ushort BitrateMbps, byte Flags = HelloInfo.FlagAudio)
{
    public const byte FlagAudio = 1;       // client wants audio
    public const byte FlagExtend = 2;      // client wants an extended desktop (virtual display) instead of a mirror

    public bool WantsAudio => (Flags & FlagAudio) != 0;
    public bool WantsExtend => (Flags & FlagExtend) != 0;

    public static bool TryDecode(ReadOnlySpan<byte> d, out HelloInfo info)
    {
        info = default;
        if (d.Length < 7) return false;
        info = new HelloInfo(BinaryPrimitives.ReadUInt16BigEndian(d), BinaryPrimitives.ReadUInt16BigEndian(d[2..]),
                             d[4], BinaryPrimitives.ReadUInt16BigEndian(d[5..]), d.Length >= 8 ? d[7] : FlagAudio);
        return true;
    }
}

public readonly record struct WelcomeInfo(ushort Width, ushort Height, byte Fps, byte Caps = 0, ushort SourceWidth = 0, ushort SourceHeight = 0, byte Mode = 0)
{
    public const byte CapNativeTouch = 1;     // host can inject real multi-touch events
    public const byte CapAudio = 2;           // host streams system audio

    public byte[] Encode()
    {
        var b = new byte[11];
        BinaryPrimitives.WriteUInt16BigEndian(b, Width);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(2), Height);
        b[4] = Fps;
        b[5] = Caps;
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(6), SourceWidth);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(8), SourceHeight);
        b[10] = Mode;                             // 0 mirror, 1 extend (what the host is actually doing)
        return b;
    }
}

public enum InputKind { MouseRel = 1, MouseAbs = 2, Button = 3, Scroll = 4, Key = 5, Touch = 6 }

public readonly record struct InputEvent(InputKind Kind, int A, int B, int C = 0, int D = 0)
{
    // Touch: A=id B=phase (0 down, 1 move, 2 up, 3 cancel) C=x D=y (0..65535) | MouseRel: A=dx B=dy | MouseAbs: A=x B=y (0..65535) | Button: A=index B=down | Scroll: A=dx B=dy | Key: A=mac code B=down
    public static bool TryDecode(ReadOnlySpan<byte> d, out InputEvent e)
    {
        e = default;
        if (d.Length < 1) return false;
        switch (d[0])
        {
            case 1 when d.Length >= 5: e = new(InputKind.MouseRel, BinaryPrimitives.ReadInt16BigEndian(d[1..]), BinaryPrimitives.ReadInt16BigEndian(d[3..])); return true;
            case 2 when d.Length >= 5: e = new(InputKind.MouseAbs, BinaryPrimitives.ReadUInt16BigEndian(d[1..]), BinaryPrimitives.ReadUInt16BigEndian(d[3..])); return true;
            case 3 when d.Length >= 3: e = new(InputKind.Button, d[1], d[2]); return true;
            case 4 when d.Length >= 5: e = new(InputKind.Scroll, BinaryPrimitives.ReadInt16BigEndian(d[1..]), BinaryPrimitives.ReadInt16BigEndian(d[3..])); return true;
            case 5 when d.Length >= 4: e = new(InputKind.Key, BinaryPrimitives.ReadUInt16BigEndian(d[1..]), d[3]); return true;
            case 6 when d.Length >= 7: e = new(InputKind.Touch, d[1], d[2], BinaryPrimitives.ReadUInt16BigEndian(d[3..]), BinaryPrimitives.ReadUInt16BigEndian(d[5..])); return true;
        }
        return false;
    }
}

/// Length-prefixed packets over a byte stream (u32 big-endian length, then the packet).
public sealed class Deframer
{
    private byte[] _buf = new byte[1 << 16];
    private int _len;

    public List<byte[]> Push(byte[] data, int count)
    {
        if (_len + count > _buf.Length) Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _len + count));
        Buffer.BlockCopy(data, 0, _buf, _len, count);
        _len += count;

        var output = new List<byte[]>();
        int pos = 0;
        while (_len - pos >= 4)
        {
            uint l = BinaryPrimitives.ReadUInt32BigEndian(_buf.AsSpan(pos));
            if (l > 64u * 1024 * 1024) { _len = 0; return output; }
            if (_len - pos < 4 + (long)l) break;
            output.Add(_buf.AsSpan(pos + 4, (int)l).ToArray());
            pos += 4 + (int)l;
        }
        if (pos > 0)
        {
            Buffer.BlockCopy(_buf, pos, _buf, 0, _len - pos);
            _len -= pos;
        }
        return output;
    }
}
