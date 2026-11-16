using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using System.Windows;

namespace VNotch.Services.Displays;

/// <summary>Windows display configuration, with no native changes during enumeration.</summary>
public sealed partial class Win32DisplayApi : IDisplayApi, IDisposable
{
    private const uint AllPaths = 1, ActivePath = 1, InvalidMode = uint.MaxValue;
    private const uint UpdateRegistry = 1, TestMode = 2, SetPrimaryFlag = 0x10, NoReset = 0x10000000;
    private const uint PositionField = 0x20, ModeFields = 0x7c0000;
    private static readonly int[] DpiSteps = [100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500];
    private readonly object _configurationGate = new();
    private readonly RefreshRateMap _refreshRates = new();

    public partial int? GetBrightness(string id);
    public partial bool SetBrightness(string id, int percent);
    public partial void Dispose();

    internal DisplayInfo? ResolveDisplay(string id) => List().FirstOrDefault(d => d.Id == id);

    public IReadOnlyList<DisplayInfo> List()
    {
        try
        {
            if (!TryQuery(out var paths, out var modes)) return [];
            var activeSources = paths.Where(IsActive).Select(p => SourceKey(p)).ToHashSet();
            var displays = new List<DisplayInfo>();
            foreach (var group in paths.Where(p => p.Target.Available != 0 || IsActive(p))
                .GroupBy(p => TargetKey(p)))
            {
                var path = group.OrderByDescending(IsActive)
                    .ThenBy(p => activeSources.Contains(SourceKey(p))).First();
                var active = IsActive(path);
                var target = TargetName(path);
                var device = SourceName(path);
                var current = NewDevMode();
                bool hasCurrent = active && device.Length != 0 && EnumDisplaySettings(device, -1, ref current);
                var source = path.Source.ModeIndex < modes.Length
                    && modes[path.Source.ModeIndex].Type == 1
                    ? modes[path.Source.ModeIndex].Source : default;
                var preferred = new TargetPreferred { Header = Header(3, path.Target.Adapter, path.Target.Id,
                    Marshal.SizeOf<TargetPreferred>()) };
                bool hasPreferred = GetPreferredMode(ref preferred) == 0;
                int width = active ? (hasCurrent ? (int)current.Width : (int)source.Width)
                    : hasPreferred ? (int)preferred.Width : 0;
                int height = active ? (hasCurrent ? (int)current.Height : (int)source.Height)
                    : hasPreferred ? (int)preferred.Height : 0;
                int hz = active ? Refresh(path.Target.Refresh)
                    : hasPreferred ? Refresh(preferred.Signal.VSync) : 0;
                if (hz == 0 && hasCurrent) hz = (int)current.Frequency;
                int x = hasCurrent ? current.X : active ? source.X : 0;
                int y = hasCurrent ? current.Y : active ? source.Y : 0;
                var mode = new DisplayMode(Math.Max(0, width), Math.Max(0, height), hz);
                var id = DisplayId(path, target);
                if (hasCurrent) _refreshRates.Observe(id, ToMode(current), hz);
                // An inactive target can borrow an active source in QDC_ALL_PATHS. Its modes
                // belong to that other monitor, so only advertise its own preferred mode.
                bool ownSource = active || !activeSources.Contains(SourceKey(path));
                var choices = ownSource ? EnumerateModes(device)
                    .Select(native => _refreshRates.Normalize(id, ToMode(native))).ToList() : [];
                if (mode.Width > 0 && mode.Height > 0 && mode.RefreshHz > 0) choices.Add(mode);
                var color = new AdvancedColor { Header = Header(9, path.Target.Adapter, path.Target.Id,
                    Marshal.SizeOf<AdvancedColor>()) };
                bool hasColor = GetAdvancedColor(ref color) == 0;
                var dpi = GetDpi(path);
                var (zoom, steps) = DpiChoices(dpi);
                displays.Add(new DisplayInfo(id,
                    string.IsNullOrWhiteSpace(target.FriendlyName) ? device : target.FriendlyName,
                    device, active, active && x == 0 && y == 0,
                    new Int32Rect(x, y, mode.Width, mode.Height), mode,
                    choices.Distinct().OrderBy(m => m.Width).ThenBy(m => m.Height).ThenBy(m => m.RefreshHz).ToArray(),
                    hasColor && (color.Flags & 1) != 0, hasColor && (color.Flags & 2) != 0,
                    zoom, active ? steps : [],
                    path.Target.OutputTechnology is 0x80000000 or 6 or 11 or 13));
            }
            return displays.OrderByDescending(d => d.Primary).ThenByDescending(d => d.Active)
                .ThenBy(d => d.Bounds.X).ThenBy(d => d.Bounds.Y).ToArray();
        }
        catch { return []; }
    }

