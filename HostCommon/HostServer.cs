using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Lumen;

public interface ILink
{
    /// Droppable packets (video) may be discarded under back-pressure.
    void Send(IReadOnlyList<byte[]> packets, bool droppable);
    void Close();
    /// Encryption state of this link (null until a handshake arrived).
    SecureSession? Session { get; set; }
    /// Pairing attempt in progress on this link.
    PairingHost? Pairing { get; set; }
}

/// Wi-Fi / Ethernet: one datagram per packet.
internal sealed class UdpLink : ILink
{
    private readonly Socket _sock;
    public readonly EndPoint Remote;
    public DateTime LastSeen = DateTime.UtcNow;
    public SecureSession? Session { get; set; }
    public PairingHost? Pairing { get; set; }

    public UdpLink(Socket sock, EndPoint remote) { _sock = sock; Remote = remote; }

    public void Send(IReadOnlyList<byte[]> packets, bool droppable)
    {
        foreach (var p in packets)
        {
            try { _sock.SendTo(p, Remote); }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
        }
    }

    public void Close() { }
}

/// Cable: a usbmuxd tunnel (reliable byte stream), length-prefixed packets.
internal sealed class TcpLink : ILink
{
    private readonly Socket _sock;
    private readonly Stream _stream;
    private readonly BlockingCollection<byte[]> _queue = new();
    private readonly object _lock = new();
    private int _pending;
    private bool _closed;
    public long Dropped;

    public readonly DateTime Created = DateTime.UtcNow;
    public SecureSession? Session { get; set; }
    public PairingHost? Pairing { get; set; }
    public Action<byte[]>? OnPacket;
    public Action? OnClosed;

    public TcpLink(Socket sock)
    {
        _sock = sock;
        try { _sock.NoDelay = true; } catch { }       // not applicable to unix-domain sockets
        _stream = new NetworkStream(sock, ownsSocket: true);
    }

    public void Start()
    {
        new Thread(ReadLoop) { IsBackground = true, Name = "lumen-usb-read" }.Start();
        new Thread(WriteLoop) { IsBackground = true, Name = "lumen-usb-write" }.Start();
    }

    private void ReadLoop()
    {
        var buf = new byte[65536];
        var deframer = new Deframer();
        try
        {
            while (true)
            {
                int n = _stream.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                foreach (var p in deframer.Push(buf, n)) OnPacket?.Invoke(p);
            }
        }
        catch { }
        Close();
        OnClosed?.Invoke();
    }

    private void WriteLoop()
    {
        try
        {
            foreach (var chunk in _queue.GetConsumingEnumerable())
            {
                _stream.Write(chunk, 0, chunk.Length);
                lock (_lock) _pending -= chunk.Length;
            }
        }
        catch { Close(); }
    }

    public void Send(IReadOnlyList<byte[]> packets, bool droppable)
    {
        int size = 0;
        foreach (var p in packets) size += p.Length + 4;

        lock (_lock)
        {
            // Back-pressure: skip whole video frames if the device can't keep up. The client sees the
            // gap in frame ids and waits for the next keyframe.
            if (_closed) return;
            if (droppable && _pending > 4_000_000) { Interlocked.Increment(ref Dropped); return; }
            _pending += size;
        }

        var buf = new byte[size];
        int o = 0;
        foreach (var p in packets)
        {
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(o), (uint)p.Length);
            o += 4;
            p.CopyTo(buf, o);
            o += p.Length;
        }
        try { _queue.Add(buf); } catch (InvalidOperationException) { }
    }

    public void Close()
    {
        lock (_lock)
        {
            if (_closed) return;
            _closed = true;
        }
        try { _queue.CompleteAdding(); } catch { }
        try { _stream.Dispose(); } catch { }
        try { _sock.Close(); } catch { }
    }
}

/// Serves a single client at a time over UDP (advertised with mDNS) or a USB tunnel.
/// A link only becomes the active client once it sends a valid hello.
public sealed class HostServer : IDisposable
{
    private readonly object _gate = new();
    private Socket? _udp;
    private readonly Dictionary<string, UdpLink> _udpLinks = new();
    private readonly List<TcpLink> _tunnels = new();
    private ILink? _client;
    private DateTime _lastHeard = DateTime.UtcNow;
    private uint _frameCounter, _audioCounter;
    private readonly HashSet<uint> _recent = new();
    private readonly Queue<uint> _recentOrder = new();
    private System.Threading.Timer? _watchdog;
    private bool _running;

