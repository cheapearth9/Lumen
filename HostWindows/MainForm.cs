namespace Lumen;

public sealed class MainForm : Form
{
    private readonly HostSession _s = new();
    private readonly ComboBox _display = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 380 };
    private readonly ComboBox _encoder = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 380 };
    private readonly NumericUpDown _output = new() { Minimum = 0, Maximum = 7, Width = 60 };
    private readonly Button _start = new() { Text = "Start", AutoSize = true, Padding = new Padding(16, 4, 16, 4) };
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(400, 0), Text = "Stopped" };
    private readonly Label _stats = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly Label _cable = new() { AutoSize = true, MaximumSize = new Size(400, 0), ForeColor = SystemColors.GrayText };
    private readonly Panel _dot = new() { Width = 12, Height = 12, BackColor = Color.Gray, Margin = new Padding(0, 6, 8, 0) };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private bool _running, _connected;

    public MainForm()
    {
        Text = "Lumen Host";
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f);

        var screens = Screen.AllScreens;
        for (int i = 0; i < screens.Length; i++)
        {
            var sc = screens[i];
            _display.Items.Add($"{i + 1}: {sc.DeviceName.TrimStart('\\', '.')}  {sc.Bounds.Width}×{sc.Bounds.Height}{(sc.Primary ? "  (primary)" : "")}");
        }
        _display.SelectedIndex = Math.Max(0, Array.FindIndex(screens, s => s.Primary));
        _display.SelectedIndexChanged += (_, _) => _output.Value = Math.Min(_display.SelectedIndex, (int)_output.Maximum);
        _output.Value = Math.Min(_display.SelectedIndex, (int)_output.Maximum);

        foreach (var k in Enum.GetValues<EncoderKind>()) _encoder.Items.Add(Ffmpeg.Label(k));
        _encoder.SelectedIndex = 0;

        var title = new Label { Text = "Lumen Host", Font = new Font("Segoe UI", 18f, FontStyle.Bold), AutoSize = true };
        var hint = new Label
        {
            AutoSize = true, MaximumSize = new Size(400, 0), ForeColor = SystemColors.GrayText,
            Text = "To stream an extra screen instead of mirroring, add a virtual display (for example Virtual Display Driver) and pick it above."
        };
        var ffmpegWarn = new Label
        {
            AutoSize = true, MaximumSize = new Size(400, 0), ForeColor = Color.Firebrick, Visible = !Ffmpeg.Bundled,
            Text = "ffmpeg.exe was not found next to LumenHost.exe. Download it from the release zip or put ffmpeg on your PATH."
        };

        var outputRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        outputRow.Controls.Add(new Label { Text = "Capture output index:", AutoSize = true, Margin = new Padding(0, 6, 8, 0) });
        outputRow.Controls.Add(_output);

        var runRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        runRow.Controls.Add(_start);
        runRow.Controls.Add(new Panel { Width = 12 });
        runRow.Controls.Add(_dot);
        runRow.Controls.Add(_status);

        var panel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Padding = new Padding(20)
        };
        panel.Controls.Add(title);
        panel.Controls.Add(new Label { Text = "Display to stream", AutoSize = true, Margin = new Padding(0, 12, 0, 2) });
        panel.Controls.Add(_display);
        panel.Controls.Add(outputRow);
        panel.Controls.Add(new Label { Text = "Video encoder", AutoSize = true, Margin = new Padding(0, 12, 0, 2) });
        panel.Controls.Add(_encoder);
        panel.Controls.Add(hint);
        panel.Controls.Add(ffmpegWarn);
        panel.Controls.Add(new Label { Text = "", AutoSize = true, Height = 8 });
        panel.Controls.Add(runRow);
        panel.Controls.Add(_stats);
        panel.Controls.Add(_cable);
        Controls.Add(panel);

        _s.Status += t => Ui(() => _status.Text = t);
        _s.ClientChanged += c => Ui(() => { _connected = c; UpdateDot(); if (!c) _stats.Text = ""; });
        _start.Click += (_, _) => Toggle();

        _timer.Tick += (_, _) =>
        {
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
            _display.Enabled = _encoder.Enabled = _output.Enabled = true;
            _stats.Text = ""; _cable.Text = "";
        }
        else
        {
            _s.ScreenIndex = Math.Max(0, _display.SelectedIndex);
            _s.OutputIndex = (int)_output.Value;
            _s.Encoder = (EncoderKind)Math.Max(0, _encoder.SelectedIndex);
            try
            {
                _s.Start();
                _running = true;
                _start.Text = "Stop";
                _display.Enabled = _encoder.Enabled = _output.Enabled = false;
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
