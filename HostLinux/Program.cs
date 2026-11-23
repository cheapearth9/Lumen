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
          --new-code         generate a new pairing code and exit
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
                case "--new-code":
                    Console.WriteLine("New pairing code: " + PairingCode.Format(PairingCode.Regenerate()));
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

        try { core.Start(cable); }
        catch (Exception ex) { Console.Error.WriteLine("Could not start: " + ex.Message); return 1; }

        Console.WriteLine("Lumen Host - " + name);
        foreach (var (iface, addr) in LocalNet.Ipv4()) Console.WriteLine($"  IP address: {addr}  ({iface})");
        Console.WriteLine("  Pairing code (type it into the app once): " + PairingCode.Format(core.PairingKey));
        if (LinuxPlatform.IsWayland) Console.WriteLine("  Wayland session: your desktop will ask once which screen to share. Run 'lumenhost --check' if capture fails.");
        Console.WriteLine("Press Ctrl+C to stop.");

        await done.Task;
        return 0;
    }
}
