using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Lumen;

/// USB cable for Android phones/tablets: uses the Android platform-tools `adb` that ships next to the host.
/// For every attached device with USB debugging on, `adb forward` maps a local port to the app's loopback listener
/// (Wire.CablePort on the device) and we connect to it - the same shape as the iOS usbmuxd tunnel, so the server
/// treats the resulting socket like any other cable link (length-prefixed packets, encrypted).
public sealed class AdbTunnel : IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<string, int> _ports = new();        // serial -> forwarded local port
    private readonly Dictionary<string, int> _fails = new();        // serial -> consecutive failed connects
    private readonly HashSet<string> _launched = new();
    private List<string> _ready = new();
    private bool _running;
    private int _gen;
    private string? _adb;

    public Func<bool> WantsTunnel { get; set; } = () => true;
    public Action<Socket>? OnTunnel { get; set; }

    /// Android devices attached and authorised for debugging.
    public int DeviceCount { get { lock (_lock) return _ready.Count; } }
    /// A device is attached but has not been allowed to debug yet ("Allow USB debugging?" on the phone).
    public bool NeedsAuthorization { get; private set; }
    public bool AdbFound => FindAdb() != null;

    private string? FindAdb()
    {
        if (_adb != null) return _adb;
        var env = Environment.GetEnvironmentVariable("LUMEN_ADB");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return _adb = env;
        var name = OperatingSystem.IsWindows() ? "adb.exe" : "adb";
        foreach (var dir in new[] { Path.Combine(AppContext.BaseDirectory, "platform-tools"), AppContext.BaseDirectory })
        {
            var p = Path.Combine(dir, name);
            if (File.Exists(p)) return _adb = p;
        }
        // Fall back to whatever is on PATH.
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { var p = Path.Combine(dir, name); if (File.Exists(p)) return _adb = p; } catch { }
        }
        return null;
    }

    public void Start()
    {
        int gen;
        lock (_lock) { if (_running) return; _running = true; gen = ++_gen; }
        new Thread(() => Loop(gen)) { IsBackground = true, Name = "lumen-adb" }.Start();
    }

    public void Stop()
    {
        List<int> mine;
        lock (_lock) { _running = false; _ready = new(); mine = _ports.Values.ToList(); _ports.Clear(); _fails.Clear(); _launched.Clear(); }
        NeedsAuthorization = false;
        // Remove only our own forwards; the user's adb server and other forwards stay untouched.
        foreach (var port in mine) { try { Run("forward", "--remove", "tcp:" + port); } catch { } }
    }

    public void Dispose() => Stop();

    private bool Alive(int gen) { lock (_lock) return _running && _gen == gen; }

    private void Loop(int gen)
    {
        while (Alive(gen))
        {
            try { Tick(gen); } catch (Exception ex) { HostLog.Write("adb: " + ex.Message); }
            for (int i = 0; i < 15 && Alive(gen); i++) Thread.Sleep(100);
        }
    }

    private void Tick(int gen)
    {
        if (FindAdb() == null) return;
        var listing = Run("devices");
        if (listing == null) return;

        var ready = new List<string>();
        bool unauthorized = false;
        foreach (var line in listing.Split('\n').Skip(1))
        {
            var parts = line.Trim().Split('\t', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            if (parts[1] == "device") ready.Add(parts[0]);
            else if (parts[1] == "unauthorized") unauthorized = true;
        }
        NeedsAuthorization = unauthorized;
        lock (_lock)
        {
            _ready = ready;
            foreach (var gone in _ports.Keys.Where(k => !ready.Contains(k)).ToList()) { _ports.Remove(gone); _fails.Remove(gone); _launched.Remove(gone); }
        }
        if (!WantsTunnel()) return;

        foreach (var serial in ready)
        {
            if (!Alive(gen)) return;
            int port;
            lock (_lock) { if (!_ports.TryGetValue(serial, out port)) port = 0; }
            if (port == 0)
            {
                var o = Run("-s", serial, "forward", "tcp:0", "tcp:" + Wire.CablePort);
                if (o == null || !int.TryParse(o.Trim(), out port) || port <= 0) continue;
                lock (_lock) _ports[serial] = port;
            }
            var sock = TryConnect(port);
            if (sock != null)
            {
                lock (_lock) _fails[serial] = 0;
                OnTunnel?.Invoke(sock);
                return;
            }
            int fails;
            lock (_lock) { _fails[serial] = fails = _fails.GetValueOrDefault(serial) + 1; }
            // The app is not running on the phone: open it once so the cable works without touching the phone.
            if (fails >= 2 && _launched.Add(serial))
            {
                HostLog.Write("adb: starting the Lumen app on " + serial);
                Run("-s", serial, "shell", "am", "start", "-n", "com.lumen.client.android/.MainActivity");
            }
        }
    }

    /// adb accepts the local connection even when nothing listens on the phone and then closes it; a real tunnel stays open.
    private static Socket? TryConnect(int port)
    {
        Socket? s = null;
        try
        {
            s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            if (!s.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port)).Wait(1500)) { s.Close(); return null; }
            if (s.Poll(300_000, SelectMode.SelectRead) && s.Available == 0) { s.Close(); return null; }   // closed by adb
            return s;
        }
        catch { try { s?.Close(); } catch { } return null; }
    }

    private string? Run(params string[] args)
    {
        var exe = FindAdb();
        if (exe == null) return null;
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) return null;
            var err = p.StandardError.ReadToEndAsync();
            var outp = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(8000)) { try { p.Kill(true); } catch { } return null; }
            return p.ExitCode == 0 ? outp.Result : null;
        }
        catch { return null; }
    }
}