    /// Shown on the iOS app's list; sent in Announce replies.
    public string ServerName { get; set; } = Environment.MachineName;
    public byte PlatformId { get; set; }
    /// Devices that have paired with this computer. Everything except discover / pairing / handshake needs a session keyed by one of them.
    public PairedStore Paired { get; } = new();

    /// A device wants to pair: show its name and ask the user for the 5-digit code displayed on the device.
    public event Action<PairRequest>? PairRequested;
    /// The pairing request was resolved or abandoned (hide the prompt).
    public event Action? PairResolved;

    public sealed class PairRequest
    {
        public string DeviceName { get; init; } = "";
        internal Func<string, bool> SubmitImpl = _ => false;
        internal Action CancelImpl = () => { };
        /// Returns true when the typed code was right (the device is then paired).
        public bool Submit(string typed) => SubmitImpl(typed);
        public void Cancel() => CancelImpl();
    }

    private PairRequest? _pendingRequest;
    private int _pairFailures;
    private DateTime _lockedUntil;

    public event Action<HelloInfo>? Hello;
    public event Action<InputEvent>? Input;
    public event Action<bool>? ClientChanged;

    /// True while no client is connected and no tunnel is waiting for its hello.
    public bool WantsTunnel { get { lock (_gate) return _running && _client == null && _tunnels.Count == 0; } }

