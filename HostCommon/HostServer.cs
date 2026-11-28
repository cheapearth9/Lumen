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
    /// Id of the paired device on this link (from its handshake); the same device reconnecting replaces its old link.
    string? KeyId { get; set; }
    /// Cable links: which attached phone this tunnel belongs to ("usb:<id>" / "adb:<serial>"), so each phone gets one tunnel.
    string? DeviceKey { get; set; }
}

/// One connected device. HostCore keeps its session state in Tag.
public sealed class HostClient
{
    internal ILink Link = null!;
    internal DateTime LastHeard = DateTime.UtcNow;
    internal uint FrameCounter, AudioCounter;
    internal readonly HashSet<uint> Recent = new();
    internal readonly Queue<uint> RecentOrder = new();
    public object? Tag { get; set; }
    /// Hex id of the paired device behind this connection.
    public string? KeyId => Link.KeyId;
    public bool IsCable => Link is TcpLink;
    public long Dropped => (Link as TcpLink)?.Dropped ?? 0;
}

/// Wi-Fi / Ethernet: one datagram per packet.
internal sealed class UdpLink : ILink
{
    private readonly Socket _sock;
    public readonly EndPoint Remote;
    public DateTime LastSeen = DateTime.UtcNow;
    public SecureSession? Session { get; set; }
    public PairingHost? Pairing { get; set; }
    public string? KeyId { get; set; }
    public string? DeviceKey { get; set; }

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
    public string? KeyId { get; set; }
    public string? DeviceKey { get; set; }
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

/// Serves several clients at once over UDP (advertised with mDNS) or USB tunnels.
/// A link only becomes the active client once it sends a valid hello.
public sealed class HostServer : IDisposable
{
    private readonly object _gate = new();
    private Socket? _udp;
    private readonly Dictionary<string, UdpLink> _udpLinks = new();
    private readonly List<TcpLink> _tunnels = new();
    private readonly List<HostClient> _clients = new();
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
        public bool Submit(string typed, string? name = null) { ChosenName = name; return SubmitImpl(typed); }
        /// The name the host's owner gave the device (it also names the virtual display in Windows).
        internal string? ChosenName;
        public void Cancel() => CancelImpl();
    }

    private PairRequest? _pendingRequest;
    private int _pairFailures;
    private DateTime _lockedUntil;

    public event Action<HostClient, HelloInfo>? Hello;
    public event Action<HostClient, InputEvent>? Input;
    public event Action<HostClient, bool>? ClientChanged;

    public int ClientCount { get { lock (_gate) return _clients.Count; } }

    /// True while the phone behind `deviceKey` has neither a connected client nor a tunnel waiting for its hello.
    public bool WantsTunnel(string deviceKey)
    {
        lock (_gate)
            return _running && !_tunnels.Any(t => t.DeviceKey == deviceKey) && !_clients.Any(c => c.Link.DeviceKey == deviceKey);
    }

