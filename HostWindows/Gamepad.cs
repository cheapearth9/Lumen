using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace Lumen;

/// Virtual Xbox 360 controllers (via the ViGEmBus driver, the same one Sunshine uses).
/// If the driver is not installed the first controller event logs a hint and everything else keeps working.
public sealed class GamepadBridge : IDisposable
{
    private ViGEmClient? _client;
    private bool _failed;
    private readonly IXbox360Controller?[] _pads = new IXbox360Controller?[4];

    private static readonly (int Bit, Xbox360Button Button)[] Buttons =
    {
        (0x0001, Xbox360Button.Up), (0x0002, Xbox360Button.Down), (0x0004, Xbox360Button.Left), (0x0008, Xbox360Button.Right),
        (0x0010, Xbox360Button.Start), (0x0020, Xbox360Button.Back), (0x0040, Xbox360Button.LeftThumb), (0x0080, Xbox360Button.RightThumb),
        (0x0100, Xbox360Button.LeftShoulder), (0x0200, Xbox360Button.RightShoulder), (0x0400, Xbox360Button.Guide),
        (0x1000, Xbox360Button.A), (0x2000, Xbox360Button.B), (0x4000, Xbox360Button.X), (0x8000, Xbox360Button.Y)
    };

    private IXbox360Controller? Pad(int i, bool create)
    {
        if (i < 0 || i > 3 || _failed) return null;
        if (_pads[i] != null || !create) return _pads[i];
        try
        {
            _client ??= new ViGEmClient();
            var pad = _client.CreateXbox360Controller();
            pad.AutoSubmitReport = false;
            pad.Connect();
            _pads[i] = pad;
            HostLog.Write($"controller {i + 1} connected (virtual Xbox 360)");
        }
        catch (Exception ex)
        {
            _failed = true;
            HostLog.Write("Controller support needs the ViGEmBus driver (https://github.com/nefarius/ViGEmBus/releases). " + ex.Message);
        }
        return _pads[i];
    }

    public void Presence(int index, bool present)
    {
        if (present) { Pad(index, true); return; }
        if (index < 0 || index > 3 || _pads[index] is not { } p) return;
        try { p.Disconnect(); } catch { }
        _pads[index] = null;
        HostLog.Write($"controller {index + 1} disconnected");
    }

    public void Update(InputEvent e)
    {
        if (Pad(e.A, true) is not { } p) return;
        int b = e.B;
        foreach (var (bit, btn) in Buttons) p.SetButtonState(btn, (b & bit) != 0);
        p.SetSliderValue(Xbox360Slider.LeftTrigger, (byte)e.C);
        p.SetSliderValue(Xbox360Slider.RightTrigger, (byte)e.D);
        p.SetAxisValue(Xbox360Axis.LeftThumbX, (short)e.E);
        p.SetAxisValue(Xbox360Axis.LeftThumbY, (short)e.F);
        p.SetAxisValue(Xbox360Axis.RightThumbX, (short)e.G);
        p.SetAxisValue(Xbox360Axis.RightThumbY, (short)e.Ry);
        try { p.SubmitReport(); } catch { }
    }

    public void Dispose()
    {
        for (int i = 0; i < 4; i++) Presence(i, false);
        try { _client?.Dispose(); } catch { }
        _client = null;
    }
}
