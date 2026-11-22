namespace Lumen;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Physical pixel coordinates are required for correct capture size and mouse placement.
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
    }
}