    public void Start()
    {
        var s = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp) { DualMode = true };
        s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.SendBuffer, 4 * 1024 * 1024);
        s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReceiveBuffer, 1024 * 1024);
        try { s.IOControl((IOControlCode)(-1744830452), new byte[] { 0, 0, 0, 0 }, null); } catch { } // ignore ICMP port-unreachable resets
        s.Bind(new IPEndPoint(IPAddress.IPv6Any, Wire.UdpPort));

        lock (_gate) { _udp = s; _running = true; }
        new Thread(UdpLoop) { IsBackground = true, Name = "lumen-udp" }.Start();
        _watchdog = new System.Threading.Timer(_ => Tick(), null, 1000, 1000);
    }

    public void Stop()
    {
        List<TcpLink> tunnels;
        ILink? client;
        Socket? udp;
        lock (_gate)
        {
            _running = false;
            tunnels = _tunnels.ToList(); _tunnels.Clear();
            client = _client; _client = null;
            udp = _udp; _udp = null;
            _udpLinks.Clear();
        }
        _watchdog?.Dispose(); _watchdog = null;
        foreach (var t in tunnels) t.Close();
        client?.Close();
        try { udp?.Close(); } catch { }
        if (client != null) ClientChanged?.Invoke(false);
    }

    public void Dispose() => Stop();

    private void Tick()
    {
        ILink? drop = null;
        List<TcpLink> stale;
        lock (_gate)
        {
            if (_client != null && (DateTime.UtcNow - _lastHeard).TotalSeconds > 5) drop = _client;
            // A tunnel that never says hello (app busy / not ready) is closed so the next attempt starts clean.
            stale = _tunnels.Where(t => (DateTime.UtcNow - t.Created).TotalSeconds > 4).ToList();
            foreach (var k in _udpLinks.Where(kv => !ReferenceEquals(kv.Value, _client) && (DateTime.UtcNow - kv.Value.LastSeen).TotalSeconds > 60)
                                       .Select(kv => kv.Key).ToList())
                _udpLinks.Remove(k);
        }
        if (drop != null) DropClient(drop);
        foreach (var t in stale) t.Close();
    }

    // MARK: Wi-Fi

    private void UdpLoop()
    {
        var buf = new byte[2048];
        while (true)
        {
            Socket? s;
            lock (_gate) s = _udp;
            if (s == null) return;

            int n;
            EndPoint remote = new IPEndPoint(IPAddress.IPv6Any, 0);
            try { n = s.ReceiveFrom(buf, ref remote); }
            catch (SocketException) { lock (_gate) { if (!_running) return; } continue; }
            catch (ObjectDisposedException) { return; }
            if (n <= 0) continue;

            var data = new byte[n];
            Buffer.BlockCopy(buf, 0, data, 0, n);

            UdpLink link;
            lock (_gate)
            {
                string key = remote.ToString() ?? "";
                if (!_udpLinks.TryGetValue(key, out link!)) _udpLinks[key] = link = new UdpLink(s, remote);
                link.LastSeen = DateTime.UtcNow;
            }
            HandlePacket(data, link);
        }
    }

    // MARK: Cable

    /// Called by the usbmux watcher with a connected tunnel.
    public void AddTunnel(Socket tunnel)
    {
        var link = new TcpLink(tunnel);
        link.OnPacket = d => HandlePacket(d, link);
        link.OnClosed = () => LinkClosed(link);
        lock (_gate)
        {
            if (!_running) { link.Close(); return; }
            _tunnels.Add(link);
        }
        link.Start();
    }

    // MARK: Client management

    private void DropClient(ILink link)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_client, link)) return;
            _client = null;
        }
        link.Close();
        ClientChanged?.Invoke(false);
    }

    private void LinkClosed(ILink link)
    {
        if (link is TcpLink t) lock (_gate) _tunnels.Remove(t);
        DropClient(link);
    }

    private void HandlePacket(byte[] raw, ILink link)
    {
        byte[] data = raw;
        if (SecureSession.IsSealed(raw))
        {
            var session = link.Session;
            var plain = session?.Open(raw);
            if (plain == null) return;                    // forged, replayed, wrong pairing code, or no handshake
            data = plain;
        }
        else
        {
            // Only discovery and the key exchange may travel unencrypted.
            if (!Wire.TryDecode(raw, out var ph, out var pp)) return;
            if (ph.Type == PacketType.Handshake)
            {
                var hello = pp.ToArray();
                var id = SecureSession.KeyIdOf(hello);
                if (id == null) return;
                var secret = Paired.SecretFor(id);
                if (secret == null)
                {
                    link.Send(new[] { Wire.Simple(PacketType.Handshake, 0, SecureSession.PairingRequiredReply) }, false);
                    return;
                }
                var s = SecureSession.Accept(hello, secret, out var reply);
                if (s == null) return;
                link.Session = s;
                link.Send(new[] { Wire.Simple(PacketType.Handshake, 0, reply) }, false);
                return;
            }
            if (ph.Type == PacketType.Pair) { HandlePair(pp.ToArray(), link); return; }
            if (ph.Type != PacketType.Discover) return;
        }
        HandlePlain(data, link);
    }

    // MARK: Pairing (see Crypto.cs)

    private void HandlePair(byte[] p, ILink link)
    {
        if (p.Length < 1) return;
        if (p[0] == 1)
        {
            if (DateTime.UtcNow < _lockedUntil) { SendPairResult(link, 2); return; }
            var ph = new PairingHost();
            var reply = ph.HandleStep1(p);
            if (reply == null) return;
            link.Pairing = ph;
            link.Send(new[] { Wire.Simple(PacketType.Pair, 0, reply) }, false);
        }
        else if (p[0] == 3)
        {
            var ph = link.Pairing;
            if (ph == null) return;
            if (ph.Code != null) return;                     // duplicate step 3 (the client resends until it hears back)
            if (!ph.HandleStep3(p)) { link.Pairing = null; SendPairResult(link, 3); return; }

            PairRequest? previous;
            var req = new PairRequest { DeviceName = ph.DeviceName };
            req.SubmitImpl = typed =>
            {
                var digits = new string(typed.Where(char.IsAsciiDigit).ToArray());
                bool ok = digits.Length == 5 && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.ASCII.GetBytes(digits), System.Text.Encoding.ASCII.GetBytes(ph.Code!));
                lock (_gate) { if (_pendingRequest == req) _pendingRequest = null; }
                link.Pairing = null;
                if (ok)
                {
                    _pairFailures = 0;
                    Paired.Add(ph.DeviceName, ph.Secret!);
                    SendPairResult(link, 0);
                }
                else
                {
                    if (++_pairFailures >= 3) { _lockedUntil = DateTime.UtcNow.AddSeconds(60); _pairFailures = 0; }
                    SendPairResult(link, 1);
                }
                PairResolved?.Invoke();
                return ok;
            };
            req.CancelImpl = () =>
            {
                lock (_gate) { if (_pendingRequest == req) _pendingRequest = null; }
                link.Pairing = null;
                SendPairResult(link, 3);
                PairResolved?.Invoke();
            };
            lock (_gate) { previous = _pendingRequest; _pendingRequest = req; }
            previous?.Cancel();
            PairRequested?.Invoke(req);
        }
    }

    private static void SendPairResult(ILink link, byte result) =>
        link.Send(new[] { Wire.Simple(PacketType.Pair, 0, new byte[] { 4, result }) }, false);

    /// Seals packets with the link's session (dropping them if there is none) and sends.
    private static void SendSecure(ILink link, IReadOnlyList<byte[]> packets, bool droppable)
    {
        var session = link.Session;
        if (session == null) return;
        var sealedPackets = new List<byte[]>(packets.Count);
        foreach (var p in packets)
        {
            var sp = session.Seal(p);
            if (sp != null) sealedPackets.Add(sp);
        }
        link.Send(sealedPackets, droppable);
    }

    private void HandlePlain(byte[] data, ILink link)
    {
        if (!Wire.TryDecode(data, out var h, out var payload)) return;

        // Subnet-scan discovery from the iOS app (works even where mDNS is blocked).
        if (h.Type == PacketType.Discover)
        {
            var name = System.Text.Encoding.UTF8.GetBytes(ServerName.Length > 60 ? ServerName[..60] : ServerName);
            var body = new byte[1 + name.Length];
            body[0] = PlatformId;
            name.CopyTo(body, 1);
            link.Send(new[] { Wire.Simple(PacketType.Announce, 0, body) }, false);
            return;
        }

        ILink? old = null;
        bool becameConnected = false;
        lock (_gate)
        {
            if (!ReferenceEquals(_client, link))
            {
                if (h.Type != PacketType.Hello) return;
                old = _client;
                becameConnected = old == null;
                _client = link;
                _recent.Clear(); _recentOrder.Clear();
                if (link is TcpLink t) _tunnels.Remove(t);
            }
            _lastHeard = DateTime.UtcNow;
        }
        if (old != null && !ReferenceEquals(old, link)) old.Close();
        if (becameConnected) ClientChanged?.Invoke(true);

        switch (h.Type)
        {
            case PacketType.Hello:
                if (HelloInfo.TryDecode(payload.Span, out var info)) Hello?.Invoke(info);
                break;
            case PacketType.Input:
                if (IsNew(h.Id) && InputEvent.TryDecode(payload.Span, out var e)) Input?.Invoke(e);
                break;
            case PacketType.Ping:
                SendSecure(link, new[] { Wire.Simple(PacketType.Ping, h.Id) }, false);
                break;
            case PacketType.Bye:
                DropClient(link);
                break;
            // KeyframeRequest: ffmpeg can't be told to emit an IDR on demand; periodic keyframes (1 s) cover loss.
        }
    }

    /// Input events are sometimes sent twice over Wi-Fi; ignore sequence numbers we have already seen.
    private bool IsNew(uint seq)
    {
        lock (_gate)
        {
            if (!_recent.Add(seq)) return false;
            _recentOrder.Enqueue(seq);
            if (_recentOrder.Count > 512) _recent.Remove(_recentOrder.Dequeue());
            return true;
        }
    }

    // MARK: Sending

    /// Tell the client something (progress or an error). Errors make the client show the message and disconnect.
    public void SendStatus(string text, bool error)
    {
        ILink? c;
        lock (_gate) c = _client;
        if (c == null) return;
        var t = System.Text.Encoding.UTF8.GetBytes(text.Length > 300 ? text[..300] : text);
        var body = new byte[1 + t.Length];
        body[0] = error ? (byte)1 : (byte)0;
        t.CopyTo(body, 1);
        SendSecure(c, new[] { Wire.Simple(PacketType.Status, 0, body) }, false);
    }

    public void SendWelcome(WelcomeInfo w)
    {
        ILink? c;
        lock (_gate) c = _client;
        if (c != null) SendSecure(c, new[] { Wire.Simple(PacketType.Welcome, 0, w.Encode()) }, false);
    }

    /// 960-byte PCM audio packet (48 kHz stereo s16le, 240 frames).
    public void SendAudio(byte[] pcm)
    {
        ILink? c;
        uint id;
        lock (_gate) { c = _client; if (c != null) id = ++_audioCounter; else return; }
        SendSecure(c, new[] { Wire.Simple(PacketType.Audio, id, pcm) }, true);
    }

    public void SendVideo(byte[] payload, bool keyframe, uint timestampMs)
    {
        ILink? c;
        uint id;
        lock (_gate) { c = _client; if (c != null) id = ++_frameCounter; else return; }
        // Keyframes are never dropped: losing one stalls the picture for a whole second.
        SendSecure(c, Wire.Fragment(PacketType.Video, keyframe ? (byte)1 : (byte)0, id, timestampMs, payload), !keyframe);
    }

    public bool ClientIsCable { get { lock (_gate) return _client is TcpLink; } }
    public long DroppedFrames { get { lock (_gate) return (_client as TcpLink)?.Dropped ?? 0; } }

    /// A frame was lost before it could be sent: burn its id so the client notices the gap and waits for a keyframe.
    public void SkipFrame()
    {
        lock (_gate) { if (_client != null) _frameCounter++; }
    }
}
