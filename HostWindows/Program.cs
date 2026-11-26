namespace Lumen;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // One copy only (it owns the network port); a second launch just brings the first one to the front.
        using var mutex = new Mutex(true, @"Local\LumenHostSingleInstance", out bool first);
        using var wake = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\LumenHostWake");
        if (!first) { try { wake.Set(); } catch { } return; }

        // Physical pixel coordinates are required for correct capture size and mouse placement.
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        bool tray = args.Contains("--tray");
        var form = new MainForm(tray);
        var waiter = new Thread(() => { while (wake.WaitOne()) { try { form.BeginInvoke(form.ShowFromTray); } catch { return; } } }) { IsBackground = true };
        waiter.Start();
        Application.Run(form);
    }
}
