using System;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace VNotch.Services;

/// <summary>
/// Hears the default speaker's (or capture endpoint's) master volume and mute change and follows
/// the default device when it switches. Windows calls back; nothing polls. Writes made through
/// <see cref="SetVolume"/> carry an own context and are not reported back.
/// </summary>
internal sealed class MasterVolumeWatcher : IDisposable
{
    private const int DataFlowRender = 0;
    private const int DataFlowCapture = 1;
    private const int RoleConsole = 0;
    private const int ClsCtxAll = 0x17;
    private static readonly Guid EndpointVolumeId = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    private static readonly Guid OwnContext = new("6E1C3F0A-8B52-4C7E-9B0D-2A6F4E8D1C55");
    private static readonly Type EnumeratorType =
        Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"), throwOnError: true)!;

    // Bind and unbind run under this; callbacks never take it (unregistering waits for a running callback).
    private readonly object _gate = new();
    private readonly object _values = new();
    private readonly bool _capture;
    private readonly VolumeCallback _callback;
    private readonly DeviceEvents _deviceEvents;
    private IMMDeviceEnumerator? _enumerator;
    private IAudioEndpointVolume? _endpoint;
    private string? _boundDeviceId;
    public bool IsBoundDevice(string id) { lock (_gate) return string.Equals(_boundDeviceId, id, StringComparison.OrdinalIgnoreCase); }
    public bool HasBoundDevice { get { lock (_gate) return _endpoint != null; } }
    private float _lastVolume = -1;
    private bool _lastMuted;
    private bool _disposed;

    private string LogTag => _capture ? "MIC-MUTE" : "LEVEL-HUD";
    private int DataFlow => _capture ? DataFlowCapture : DataFlowRender;

    /// <summary>Volume 0..1 and whether it is muted; raised on a Windows audio thread.</summary>
    public event Action<float, bool>? Changed;

    /// <summary>Another watched endpoint became the default (also initial capture binding); its volume and mute. Raised on a worker thread.</summary>
    public event Action<float, bool>? SpeakerSwitched;

    /// <summary>A sound device (any direction) became usable (true) or went away (false). Raised on a worker thread.</summary>
    public event Action<string, bool>? EndpointStateChanged;
    public event Action? DefaultUnavailable;

    /// <summary>The watched default endpoint's volume 0..1 and mute, as last seen.</summary>
    public (float Volume, bool Muted) Current
    {
        get
        {
            lock (_values) return (Math.Max(0f, _lastVolume), _lastMuted);
        }
    }

    public MasterVolumeWatcher(bool capture = false)
    {
        _capture = capture;
        _callback = new VolumeCallback(this);
        _deviceEvents = new DeviceEvents(this);
    }

    internal static bool WatchesDefaultChange(bool capture, int flow, int role) =>
        flow == (capture ? DataFlowCapture : DataFlowRender) && role == RoleConsole;

