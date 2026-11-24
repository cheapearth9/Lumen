using System.Runtime.InteropServices;

namespace Lumen;

internal static class Program
{
    private const string Help = """
        lumenhost - stream this Linux desktop (Wayland or X11) to the Lumen iPhone/iPad app

        usage: lumenhost [options]
          --name NAME        name shown in the iPad app (default: host name)
          --encoder KIND     auto | vaapi | nvenc | software   (default: auto)
          --display :N       X11 display to capture (default: $DISPLAY or :0)
          --size WxH         screen size, if detection gets it wrong
          --no-cable         do not look for iPhones/iPads on USB
          --unpair-all       forget all paired devices and exit
          --check            test ffmpeg, input permissions and encoders, then exit
          --help             this text
        """;

    private static async Task<int> Main(string[] args)
    {
        var platform = new LinuxPlatform();
        string name = Environment.MachineName;
        bool cable = true, check = false;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (a)
            {
                case "--help": case "-h": Console.WriteLine(Help); return 0;
                case "--name": name = Next() ?? name; break;
                case "--display": platform.Display = Next() ?? platform.Display; break;
                case "--size": platform.SizeOverride = Next(); break;
                case "--no-cable": cable = false; break;
                case "--check": check = true; break;
                case "--unpair-all":
                    new PairedStore().RemoveAll();
                    Console.WriteLine("All paired devices were forgotten.");
                    return 0;
                case "--encoder":
                    platform.Encoder = (Next() ?? "auto").ToLowerInvariant() switch
                    {
                        "vaapi" => LinuxEncoder.Vaapi, "nvenc" => LinuxEncoder.Nvenc, "software" or "x265" or "cpu" => LinuxEncoder.Software, _ => LinuxEncoder.Auto
                    };
                    break;
                default: Console.Error.WriteLine("unknown option " + a + "\n\n" + Help); return 2;
            }
        }

        if (check) return await platform.CheckAsync();

        using var core = new HostCore(platform, name);
        var done = new TaskCompletionSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.TrySetResult(); };
        using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => done.TrySetResult());

        string last = "";
        core.Status += t => { if (t != last) { last = t; Console.WriteLine(t); } };
        core.ClientChanged += c => Console.WriteLine(c ? "Client connected" : "Client disconnected");

        core.PairRequested += req =>
        {
            Console.WriteLine($"\n*** \"{req.DeviceName}\" wants to pair. Type the 5-digit code shown on the device and press Enter (empty line = cancel):");
            _ = Task.Run(() =>
            {
                var line = Console.ReadLine();
                if (string.IsNullOrWhiteSpace(line)) { req.Cancel(); Console.WriteLine("Pairing cancelled."); return; }
                Console.WriteLine(req.Submit(line) ? "Paired." : "Wrong code. Pairing failed, start it again on the device.");
            });
        };

        try { core.Start(cable); }
        catch (Exception ex) { Console.Error.WriteLine("Could not start: " + ex.Message); return 1; }

        Console.WriteLine("Lumen Host - " + name);
        foreach (var (iface, addr) in LocalNet.Ipv4()) Console.WriteLine($"  IP address: {addr}  ({iface})");
        Console.WriteLine($"  Paired devices: {core.Paired.All().Count}. A new iPhone/iPad shows a 5-digit code when it connects; type it here when asked.");
        if (LinuxPlatform.IsWayland) Console.WriteLine("  Wayland session: your desktop will ask once which screen to share. Run 'lumenhost --check' if capture fails.");
        Console.WriteLine("Press Ctrl+C to stop.");

        await done.Task;
        return 0;
    }
}
