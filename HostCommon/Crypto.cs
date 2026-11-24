using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Lumen;

// End-to-end encryption and device pairing for the Lumen protocol. Must stay byte-compatible with Shared/Crypto.swift
// (the protocol is described at the top of that file).

internal static class PairingCrypto
{
    public static byte[] Sha256(params byte[][] parts)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var p in parts) h.AppendData(p);
        return h.GetHashAndReset();
    }

    public static byte[] KeyId(byte[] secret) => Sha256(Encoding.ASCII.GetBytes("lumen-keyid"), secret)[..8];

    public static string Code(byte[] pkC, byte[] pkH, byte[] nonceC, byte[] nonceH)
    {
        var d = Sha256(Encoding.ASCII.GetBytes("lumen-sas-v1"), pkC, pkH, nonceC, nonceH);
        return (BinaryPrimitives.ReadUInt32BigEndian(d) % 100000).ToString("D5");
    }

    public static byte[] Secret(byte[] ecdh, byte[] nonceC, byte[] nonceH) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, ecdh, 32, salt: nonceC.Concat(nonceH).ToArray(), info: Encoding.ASCII.GetBytes("lumen-pair-v1"));

    public static byte[] PublicKey(ECDiffieHellman e)
    {
        var q = e.ExportParameters(false).Q;
        var pub = new byte[65];
        pub[0] = 4;                                  // x9.63 uncompressed point
        q.X!.CopyTo(pub, 1 + (32 - q.X.Length));
        q.Y!.CopyTo(pub, 33 + (32 - q.Y.Length));
        return pub;
    }

    public static byte[]? Agree(ECDiffieHellman mine, ReadOnlySpan<byte> peerPublic)
    {
        try
        {
            if (peerPublic.Length != 65 || peerPublic[0] != 4) return null;
            var p = new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = peerPublic.Slice(1, 32).ToArray(), Y = peerPublic.Slice(33, 32).ToArray() }
            };
            using var peer = ECDiffieHellman.Create(p);
            return mine.DeriveRawSecretAgreement(peer.PublicKey);
        }
        catch { return null; }
    }
}

/// Host side of one pairing attempt (one per client link).
public sealed class PairingHost
{
    private readonly ECDiffieHellman _key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    private readonly byte[] _nonce = RandomNumberGenerator.GetBytes(16);
    private byte[] _commit = Array.Empty<byte>();

    public string DeviceName { get; private set; } = "Device";
    /// What the user must type on the computer; set after step 3.
    public string? Code { get; private set; }
    public byte[]? Secret { get; private set; }
    public DateTime Created { get; } = DateTime.UtcNow;

    /// Step 1 payload (including the leading step byte) -> step 2 payload.
    public byte[]? HandleStep1(ReadOnlySpan<byte> p)
    {
        if (p.Length < 33 || p[0] != 1) return null;
        _commit = p.Slice(1, 32).ToArray();
        var name = Encoding.UTF8.GetString(p[33..]).Replace("\0", "").Replace("\n", " ").Replace("\r", " ").Trim();
        DeviceName = name.Length == 0 ? "Device" : (name.Length > 40 ? name[..40] : name);
        var d = new byte[1 + 65 + 16];
        d[0] = 2;
        PairingCrypto.PublicKey(_key).CopyTo(d, 1);
        _nonce.CopyTo(d, 66);
        return d;
    }

    /// Step 3 payload -> true when the commit matched; `Code` and `Secret` are then set.
    public bool HandleStep3(ReadOnlySpan<byte> p)
    {
        if (p.Length != 1 + 65 + 16 || p[0] != 3) return false;
        var pkC = p.Slice(1, 65).ToArray();
        var nonceC = p.Slice(66, 16).ToArray();
        if (!CryptographicOperations.FixedTimeEquals(PairingCrypto.Sha256(pkC, nonceC), _commit)) return false;
        var shared = PairingCrypto.Agree(_key, pkC);
        if (shared == null) return false;
        Code = PairingCrypto.Code(pkC, PairingCrypto.PublicKey(_key), nonceC, _nonce);
        Secret = PairingCrypto.Secret(shared, nonceC, _nonce);
        return true;
    }
}

