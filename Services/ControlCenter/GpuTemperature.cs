using System;
using System.Runtime.InteropServices;

namespace VNotch.Services.ControlCenter;

/// <summary>
/// The graphics card's temperature as its display driver reports it to Windows (what Task Manager shows), read
/// without admin rights. On a laptop with two GPUs it is the discrete one; the integrated GPU shares the CPU's die and
/// says little of its own.
/// </summary>
internal static partial class GpuTemperature
{
    private const int QueryAdapterType = 15;      // KMTQAITYPE_ADAPTERTYPE
    private const int QueryAdapterPerfData = 62;  // KMTQAITYPE_ADAPTERPERFDATA (WDDM 2.4)
    private const uint SoftwareDevice = 1 << 2, HybridDiscrete = 1 << 4, HybridIntegrated = 1 << 5;
    private static bool _unsupported;

    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterInfo
    {
        public uint Handle;
        public uint LuidLow;
        public int LuidHigh;
        public uint Sources;
        public int PrecisePresentRegions;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EnumAdapters
    {
        public uint Count;
        public IntPtr Adapters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct QueryInfo
    {
        public uint Adapter;
        public int Type;
        public IntPtr Data;
        public uint Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PerfData
    {
        public uint PhysicalAdapterIndex;
        public ulong MemoryFrequency, MaxMemoryFrequency, MaxMemoryFrequencyOC, MemoryBandwidth, PcieBandwidth;
        public uint FanRpm, Power, Temperature; // Temperature in tenths of a degree Celsius
        public byte PowerStateOverride;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CloseAdapter
    {
        public uint Adapter;
    }

    [LibraryImport("gdi32.dll")]
    private static partial int D3DKMTEnumAdapters2(ref EnumAdapters adapters);

    [LibraryImport("gdi32.dll")]
    private static partial int D3DKMTQueryAdapterInfo(ref QueryInfo query);

    [LibraryImport("gdi32.dll")]
    private static partial int D3DKMTCloseAdapter(ref CloseAdapter close);

    /// <summary>Whole degrees Celsius, or null when no GPU reports one (older drivers, a GPU that is asleep).</summary>
    public static int? Read()
    {
        if (_unsupported) return null;
        try { return ReadOnce(); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _unsupported = true;
            return null;
        }
    }

    private static unsafe int? ReadOnce()
    {
        var list = new EnumAdapters();
        if (D3DKMTEnumAdapters2(ref list) != 0 || list.Count == 0) return null;
        var adapters = new AdapterInfo[Math.Min(list.Count, 16u)];
        int? discrete = null, other = null;
        fixed (AdapterInfo* buffer = adapters)
        {
            list.Adapters = (IntPtr)buffer;
            list.Count = (uint)adapters.Length;
            if (D3DKMTEnumAdapters2(ref list) != 0) return null;
            for (int i = 0; i < list.Count; i++)
            {
                uint handle = adapters[i].Handle;
                try
                {
                    uint type = 0;
                    var typeQuery = new QueryInfo { Adapter = handle, Type = QueryAdapterType, Data = (IntPtr)(&type), Size = sizeof(uint) };
                    if (D3DKMTQueryAdapterInfo(ref typeQuery) != 0 || (type & SoftwareDevice) != 0) continue;
                    var perf = new PerfData();
                    var perfQuery = new QueryInfo { Adapter = handle, Type = QueryAdapterPerfData, Data = (IntPtr)(&perf), Size = (uint)sizeof(PerfData) };
                    if (D3DKMTQueryAdapterInfo(ref perfQuery) != 0) continue;
                    int? celsius = Celsius(perf.Temperature);
                    if (celsius == null) continue;
                    if ((type & HybridDiscrete) != 0) discrete = Math.Max(discrete ?? 0, celsius.Value);
                    else if ((type & HybridIntegrated) == 0) other = Math.Max(other ?? 0, celsius.Value);
                }
                finally
                {
                    var close = new CloseAdapter { Adapter = handle };
                    D3DKMTCloseAdapter(ref close);
                }
            }
        }
        return discrete ?? other;
    }

    /// <summary>Tenths of a degree to whole degrees; 0 or an absurd value means the driver did not say.</summary>
    internal static int? Celsius(uint tenths) => tenths is > 0 and < 1500 ? (int)Math.Round(tenths / 10.0) : null;
}
