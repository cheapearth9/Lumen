using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Lumen;

/// Opens TCP tunnels to a USB-connected iPhone/iPad through Apple's usbmuxd, which on Windows is provided by
/// "Apple Mobile Device Service" (installed with iTunes or the Apple Devices app) on 127.0.0.1:27015.
/// The iOS app listens on Wire.CablePort; we connect to it whenever a device is attached and the host has no client.
public sealed class UsbMux : IDisposable
{
    private const int DaemonPort = 27015;

    private readonly object _lock = new();
    private readonly List<long> _devices = new();
    private bool _running;
    private int _generation;
    private Socket? _listenClient;

    public Func<string, bool> WantsTunnel { get; set; } = _ => true;
    public Action<Socket, string>? OnTunnel { get; set; }

    /// True once we have a working connection to the Apple Mobile Device Service.
    public bool DaemonReachable { get; private set; }
    public int DeviceCount { get { lock (_lock) return _devices.Count; } }

    public void Start()
    {
        int gen;
        lock (_lock)
        {
            if (_running) return;
            _running = true;
            gen = ++_generation;
        }
        new Thread(() => ListenLoop(gen)) { IsBackground = true, Name = "lumen-usbmux-listen" }.Start();
        new Thread(() => ConnectLoop(gen)) { IsBackground = true, Name = "lumen-usbmux-connect" }.Start();
    }

    public void Stop()
    {
        Socket? c;
        lock (_lock) { _running = false; c = _listenClient; _listenClient = null; _devices.Clear(); }
        DaemonReachable = false;
        try { c?.Close(); } catch { }
    }

    public void Dispose() => Stop();

    private bool Alive(int gen) { lock (_lock) return _running && _generation == gen; }

    // MARK: Device list

    private void ListenLoop(int gen)
    {
        while (Alive(gen))
        {
            try
            {
                var c = OpenDaemon();
                if (c != null)
                {
                    lock (_lock) _listenClient = c;
                    var s = new NetworkStream(c, ownsSocket: false);
                    Send(s, new Dictionary<string, object> { ["MessageType"] = "Listen", ["ClientVersionString"] = "Lumen", ["ProgName"] = "Lumen" }, 1);
                    DaemonReachable = true;
                    while (Alive(gen))
                    {
                        var msg = Read(s);
                        if (msg == null) break;
                        switch (msg.GetValueOrDefault("MessageType") as string)
                        {
                            case "Attached":
                                var props = msg.GetValueOrDefault("Properties") as Dictionary<string, object?>;
                                if (msg.GetValueOrDefault("DeviceID") is long id && (props?.GetValueOrDefault("ConnectionType") as string) == "USB")
                                    lock (_lock) if (!_devices.Contains(id)) _devices.Add(id);
                                break;
                            case "Detached":
                                if (msg.GetValueOrDefault("DeviceID") is long gone) lock (_lock) _devices.Remove(gone);
                                break;
                        }
                    }
                    c.Close();
                }
            }
            catch { }
            DaemonReachable = false;
            lock (_lock) _devices.Clear();
            for (int i = 0; i < 20 && Alive(gen); i++) Thread.Sleep(100);
        }
    }

    // MARK: Tunnels

    private void ConnectLoop(int gen)
    {
        while (Alive(gen))
        {
            Thread.Sleep(1500);
            List<long> ids;
            lock (_lock) ids = _devices.ToList();
            foreach (var id in ids)
            {
                string key = "usb:" + id;
                if (!WantsTunnel(key)) continue;
                var tunnel = OpenTunnel(id);
                if (tunnel != null) OnTunnel?.Invoke(tunnel, key);
            }
        }
    }

