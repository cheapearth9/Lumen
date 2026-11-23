using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Lumen;

// End-to-end encryption for the Lumen protocol. Must stay byte-compatible with Shared/Crypto.swift
// (see the protocol description at the top of that file).

public static class PairingCode
{
    /// A 5-digit PIN. The key bytes are the ASCII digits; SecureSession stretches them with PBKDF2 before use.
    public static byte[] Generate()
    {
        var d = new byte[5];
        for (int i = 0; i < 5; i++) d[i] = (byte)('0' + RandomNumberGenerator.GetInt32(10));
        return d;
    }

    public static string Format(byte[] key) => Encoding.ASCII.GetString(key);

    /// Accepts digits with any separators. Null unless exactly 5 digits.
    public static byte[]? Parse(string text)
    {
        var digits = text.Where(char.IsAsciiDigit).ToArray();
        return digits.Length == 5 ? Encoding.ASCII.GetBytes(digits) : null;
    }

    private static string FilePath
    {
        get
        {
            string dir = OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lumen")
                : Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x
                               : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"), "lumen");
            try { Directory.CreateDirectory(dir); } catch { }
            return Path.Combine(dir, "pairing.key");
        }
    }

    /// The host's pairing key, created on first use and stored next to the other per-user settings.
    public static byte[] LoadOrCreate()
    {
        try
        {
            if (File.Exists(FilePath) && Parse(File.ReadAllText(FilePath)) is { } existing) return existing;
        }
        catch { }
        return Regenerate();
    }

    public static byte[] Regenerate()
    {
        var key = Generate();
        try { File.WriteAllText(FilePath, Format(key)); } catch { }
        return key;
    }
}

/// Duplicate / replay filter for sealed packets (applied only after a packet authenticated).
internal sealed class ReplayWindow
{
    private ulong _highest;
    private HashSet<ulong> _seen = new();
    private const ulong Span = 1024;

    public bool Accept(ulong seq)
    {
        if (seq + Span <= _highest) return false;
        if (!_seen.Add(seq)) return false;
        if (seq > _highest)
        {
            _highest = seq;
            if (_seen.Count > 2048) _seen = _seen.Where(s => s + Span > _highest).ToHashSet();
        }
        return true;
    }
}

public sealed class SecureSession
{
    private const byte Version = 1;

    private readonly bool _isClient;
    private readonly byte[] _psk;
    private readonly ECDiffieHellman _ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    private readonly byte[] _nonce = RandomNumberGenerator.GetBytes(16);
    private AesGcm? _send, _recv;
    private ulong _sendSeq;
    private readonly ReplayWindow _window = new();
    private readonly object _lock = new();

    public bool IsReady { get; private set; }

    private static readonly Dictionary<string, byte[]> StretchCache = new();

    /// A 5-digit PIN has little entropy, so it is stretched (PBKDF2-SHA256, 200k iterations) before it enters the key derivation.
    private static byte[] Stretch(byte[] pin)
    {
        var k = Convert.ToHexString(pin);
        lock (StretchCache)
        {
            if (StretchCache.TryGetValue(k, out var c)) return c;
            var r = Rfc2898DeriveBytes.Pbkdf2(pin, Encoding.ASCII.GetBytes("lumen-pin-v1"), 200_000, HashAlgorithmName.SHA256, 32);
            return StretchCache[k] = r;
        }
    }

    private SecureSession(bool isClient, byte[] psk) { _isClient = isClient; _psk = Stretch(psk); }

    private byte[] PublicKey()
    {
        var q = _ecdh.ExportParameters(false).Q;
        var pub = new byte[65];
        pub[0] = 4;                                  // x9.63 uncompressed point
        q.X!.CopyTo(pub, 1 + (32 - q.X.Length));
        q.Y!.CopyTo(pub, 33 + (32 - q.Y.Length));
        return pub;
    }

    /// Host: accept a client's handshake. Returns the session and the reply to send back (null if malformed).
    public static SecureSession? Accept(byte[] clientHello, byte[] pairingKey, out byte[] reply)
    {
        reply = Array.Empty<byte>();
        if (clientHello.Length != 2 + 65 + 16 || clientHello[0] != 0 || clientHello[1] != Version) return null;
        var s = new SecureSession(false, pairingKey);
        if (!s.Derive(clientHello.AsSpan(2, 65), clientHello.AsSpan(67, 16), s._nonce)) return null;
        reply = new byte[2 + 65 + 16];
        reply[0] = 1; reply[1] = Version;
        s.PublicKey().CopyTo(reply, 2);
        s._nonce.CopyTo(reply, 67);
        return s;
    }

    private bool Derive(ReadOnlySpan<byte> peerPublic, ReadOnlySpan<byte> clientNonce, ReadOnlySpan<byte> serverNonce)
    {
        try
        {
            if (peerPublic[0] != 4) return false;
            var p = new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = peerPublic.Slice(1, 32).ToArray(), Y = peerPublic.Slice(33, 32).ToArray() }
            };
            using var peer = ECDiffieHellman.Create(p);
            var secret = _ecdh.DeriveRawSecretAgreement(peer.PublicKey);
            var ikm = new byte[secret.Length + _psk.Length];
            secret.CopyTo(ikm, 0);
            _psk.CopyTo(ikm, secret.Length);
            var salt = new byte[32];
            clientNonce.CopyTo(salt);
            serverNonce.CopyTo(salt.AsSpan(16));
            var c2s = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 32, salt, Encoding.ASCII.GetBytes("lumen-v1 c2s"));
            var s2c = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 32, salt, Encoding.ASCII.GetBytes("lumen-v1 s2c"));
            _send = new AesGcm(_isClient ? c2s : s2c, 16);
            _recv = new AesGcm(_isClient ? s2c : c2s, 16);
            IsReady = true;
            return true;
        }
        catch { return false; }
    }

    public static bool IsSealed(byte[] d) => d.Length >= 2 && d[0] == 0x4C && d[1] == 0x45;

    /// Encrypts one Lumen packet. Null if the handshake has not completed.
    public byte[]? Seal(byte[] packet)
    {
        if (!IsReady || _send == null) return null;
        var output = new byte[10 + packet.Length + 16];
        output[0] = 0x4C; output[1] = 0x45;
        lock (_lock)
        {
            ulong seq = ++_sendSeq;
            BinaryPrimitives.WriteUInt64BigEndian(output.AsSpan(2), seq);
            Span<byte> nonce = stackalloc byte[12];
            BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], seq);
            _send.Encrypt(nonce, packet, output.AsSpan(10, packet.Length), output.AsSpan(10 + packet.Length, 16), output.AsSpan(0, 10));
        }
        return output;
    }

    /// Decrypts and authenticates a sealed packet. Null for forged, corrupted or replayed packets.
    public byte[]? Open(byte[] sealedPacket)
    {
        if (!IsReady || _recv == null || sealedPacket.Length < 10 + 16 || !IsSealed(sealedPacket)) return null;
        ulong seq = BinaryPrimitives.ReadUInt64BigEndian(sealedPacket.AsSpan(2));
        int len = sealedPacket.Length - 10 - 16;
        var plain = new byte[len];
        Span<byte> nonce = stackalloc byte[12];
        BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], seq);
        lock (_lock)
        {
            try
            {
                _recv.Decrypt(nonce, sealedPacket.AsSpan(10, len), sealedPacket.AsSpan(10 + len, 16), plain, sealedPacket.AsSpan(0, 10));
            }
            catch (CryptographicException) { return null; }
            if (!_window.Accept(seq)) return null;
        }
        return plain;
    }
}
