using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Lumen;

/// Client for the SudoVDA virtual display driver (SudoMaker, MIT/CC0; the driver Apollo uses).
/// Creates a virtual monitor with an exact resolution / refresh rate on demand and removes it again.
/// Layouts and IOCTL codes come from SudoVDA's Common/Include/sudovda-ioctl.h.
internal sealed class SudoVda : IDisposable
{
    private static readonly Guid InterfaceGuid = new("e5bcc234-1e0c-418a-a0d4-ef8b7501414d");

    // CTL_CODE(FILE_DEVICE_UNKNOWN = 0x22, function, METHOD_BUFFERED, FILE_ANY_ACCESS)
    private static uint Ctl(uint fn) => (0x22u << 16) | (fn << 2);
    private static readonly uint IoctlAdd = Ctl(0x800), IoctlRemove = Ctl(0x801), IoctlWatchdog = Ctl(0x803),
                                 IoctlPing = Ctl(0x888), IoctlVersion = Ctl(0x8FF);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct AddParams
    {
        public uint Width, Height, RefreshRate;
        public Guid MonitorGuid;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 14)] public byte[] DeviceName;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 14)] public byte[] SerialNumber;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)] private struct AddOut { public uint LuidLow; public int LuidHigh; public uint TargetId; }
    [StructLayout(LayoutKind.Sequential)] private struct WatchdogOut { public uint Timeout, Countdown; }
    [StructLayout(LayoutKind.Sequential)] private struct VersionOut { public byte Major, Minor, Incremental, TestBuild; }

    [DllImport("setupapi.dll", SetLastError = true)] private static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr enumerator, IntPtr parent, uint flags);
    [StructLayout(LayoutKind.Sequential)] private struct SpDeviceInterfaceData { public int Size; public Guid Class; public int Flags; public IntPtr Reserved; }
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid g, uint index, ref SpDeviceInterfaceData data);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SpDeviceInterfaceData data, IntPtr detail, uint size, out uint required, IntPtr info);
    [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, int inSize, IntPtr outBuf, int outSize, out int returned, IntPtr overlapped);

    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint paths, byte[] pathArray, ref uint modes, byte[] modeArray, IntPtr topology);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(byte[] request);

    private SafeFileHandle? _h;
    private Guid _monitor;
    private Thread? _ping;
    private volatile bool _stop;

    public string? DeviceName { get; private set; }   // e.g. \\.\DISPLAY5

    /// True when the driver is installed and answers on its device interface.
    public static bool Available()
    {
        using var v = Open();
        return v != null;
    }

    private static SafeFileHandle? Open()
    {
        var g = InterfaceGuid;
        IntPtr set = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, 0x2 | 0x10);   // DIGCF_PRESENT | DIGCF_DEVICEINTERFACE
        if (set == new IntPtr(-1)) return null;
        try
        {
            var data = new SpDeviceInterfaceData { Size = Marshal.SizeOf<SpDeviceInterfaceData>() };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref data); i++)
            {
                SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out uint need, IntPtr.Zero);
                IntPtr detail = Marshal.AllocHGlobal((int)need);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);        // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref data, detail, need, out _, IntPtr.Zero)) continue;
                    string path = Marshal.PtrToStringUni(detail + 4)!;
                    // GENERIC_READ|GENERIC_WRITE, share RW, OPEN_EXISTING, FILE_FLAG_OVERLAPPED|NO_BUFFERING|WRITE_THROUGH like the reference client
                    var h = CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000 | 0x20000000 | 0x80000000, IntPtr.Zero);
                    if (!h.IsInvalid) return h;
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return null;
    }

    private bool Ioctl(uint code, IntPtr inBuf, int inSize, IntPtr outBuf, int outSize) =>
        _h != null && DeviceIoControl(_h, code, inBuf, inSize, outBuf, outSize, out _, IntPtr.Zero);

    private T? Query<T>(uint code) where T : struct
    {
        IntPtr p = Marshal.AllocHGlobal(Marshal.SizeOf<T>());
        try { return Ioctl(code, IntPtr.Zero, 0, p, Marshal.SizeOf<T>()) ? Marshal.PtrToStructure<T>(p) : null; }
        finally { Marshal.FreeHGlobal(p); }
    }

    /// Creates a virtual monitor. Returns null (and logs why) if the driver is missing or refuses.
    public static SudoVda? Create(int width, int height, int refresh, string name)
    {
        var v = new SudoVda { _h = Open() };
        if (v._h == null) return null;
        try
        {
            var ver = v.Query<VersionOut>(IoctlVersion);
            if (ver is not { } vv || vv.Major != 0 || vv.Minor < 2) { HostLog.Write("SudoVDA protocol not compatible"); v.Dispose(); return null; }

            // Same identity every time (per computer), so Windows remembers this monitor's position, scale and resolution
            // between sessions instead of treating it as a brand-new display on every connect.
            v._monitor = new Guid(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes("lumen-vda-" + Environment.MachineName)));
            byte[] Pad(string s) { var b = new byte[14]; Encoding.ASCII.GetBytes(s.Length > 13 ? s[..13] : s).CopyTo(b, 0); return b; }
            var p = new AddParams
            {
                Width = (uint)width, Height = (uint)height, RefreshRate = (uint)refresh, MonitorGuid = v._monitor,
                DeviceName = Pad(name), SerialNumber = Pad("LUMEN0001")
            };
            IntPtr pin = Marshal.AllocHGlobal(Marshal.SizeOf<AddParams>()), pout = Marshal.AllocHGlobal(Marshal.SizeOf<AddOut>());
            try
            {
                Marshal.StructureToPtr(p, pin, false);
                if (!v.Ioctl(IoctlAdd, pin, Marshal.SizeOf<AddParams>(), pout, Marshal.SizeOf<AddOut>()))
                {
                    HostLog.Write("SudoVDA add display failed: " + Marshal.GetLastWin32Error());
                    v.Dispose(); return null;
                }
                var o = Marshal.PtrToStructure<AddOut>(pout);
                v.StartWatchdog();
                for (int i = 0, wait = 20; i < 40; i++, wait = Math.Min(wait * 2, 320))
                {
                    var n = DisplayNameFor(o);
                    if (n != null) { v.DeviceName = n; break; }
                    Thread.Sleep(wait);
                }
                if (v.DeviceName == null) { HostLog.Write("SudoVDA display did not appear"); v.Dispose(); return null; }
                HostLog.Write($"SudoVDA display {v.DeviceName} {width}x{height}@{refresh}");
                return v;
            }
            finally { Marshal.FreeHGlobal(pin); Marshal.FreeHGlobal(pout); }
        }
        catch (Exception ex) { HostLog.Write("SudoVDA error: " + ex.Message); v.Dispose(); return null; }
    }

    /// The driver removes its displays if it is not pinged within its watchdog timeout (default 3 s).
    private void StartWatchdog()
    {
        var wd = Query<WatchdogOut>(IoctlWatchdog);
        int timeout = wd?.Timeout > 0 ? (int)wd.Value.Timeout : 3;
        _ping = new Thread(() =>
        {
            while (!_stop)
            {
                Ioctl(IoctlPing, IntPtr.Zero, 0, IntPtr.Zero, 0);
                Thread.Sleep(Math.Max(200, timeout * 1000 / 3));
            }
        }) { IsBackground = true, Name = "lumen-vda-ping" };
        _ping.Start();
    }

    private static string? DisplayNameFor(AddOut o)
    {
        if (GetDisplayConfigBufferSizes(2, out uint pc, out uint mc) != 0) return null;       // QDC_ONLY_ACTIVE_PATHS
        var paths = new byte[72 * pc];
        var modes = new byte[64 * mc];
        if (QueryDisplayConfig(2, ref pc, paths, ref mc, modes, IntPtr.Zero) != 0) return null;
        for (int i = 0; i < pc; i++)
        {
            int b = i * 72;
            uint targetId = BitConverter.ToUInt32(paths, b + 28);
            if (targetId != o.TargetId) continue;
            var req = new byte[84];
            BitConverter.GetBytes(1u).CopyTo(req, 0);                  // DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME
            BitConverter.GetBytes(84u).CopyTo(req, 4);
            BitConverter.GetBytes(o.LuidLow).CopyTo(req, 8);
            BitConverter.GetBytes(o.LuidHigh).CopyTo(req, 12);
            Array.Copy(paths, b + 8, req, 16, 4);                       // source id
            if (DisplayConfigGetDeviceInfo(req) != 0) return null;
            return Encoding.Unicode.GetString(req, 20, 64).TrimEnd('\0');
        }
        return null;
    }

    public void Dispose()
    {
        _stop = true;
        if (_h != null && DeviceName != null)
        {
            var g = _monitor.ToByteArray();
            IntPtr p = Marshal.AllocHGlobal(16);
            try { Marshal.Copy(g, 0, p, 16); Ioctl(IoctlRemove, p, 16, IntPtr.Zero, 0); }
            finally { Marshal.FreeHGlobal(p); }
        }
        _h?.Dispose();
        _h = null;
    }
}