    public bool SetModes(IReadOnlyDictionary<string, DisplayMode> modes) => TryChange(() =>
    {
        if (modes is null) return false;
        var displays = List();
        var changes = new List<ModeUpdate>();
        foreach (var (id, desired) in modes)
        {
            var display = displays.FirstOrDefault(d => d.Id == id && d.Active);
            if (display is null || desired is null || desired.Width <= 0 || desired.Height <= 0
                || desired.RefreshHz <= 0 || !TryCurrent(display.DeviceName, out var current)) return false;
            var candidates = EnumerateModes(display.DeviceName)
                .Where(m => _refreshRates.Normalize(id, ToMode(m)) == desired).ToArray();
            if (candidates.Length == 0) return false;
            var candidate = candidates.FirstOrDefault(m => m.Orientation == current.Orientation);
            if (candidate.Size == 0) return false;
            current.Width = candidate.Width;
            current.Height = candidate.Height;
            current.Frequency = candidate.Frequency;
            current.BitsPerPixel = candidate.BitsPerPixel;
            current.DisplayFlags = candidate.DisplayFlags;
            current.Fields = ModeFields;
            changes.Add(new(display.DeviceName, current, 0));
        }
        return ApplyModes(changes, displays);
    });

    public bool SetPositions(IReadOnlyDictionary<string, Int32Point> topLefts) => TryChange(() =>
    {
        if (topLefts is null) return false;
        var displays = List();
        var changes = new List<ModeUpdate>();
        foreach (var (id, point) in topLefts)
        {
            var display = displays.FirstOrDefault(d => d.Id == id && d.Active);
            if (display is null || (display.Primary && point != new Int32Point(0, 0))
                || !FitsDesktop(point, display.Mode)
                || !TryCurrent(display.DeviceName, out var current)) return false;
            current.X = point.X;
            current.Y = point.Y;
            current.Fields = PositionField;
            changes.Add(new(display.DeviceName, current, 0));
        }
        return ApplyModes(changes, displays);
    });

    public bool SetPrimary(string id) => TryChange(() =>
    {
        var displays = List();
        var primary = displays.FirstOrDefault(d => d.Id == id && d.Active);
        if (primary is null) return false;
        if (primary.Primary) return true;
        var changes = new List<ModeUpdate>();
        foreach (var display in displays.Where(d => d.Active).DistinctBy(d => d.DeviceName))
        {
            long x = (long)display.Bounds.X - primary.Bounds.X;
            long y = (long)display.Bounds.Y - primary.Bounds.Y;
            if (x < int.MinValue || x > int.MaxValue || y < int.MinValue || y > int.MaxValue
                || !FitsDesktop(new((int)x, (int)y), display.Mode)
                || !TryCurrent(display.DeviceName, out var current)) return false;
            current.X = (int)x;
            current.Y = (int)y;
            current.Fields = PositionField;
            changes.Add(new(display.DeviceName, current,
                display.DeviceName == primary.DeviceName ? SetPrimaryFlag : 0));
        }
        return ApplyModes(changes, displays);
    });

