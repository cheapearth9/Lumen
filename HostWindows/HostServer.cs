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
}

/// Wi-Fi / Ethernet: one datagram per packet.
internal sealed class UdpLink : ILink
{
    private readonly Socket _sock;
    public readonly EndPoint Remote;
    public DateTime LastSeen = DateTime.UtcNow;

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
    private readonly TcpClient _tcp;
    private readonly Stream _stream;
    private readonly BlockingCollection<byte[]> _queue = new();
    private readonly object _lock = new();
    private int _pending;
    private bool _closed;

    public readonly DateTime Created = DateTime.UtcNow;
    public Action<byte[]>? OnPacket;
    public Action? OnClosed;

    public TcpLink(TcpClient tcp)
    {
        _tcp = tcp;
        _tcp.NoDelay = true;
        _stream = tcp.GetStream();
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
            if (_closed || (droppable && _pending > 1_500_000)) return;
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
        try { _tcp.Close(); } catch { }
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
    private uint _frameCounter;
    private readonly HashSet<uint> _recent = new();
    private readonly Queue<uint> _recentOrder = new();
    private System.Threading.Timer? _watchdog;
    private bool _running;

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
    public void AddTunnel(TcpClient tcp)
    {
        var link = new TcpLink(tcp);
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

    private void HandlePacket(byte[] data, ILink link)
    {
        if (!Wire.TryDecode(data, out var h, out var payload)) return;

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
                link.Send(new[] { Wire.Simple(PacketType.Ping, h.Id) }, false);
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

    public void SendWelcome(WelcomeInfo w)
    {
        ILink? c;
        lock (_gate) c = _client;
        c?.Send(new[] { Wire.Simple(PacketType.Welcome, 0, w.Encode()) }, false);
    }

    public void SendVideo(byte[] payload, bool keyframe, uint timestampMs)
    {
        ILink? c;
        uint id;
        lock (_gate) { c = _client; if (c != null) id = ++_frameCounter; else return; }
        c.Send(Wire.Fragment(PacketType.Video, keyframe ? (byte)1 : (byte)0, id, timestampMs, payload), true);
    }

    /// A frame was lost before it could be sent: burn its id so the client notices the gap and waits for a keyframe.
    public void SkipFrame()
    {
        lock (_gate) { if (_client != null) _frameCounter++; }
    }
}