    private static Socket? OpenTunnel(long deviceId)
    {
        Socket? c = null;
        try
        {
            c = OpenDaemon();
            if (c == null) return null;
            var s = new NetworkStream(c, ownsSocket: false);
            int port = ((Wire.CablePort & 0xFF) << 8) | (Wire.CablePort >> 8);   // network byte order
            Send(s, new Dictionary<string, object>
            {
                ["MessageType"] = "Connect", ["ClientVersionString"] = "Lumen", ["ProgName"] = "Lumen",
                ["DeviceID"] = deviceId, ["PortNumber"] = port
            }, 2);
            var reply = Read(s);
            if (reply?.GetValueOrDefault("MessageType") as string == "Result" && reply.GetValueOrDefault("Number") is long n && n == 0)
                return c;    // from here on the socket is a raw byte pipe to the device
        }
        catch { }
        try { c?.Close(); } catch { }
        return null;
    }

    /// Apple Mobile Device Service listens on TCP 127.0.0.1:27015 on Windows; usbmuxd uses a unix socket
    /// at /var/run/usbmuxd on macOS and Linux.
    private static Socket? OpenDaemon()
    {
        Socket? sock = null;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                if (!sock.ConnectAsync(new IPEndPoint(IPAddress.Loopback, DaemonPort)).Wait(1500)) { sock.Close(); return null; }
            }
            else
            {
                sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                sock.Connect(new UnixDomainSocketEndPoint("/var/run/usbmuxd"));
            }
            return sock;
        }
        catch
        {
            try { sock?.Close(); } catch { }
            return null;
        }
    }

    // MARK: usbmuxd plist protocol (16-byte little-endian header + XML plist)

    private static void Send(NetworkStream s, Dictionary<string, object> dict, uint tag)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.Append("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n");
        sb.Append("<plist version=\"1.0\"><dict>");
        foreach (var kv in dict)
        {
            sb.Append("<key>").Append(System.Security.SecurityElement.Escape(kv.Key)).Append("</key>");
            if (kv.Value is int or long) sb.Append("<integer>").Append(Convert.ToInt64(kv.Value)).Append("</integer>");
            else sb.Append("<string>").Append(System.Security.SecurityElement.Escape(kv.Value.ToString())).Append("</string>");
        }
        sb.Append("</dict></plist>");

        var body = Encoding.UTF8.GetBytes(sb.ToString());
        var pkt = new byte[16 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(pkt.AsSpan(0), (uint)pkt.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(pkt.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(pkt.AsSpan(8), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(pkt.AsSpan(12), tag);
        body.CopyTo(pkt, 16);
        s.Write(pkt, 0, pkt.Length);
    }

    private static Dictionary<string, object?>? Read(NetworkStream s)
    {
        var hdr = new byte[16];
        if (!ReadExactly(s, hdr)) return null;
        uint len = BinaryPrimitives.ReadUInt32LittleEndian(hdr);
        if (len < 16 || len > (1 << 20)) return null;
        var body = new byte[len - 16];
        if (!ReadExactly(s, body)) return null;

        using var reader = XmlReader.Create(new StringReader(Encoding.UTF8.GetString(body)),
            new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
        var doc = XDocument.Load(reader);
        var dict = doc.Root?.Element("dict");
        return dict == null ? null : ParseDict(dict);
    }

    private static bool ReadExactly(NetworkStream s, byte[] buf)
    {
        int got = 0;
        while (got < buf.Length)
        {
            int n = s.Read(buf, got, buf.Length - got);
            if (n <= 0) return false;
            got += n;
        }
        return true;
    }

    private static Dictionary<string, object?> ParseDict(XElement d)
    {
        var r = new Dictionary<string, object?>();
        string? key = null;
        foreach (var c in d.Elements())
        {
            if (c.Name.LocalName == "key") key = c.Value;
            else if (key != null) { r[key] = ParseValue(c); key = null; }
        }
        return r;
    }

    private static object? ParseValue(XElement e) => e.Name.LocalName switch
    {
        "dict" => ParseDict(e),
        "string" => e.Value,
        "integer" => long.TryParse(e.Value, out var v) ? v : 0L,
        "true" => true,
        "false" => false,
        "array" => e.Elements().Select(ParseValue).ToList(),
        _ => e.Value
    };
}
