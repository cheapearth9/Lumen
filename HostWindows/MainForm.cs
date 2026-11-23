using System.Diagnostics;

namespace Lumen;

public sealed class MainForm : Form
{
    private readonly WindowsPlatform _platform = new();
    private readonly HostCore _s;
    private readonly ComboBox _display = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 400 };
    private readonly ComboBox _encoder = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 400 };
    private readonly Button _start = new() { Text = "Start", AutoSize = true, Padding = new Padding(16, 4, 16, 4) };
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(380, 0), Text = "Stopped" };
    private readonly Label _stats = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly Label _cable = new() { AutoSize = true, MaximumSize = new Size(400, 0), ForeColor = SystemColors.GrayText };
    private readonly TextBox _ips = new() { ReadOnly = true, Multiline = true, Width = 400, Height = 52, BorderStyle = BorderStyle.FixedSingle, BackColor = SystemColors.Window };
    private readonly Panel _dot = new() { Width = 12, Height = 12, BackColor = Color.Gray, Margin = new Padding(0, 6, 8, 0) };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private readonly Button _mirror = new() { Text = "Mirror", AutoSize = true, Padding = new Padding(10, 3, 10, 3) };
    private readonly Button _extend = new() { Text = "Extend", AutoSize = true, Padding = new Padding(10, 3, 10, 3) };
    private readonly Label _modeNote = new() { AutoSize = true, MaximumSize = new Size(400, 0), ForeColor = SystemColors.GrayText };
    private readonly TextBox _code = new() { ReadOnly = true, Width = 230, Font = new Font("Consolas", 12f), BorderStyle = BorderStyle.FixedSingle, BackColor = SystemColors.Window };
    private bool _running, _connected;
    private int _tick;

    public MainForm()
    {
        _s = new HostCore(_platform, Environment.MachineName);

        Text = "Lumen Host";
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        var screens = Screen.AllScreens;
        for (int i = 0; i < screens.Length; i++)
        {
            var sc = screens[i];
            _display.Items.Add($"{i + 1}: {sc.DeviceName.TrimStart('\\', '.')}  {sc.Bounds.Width}×{sc.Bounds.Height}{(sc.Primary ? "  (primary)" : "")}");
        }
        _display.SelectedIndex = Math.Max(0, Array.FindIndex(screens, s => s.Primary));

        foreach (var k in Enum.GetValues<EncoderKind>()) _encoder.Items.Add(WindowsPlatform.Label(k));
        _encoder.SelectedIndex = 0;

        var title = new Label { Text = "Lumen Host", Font = new Font("Segoe UI", 18f, FontStyle.Bold), AutoSize = true };

        var ipLabel = new Label { Text = "Your IP address (type this into the iPad app if the PC is not listed)", AutoSize = true, Margin = new Padding(0, 12, 0, 2) };
        var copy = new Button { Text = "Copy IP", AutoSize = true };
        copy.Click += (_, _) =>
        {
            var first = LocalNet.Ipv4().FirstOrDefault().Address;
            if (first != null) { try { Clipboard.SetText(first); } catch { } }
        };

        _code.Text = PairingCode.Format(_s.PairingKey);
        var regen = new Button { Text = "New code", AutoSize = true };
        regen.Click += (_, _) => _code.Text = PairingCode.Format(_s.NewPairingKey());
        var codeRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        codeRow.Controls.Add(_code);
        codeRow.Controls.Add(regen);
        var codeLabel = new Label { Text = "Pairing code (enter it once in the app; all traffic is encrypted)", AutoSize = true, Margin = new Padding(0, 12, 0, 2) };

        var modeRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        modeRow.Controls.Add(_mirror);
        modeRow.Controls.Add(_extend);
        _mirror.Click += (_, _) => { _s.SetMode(false); ShowMode(false); };
        _extend.Click += (_, _) => { _s.SetMode(true); ShowMode(true); };
        _s.ModeChanged += ex => Ui(() => ShowMode(ex));
        ShowMode(false);
        var hint = _modeNote;
        var vdd = new Button { Text = "Install virtual display (for Extend)", AutoSize = true, Visible = !WindowsPlatform.VirtualDisplayInstalled() };
        vdd.Click += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo("powershell.exe",
                    "-NoProfile -Command \"winget install --id=VirtualDrivers.Virtual-Display-Driver -e --silent --accept-package-agreements --accept-source-agreements; Read-Host 'Done. Press Enter to close'\"")
                    { UseShellExecute = true, Verb = "runas" });
                _modeNote.Text = "Installing the virtual display driver… when it finishes, press Extend.";
                vdd.Visible = false;
            }
            catch { _modeNote.Text = "Could not start the installer (needs administrator rights and winget)."; }
        };
        var ffmpegWarn = new Label
        {
            AutoSize = true, MaximumSize = new Size(400, 0), ForeColor = Color.Firebrick, Visible = !FfmpegUtil.Bundled,
            Text = "ffmpeg.exe was not found next to LumenHost.exe. Reinstall Lumen Host, or put ffmpeg on your PATH."
        };
        var log = new LinkLabel { Text = "Open log file", AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
        log.LinkClicked += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{HostLog.Path}\"") { UseShellExecute = true }); } catch { }
        };

        var runRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 12, 0, 0) };
        runRow.Controls.Add(_start);
        runRow.Controls.Add(new Panel { Width = 12 });
        runRow.Controls.Add(_dot);
        runRow.Controls.Add(_status);

        var panel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Padding = new Padding(20)
        };
        panel.Controls.Add(title);
        panel.Controls.Add(ipLabel);
        panel.Controls.Add(_ips);
        panel.Controls.Add(copy);
        panel.Controls.Add(codeLabel);
        panel.Controls.Add(codeRow);
        panel.Controls.Add(new Label { Text = "Screen mode", AutoSize = true, Margin = new Padding(0, 12, 0, 2) });
        panel.Controls.Add(modeRow);
        panel.Controls.Add(vdd);
        panel.Controls.Add(new Label { Text = "Display to stream", AutoSize = true, Margin = new Padding(0, 12, 0, 2) });
        panel.Controls.Add(_display);
        panel.Controls.Add(new Label { Text = "Video encoder", AutoSize = true, Margin = new Padding(0, 12, 0, 2) });
        panel.Controls.Add(_encoder);
        panel.Controls.Add(hint);
        panel.Controls.Add(ffmpegWarn);
        panel.Controls.Add(runRow);
        panel.Controls.Add(_stats);
        panel.Controls.Add(_cable);
        panel.Controls.Add(log);
        Controls.Add(panel);

        RefreshIps();

        _s.Status += t => Ui(() => _status.Text = t);
        _s.ClientChanged += c => Ui(() => { _connected = c; UpdateDot(); if (!c) _stats.Text = ""; });
        _start.Click += (_, _) => Toggle();

        _timer.Tick += (_, _) =>
        {
            if (++_tick % 5 == 0) RefreshIps();
            if (!_running) return;
            var (bytes, frames) = _s.DrainStats();
            if (_connected) _stats.Text = $"{bytes * 8 / 1e6:0.0} Mbps · {frames} fps";
            _cable.Text = _s.CableDaemonReachable
                ? (_s.CableDevices > 0 ? $"Cable: {_s.CableDevices} iPhone/iPad connected over USB." : "Cable: plug in an iPhone/iPad over USB (tap Trust).")
                : "Cable needs iTunes or the Apple Devices app (Microsoft Store) installed.";
        };
        _timer.Start();

        FormClosing += (_, _) => { _timer.Stop(); _s.Dispose(); };
    }

    private void ShowMode(bool extended)
    {
        _mirror.Font = new Font(_mirror.Font, extended ? FontStyle.Regular : FontStyle.Bold);
        _extend.Font = new Font(_extend.Font, extended ? FontStyle.Bold : FontStyle.Regular);
        _modeNote.Text = extended ? "Extend: the iPad shows an additional screen." : "Mirror: the iPad shows your main screen. Extend uses a second or virtual display.";
    }

    private void RefreshIps()
    {
        var ips = LocalNet.Ipv4();
        var text = ips.Count == 0 ? "No network connection found" : string.Join(Environment.NewLine, ips.Select(i => $"{i.Address}    ({i.Interface})"));
        if (_ips.Text != text) _ips.Text = text;
    }

    private void Ui(Action a)
    {
        if (IsDisposed) return;
        try { if (InvokeRequired) BeginInvoke(a); else a(); } catch (ObjectDisposedException) { }
    }

    private void Toggle()
    {
        if (_running)
        {
            _s.Stop();
            _running = false;
            _connected = false;
            _start.Text = "Start";
            _display.Enabled = _encoder.Enabled = true;
            _stats.Text = ""; _cable.Text = "";
        }
        else
        {
            _platform.ScreenIndex = Math.Max(0, _display.SelectedIndex);
            _platform.Encoder = (EncoderKind)Math.Max(0, _encoder.SelectedIndex);
            try
            {
                _s.Start();
                _running = true;
                _start.Text = "Stop";
                _display.Enabled = _encoder.Enabled = false;
            }
            catch (Exception ex)
            {
                _status.Text = "Could not start: " + ex.Message;
            }
        }
        UpdateDot();
    }

    private void UpdateDot() => _dot.BackColor = _connected ? Color.SeaGreen : (_running ? Color.Orange : Color.Gray);
}
