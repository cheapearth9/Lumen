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

        if (!Console.IsInputRedirected && !Console.IsOutputRedirected && await OfferUpdateAsync()) return 0;

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

    /// Asks (Y/N) when a newer release exists. Returns true when an update was installed and the program should exit.
    private static async Task<bool> OfferUpdateAsync()
    {
        var u = await Updater.CheckAsync();
        if (u == null) return false;
        Console.Write($"Lumen Host {u.Latest} is available (you have {Updater.Current}). Would you like to update to the latest version? [y/N/s=skip this version] ");
        var answer = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
        if (answer == "s") { Updater.Skip(u); return false; }
        if (answer != "y" && answer != "yes") { Updater.Later(); return false; }

        try
        {
            string? appImage = Environment.GetEnvironmentVariable("APPIMAGE");
            bool dpkg = File.Exists("/usr/bin/dpkg") && File.Exists("/usr/bin/lumenhost");
            bool pacman = File.Exists("/usr/bin/pacman") && File.Exists("/usr/bin/lumenhost");
            if (!string.IsNullOrEmpty(appImage) && File.Exists(appImage))
            {
                string? name = Updater.FindAsset(u, "Lumen-Host-", RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "aarch64.AppImage" : "x86_64.AppImage")
                               ?? Updater.FindAsset(u, "Lumen-Host-", ".AppImage");
                if (name == null) throw new InvalidOperationException("no AppImage in the release");
                string tmp = await Updater.DownloadAsync(u, name);
                File.Copy(tmp, appImage + ".new", true);
                File.SetUnixFileMode(appImage + ".new", (UnixFileMode)0b111_101_101);
                File.Move(appImage + ".new", appImage, true);
                Console.WriteLine("Updated. Start lumenhost again.");
                return true;
            }
            if (dpkg)
            {
                string name = Updater.FindAsset(u, "lumenhost_", "_amd64.deb") ?? throw new InvalidOperationException("no .deb in the release");
                string tmp = await Updater.DownloadAsync(u, name);
                if (!await RunAsync("sudo", "dpkg", "-i", tmp)) throw new InvalidOperationException("dpkg failed");
                Console.WriteLine("Updated. Start lumenhost again.");
                return true;
            }
            if (pacman)
            {
                string name = Updater.FindAsset(u, "lumenhost-", ".pkg.tar.zst") ?? throw new InvalidOperationException("no Arch package in the release");
                string tmp = await Updater.DownloadAsync(u, name);
                if (!await RunAsync("sudo", "pacman", "-U", tmp)) throw new InvalidOperationException("pacman failed");
                Console.WriteLine("Updated. Start lumenhost again.");
                return true;
            }
            Console.WriteLine("Download the new version from " + u.PageUrl);
        }
        catch (Exception ex) { Console.WriteLine("Update failed: " + ex.Message + "\nYou can download it from " + u.PageUrl); }
        return false;
    }

    private static async Task<bool> RunAsync(string file, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(file) { UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        await p.WaitForExitAsync();
        return p.ExitCode == 0;
    }
}
