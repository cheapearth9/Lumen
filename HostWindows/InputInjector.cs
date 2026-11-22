using System.Runtime.InteropServices;

namespace Lumen;

/// Turns client input into real Windows input with SendInput.
/// Keys are sent as hardware scancodes (works in games / DirectInput / raw input);
/// relative mouse motion goes through MOUSEEVENTF_MOVE so games that lock the cursor get proper deltas.
public sealed class InputInjector
{
    private const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    private const uint MOUSEEVENTF_MOVE = 0x0001, LEFTDOWN = 0x0002, LEFTUP = 0x0004, RIGHTDOWN = 0x0008, RIGHTUP = 0x0010,
        MIDDLEDOWN = 0x0020, MIDDLEUP = 0x0040, WHEEL = 0x0800, HWHEEL = 0x1000, VIRTUALDESK = 0x4000, ABSOLUTE = 0x8000;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001, KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_SCANCODE = 0x0008;

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion u; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint n, INPUT[] inputs, int size);

    private static readonly int Size = Marshal.SizeOf<INPUT>();

    // Target monitor (virtual-desktop coordinates, physical pixels).
    private Rectangle _monitor = new(0, 0, 1920, 1080);
    private Rectangle _virtual = new(0, 0, 1920, 1080);

    private readonly HashSet<ushort> _heldKeys = new();
    private readonly HashSet<int> _heldButtons = new();
    private readonly object _lock = new();

    public void Configure(Rectangle monitor, Rectangle virtualScreen)
    {
        lock (_lock) { _monitor = monitor; _virtual = virtualScreen; }
    }

    public void Handle(InputEvent e)
    {
        lock (_lock)
        {
            switch (e.Kind)
            {
                case InputKind.MouseRel:
                    Mouse(MOUSEEVENTF_MOVE, e.A, e.B, 0);
                    break;
                case InputKind.MouseAbs:
                {
                    double px = _monitor.X + e.A / 65535.0 * (_monitor.Width - 1);
                    double py = _monitor.Y + e.B / 65535.0 * (_monitor.Height - 1);
                    int ax = (int)Math.Round((px - _virtual.X) * 65535.0 / Math.Max(1, _virtual.Width - 1));
                    int ay = (int)Math.Round((py - _virtual.Y) * 65535.0 / Math.Max(1, _virtual.Height - 1));
                    Mouse(MOUSEEVENTF_MOVE | ABSOLUTE | VIRTUALDESK, ax, ay, 0);
                    break;
                }
                case InputKind.Button:
                    Button(e.A, e.B != 0);
                    break;
                case InputKind.Scroll:
                    // Client sends pixel-ish deltas; one wheel notch is 120.
                    if (e.B != 0) Mouse(WHEEL, 0, 0, (uint)(e.B * 4));
                    if (e.A != 0) Mouse(HWHEEL, 0, 0, (uint)(e.A * 4));
                    break;
                case InputKind.Key:
                    Key((ushort)e.A, e.B != 0);
                    break;
                case InputKind.Touch:
                    Touch(e.A, e.B, e.C, e.D);
                    break;
            }
        }
    }

    /// Release everything that is still held (client disconnected) so nothing gets stuck.
    public void ReleaseAll()
    {
        lock (_lock)
        {
            foreach (var b in _heldButtons.ToArray()) Button(b, false);
            foreach (var k in _heldKeys.ToArray()) Key(k, false);
            foreach (var id in _contacts.Keys.ToArray()) Touch(id, 3, 0, 0);
        }
    }


    // MARK: Native multi-touch (Windows touch injection: shows the real touch feedback, taps/scroll/pinch are handled by Windows)

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTER_INFO
    {
        public uint pointerType, pointerId, frameId, pointerFlags;
        public IntPtr sourceDevice, hwndTarget;
        public POINT ptPixelLocation, ptHimetricLocation, ptPixelLocationRaw, ptHimetricLocationRaw;
        public uint dwTime, historyCount;
        public int InputData;
        public uint dwKeyStates;
        public ulong PerformanceCount;
        public uint ButtonChangeType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTER_TOUCH_INFO
    {
        public POINTER_INFO pointerInfo;
        public uint touchFlags, touchMask;
        public RECT rcContact, rcContactRaw;
        public uint orientation, pressure;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool InitializeTouchInjection(uint maxCount, uint dwMode);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool InjectTouchInput(uint count, POINTER_TOUCH_INFO[] contacts);

    private static bool _touchInit, _touchOk;
    private readonly Dictionary<int, POINTER_TOUCH_INFO> _contacts = new();

    /// True when Windows lets us inject touch (Windows 8 or newer).
    public static bool TouchAvailable
    {
        get
        {
            if (!_touchInit)
            {
                _touchInit = true;
                try { _touchOk = InitializeTouchInjection(10, 1 /* TOUCH_FEEDBACK_DEFAULT */); } catch { _touchOk = false; }
            }
            return _touchOk;
        }
    }

    private static POINTER_TOUCH_INFO MakeContact(int id, int px, int py, uint flags)
    {
        var c = new POINTER_TOUCH_INFO
        {
            touchMask = 0x7,                       // contact area | orientation | pressure
            orientation = 90,
            pressure = 32000,
            rcContact = new RECT { left = px - 2, top = py - 2, right = px + 2, bottom = py + 2 }
        };
        c.pointerInfo.pointerType = 2;             // PT_TOUCH
        c.pointerInfo.pointerId = (uint)id;
        c.pointerInfo.pointerFlags = flags;
        c.pointerInfo.ptPixelLocation = new POINT { x = px, y = py };
        return c;
    }

    /// phase: 0 down, 1 move, 2 up, 3 cancel; x/y are 0..65535 across the streamed display.
    private void Touch(int id, int phase, int nx, int ny)
    {
        if (!TouchAvailable) return;
        const uint NEW = 0x1, INRANGE = 0x2, INCONTACT = 0x4, FIRSTBUTTON = 0x10, CANCELED = 0x8000, DOWN = 0x10000, UPDATE = 0x20000, UP = 0x40000;

        int px = _monitor.X + (int)Math.Round(nx / 65535.0 * (_monitor.Width - 1));
        int py = _monitor.Y + (int)Math.Round(ny / 65535.0 * (_monitor.Height - 1));

        var frame = new List<POINTER_TOUCH_INFO>();
        foreach (var key in _contacts.Keys.ToArray())
        {
            if (key == id) continue;
            var other = _contacts[key];
            other.pointerInfo.pointerFlags = INRANGE | INCONTACT | FIRSTBUTTON | UPDATE;
            _contacts[key] = other;
            frame.Add(other);
        }

        POINTER_TOUCH_INFO me;
        switch (phase)
        {
            case 0: me = MakeContact(id, px, py, INRANGE | INCONTACT | FIRSTBUTTON | NEW | DOWN); break;
            case 1:
                if (!_contacts.ContainsKey(id)) return;
                me = MakeContact(id, px, py, INRANGE | INCONTACT | FIRSTBUTTON | UPDATE); break;
            default:
                if (!_contacts.TryGetValue(id, out var last)) return;
                me = MakeContact(id, last.pointerInfo.ptPixelLocation.x, last.pointerInfo.ptPixelLocation.y, phase == 3 ? (UP | CANCELED) : UP);
                break;
        }
        frame.Add(me);
        InjectTouchInput((uint)frame.Count, frame.ToArray());

        if (phase >= 2) _contacts.Remove(id); else _contacts[id] = me;
    }

    private void Button(int index, bool down)
    {
        if (down) _heldButtons.Add(index); else _heldButtons.Remove(index);
        uint flag = index switch
        {
            0 => down ? LEFTDOWN : LEFTUP,
            1 => down ? RIGHTDOWN : RIGHTUP,
            _ => down ? MIDDLEDOWN : MIDDLEUP
        };
        Mouse(flag, 0, 0, 0);
    }

    private static void Mouse(uint flags, int dx, int dy, uint data)
    {
        var i = new INPUT { type = INPUT_MOUSE };
        i.u.mi = new MOUSEINPUT { dx = dx, dy = dy, mouseData = data, dwFlags = flags };
        SendInput(1, new[] { i }, Size);
    }

    private void Key(ushort macCode, bool down)
    {
        if (!KeyTable.TryGetValue(macCode, out var k)) return;
        if (down) _heldKeys.Add(macCode); else _heldKeys.Remove(macCode);
        uint flags = KEYEVENTF_SCANCODE | (k.Extended ? KEYEVENTF_EXTENDEDKEY : 0) | (down ? 0 : KEYEVENTF_KEYUP);
        var i = new INPUT { type = INPUT_KEYBOARD };
        i.u.ki = new KEYBDINPUT { wVk = 0, wScan = k.Scan, dwFlags = flags };
        SendInput(1, new[] { i }, Size);
    }

    // macOS virtual key code (what the iOS client sends) -> Windows scancode (set 1).
    private static readonly Dictionary<ushort, (ushort Scan, bool Extended)> KeyTable = new()
    {
        [0] = (0x1E, false), [1] = (0x1F, false), [2] = (0x20, false), [3] = (0x21, false), [4] = (0x23, false),
        [5] = (0x22, false), [6] = (0x2C, false), [7] = (0x2D, false), [8] = (0x2E, false), [9] = (0x2F, false),
        [11] = (0x30, false), [12] = (0x10, false), [13] = (0x11, false), [14] = (0x12, false), [15] = (0x13, false),
        [16] = (0x15, false), [17] = (0x14, false),
        [18] = (0x02, false), [19] = (0x03, false), [20] = (0x04, false), [21] = (0x05, false), [22] = (0x07, false),
        [23] = (0x06, false), [24] = (0x0D, false), [25] = (0x0A, false), [26] = (0x08, false), [27] = (0x0C, false),
        [28] = (0x09, false), [29] = (0x0B, false),
        [30] = (0x1B, false), [31] = (0x18, false), [32] = (0x16, false), [33] = (0x1A, false), [34] = (0x17, false),
        [35] = (0x19, false), [36] = (0x1C, false), [37] = (0x26, false), [38] = (0x24, false), [39] = (0x28, false),
        [40] = (0x25, false), [41] = (0x27, false), [42] = (0x2B, false), [43] = (0x33, false), [44] = (0x35, false),
        [45] = (0x31, false), [46] = (0x32, false), [47] = (0x34, false),
        [48] = (0x0F, false), [49] = (0x39, false), [50] = (0x29, false), [51] = (0x0E, false), [53] = (0x01, false),
        [54] = (0x5C, true),  [55] = (0x5B, true),  [56] = (0x2A, false), [57] = (0x3A, false), [58] = (0x38, false),
        [59] = (0x1D, false), [60] = (0x36, false), [61] = (0x38, true),  [62] = (0x1D, true),
        [65] = (0x53, false), [67] = (0x37, false), [69] = (0x4E, false), [75] = (0x35, true),  [76] = (0x1C, true),
        [78] = (0x4A, false),
        [82] = (0x52, false), [83] = (0x4F, false), [84] = (0x50, false), [85] = (0x51, false), [86] = (0x4B, false),
        [87] = (0x4C, false), [88] = (0x4D, false), [89] = (0x47, false), [91] = (0x48, false), [92] = (0x49, false),
        [96] = (0x3F, false), [97] = (0x40, false), [98] = (0x41, false), [99] = (0x3D, false), [100] = (0x42, false),
        [101] = (0x43, false), [103] = (0x57, false), [109] = (0x44, false), [111] = (0x58, false),
        [114] = (0x52, true), [115] = (0x47, true), [116] = (0x49, true), [117] = (0x53, true), [118] = (0x3E, false),
        [119] = (0x4F, true), [120] = (0x3C, false), [121] = (0x51, true), [122] = (0x3B, false),
        [123] = (0x4B, true), [124] = (0x4D, true), [125] = (0x50, true), [126] = (0x48, true),
    };
}