    public bool SetActive(string id, bool on) => TryChange(() =>
    {
        if (!TryQuery(out var paths, out var modes)) return false;
        var matches = paths.Where(p => (p.Target.Available != 0 || IsActive(p))
            && DisplayId(p, TargetName(p)) == id).ToArray();
        if (matches.Length == 0) return false;
        var active = paths.Where(IsActive).ToList();
        bool wasActive = matches.Any(IsActive);
        if (wasActive == on) return true;
        if (!on)
        {
            active.RemoveAll(p => TargetKey(p) == TargetKey(matches[0]));
            if (active.Count == 0) return false;
            var sourceIndexes = active.Select(p => p.Source.ModeIndex).Distinct().ToArray();
            if (sourceIndexes.Any(index => index >= modes.Length || modes[index].Type != 1)) return false;
            if (!sourceIndexes.Any(index => modes[index].Source.X == 0 && modes[index].Source.Y == 0))
            {
                var origin = modes[sourceIndexes[0]].Source;
                foreach (uint index in sourceIndexes)
                {
                    var source = modes[index].Source;
                    long x = (long)source.X - origin.X, y = (long)source.Y - origin.Y;
                    if (x < int.MinValue || x + source.Width > int.MaxValue
                        || y < int.MinValue || y + source.Height > int.MaxValue) return false;
                    modes[index].Source.X = (int)x;
                    modes[index].Source.Y = (int)y;
                }
            }
        }
        else
        {
            var usedSources = active.Select(SourceKey).ToHashSet();
            var candidates = matches.Where(p => !usedSources.Contains(SourceKey(p))).ToArray();
            if (candidates.Length == 0) return false;
            var path = candidates[0];
            var preferred = new TargetPreferred { Header = Header(3, path.Target.Adapter, path.Target.Id,
                Marshal.SizeOf<TargetPreferred>()) };
            if (GetPreferredMode(ref preferred) != 0 || preferred.Width == 0 || preferred.Height == 0
                || preferred.Width > int.MaxValue || preferred.Height > int.MaxValue) return false;
            var sources = active.Where(p => p.Source.ModeIndex < modes.Length)
                .Select(p => modes[p.Source.ModeIndex].Source).ToArray();
            if (sources.Length != active.Count) return false;
            long right = sources.Max(s => (long)s.X + s.Width);
            var neighbour = sources.First(s => (long)s.X + s.Width == right);
            if (right < int.MinValue || right + preferred.Width > int.MaxValue
                || (long)neighbour.Y + preferred.Height > int.MaxValue) return false;
            path.Flags = ActivePath;
            path.Source.ModeIndex = (uint)modes.Length;
            path.Target.ModeIndex = InvalidMode;
            path.Target.Rotation = 1;
            path.Target.Scaling = 128;
            path.Target.Refresh = default;
            path.Target.ScanLineOrdering = 0;
            modes = [.. modes, new ModeInfo { Type = 1, Id = path.Source.Id, Adapter = path.Source.Adapter,
                Source = new SourceMode { Width = preferred.Width, Height = preferred.Height,
                    PixelFormat = 4, X = (int)right, Y = neighbour.Y } }];
            active.Add(path);
        }
        // Keep existing positions and extend at the right edge; Windows chooses the
        // supported target timing for the newly activated monitor.
        for (int i = 0; i < modes.Length; i++)
            if (modes[i].Type == 1 && modes[i].Source.PixelFormat == 5) modes[i].Source.PixelFormat = 4;
        var suppliedModes = new List<ModeInfo>();
        var indexes = new Dictionary<uint, uint>();
        uint SuppliedIndex(uint oldIndex)
        {
            if (oldIndex == InvalidMode) return InvalidMode;
            if (!indexes.TryGetValue(oldIndex, out uint newIndex))
            {
                newIndex = (uint)suppliedModes.Count;
                suppliedModes.Add(modes[oldIndex]);
                indexes.Add(oldIndex, newIndex);
            }
            return newIndex;
        }
        // Supply only referenced modes, retaining shared indexes for clone sources.
        for (int i = 0; i < active.Count; i++)
        {
            var path = active[i];
            path.Source.ModeIndex = SuppliedIndex(path.Source.ModeIndex);
            path.Target.ModeIndex = SuppliedIndex(path.Target.ModeIndex);
            active[i] = path;
        }
        const uint suppliedApply = 0x20 | 0x80 | 0x200 | 0x400;
        return SetDisplayConfig((uint)active.Count, active.ToArray(), (uint)suppliedModes.Count,
            suppliedModes.ToArray(), suppliedApply) == 0;
    });

    public bool SetHdr(string id, bool on) => TryChange(() =>
    {
        if (!TryFindPath(id, out var path) || !IsActive(path)) return false;
        var info = new AdvancedColor { Header = Header(9, path.Target.Adapter, path.Target.Id,
            Marshal.SizeOf<AdvancedColor>()) };
        if (GetAdvancedColor(ref info) != 0 || (info.Flags & 1) == 0 || (info.Flags & 8) != 0) return false;
        var state = new DeviceValue { Header = Header(10, path.Target.Adapter, path.Target.Id,
            Marshal.SizeOf<DeviceValue>()), Value = on ? 1 : 0 };
        return SetDeviceValue(ref state) == 0;
    });