    public void Start()
    {
        Socket s;
        try
        {
            s = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp) { DualMode = true };
            s.Bind(new IPEndPoint(IPAddress.IPv6Any, Wire.UdpPort));
        }
        catch (Exception ex)
        {
            // IPv6 disabled or unavailable (common on some Linux setups): listen on IPv4 only.
            HostLog.Write("IPv6 socket failed (" + ex.Message + "); using IPv4");
            s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Bind(new IPEndPoint(IPAddress.Any, Wire.UdpPort));
        }
        s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.SendBuffer, 4 * 1024 * 1024);
        s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReceiveBuffer, 1024 * 1024);
        try { s.IOControl((IOControlCode)(-1744830452), new byte[] { 0, 0, 0, 0 }, null); } catch { } // ignore ICMP port-unreachable resets

        lock (_gate) { _udp = s; _running = true; }
        new Thread(UdpLoop) { IsBackground = true, Name = "lumen-udp" }.Start();
        _watchdog = new System.Threading.Timer(_ => Tick(), null, 1000, 1000);
    }

    public void Stop()
    {
        List<TcpLink> tunnels;
        List<HostClient> clients;
        Socket? udp;
        lock (_gate)
        {
            _running = false;
            tunnels = _tunnels.ToList(); _tunnels.Clear();
            clients = _clients.ToList(); _clients.Clear();
            udp = _udp; _udp = null;
            _udpLinks.Clear();
        }
        _watchdog?.Dispose(); _watchdog = null;
        foreach (var t in tunnels) t.Close();
        foreach (var c in clients) c.Link.Close();
        try { udp?.Close(); } catch { }
        foreach (var c in clients) ClientChanged?.Invoke(c, false);
    }

    public void Dispose() => Stop();

    private void Tick()
    {
        List<HostClient> drop;
        List<TcpLink> stale;
        lock (_gate)
        {
            drop = _clients.Where(c => (DateTime.UtcNow - c.LastHeard).TotalSeconds > 5).ToList();
            // A tunnel that never says hello (app busy / not ready) is closed so the next attempt starts clean.
            // Not while the device is pairing: the person is typing the code, and closing would make it show a new one.
            stale = _tunnels.Where(t => (DateTime.UtcNow - t.Created).TotalSeconds > (t.Pairing != null ? 150 : 4)).ToList();
            foreach (var k in _udpLinks.Where(kv => !_clients.Any(c => ReferenceEquals(c.Link, kv.Value)) && (DateTime.UtcNow - kv.Value.LastSeen).TotalSeconds > 60)
                                       .Select(kv => kv.Key).ToList())
                _udpLinks.Remove(k);
        }
        foreach (var c in drop) DropClient(c);
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
    public void AddTunnel(Socket tunnel, string deviceKey = "")
    {
        var link = new TcpLink(tunnel) { DeviceKey = deviceKey };
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

    private void DropClient(HostClient client)
    {
        lock (_gate)
        {
            if (!_clients.Remove(client)) return;
        }
        client.Link.Close();
        ClientChanged?.Invoke(client, false);
    }

    private void LinkClosed(ILink link)
    {
        HostClient? c;
        lock (_gate)
        {
            if (link is TcpLink t) _tunnels.Remove(t);
            c = _clients.FirstOrDefault(x => ReferenceEquals(x.Link, link));
        }
        if (c != null) DropClient(c);
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
                link.KeyId = Convert.ToHexString(id);
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
                    Paired.Add(string.IsNullOrWhiteSpace(req.ChosenName) ? ph.DeviceName : req.ChosenName.Trim(), ph.Secret!);
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

        HostClient? client;
        List<HostClient> replaced = new();
        bool isNew = false;
        lock (_gate)
        {
            client = _clients.FirstOrDefault(c => ReferenceEquals(c.Link, link));
            if (client == null)
            {
                if (h.Type != PacketType.Hello) return;
                client = new HostClient { Link = link };
                // The same paired device reconnecting (new Wi-Fi port, or cable instead of Wi-Fi) replaces its old connection.
                if (link.KeyId != null) replaced = _clients.Where(c => c.Link.KeyId == link.KeyId).ToList();
                foreach (var r in replaced) _clients.Remove(r);
                _clients.Add(client);
                if (link is TcpLink t) _tunnels.Remove(t);
                isNew = true;
            }
            client.LastHeard = DateTime.UtcNow;
        }
        foreach (var r in replaced) { r.Link.Close(); ClientChanged?.Invoke(r, false); }
        if (isNew) ClientChanged?.Invoke(client, true);

        switch (h.Type)
        {
            case PacketType.Hello:
                if (HelloInfo.TryDecode(payload.Span, out var info)) Hello?.Invoke(client, info);
                break;
            case PacketType.Input:
                if (IsNew(client, h.Id) && InputEvent.TryDecode(payload.Span, out var e)) Input?.Invoke(client, e);
                break;
            case PacketType.Ping:
                SendSecure(link, new[] { Wire.Simple(PacketType.Ping, h.Id) }, false);
                break;
            case PacketType.Bye:
                DropClient(client);
                break;
            // KeyframeRequest: ffmpeg can't be told to emit an IDR on demand; periodic keyframes (1 s) cover loss.
        }
    }

    /// Input events are sometimes sent twice over Wi-Fi; ignore sequence numbers we have already seen.
    private bool IsNew(HostClient c, uint seq)
    {
        lock (_gate)
        {
            if (!c.Recent.Add(seq)) return false;
            c.RecentOrder.Enqueue(seq);
            if (c.RecentOrder.Count > 512) c.Recent.Remove(c.RecentOrder.Dequeue());
            return true;
        }
    }

    // MARK: Sending

    /// Tell the client something (progress or an error). Errors make the client show the message and disconnect.
    public void SendStatus(HostClient c, string text, bool error)
    {
        var t = System.Text.Encoding.UTF8.GetBytes(text.Length > 300 ? text[..300] : text);
        var body = new byte[1 + t.Length];
        body[0] = error ? (byte)1 : (byte)0;
        t.CopyTo(body, 1);
        SendSecure(c.Link, new[] { Wire.Simple(PacketType.Status, 0, body) }, false);
    }

    /// Now-playing info for the iPad's Touch Bar scrubber: flags (1 available, 2 playing), position ms, duration ms, title.
    public void SendMedia(HostClient c, byte[] payload) =>
        SendSecure(c.Link, new[] { Wire.Simple(PacketType.Media, 0, payload) }, false);

    public void SendWelcome(HostClient c, WelcomeInfo w) =>
        SendSecure(c.Link, new[] { Wire.Simple(PacketType.Welcome, 0, w.Encode()) }, false);

    /// 960-byte PCM audio packet (48 kHz stereo s16le, 240 frames).
    public void SendAudio(HostClient c, byte[] pcm)
    {
        uint id;
        lock (_gate) id = ++c.AudioCounter;
        SendSecure(c.Link, new[] { Wire.Simple(PacketType.Audio, id, pcm) }, true);
    }

    public void SendVideo(HostClient c, byte[] payload, bool keyframe, uint timestampMs)
    {
        uint id;
        lock (_gate) id = ++c.FrameCounter;
        // Keyframes are never dropped: losing one stalls the picture for a whole second.
        SendSecure(c.Link, Wire.Fragment(PacketType.Video, keyframe ? (byte)1 : (byte)0, id, timestampMs, payload), !keyframe);
    }

    /// A frame was lost before it could be sent: burn its id so the client notices the gap and waits for a keyframe.
    public void SkipFrame(HostClient c)
    {
        lock (_gate) c.FrameCounter++;
    }

    public long DroppedFrames { get { lock (_gate) return _clients.Sum(c => c.Dropped); } }
}
