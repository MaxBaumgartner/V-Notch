using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace VNotch.Services.Displays;

public interface IDdcPort
{
    int? Read(string id);
    bool Write(string id, int value);
}

/// <summary>Caches slow monitor reads and retains only the newest pending slider value.</summary>
public sealed class DdcBrightness : IDisposable
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan WriteInterval = TimeSpan.FromMilliseconds(120);
    private readonly IDdcPort _port;
    private readonly Func<DateTime> _now;
    private readonly Func<TimeSpan, Task> _delay;
    private readonly object _gate = new();
    private readonly Dictionary<string, MonitorState> _monitors = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public DdcBrightness(IDdcPort port, Func<DateTime> now) : this(port, now, Task.Delay) { }

    internal DdcBrightness(IDdcPort port, Func<DateTime> now, Func<TimeSpan, Task> delay)
    {
        _port = port;
        _now = now;
        _delay = delay;
    }

    public Task<int?> GetAsync(string id)
    {
        lock (_gate)
        {
            if (_disposed || string.IsNullOrWhiteSpace(id)) return Task.FromResult<int?>(null);
            var state = State(id);
            if (state.HasCached && _now() - state.CachedAt < CacheLifetime)
                return Task.FromResult(state.Cached);
            if (state.Reading != null) return state.Reading.Task;
            var completion = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
            state.Reading = completion;
            long revision = state.Revision;
            _ = Task.Run(() =>
            {
                int? value;
                try { value = _port.Read(id); }
                catch (Exception) { value = null; }
                if (value.HasValue) value = Math.Clamp(value.Value, 0, 100);
                lock (_gate)
                {
                    if (!_disposed && state.Revision == revision)
                    {
                        state.Cached = value;
                        state.CachedAt = _now();
                        state.HasCached = true;
                    }
                    else value = null;
                    if (state.Reading == completion) state.Reading = null;
                }
                completion.TrySetResult(value);
            });
            return completion.Task;
        }
    }

    public void Set(string id, int percent) => _ = SetAsync(id, percent);

    // A superseded pending value returns false; a value already being written returns its actual result.
    internal Task<bool> SetAsync(string id, int percent)
    {
        lock (_gate)
        {
            if (_disposed || string.IsNullOrWhiteSpace(id)) return Task.FromResult(false);
            var state = State(id);
            state.PendingResult?.TrySetResult(false);
            var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            state.PendingResult = result;
            state.Pending = Math.Clamp(percent, 0, 100);
            state.HasCached = false;
            state.Revision++;
            if (!state.Writing)
            {
                state.Writing = true;
                _ = Task.Run(() => DrainAsync(id, state));
            }
            return result.Task;
        }
    }

    internal bool IsKnownUnsupported(string id)
    {
        lock (_gate)
            return _monitors.TryGetValue(id, out var state) && state.HasCached &&
                _now() - state.CachedAt < CacheLifetime && state.Cached == null;
    }

    /// <summary>Called on display changes; a read already in progress cannot repopulate the cache.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            foreach (var state in _monitors.Values)
            {
                state.HasCached = false;
                state.Reading = null;
                state.Pending = null;
                state.PendingResult?.TrySetResult(false);
                state.PendingResult = null;
                state.ActiveResult?.TrySetResult(false);
                state.Generation++;
                state.Revision++;
            }
        }
    }

    private async Task DrainAsync(string id, MonitorState state)
    {
        while (true)
        {
            TimeSpan wait;
            int value;
            long revision;
            long generation;
            TaskCompletionSource<bool>? result;
            lock (_gate)
            {
                if (_disposed || state.Pending == null)
                {
                    state.Writing = false;
                    return;
                }
                wait = state.LastWrite.HasValue ? WriteInterval - (_now() - state.LastWrite.Value) : TimeSpan.Zero;
                if (wait <= TimeSpan.Zero)
                {
                    // Pick the value after the delay, so slider motion during the wait is coalesced too.
                    value = state.Pending.Value;
                    state.Pending = null;
                    result = state.PendingResult;
                    state.PendingResult = null;
                    state.ActiveResult = result;
                    revision = state.Revision;
                    generation = state.Generation;
                }
                else { value = 0; revision = 0; generation = 0; result = null; }
            }
            if (wait > TimeSpan.Zero)
            {
                await _delay(wait).ConfigureAwait(false);
                continue;
            }
            bool success;
            try { success = _port.Write(id, value); }
            catch (Exception) { success = false; }
            lock (_gate)
            {
                // A native write may wait behind a slow read; measure spacing after it returns.
                state.LastWrite = _now();
                result?.TrySetResult(success && !_disposed && state.Generation == generation);
                state.ActiveResult = null;
                if (_disposed) continue;
                if (!success) state.HasCached = false;
                if (state.Revision != revision) continue;
                state.HasCached = success;
                state.Cached = success ? value : null;
                state.CachedAt = _now();
            }
        }
    }

    private MonitorState State(string id)
    {
        if (!_monitors.TryGetValue(id, out var state)) _monitors[id] = state = new MonitorState();
        return state;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (var state in _monitors.Values)
            {
                state.PendingResult?.TrySetResult(false);
                state.ActiveResult?.TrySetResult(false);
            }
            _monitors.Clear();
        }
    }

    private sealed class MonitorState
    {
        public bool HasCached;
        public int? Cached;
        public DateTime CachedAt;
        public TaskCompletionSource<int?>? Reading;
        public int? Pending;
        public TaskCompletionSource<bool>? PendingResult;
        public TaskCompletionSource<bool>? ActiveResult;
        public DateTime? LastWrite;
        public bool Writing;
        public long Revision;
        public long Generation;
    }
}