    public bool SetZoom(string id, int percent) => TryChange(() =>
    {
        if (!TryFindPath(id, out var path) || !IsActive(path)) return false;
        var dpi = GetDpi(path);
        if (dpi is not { } scale) return false;
        var (_, choices) = DpiChoices(dpi);
        if (!choices.Contains(percent)) return false;
        int index = Array.IndexOf(DpiSteps, percent);
        var value = new DeviceValue { Header = Header(-4, path.Source.Adapter, path.Source.Id,
            Marshal.SizeOf<DeviceValue>()), Value = index + scale.Min };
        return SetDeviceValue(ref value) == 0;
    });

    private bool TryChange(Func<bool> change)
    {
        lock (_configurationGate)
        {
            try { return change(); }
            catch { return false; }
        }
    }

    private static bool FitsDesktop(Int32Point p, DisplayMode mode) =>
        (long)p.X + mode.Width <= int.MaxValue && (long)p.Y + mode.Height <= int.MaxValue;

    private readonly record struct ModeUpdate(string Device, DevMode Mode, uint Flags);

    private static bool ApplyModes(List<ModeUpdate> changes, IReadOnlyList<DisplayInfo> displays)
    {
        if (changes.Count == 0) return true;
        // Mirrored targets share one source; conflicting requests must fail before staging.
        if (changes.GroupBy(c => c.Device).Any(g => g.Select(c => c.Mode).Distinct().Count() != 1)) return false;
        changes = changes.DistinctBy(c => c.Device).ToList();
        var registry = new List<ModeUpdate>();
        var current = new List<ModeUpdate>();
        foreach (var change in changes)
        {
            if (!TryCurrent(change.Device, out var live)) return false;
            var saved = NewDevMode();
            if (!EnumDisplaySettings(change.Device, -2, ref saved)) return false;
            uint primary = displays.Any(d => d.DeviceName == change.Device && d.Primary) ? SetPrimaryFlag : 0;
            current.Add(new(change.Device, live, primary));
            registry.Add(new(change.Device, saved, primary));
            var test = change.Mode;
            if (ChangeDisplaySettings(change.Device, ref test, 0, TestMode | change.Flags, 0) != 0) return false;
        }
        bool success = false, applied = false;
        try
        {
            foreach (var change in changes)
            {
                var mode = change.Mode;
                if (ChangeDisplaySettings(change.Device, ref mode, 0, UpdateRegistry | NoReset | change.Flags, 0) != 0)
                    return false;
            }
            applied = true;
            success = ApplyStagedSettings(null, 0, 0, 0, 0) == 0;
            return success;
        }
        finally
        {
            if (!success)
            {
                // Restore live settings after a failed commit, then undo every registry
                // staging write so an unrelated later change cannot apply this request.
                if (applied)
                {
                    RestoreModes(current);
                    ApplyStagedSettings(null, 0, 0, 0, 0);
                }
                RestoreModes(registry);
            }
        }
    }

    private static void RestoreModes(IEnumerable<ModeUpdate> snapshots)
    {
        foreach (var snapshot in snapshots.OrderBy(s => s.Flags != 0))
        {
            try
            {
                var mode = snapshot.Mode;
                ChangeDisplaySettings(snapshot.Device, ref mode, 0, UpdateRegistry | NoReset | snapshot.Flags, 0);
            }
            catch { /* A display can disappear while Windows is applying the transaction. */ }
        }
    }

    private static bool TryQuery(out PathInfo[] paths, out ModeInfo[] modes)
    {
        paths = [];
        modes = [];
        for (int retry = 0; retry < 4; retry++)
        {
            if (GetDisplayConfigBufferSizes(AllPaths, out uint pathCount, out uint modeCount) != 0) return false;
            var pathBuffer = new PathInfo[pathCount];
            var modeBuffer = new ModeInfo[modeCount];
            int result = QueryDisplayConfig(AllPaths, ref pathCount, pathBuffer, ref modeCount, modeBuffer, 0);
            if (result == 122) continue;
            if (result != 0) return false;
            paths = pathBuffer.Take((int)pathCount).ToArray();
            modes = modeBuffer.Take((int)modeCount).ToArray();
            return true;
        }
        return false;
    }

