using System.Runtime.InteropServices;
using System.Text;

namespace Lumen;

/// Virtual keyboard / mouse / touchscreen through /dev/uinput. Works on X11 and Wayland because the compositor sees
/// them as real hardware. Needs write access to /dev/uinput (the .deb / Arch package installs a udev rule for it).
internal sealed class UInputInjector : IDisposable
{
    [DllImport("libc", SetLastError = true)] private static extern int open([MarshalAs(UnmanagedType.LPStr)] string path, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int close(int fd);
    [DllImport("libc", SetLastError = true)] private static extern nint write(int fd, byte[] buf, nint count);
    [DllImport("libc", SetLastError = true, EntryPoint = "ioctl")] private static extern int IoctlInt(int fd, ulong request, int value);
    [DllImport("libc", SetLastError = true, EntryPoint = "ioctl")] private static extern int IoctlNone(int fd, ulong request);
    [DllImport("libc", SetLastError = true, EntryPoint = "ioctl")] private static extern int IoctlSetup(int fd, ulong request, ref UinputSetup s);
    [DllImport("libc", SetLastError = true, EntryPoint = "ioctl")] private static extern int IoctlAbs(int fd, ulong request, ref UinputAbsSetup s);

    private const int O_WRONLY = 1, O_NONBLOCK = 0x800;
    private const ulong UI_DEV_CREATE = 0x5501, UI_DEV_DESTROY = 0x5502, UI_DEV_SETUP = 0x405c5503, UI_ABS_SETUP = 0x401c5504,
        UI_SET_EVBIT = 0x40045564, UI_SET_KEYBIT = 0x40045565, UI_SET_RELBIT = 0x40045566, UI_SET_ABSBIT = 0x40045567, UI_SET_PROPBIT = 0x4004556e;
    private const ushort EV_SYN = 0, EV_KEY = 1, EV_REL = 2, EV_ABS = 3;
    private const ushort REL_X = 0, REL_Y = 1, REL_HWHEEL = 6, REL_WHEEL = 8;
    private const ushort ABS_X = 0, ABS_Y = 1, ABS_MT_SLOT = 0x2f, ABS_MT_POSITION_X = 0x35, ABS_MT_POSITION_Y = 0x36, ABS_MT_TRACKING_ID = 0x39;
    private const ushort BTN_LEFT = 0x110, BTN_RIGHT = 0x111, BTN_MIDDLE = 0x112, BTN_TOUCH = 0x14a;

    [StructLayout(LayoutKind.Sequential)] private struct InputId { public ushort bustype, vendor, product, version; }
    [StructLayout(LayoutKind.Sequential)] private struct UinputSetup { public InputId id; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 80)] public byte[] name; public uint ff_effects_max; }
    [StructLayout(LayoutKind.Sequential)] private struct AbsInfo { public int value, minimum, maximum, fuzz, flat, resolution; }
    [StructLayout(LayoutKind.Sequential)] private struct UinputAbsSetup { public ushort code; public AbsInfo absinfo; }

    private sealed class Device
    {
        public int Fd = -1;
        private readonly byte[] _ev = new byte[24];

        public void Emit(ushort type, ushort code, int value)
        {
            if (Fd < 0) return;
            Array.Clear(_ev);
            BitConverter.TryWriteBytes(_ev.AsSpan(16), type);
            BitConverter.TryWriteBytes(_ev.AsSpan(18), code);
            BitConverter.TryWriteBytes(_ev.AsSpan(20), value);
            write(Fd, _ev, 24);
        }
        public void Sync() => Emit(EV_SYN, 0, 0);
    }

    private Device? _keyboard, _mouse, _abs, _touch;
    private readonly object _lock = new();
    private readonly HashSet<ushort> _heldKeys = new();
    private readonly HashSet<int> _heldButtons = new();
    private readonly Dictionary<int, int> _slots = new();     // client touch id -> MT slot
    private int _trackingId;
    private double _scrollY, _scrollX;

    public string? Error { get; private set; }
    public bool TouchAvailable => _touch != null;

    private static Device? Create(string name, ushort[] keys, ushort[] rels, (ushort code, int min, int max)[] abs, ushort[] props, out string? error)
    {
        error = null;
        int fd = open("/dev/uinput", O_WRONLY | O_NONBLOCK);
        if (fd < 0) { error = "cannot open /dev/uinput (errno " + Marshal.GetLastWin32Error() + ")"; return null; }

        void Bit(ulong req, int v) => IoctlInt(fd, req, v);
        if (keys.Length > 0) { Bit(UI_SET_EVBIT, EV_KEY); foreach (var k in keys) Bit(UI_SET_KEYBIT, k); }
        if (rels.Length > 0) { Bit(UI_SET_EVBIT, EV_REL); foreach (var r in rels) Bit(UI_SET_RELBIT, r); }
        if (abs.Length > 0)
        {
            Bit(UI_SET_EVBIT, EV_ABS);
            foreach (var a in abs)
            {
                Bit(UI_SET_ABSBIT, a.code);
                var setup = new UinputAbsSetup { code = a.code, absinfo = new AbsInfo { minimum = a.min, maximum = a.max } };
                IoctlAbs(fd, UI_ABS_SETUP, ref setup);
            }
        }
        foreach (var p in props) Bit(UI_SET_PROPBIT, p);

        var nameBytes = new byte[80];
        Encoding.ASCII.GetBytes(name).CopyTo(nameBytes, 0);
        var s = new UinputSetup { id = new InputId { bustype = 0x06 /* BUS_VIRTUAL */, vendor = 0x4c4d, product = 0x0001, version = 1 }, name = nameBytes };
        if (IoctlSetup(fd, UI_DEV_SETUP, ref s) < 0 || IoctlNone(fd, UI_DEV_CREATE) < 0)
        {
            error = "uinput device setup failed (errno " + Marshal.GetLastWin32Error() + ")";
            close(fd);
            return null;
        }
        Thread.Sleep(80);    // give udev/compositor a moment to pick the device up
        return new Device { Fd = fd };
    }

    /// Creates the virtual devices. Returns false (and sets Error) if /dev/uinput is not usable.
    public bool Open()
    {
        var keys = KeyTable.Values.Distinct().Select(v => (ushort)v).ToArray();
        _keyboard = Create("Lumen Virtual Keyboard", keys, Array.Empty<ushort>(), Array.Empty<(ushort, int, int)>(), Array.Empty<ushort>(), out var e1);
        if (_keyboard == null) { Error = e1; return false; }

        _mouse = Create("Lumen Virtual Mouse", new ushort[] { BTN_LEFT, BTN_RIGHT, BTN_MIDDLE },
                        new ushort[] { REL_X, REL_Y, REL_WHEEL, REL_HWHEEL }, Array.Empty<(ushort, int, int)>(), Array.Empty<ushort>(), out _);
        _abs = Create("Lumen Virtual Pointer", new ushort[] { BTN_LEFT, BTN_RIGHT, BTN_MIDDLE }, Array.Empty<ushort>(),
                      new (ushort, int, int)[] { (ABS_X, 0, 65535), (ABS_Y, 0, 65535) }, Array.Empty<ushort>(), out _);
        _touch = Create("Lumen Virtual Touchscreen", new ushort[] { BTN_TOUCH }, Array.Empty<ushort>(),
                        new (ushort, int, int)[] { (ABS_X, 0, 65535), (ABS_Y, 0, 65535), (ABS_MT_SLOT, 0, 9), (ABS_MT_TRACKING_ID, 0, 65535),
                                                   (ABS_MT_POSITION_X, 0, 65535), (ABS_MT_POSITION_Y, 0, 65535) },
                        new ushort[] { 0x01 /* INPUT_PROP_DIRECT */ }, out _);
        return true;
    }

    public void Handle(InputEvent e)
    {
        lock (_lock)
        {
            switch (e.Kind)
            {
                case InputKind.MouseRel:
                    if (_mouse == null) return;
                    if (e.A != 0) _mouse.Emit(EV_REL, REL_X, e.A);
                    if (e.B != 0) _mouse.Emit(EV_REL, REL_Y, e.B);
                    _mouse.Sync();
                    break;
                case InputKind.MouseAbs:
                    if (_abs == null) return;
                    _abs.Emit(EV_ABS, ABS_X, e.A);
                    _abs.Emit(EV_ABS, ABS_Y, e.B);
                    _abs.Sync();
                    break;
                case InputKind.Button:
                    Button(e.A, e.B != 0);
                    break;
                case InputKind.Scroll:
                    Scroll(e.A, e.B);
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

    public void ReleaseAll()
    {
        lock (_lock)
        {
            foreach (var b in _heldButtons.ToArray()) Button(b, false);
            foreach (var k in _heldKeys.ToArray()) Key(k, false);
            foreach (var id in _slots.Keys.ToArray()) Touch(id, 3, 0, 0);
        }
    }

    private void Button(int index, bool down)
    {
        if (down) _heldButtons.Add(index); else _heldButtons.Remove(index);
        ushort code = index switch { 0 => BTN_LEFT, 1 => BTN_RIGHT, _ => BTN_MIDDLE };
        if (_mouse != null) { _mouse.Emit(EV_KEY, code, down ? 1 : 0); _mouse.Sync(); }
        if (_abs != null) { _abs.Emit(EV_KEY, code, down ? 1 : 0); _abs.Sync(); }
    }

    private void Scroll(int dx, int dy)
    {
        if (_mouse == null) return;
        _scrollY += dy / 24.0; _scrollX += dx / 24.0;
        int ny = (int)Math.Truncate(_scrollY), nx = (int)Math.Truncate(_scrollX);
        _scrollY -= ny; _scrollX -= nx;
        if (ny != 0) _mouse.Emit(EV_REL, REL_WHEEL, ny);
        if (nx != 0) _mouse.Emit(EV_REL, REL_HWHEEL, nx);
        if (ny != 0 || nx != 0) _mouse.Sync();
    }

    private void Key(ushort macCode, bool down)
    {
        if (_keyboard == null || !KeyTable.TryGetValue(macCode, out var code)) return;
        if (down) _heldKeys.Add(macCode); else _heldKeys.Remove(macCode);
        _keyboard.Emit(EV_KEY, (ushort)code, down ? 1 : 0);
        _keyboard.Sync();
    }

    /// phase: 0 down, 1 move, 2 up, 3 cancel; x/y 0..65535
    private void Touch(int id, int phase, int x, int y)
    {
        var t = _touch;
        if (t == null) return;
        switch (phase)
        {
            case 0:
            {
                int slot = Enumerable.Range(0, 10).FirstOrDefault(s => !_slots.ContainsValue(s), -1);
                if (slot < 0) return;
                bool first = _slots.Count == 0;
                _slots[id] = slot;
                t.Emit(EV_ABS, ABS_MT_SLOT, slot);
                t.Emit(EV_ABS, ABS_MT_TRACKING_ID, (++_trackingId) & 0xFFFF);
                t.Emit(EV_ABS, ABS_MT_POSITION_X, x);
                t.Emit(EV_ABS, ABS_MT_POSITION_Y, y);
                if (first) { t.Emit(EV_KEY, BTN_TOUCH, 1); t.Emit(EV_ABS, ABS_X, x); t.Emit(EV_ABS, ABS_Y, y); }
                t.Sync();
                break;
            }
            case 1:
                if (!_slots.TryGetValue(id, out var s1)) return;
                t.Emit(EV_ABS, ABS_MT_SLOT, s1);
                t.Emit(EV_ABS, ABS_MT_POSITION_X, x);
                t.Emit(EV_ABS, ABS_MT_POSITION_Y, y);
                t.Emit(EV_ABS, ABS_X, x);
                t.Emit(EV_ABS, ABS_Y, y);
                t.Sync();
                break;
            default:
                if (!_slots.TryGetValue(id, out var s2)) return;
                t.Emit(EV_ABS, ABS_MT_SLOT, s2);
                t.Emit(EV_ABS, ABS_MT_TRACKING_ID, -1);
                _slots.Remove(id);
                if (_slots.Count == 0) t.Emit(EV_KEY, BTN_TOUCH, 0);
                t.Sync();
                break;
        }
    }

    public void Dispose()
    {
        foreach (var d in new[] { _keyboard, _mouse, _abs, _touch })
        {
            if (d == null || d.Fd < 0) continue;
            IoctlNone(d.Fd, UI_DEV_DESTROY);
            close(d.Fd);
            d.Fd = -1;
        }
    }

    // macOS virtual key code (what the iOS client sends) -> Linux input event code.
    private static readonly Dictionary<ushort, int> KeyTable = new()
    {
        [0] = 30, [1] = 31, [2] = 32, [3] = 33, [4] = 35, [5] = 34, [6] = 44, [7] = 45, [8] = 46, [9] = 47,
        [11] = 48, [12] = 16, [13] = 17, [14] = 18, [15] = 19, [16] = 21, [17] = 20,
        [18] = 2, [19] = 3, [20] = 4, [21] = 5, [22] = 7, [23] = 6, [24] = 13, [25] = 10, [26] = 8, [27] = 12, [28] = 9, [29] = 11,
        [30] = 27, [31] = 24, [32] = 22, [33] = 26, [34] = 23, [35] = 25, [36] = 28, [37] = 38, [38] = 36, [39] = 40,
        [40] = 37, [41] = 39, [42] = 43, [43] = 51, [44] = 53, [45] = 49, [46] = 50, [47] = 52,
        [48] = 15, [49] = 57, [50] = 41, [51] = 14, [53] = 1,
        [54] = 126, [55] = 125, [56] = 42, [57] = 58, [58] = 56, [59] = 29, [60] = 54, [61] = 100, [62] = 97,
        [65] = 83, [67] = 55, [69] = 78, [75] = 98, [76] = 96, [78] = 74,
        [82] = 82, [83] = 79, [84] = 80, [85] = 81, [86] = 75, [87] = 76, [88] = 77, [89] = 71, [91] = 72, [92] = 73,
        [96] = 63, [97] = 64, [98] = 65, [99] = 61, [100] = 66, [101] = 67, [103] = 87, [109] = 68, [111] = 88,
        [114] = 110, [115] = 102, [116] = 104, [117] = 111, [118] = 62, [119] = 107, [120] = 60, [121] = 109, [122] = 59,
        [123] = 105, [124] = 106, [125] = 108, [126] = 103,
    };
}
