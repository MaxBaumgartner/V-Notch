using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;

namespace VNotch.Services;

public enum KeepAwakeMode
{
    Off,
    Automatic,
    Always
}

/// <summary>Why the PC stays awake right now, if it does.</summary>
public enum KeepAwakeReason
{
    None,
    Claude,
    Download,
    Timer,
    Always
}

/// <summary>
/// Whether the PC should stay awake: always, never, or (automatic) while Claude Code works, a download runs or a
/// timer counts down. On battery below 20 % automatic lets it sleep anyway, so a forgotten session cannot drain it.
/// </summary>
internal static class KeepAwakePolicy
{
    internal const int LowBatteryPercent = 20;

    public static KeepAwakeReason Decide(KeepAwakeMode mode, bool claudeWorking, bool downloading, bool timerRunning,
        bool onBattery, int batteryPercent)
    {
        if (mode == KeepAwakeMode.Off) return KeepAwakeReason.None;
        if (mode == KeepAwakeMode.Always) return KeepAwakeReason.Always;
        if (onBattery && batteryPercent < LowBatteryPercent) return KeepAwakeReason.None;
        if (claudeWorking) return KeepAwakeReason.Claude;
        if (downloading) return KeepAwakeReason.Download;
        if (timerRunning) return KeepAwakeReason.Timer;
        return KeepAwakeReason.None;
    }
}

/// <summary>
/// Holds the "system required" request (the display may still turn off). Windows ties that request to the thread
/// that set it, so it lives on its own thread: it ends with the process, and a crash cannot leave the PC awake.
/// </summary>
internal sealed class KeepAwakeService : IDisposable
{
    private const uint EsContinuous = 0x80000000, EsSystemRequired = 0x00000001;
    private readonly BlockingCollection<bool> _requests = new();
    private readonly Thread _thread;
    private readonly Func<uint, uint> _setState;
    private bool? _last;
    private bool _disposed;

    public KeepAwakeService(Func<uint, uint>? setState = null)
    {
        _setState = setState ?? SetThreadExecutionState;
        _thread = new Thread(Run) { IsBackground = true, Name = "V-Notch keep awake" };
        _thread.Start();
    }

    /// <summary>True keeps the PC from sleeping until false (or until this is disposed).</summary>
    public void Set(bool awake)
    {
        if (_disposed || _last == awake) return;
        _last = awake;
        _requests.Add(awake);
    }

    private void Run()
    {
        foreach (bool awake in _requests.GetConsumingEnumerable())
            _setState(awake ? EsContinuous | EsSystemRequired : EsContinuous);
        _setState(EsContinuous);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _requests.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(1));
        _requests.Dispose();
    }

    [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);
}