    /// <summary>Everything lives on pool (MTA) threads, so the UI thread never marshals into the audio objects.</summary>
    public void Start() => Task.Run(() =>
    {
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                _enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(EnumeratorType)!;
                int hr = _enumerator.RegisterEndpointNotificationCallback(_deviceEvents);
                if (hr != 0) RuntimeLog.Warn(LogTag, $"Default speaker changes not heard (0x{hr:X8})");
                BindDefault();
                // Capture needs a quiet initial mute state before its first live change; render startup stays silent.
                if (_capture && _endpoint != null)
                {
                    var (volume, muted) = Current;
                    SpeakerSwitched?.Invoke(volume, muted);
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                RuntimeLog.Warn(LogTag, $"Volume watcher off: {ex.Message}");
            }
        }
    });

    public void SetVolume(float volume)
    {
        lock (_gate)
        {
            if (_capture || _disposed || _endpoint == null) return;
            var context = OwnContext;
            float level = Math.Clamp(volume, 0f, 1f);
            bool muted;
            lock (_values)
            {
                muted = _lastMuted;
                _lastVolume = level;
                if (level > 0) _lastMuted = false;
            }
            try
            {
                if (muted && level > 0) _endpoint.SetMute(false, ref context);
                _endpoint.SetMasterVolumeLevelScalar(level, ref context);
            }
            catch (COMException ex)
            {
                RuntimeLog.Warn(LogTag, $"Volume write failed: {ex.Message}");
            }
        }
    }

    private void BindDefault()
    {
        Unbind();
        if (_enumerator == null) return;
        if (_enumerator.GetDefaultAudioEndpoint(DataFlow, RoleConsole, out IMMDevice? device) != 0 || device == null)
            return;
        var id = EndpointVolumeId;
        if (device.Activate(ref id, ClsCtxAll, IntPtr.Zero, out object endpoint) != 0) return;
        var volume = (IAudioEndpointVolume)endpoint;
        lock (_values)
        {
            if (volume.GetMasterVolumeLevelScalar(out float level) == 0) _lastVolume = level;
            if (volume.GetMute(out bool muted) == 0) _lastMuted = muted;
        }
        int hr = volume.RegisterControlChangeNotify(_callback);
        if (hr != 0)
        {
            RuntimeLog.Warn(LogTag, $"Volume changes not heard (0x{hr:X8})");
            return;
        }
        _endpoint = volume;
        if (device.GetId(out string deviceId) == 0) _boundDeviceId = deviceId;
    }

    private void Unbind()
    {
        _boundDeviceId = null;
        if (_endpoint == null) return;
        try { _endpoint.UnregisterControlChangeNotify(_callback); }
        catch (COMException) { }
        _endpoint = null;
    }

    private void OnNotify(IntPtr data)
    {
        if (data == IntPtr.Zero) return;
        // AUDIO_VOLUME_NOTIFICATION_DATA: GUID context, BOOL muted, float master volume, ...
        var context = Marshal.PtrToStructure<Guid>(data);
        bool muted = Marshal.ReadInt32(data, 16) != 0;
        float volume = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(data, 20));
        bool changed;
        lock (_values)
        {
            changed = Math.Abs(volume - _lastVolume) > 0.0005f || muted != _lastMuted;
            _lastVolume = volume;
            _lastMuted = muted;
        }
        if (!changed || context == OwnContext || Volatile.Read(ref _disposed)) return;
        Changed?.Invoke(volume, muted);
    }

    private void OnDefaultDeviceChanged(int flow, int role)
    {
        if (!WatchesDefaultChange(_capture, flow, role)) return;
        // Never call back into the audio API from inside its own callback.
        Task.Run(() =>
        {
            float volume;
            bool muted;
            lock (_gate)
            {
                if (_disposed) return;
                try { BindDefault(); }
                catch (COMException ex)
                {
                    RuntimeLog.Warn(LogTag, $"Speaker switch not followed: {ex.Message}");
                    return;
                }
                if (_endpoint == null)
                {
                    DefaultUnavailable?.Invoke();
                    return;
                }
                lock (_values)
                {
                    volume = _lastVolume;
                    muted = _lastMuted;
                }
            }
            if (volume >= 0) SpeakerSwitched?.Invoke(volume, muted);
        });
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Task.Run(() =>
        {
            lock (_gate)
            {
                Unbind();
                try { _enumerator?.UnregisterEndpointNotificationCallback(_deviceEvents); }
                catch (COMException) { }
                // Never released: Windows hands the same enumerator to the other audio parts too.
                _enumerator = null;
            }
        });
    }

    private sealed class VolumeCallback(MasterVolumeWatcher owner) : IAudioEndpointVolumeCallback
    {
        public int OnNotify(IntPtr notifyData)
        {
            try { owner.OnNotify(notifyData); }
            catch (Exception ex) { RuntimeLog.Warn(owner.LogTag, $"Volume change dropped: {ex.Message}"); }
            return 0;
        }
    }

    private sealed class DeviceEvents(MasterVolumeWatcher owner) : IMMNotificationClient
    {
        public int OnDeviceStateChanged(string deviceId, int newState)
        {
            // DEVICE_STATE_ACTIVE = 1. Off the audio thread, like the default change.
            if (!Volatile.Read(ref owner._disposed))
                Task.Run(() => owner.EndpointStateChanged?.Invoke(deviceId, newState == 1));
            return 0;
        }
        public int OnDeviceAdded(string deviceId) => 0;
        public int OnDeviceRemoved(string deviceId) => 0;
        public int OnPropertyValueChanged(string deviceId, PropertyKey key) => 0;

        public int OnDefaultDeviceChanged(int flow, int role, string? defaultDeviceId)
        {
            owner.OnDefaultDeviceChanged(flow, role);
            return 0;
        }
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? endpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid id, int context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object endpoint);
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
        [PreserveSig] int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    [ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolumeCallback
    {
        [PreserveSig] int OnNotify(IntPtr notifyData);
    }

    [ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMNotificationClient
    {
        [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int newState);
        [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string? defaultDeviceId);
        [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, PropertyKey key);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public int PropertyId;
    }
}

/// <summary>
/// Hears the built-in screen's brightness change (brightness keys, the Windows slider, power plans) through WMI's
/// WmiMonitorBrightnessEvent and can set it. Screens without a brightness control simply never report.
/// </summary>
internal sealed class BrightnessWatcher : IDisposable
{
    private const string LogTag = "LEVEL-HUD";
    private const string WmiNamespace = @"root\WMI";

    private readonly object _gate = new();
    private ManagementEventWatcher? _watcher;
    private int _last = -1;
    private bool _disposed;

    /// <summary>Brightness in percent; raised on a WMI thread.</summary>
    public event Action<int>? Changed;

    public void Start() => Task.Run(() =>
    {
        try
        {
            Interlocked.Exchange(ref _last, ReadBrightness() ?? -1);
            var watcher = new ManagementEventWatcher(new ManagementScope(WmiNamespace),
                new EventQuery("SELECT * FROM WmiMonitorBrightnessEvent"));
            watcher.EventArrived += OnEventArrived;
            lock (_gate)
            {
                if (_disposed)
                {
                    watcher.Dispose();
                    return;
                }
                _watcher = watcher;
            }
            watcher.Start();
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            RuntimeLog.Warn(LogTag, $"Brightness watcher off: {ex.Message}");
        }
    });

    private void OnEventArrived(object sender, EventArrivedEventArgs e)
    {
        try
        {
            int level = Convert.ToInt32(e.NewEvent["Brightness"], CultureInfo.InvariantCulture);
            if (Interlocked.Exchange(ref _last, level) == level || Volatile.Read(ref _disposed)) return;
            Changed?.Invoke(level);
        }
        catch (Exception ex) when (ex is ManagementException or InvalidCastException or FormatException)
        {
            RuntimeLog.Warn(LogTag, $"Brightness change dropped: {ex.Message}");
        }
    }

    /// <summary>Sets the built-in screen; the change it causes is not reported back.</summary>
    public void SetBrightness(int percent) => TrySetBrightness(percent);

    internal bool TrySetBrightness(int percent)
    {
        if (Volatile.Read(ref _disposed)) return false;
        byte level = (byte)Math.Clamp(percent, 0, 100);
        Interlocked.Exchange(ref _last, level);
        try
        {
            bool success = false;
            using var searcher = new ManagementObjectSearcher(new ManagementScope(WmiNamespace),
                new SelectQuery("WmiMonitorBrightnessMethods"));
            using var screens = searcher.Get();
            foreach (ManagementObject screen in screens)
            {
                using (screen)
                {
                    object? result = screen.InvokeMethod("WmiSetBrightness", new object[] { 1u, level });
                    if (result != null && Convert.ToUInt32(result, CultureInfo.InvariantCulture) == 0) success = true;
                }
            }
            return success;
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            RuntimeLog.Warn(LogTag, $"Brightness write failed: {ex.Message}");
            return false;
        }
    }

    internal static int? ReadCurrentBrightness()
    {
        try { return ReadBrightness(); }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException or InvalidCastException or FormatException)
        {
            return null;
        }
    }

    private static int? ReadBrightness()
    {
        using var searcher = new ManagementObjectSearcher(new ManagementScope(WmiNamespace),
            new SelectQuery("WmiMonitorBrightness"));
        using var screens = searcher.Get();
        foreach (ManagementObject screen in screens)
        {
            using (screen) return Convert.ToInt32(screen["CurrentBrightness"], CultureInfo.InvariantCulture);
        }
        return null;
    }

    public void Dispose()
    {
        ManagementEventWatcher? watcher;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            watcher = _watcher;
            _watcher = null;
        }
        if (watcher == null) return;
        watcher.EventArrived -= OnEventArrived;
        Task.Run(() =>
        {
            try { watcher.Stop(); }
            catch (Exception ex) when (ex is ManagementException or COMException) { }
            watcher.Dispose();
        });
    }
}
