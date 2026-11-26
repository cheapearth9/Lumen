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
    private readonly Label _pairInfo = new() { AutoSize = true, MaximumSize = new Size(400, 0) };
    private readonly FlowLayoutPanel _pairBox = new() { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
    private readonly TextBox _pairInput = new() { Width = 110, MaxLength = 8, Font = new Font("Consolas", 14f) };
    private readonly Button _pairOk = new() { Text = "Pair", AutoSize = true };
    private readonly Button _pairCancel = new() { Text = "Cancel", AutoSize = true };
    private HostServer.PairRequest? _request;
    private bool _running, _connected;
    private int _tick;
    private readonly NotifyIcon _tray = new();
    private readonly ToolStripMenuItem _trayToggle = new("Start");
    private readonly bool _startHidden;
    private bool _quit, _balloonShown;

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static bool AutoStartOn()
    {
        try { using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue("LumenHost") != null; } catch { return false; }
    }
    private static void SetAutoStart(bool on)
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) k.SetValue("LumenHost", $"\"{Application.ExecutablePath}\" --tray"); else k.DeleteValue("LumenHost", false);
        }
        catch { }
    }

    public void ShowFromTray()
    {
        if (IsDisposed) return;
        Show();
        ShowInTaskbar = true;
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    public MainForm(bool startHidden = false)
    {
        _startHidden = startHidden;
        _s = new HostCore(_platform, Environment.MachineName);

        Text = "Lumen Host";
        Shown += async (_, _) =>
        {
            if (!_running) Toggle();                       // a background app: listen as soon as it opens
            if (_startHidden) { Hide(); return; }
            await OfferUpdateAsync();
        };
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

        var codeLabel = new Label { Text = "Pairing (all traffic is encrypted)", AutoSize = true, Margin = new Padding(0, 12, 0, 2) };
        var forget = new Button { Text = "Forget all paired devices", AutoSize = true };
        forget.Click += (_, _) => { _s.Paired.RemoveAll(); UpdatePaired(); };
        _pairInfo.Text = "";
        var codeRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        codeRow.Controls.Add(_pairInfo);
        codeRow.Controls.Add(_pairBox);
        codeRow.Controls.Add(forget);
        _pairBox.Visible = false;
        _pairOk.Click += (_, _) => SubmitPair();
        _pairCancel.Click += (_, _) => { _request?.Cancel(); };
        _pairInput.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { SubmitPair(); e.SuppressKeyPress = true; } };
        _pairBox.Controls.Add(_pairInput);
        _pairBox.Controls.Add(_pairOk);
        _pairBox.Controls.Add(_pairCancel);
        _s.PairRequested += req => Ui(() =>
        {
            _request = req;
            _pairInfo.Text = $"\"{req.DeviceName}\" wants to pair. Type the 5-digit code shown on it:";
            _pairBox.Visible = true;
            _pairInput.Text = "";
            _pairInput.Focus();
            Activate();
        });
        _s.PairResolved += () => Ui(() => { _request = null; _pairBox.Visible = false; UpdatePaired(); });
        UpdatePaired();

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
                var bat = Path.Combine(AppContext.BaseDirectory, "drivers", "sudovda", "install.bat");
                if (File.Exists(bat))
                    Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"\"{bat}\" & pause\"") { UseShellExecute = true, Verb = "runas", WorkingDirectory = Path.GetDirectoryName(bat)! });
                else
                    Process.Start(new ProcessStartInfo("powershell.exe",
                        "-NoProfile -Command \"winget install --id=VirtualDrivers.Virtual-Display-Driver -e --silent --accept-package-agreements --accept-source-agreements; Read-Host 'Done. Press Enter to close'\"")
                        { UseShellExecute = true, Verb = "runas" });
                _modeNote.Text = "Installing the virtual display driver… when it finishes, choose Extend on the iPad.";
                vdd.Visible = false;
            }
            catch { _modeNote.Text = "Could not start the installer (needs administrator rights)."; }
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
            var cable = new List<string>();
            if (_s.CableDevices > 0) cable.Add($"{_s.CableDevices} iPhone/iPad");
            if (_s.AndroidCableDevices > 0) cable.Add($"{_s.AndroidCableDevices} Android");
            if (cable.Count > 0) _cable.Text = "Cable: " + string.Join(" + ", cable) + " connected over USB.";
            else if (_s.AndroidNeedsAuthorization) _cable.Text = "Cable: tap Allow on the phone (\"Allow USB debugging?\").";
            else _cable.Text = "Cable: plug in a device. iPhone/iPad: tap Trust" + (_s.CableDaemonReachable ? "" : " (needs iTunes or Apple Devices)")
                               + ". Android: turn on USB debugging once.";
        };
        _timer.Start();

        // Tray icon: the app keeps running (and keeps listening) when the window is closed.
        _tray.Icon = Icon ?? SystemIcons.Application;
        _tray.Text = "Lumen Host";
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Lumen Host", null, (_, _) => ShowFromTray());
        _trayToggle.Click += (_, _) => Toggle();
        menu.Items.Add(_trayToggle);
        var auto = new ToolStripMenuItem("Start with Windows") { Checked = AutoStartOn(), CheckOnClick = true };
        auto.CheckedChanged += (_, _) => SetAutoStart(auto.Checked);
        menu.Items.Add(auto);
        menu.Items.Add("Check for updates", null, async (_, _) => { ShowFromTray(); await OfferUpdateAsync(); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => { _quit = true; Close(); });
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowFromTray();
        _tray.Visible = true;

        FormClosing += (_, e) =>
        {
            if (!_quit && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                if (!_balloonShown)
                {
                    _balloonShown = true;
                    _tray.ShowBalloonTip(3000, "Lumen Host is still running", "Use the tray icon to open it again or quit.", ToolTipIcon.Info);
                }
                return;
            }
            _timer.Stop();
            _tray.Visible = false; _tray.Dispose();
            _s.Dispose();
            _platform.ReleaseVirtualDisplay();
        };
    }

    private void SubmitPair()
    {
        var r = _request;
        if (r == null) return;
        bool ok = r.Submit(_pairInput.Text);
        _pairInfo.Text = ok ? "Paired." : "Wrong code. Start pairing again on the device.";
    }

    private void UpdatePaired()
    {
        if (_request == null) _pairInfo.Text = $"Paired devices: {_s.Paired.All().Count}. A new device shows a 5-digit code when it connects.";
    }

    private void ShowMode(bool extended)
    {
        _mirror.Font = new Font(_mirror.Font, extended ? FontStyle.Regular : FontStyle.Bold);
        _extend.Font = new Font(_extend.Font, extended ? FontStyle.Bold : FontStyle.Regular);
        _modeNote.Text = "";
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
            _trayToggle.Text = "Start";
            _platform.ReleaseVirtualDisplay();
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
                _trayToggle.Text = "Stop";
                _display.Enabled = _encoder.Enabled = false;
            }
            catch (Exception ex)
            {
                _status.Text = "Could not start: " + ex.Message;
            }
        }
        UpdateDot();
    }

    private void UpdateDot()
    {
        _dot.BackColor = _connected ? Color.SeaGreen : (_running ? Color.Orange : Color.Gray);
        _tray.Text = _connected ? "Lumen Host - connected" : (_running ? "Lumen Host - waiting for a device" : "Lumen Host - stopped");
    }

    // MARK: Auto-update

    private async Task OfferUpdateAsync()
    {
        var u = await Updater.CheckAsync();
        if (u == null || IsDisposed) return;
        using var dlg = new Form
        {
            Text = "Lumen Host update", FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(16), Font = Font
        };
        var msg = new Label { AutoSize = true, MaximumSize = new Size(360, 0), Text = $"Would you like to update to the latest version?\n\nVersion {u.Latest} is available (you have {Updater.Current})." };
        var update = new Button { Text = "Update now", DialogResult = DialogResult.Yes, AutoSize = true };
        var later = new Button { Text = "Remind me later", DialogResult = DialogResult.Retry, AutoSize = true };
        var skip = new Button { Text = "Skip", DialogResult = DialogResult.Ignore, AutoSize = true };
        var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 12, 0, 0) };
        row.Controls.AddRange(new Control[] { update, later, skip });
        var col = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown };
        col.Controls.AddRange(new Control[] { msg, row });
        dlg.Controls.Add(col);
        dlg.AcceptButton = update; dlg.CancelButton = later;
        var r = dlg.ShowDialog(this);
        if (r == DialogResult.Ignore) { Updater.Skip(u); return; }
        if (r != DialogResult.Yes) { Updater.Later(); return; }

        _status.Text = "Downloading update...";
        try
        {
            string path = await Updater.DownloadAsync(u, "Lumen-Host-Setup.exe", new Progress<double>(p => _status.Text = $"Downloading update... {p:P0}"));
            // The installer closes this app, replaces it and starts it again.
            Process.Start(new ProcessStartInfo(path, "/SILENT /NORESTART /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS") { UseShellExecute = true });
            Application.Exit();
        }
        catch (Exception ex)
        {
            _status.Text = "Update failed: " + ex.Message;
            HostLog.Write("update failed: " + ex);
        }
    }
}