    private static bool TryFindPath(string id, out PathInfo path)
    {
        path = default;
        if (!TryQuery(out var paths, out _)) return false;
        foreach (var candidate in paths.OrderByDescending(IsActive))
        {
            if ((candidate.Target.Available != 0 || IsActive(candidate))
                && DisplayId(candidate, TargetName(candidate)) == id)
            { path = candidate; return true; }
        }
        return false;
    }

    private static bool IsActive(PathInfo path) => (path.Flags & ActivePath) != 0;
    private static (Luid, uint) SourceKey(PathInfo path) => (path.Source.Adapter, path.Source.Id);
    private static (Luid, uint) TargetKey(PathInfo path) => (path.Target.Adapter, path.Target.Id);
    private static int Refresh(Rational rate) => rate.Denominator == 0 ? 0
        : ModeChoices.RoundRefreshRate((double)rate.Numerator / rate.Denominator);
    private static string DisplayId(PathInfo path, TargetDeviceName target) =>
        !string.IsNullOrWhiteSpace(target.DevicePath) ? target.DevicePath
            : $"{path.Target.Adapter.High:x8}{path.Target.Adapter.Low:x8}:{path.Target.Id}";

    private static TargetDeviceName TargetName(PathInfo path)
    {
        var name = new TargetDeviceName { Header = Header(2, path.Target.Adapter, path.Target.Id,
            Marshal.SizeOf<TargetDeviceName>()), FriendlyName = "", DevicePath = "" };
        GetTargetName(ref name);
        return name;
    }

    private static string SourceName(PathInfo path)
    {
        var name = new SourceDeviceName { Header = Header(1, path.Source.Adapter, path.Source.Id,
            Marshal.SizeOf<SourceDeviceName>()), DeviceName = "" };
        return GetSourceName(ref name) == 0 ? name.DeviceName : "";
    }

    private static DpiScale? GetDpi(PathInfo path)
    {
        var dpi = new DpiScale { Header = Header(-3, path.Source.Adapter, path.Source.Id, Marshal.SizeOf<DpiScale>()) };
        return GetDpiScale(ref dpi) == 0 ? dpi : null;
    }

    private static (int Percent, IReadOnlyList<int> Steps) DpiChoices(DpiScale? dpi)
    {
        if (dpi is not { } scale || scale.Min > 0 || scale.Max < 0 || scale.Current < scale.Min
            || scale.Current > scale.Max) return (100, []);
        long recommended = -(long)scale.Min;
        long current = recommended + scale.Current;
        long max = recommended + scale.Max;
        if (recommended >= DpiSteps.Length || current < 0 || current >= DpiSteps.Length || max < 0)
            return (100, []);
        return (DpiSteps[(int)current], DpiSteps.Take((int)Math.Min(max + 1, DpiSteps.Length)).ToArray());
    }

    private static DeviceHeader Header(int type, Luid adapter, uint id, int size) =>
        new() { Type = type, Size = (uint)size, Adapter = adapter, Id = id };

    private static DevMode NewDevMode() => new() { Size = (ushort)Marshal.SizeOf<DevMode>(), DeviceName = "", FormName = "" };
    private static bool TryCurrent(string device, out DevMode current)
    {
        current = NewDevMode();
        return device.Length != 0 && EnumDisplaySettings(device, -1, ref current);
    }

    private static IEnumerable<DevMode> EnumerateModes(string device)
    {
        if (device.Length == 0) yield break;
        for (int index = 0; ; index++)
        {
            var mode = NewDevMode();
            if (!EnumDisplaySettings(device, index, ref mode)) yield break;
            if (mode.Width > 0 && mode.Height > 0 && mode.Frequency > 1 && mode.BitsPerPixel >= 32)
                yield return mode;
        }
    }

    private static DisplayMode ToMode(DevMode mode) => new((int)mode.Width, (int)mode.Height, (int)mode.Frequency);

    internal static int NormalizeRefreshHz(int nativeHz, int currentNativeHz, int currentRoundedHz) =>
        nativeHz == currentNativeHz && currentRoundedHz > 0 ? currentRoundedHz : nativeHz;

    internal sealed class RefreshRateMap
    {
        // Keep observed CCD rounding after a change so a whole-Hz snapshot can still
        // select EnumDisplaySettings' truncated native frequency during rollback.
        private readonly ConcurrentDictionary<(string Id, int Width, int Height, int Hz), int> _rates = new();