/// <summary>Owns physical monitor handles. All slow native work is called by DdcBrightness workers.</summary>
internal sealed class Win32DdcPort : IDdcPort, IDisposable
{
    private readonly Func<string, string?> _deviceName;
    private readonly object _gate = new();
    private readonly Dictionary<nint, PhysicalMonitor[]> _handles = new();
    private readonly Dictionary<nint, uint> _maximums = new();
    private long _generation;
    private long _handleGeneration;
    private bool _disposed;
    internal event Action? Invalidated;

    public Win32DdcPort(Func<string, string?> deviceName)
    {
        _deviceName = deviceName;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    public int? Read(string id)
    {
        try
        {
            string? device = _deviceName(id);
            if (device == null) return null;
            lock (_gate)
            {
                if (_disposed) return null;
                foreach (var monitor in Handles(device))
                {
                    if (!GetVCPFeatureAndVCPFeatureReply(monitor.Handle, 0x10, out _, out uint value, out uint max) || max == 0)
                        continue;
                    _maximums[monitor.Handle] = max;
                    return Math.Clamp((int)Math.Round(value * 100d / max), 0, 100);
                }
                return null;
            }
        }
        catch (Exception) { return null; }
    }

    public bool Write(string id, int value)
    {
        try
        {
            string? device = _deviceName(id);
            if (device == null) return false;
            lock (_gate)
            {
                if (_disposed) return false;
                foreach (var monitor in Handles(device))
                {
                    if (!_maximums.TryGetValue(monitor.Handle, out uint max))
                    {
                        if (!GetVCPFeatureAndVCPFeatureReply(monitor.Handle, 0x10, out _, out _, out max) || max == 0)
                            continue;
                        _maximums[monitor.Handle] = max;
                    }
                    uint nativeValue = (uint)Math.Round(Math.Clamp(value, 0, 100) * max / 100d);
                    if (SetVCPFeature(monitor.Handle, 0x10, nativeValue)) return true;
                }
                return false;
            }
        }
        catch (Exception) { return false; }
    }

    private PhysicalMonitor[] Handles(string device)
    {
        if (_handleGeneration != Volatile.Read(ref _generation))
        {
            DestroyHandles();
            _handleGeneration = Volatile.Read(ref _generation);
        }
        nint found = 0;
        MonitorEnum callback = (nint monitor, nint _, ref MonitorRect __, nint ___) =>
        {
            var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info) && string.Equals(device, info.Device, StringComparison.OrdinalIgnoreCase))
                found = monitor;
            return true;
        };
        if (!EnumDisplayMonitors(0, 0, callback, 0) || found == 0) return [];
        if (_handles.TryGetValue(found, out var cached)) return cached;
        if (!GetNumberOfPhysicalMonitorsFromHMONITOR(found, out uint count) || count == 0 || count > 64) return [];
        var monitors = new PhysicalMonitor[count];
        if (!GetPhysicalMonitorsFromHMONITOR(found, count, monitors))
        {
            foreach (var monitor in monitors)
                if (monitor.Handle != 0) DestroyPhysicalMonitor(monitor.Handle);
            return [];
        }
        _handles.Add(found, monitors);
        return monitors;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs args)
    {
        Interlocked.Increment(ref _generation);
        Invalidated?.Invoke();
        // Handle destruction waits for any native call without blocking the Windows event thread.
        _ = Task.Run(() => { lock (_gate) { DestroyHandles(); } });
    }

    private void DestroyHandles()
    {
        foreach (var monitors in _handles.Values)
            foreach (var monitor in monitors)
                if (monitor.Handle != 0) DestroyPhysicalMonitor(monitor.Handle);
        _handles.Clear();
        _maximums.Clear();
    }

    public void Dispose()
    {
        Volatile.Write(ref _disposed, true);
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        // A monitor can stall for seconds, so cleanup must not join its worker from the UI thread.
        _ = Task.Run(() => { lock (_gate) { DestroyHandles(); } });
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PhysicalMonitor
    {
        public nint Handle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public uint Size;
        public MonitorRect Monitor, Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }

    private delegate bool MonitorEnum(nint monitor, nint hdc, ref MonitorRect rect, nint data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnum callback, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(nint monitor, out uint count);
    [DllImport("dxva2.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(nint monitor, uint count, [Out] PhysicalMonitor[] monitors);
    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVCPFeatureAndVCPFeatureReply(nint monitor, byte code, out uint type, out uint value, out uint maximum);
    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetVCPFeature(nint monitor, byte code, uint value);
    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyPhysicalMonitor(nint monitor);
}
