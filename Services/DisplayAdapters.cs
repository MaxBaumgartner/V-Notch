using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace VNotch.Services;

/// <summary>Which graphics adapter drives which screen, as Windows lists them (EnumDisplayDevices).</summary>
internal static class DisplayAdapters
{
    /// <summary>The adapter name (e.g. "Intel(R) UHD Graphics") behind a screen's device name, or null if unknown.</summary>
    internal static string? AdapterOf(string? displayDevice)
    {
        if (string.IsNullOrEmpty(displayDevice)) return null;
        foreach (var (device, adapter) in Enumerate())
            if (string.Equals(device, displayDevice, StringComparison.OrdinalIgnoreCase)) return adapter;
        return null;
    }

    /// <summary>The distinct adapter names in the machine, attached to a screen or not.</summary>
    internal static IReadOnlyCollection<string> All()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, adapter) in Enumerate()) names.Add(adapter);
        return names;
    }

    private static IEnumerable<(string Device, string Adapter)> Enumerate()
    {
        var list = new List<(string, string)>();
        try
        {
            for (uint i = 0; ; i++)
            {
                var device = new DisplayDevice { cb = Marshal.SizeOf<DisplayDevice>() };
                if (!EnumDisplayDevices(null, i, ref device, 0)) break;
                if (!string.IsNullOrWhiteSpace(device.DeviceString)) list.Add((device.DeviceName, device.DeviceString.Trim()));
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
        return list;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplayDevicesW")]
    private static extern bool EnumDisplayDevices(string? device, uint index, ref DisplayDevice info, uint flags);
}