/// Devices this computer has paired with (key id, name, secret), kept in the per-user Lumen settings folder.
public sealed class PairedStore
{
    public sealed record Device(string Id, string Name, string Secret);

    private readonly object _lock = new();

    private static string FilePath
    {
        get
        {
            string dir = OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lumen")
                : Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x
                               : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"), "lumen");
            try { Directory.CreateDirectory(dir); } catch { }
            return Path.Combine(dir, "paired.json");
        }
    }

    public List<Device> All()
    {
        lock (_lock)
        {
            try { return JsonSerializer.Deserialize<List<Device>>(File.ReadAllText(FilePath)) ?? new(); }
            catch { return new(); }
        }
    }

    public byte[]? SecretFor(ReadOnlySpan<byte> keyId)
    {
        var hex = Convert.ToHexString(keyId).ToLowerInvariant();
        var d = All().FirstOrDefault(x => x.Id == hex);
        return d == null ? null : Convert.FromBase64String(d.Secret);
    }

    public void Add(string name, byte[] secret)
    {
        var list = All();
        var id = Convert.ToHexString(PairingCrypto.KeyId(secret)).ToLowerInvariant();
        list.RemoveAll(x => x.Id == id);
        list.Add(new Device(id, name, Convert.ToBase64String(secret)));
        Save(list);
    }

    public void RemoveAll() => Save(new());

    private void Save(List<Device> list)
    {
        lock (_lock)
        {
            try { File.WriteAllText(FilePath, JsonSerializer.Serialize(list)); } catch { }
        }
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
    private const byte Version = 2;

    private readonly byte[] _psk;
    private readonly ECDiffieHellman _ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    private readonly byte[] _nonce = RandomNumberGenerator.GetBytes(16);
    private AesGcm? _send, _recv;
    private ulong _sendSeq;
    private readonly ReplayWindow _window = new();
    private readonly object _lock = new();

    public bool IsReady { get; private set; }

    private SecureSession(byte[] psk) { _psk = psk; }

    /// The key id a client hello asks for, or null if the hello is malformed.
    public static byte[]? KeyIdOf(byte[] clientHello) =>
        clientHello.Length == 2 + 65 + 16 + 8 && clientHello[0] == 0 && clientHello[1] == Version ? clientHello[83..91] : null;

    /// Reply telling a client that it has to pair first.
    public static byte[] PairingRequiredReply => new byte[] { 2, Version };

    /// Host: accept a client's handshake using the secret that belongs to its key id. Null if malformed.
    public static SecureSession? Accept(byte[] clientHello, byte[] secret, out byte[] reply)
    {
        reply = Array.Empty<byte>();
        if (KeyIdOf(clientHello) == null) return null;
        var s = new SecureSession(secret);
        if (!s.Derive(clientHello.AsSpan(2, 65), clientHello.AsSpan(67, 16), s._nonce)) return null;
        reply = new byte[2 + 65 + 16];
        reply[0] = 1; reply[1] = Version;
        PairingCrypto.PublicKey(s._ecdh).CopyTo(reply, 2);
        s._nonce.CopyTo(reply, 67);
        return s;
    }

    private bool Derive(ReadOnlySpan<byte> peerPublic, ReadOnlySpan<byte> clientNonce, ReadOnlySpan<byte> serverNonce)
    {
        try
        {
            var secret = PairingCrypto.Agree(_ecdh, peerPublic);
            if (secret == null) return false;
            var ikm = new byte[secret.Length + _psk.Length];
            secret.CopyTo(ikm, 0);
            _psk.CopyTo(ikm, secret.Length);
            var salt = new byte[32];
            clientNonce.CopyTo(salt);
            serverNonce.CopyTo(salt.AsSpan(16));
            var c2s = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 32, salt, Encoding.ASCII.GetBytes("lumen-v1 c2s"));
            var s2c = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 32, salt, Encoding.ASCII.GetBytes("lumen-v1 s2c"));
            _send = new AesGcm(s2c, 16);      // host: sends server->client, receives client->server
            _recv = new AesGcm(c2s, 16);
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