        internal void Observe(string id, DisplayMode native, int roundedHz)
        {
            if (roundedHz > 0)
                _rates[(id, native.Width, native.Height, native.RefreshHz)] =
                    NormalizeRefreshHz(native.RefreshHz, native.RefreshHz, roundedHz);
        }

        internal DisplayMode Normalize(string id, DisplayMode native) =>
            _rates.TryGetValue((id, native.Width, native.Height, native.RefreshHz), out int hz)
                ? native with { RefreshHz = hz } : native;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Luid(uint Low, int High);
    [StructLayout(LayoutKind.Sequential)]
    private struct Rational { public uint Numerator, Denominator; }
    [StructLayout(LayoutKind.Sequential)]
    private struct PathSource { public Luid Adapter; public uint Id, ModeIndex, Status; }
    [StructLayout(LayoutKind.Sequential)]
    private struct PathTarget
    {
        public Luid Adapter;
        public uint Id, ModeIndex, OutputTechnology, Rotation, Scaling;
        public Rational Refresh;
        public uint ScanLineOrdering;
        public int Available;
        public uint Status;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct PathInfo { public PathSource Source; public PathTarget Target; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)]
    private struct SourceMode { public uint Width, Height, PixelFormat; public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct VideoSignal
    {
        public ulong PixelRate;
        public Rational HSync, VSync;
        public uint Width, Height, TotalWidth, TotalHeight, VideoStandard, ScanLineOrdering;
    }
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct ModeInfo
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(4)] public uint Id;
        [FieldOffset(8)] public Luid Adapter;
        [FieldOffset(16)] public SourceMode Source;
        [FieldOffset(16)] public VideoSignal Signal;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceHeader { public int Type; public uint Size; public Luid Adapter; public uint Id; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TargetDeviceName
    {
        public DeviceHeader Header;
        public uint Flags, OutputTechnology;
        public ushort Manufacturer, Product;
        public uint Connector;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string FriendlyName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DevicePath;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SourceDeviceName
    {
        public DeviceHeader Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct TargetPreferred { public DeviceHeader Header; public uint Width, Height; public VideoSignal Signal; }
    [StructLayout(LayoutKind.Sequential)]
    private struct AdvancedColor { public DeviceHeader Header; public uint Flags, Encoding, BitsPerChannel; }
    [StructLayout(LayoutKind.Sequential)]
    private struct DpiScale { public DeviceHeader Header; public int Min, Current, Max; }
    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceValue { public DeviceHeader Header; public int Value; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public ushort SpecVersion, DriverVersion, Size, DriverExtra;
        public uint Fields;
        public int X, Y;
        public uint Orientation, FixedOutput;
        public short Color, Duplex, YResolution, TTOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public ushort LogPixels;
        public uint BitsPerPixel, Width, Height, DisplayFlags, Frequency, IcmMethod, IcmIntent,
            MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
    }

#pragma warning disable SYSLIB1054 // Fixed native strings and union arrays require classic marshalling.
    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);
    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] PathInfo[] paths,
        ref uint modeCount, [Out] ModeInfo[] modes, nint topology);
    [DllImport("user32.dll")]
    private static extern int SetDisplayConfig(uint pathCount, [In] PathInfo[] paths,
        uint modeCount, [In] ModeInfo[] modes, uint flags);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int GetTargetName(ref TargetDeviceName name);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int GetSourceName(ref SourceDeviceName name);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int GetPreferredMode(ref TargetPreferred mode);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int GetAdvancedColor(ref AdvancedColor color);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int GetDpiScale(ref DpiScale scale);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigSetDeviceInfo")]
    private static extern int SetDeviceValue(ref DeviceValue value);
    [DllImport("user32.dll", EntryPoint = "EnumDisplaySettingsW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettings(string device, int number, ref DevMode mode);
    [DllImport("user32.dll", EntryPoint = "ChangeDisplaySettingsExW", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettings(string device, ref DevMode mode, nint window, uint flags, nint param);
    [DllImport("user32.dll", EntryPoint = "ChangeDisplaySettingsExW", CharSet = CharSet.Unicode)]
    private static extern int ApplyStagedSettings(string? device, nint mode, nint window, uint flags, nint param);
#pragma warning restore SYSLIB1054
}
