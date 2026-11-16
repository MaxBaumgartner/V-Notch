using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using VNotch.Models;

namespace VNotch.Services;

internal static partial class MonitorSelection
{
    internal sealed record Choice(string Id, string Name, int Index, Screen? Screen)
    {
        public override string ToString() => Name;
    }

    public static Choice[] GetChoices() => Screen.AllScreens.Select((screen, index) =>
        new Choice(GetId(screen), Loc.Get("settings.display.name", index + 1)
            + (screen.Primary ? Loc.Get("settings.display.primary") : "")
            + $" ({screen.Bounds.Width} × {screen.Bounds.Height})", index, screen)).ToArray();

    public static Screen Resolve(NotchSettings settings) => Current(Pick(settings));

    private static Screen Pick(NotchSettings settings)
    {
        var screens = Screen.AllScreens;
        if (screens.Length == 0)
            return Screen.PrimaryScreen!;

        if (string.IsNullOrEmpty(settings.MonitorDeviceId))
        {
            if ((uint)settings.MonitorIndex < (uint)screens.Length)
                return screens[settings.MonitorIndex];
        }
        else
        {
            foreach (var screen in screens)
            {
                if (string.Equals(GetId(screen), settings.MonitorDeviceId, StringComparison.OrdinalIgnoreCase))
                    return screen;
            }
        }

        return Screen.PrimaryScreen ?? screens[0];
    }

    // WinForms keeps Screen.AllScreens until its SystemEvents thread hears of a change. After sleep or a
    // re-plugged monitor that lags behind our WM_DISPLAYCHANGE, and the notch got centred on the old
    // bounds (2026-11-12: x=1210, the middle of a 3440 screen, on a 2560 one). Ask Windows afresh.
    private static Screen Current(Screen cached)
    {
        string name = cached.DeviceName;
        RECT? found = null;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(monitor, ref info)
                && string.Equals(info.szDevice, name, StringComparison.OrdinalIgnoreCase))
            {
                found = info.rcMonitor;
                return false;
            }
            return true;
        }, IntPtr.Zero);

        if (found is not { } r || r.Right <= r.Left || r.Bottom <= r.Top)
            return cached;
        var fresh = Screen.FromPoint(new System.Drawing.Point((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2));
        return string.Equals(fresh.DeviceName, name, StringComparison.OrdinalIgnoreCase) ? fresh : cached;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

#pragma warning disable SYSLIB1054 // ByValTStr struct and delegate callback need classic marshalling
    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);
#pragma warning restore SYSLIB1054

    public static double GetScale(Screen screen)
    {
        var bounds = screen.Bounds;
        vn_monitor_get_scale(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2, out double scale);
        return scale;
    }

    private static string GetId(Screen screen)
    {
        string devName = screen.DeviceName ?? string.Empty;
        Span<char> buf = stackalloc char[256];
        unsafe
        {
            fixed (char* pDev = devName)
            fixed (char* pBuf = buf)
            {
                int rc = vn_monitor_get_device_id(pDev, devName.Length, pBuf, buf.Length, out int outLen);
                if (rc == 0 && outLen > 0)
                    return new string(buf[..outLen]);
            }
        }
        return devName;
    }

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_monitor_get_scale(
        int center_x, int center_y, out double out_scale);

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_monitor_get_device_id(
        char* dev_name_ptr, int dev_name_len,
        char* out_buf, int out_cap, out int out_len);
}
