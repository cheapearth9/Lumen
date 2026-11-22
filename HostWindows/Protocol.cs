using System.Buffers.Binary;

namespace Lumen;

// Wire protocol shared with Shared/Protocol.swift. Keep both in sync.

public enum PacketType : byte { Hello = 1, Welcome = 2, Video = 3, Input = 4, KeyframeRequest = 5, Ping = 6, Bye = 7 }

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
    public const int MaxPayload = 1180;
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
        if (d[2] < 1 || d[2] > 7) return false;
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

public readonly record struct HelloInfo(ushort Width, ushort Height, byte Fps, ushort BitrateMbps)
{
    public static bool TryDecode(ReadOnlySpan<byte> d, out HelloInfo info)
    {
        info = default;
        if (d.Length < 7) return false;
        info = new HelloInfo(BinaryPrimitives.ReadUInt16BigEndian(d), BinaryPrimitives.ReadUInt16BigEndian(d[2..]),
                             d[4], BinaryPrimitives.ReadUInt16BigEndian(d[5..]));
        return true;
    }
}

public readonly record struct WelcomeInfo(ushort Width, ushort Height, byte Fps)
{
    public byte[] Encode()
    {
        var b = new byte[5];
        BinaryPrimitives.WriteUInt16BigEndian(b, Width);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(2), Height);
        b[4] = Fps;
        return b;
    }
}

public enum InputKind { MouseRel = 1, MouseAbs = 2, Button = 3, Scroll = 4, Key = 5 }

public readonly record struct InputEvent(InputKind Kind, int A, int B)
{
    // MouseRel: A=dx B=dy | MouseAbs: A=x B=y (0..65535) | Button: A=index B=down | Scroll: A=dx B=dy | Key: A=mac code B=down
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
