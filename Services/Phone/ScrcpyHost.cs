using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace VNotch.Services.Phone;

/// <summary>
/// One running scrcpy whose window lives inside ours. scrcpy (C, SDL, hardware decode) is the fastest mirror there is;
/// it starts off-screen and is adopted as a child window, so it looks like part of the notch and never flashes as a
/// window of its own. Starting takes about 0.7 s (server push, encoder start, first frame), which is why the mirror
/// starts one ahead of a likely tap and keeps it warm for a while.
/// </summary>
internal sealed class ScrcpyProcess : IDisposable
{
    private const string SdlWindowClass = "SDL_app";
    private static readonly TimeSpan WindowWait = TimeSpan.FromSeconds(8);

    private readonly Process _process;
    private int _disposed;

    private ScrcpyProcess(Process process, string serial, IntPtr window)
    {
        _process = process;
        Serial = serial;
        Window = window;
    }

    public string Serial { get; }
    public IntPtr Window { get; }
    public bool HasExited
    {
        get
        {
            try { return _process.HasExited; }
            catch (InvalidOperationException) { return true; }
        }
    }

    public event Action? Exited;

    /// <summary>Starts scrcpy for the phone and waits for its window; null when it did not come up.</summary>
    public static async Task<ScrcpyProcess?> StartAsync(string serial, int width, int height, CancellationToken ct)
    {
        if (PhoneTools.Scrcpy is not { } exe) return null;
        string title = $"V-Notch Handy {Environment.ProcessId} {Guid.NewGuid():N}";
        var info = PhoneTools.StartInfo(exe,
        [
            "-s", serial,
            "--window-borderless",
            $"--window-title={title}",
            // Off-screen until it is adopted: it never shows up as a window of its own.
            "--window-x=-32000", "--window-y=-32000",
            $"--window-width={width}", $"--window-height={height}",
            "--no-audio",
            "--video-codec=h264",
            // The phone's 120 Hz; scrcpy's 8 Mbit/s default smears a 1200x2608 screen once it moves.
            "--max-fps=120",
            "--video-bit-rate=24M"
        ]);
        Process? process;
        try { process = Process.Start(info); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            RuntimeLog.Warn("PHONE", $"scrcpy did not start: {ex.Message}");
            return null;
        }
        if (process == null) return null;
        ProcessJob.Add(process);
        // Its output has to be read or it blocks once the pipe is full; only errors matter.
        process.OutputDataReceived += (_, e) => LogLine(e.Data);
        process.ErrorDataReceived += (_, e) => LogLine(e.Data);
        if (info.RedirectStandardOutput) process.BeginOutputReadLine();
        if (info.RedirectStandardError) process.BeginErrorReadLine();

        var clock = Stopwatch.StartNew();
        IntPtr window = IntPtr.Zero;
        while (clock.Elapsed < WindowWait && !ct.IsCancellationRequested)
        {
            window = FindWindowW(SdlWindowClass, title);
            if (window != IntPtr.Zero || process.HasExited) break;
            try { await Task.Delay(15, ct); }
            catch (TaskCanceledException) { break; }
        }
        if (window == IntPtr.Zero)
        {
            Kill(process);
            process.Dispose();
            if (!ct.IsCancellationRequested) RuntimeLog.Warn("PHONE", "scrcpy window did not appear");
            return null;
        }
        RuntimeLog.Info("PHONE", $"scrcpy window after {clock.ElapsedMilliseconds} ms");
        var started = new ScrcpyProcess(process, serial, window);
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => started.Exited?.Invoke();
        return started;
    }

    private static void LogLine(string? line)
    {
        if (line != null && line.Contains("ERROR", StringComparison.Ordinal)) RuntimeLog.Warn("PHONE", line.Trim());
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Kill(_process);
        _process.Dispose();
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "FindWindowW")]
    private static extern IntPtr FindWindowW(string? className, string? title);
}

/// <summary>
/// scrcpy's own window, adopted as a child of ours. Done by hand instead of with WPF's HwndHost: HwndHost throws
/// (and the dispatcher then ends the whole notch) when SetParent does not take at once, which happened when scrcpy
/// was slow to start (2026-11-14). Here a failed adoption is retried briefly and otherwise just reported.
/// </summary>
internal sealed class ScrcpyChild
{
    private const int GWL_STYLE = -16;
    private const int WS_CHILD = 0x40000000;
    private const int WS_CLIPSIBLINGS = 0x04000000;
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_CAPTION = 0x00C00000;
    private const int WS_THICKFRAME = 0x00040000;
    private const int WS_VISIBLE = 0x10000000;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const int SW_HIDE = 0;
    private const int SW_SHOWNA = 8;

    private readonly IntPtr _window;

    private ScrcpyChild(IntPtr window) => _window = window;

    /// <summary>Makes <paramref name="window"/> a hidden child of <paramref name="parent"/>; null when Windows refuses.</summary>
    public static async Task<ScrcpyChild?> AdoptAsync(IntPtr window, IntPtr parent)
    {
        int error = 0;
        for (int attempt = 0; attempt < 12; attempt++)
        {
            if (!IsWindow(window) || !IsWindow(parent)) return null;
            int style = GetWindowLongW(window, GWL_STYLE);
            style &= ~(WS_POPUP | WS_CAPTION | WS_THICKFRAME | WS_VISIBLE);
            style |= WS_CHILD | WS_CLIPSIBLINGS;
            SetWindowLongW(window, GWL_STYLE, style);
            if (SetParent(window, parent) == IntPtr.Zero) error = Marshal.GetLastWin32Error();
            if (GetParent(window) == parent) return new ScrcpyChild(window);
            await Task.Delay(40);
        }
        RuntimeLog.Warn("PHONE", $"scrcpy window could not be adopted (error {error})");
        return null;
    }

    /// <summary>Places it in the parent's client area (device pixels); the parent's region rounds its corners.</summary>
    public void Place(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0 || !IsWindow(_window)) return;
        SetWindowPos(_window, IntPtr.Zero, x, y, width, height, SWP_NOZORDER | SWP_NOACTIVATE);
    }

    public void Show(bool visible)
    {
        if (IsWindow(_window)) ShowWindow(_window, visible ? SW_SHOWNA : SW_HIDE);
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern int GetWindowLongW(IntPtr hwnd, int index);
    [DllImport("user32.dll")]
    private static extern int SetWindowLongW(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);
}

/// <summary>A job object that takes scrcpy along when the notch ends, even when it crashes.</summary>
internal static class ProcessJob
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    private static readonly Lazy<IntPtr> Job = new(CreateJob);

    public static void Add(Process process)
    {
        IntPtr job = Job.Value;
        if (job == IntPtr.Zero) return;
        try { AssignProcessToJobObject(job, process.Handle); }
        catch (InvalidOperationException) { }
    }

    private static IntPtr CreateJob()
    {
        IntPtr job = CreateJobObjectW(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return IntPtr.Zero;
        var info = new JobExtendedLimits { Basic = new JobBasicLimits { LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE } };
        int size = Marshal.SizeOf<JobExtendedLimits>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, buffer, false);
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, buffer, (uint)size))
                RuntimeLog.Warn("PHONE", "Job object limits not set");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return job;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobBasicLimits
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobExtendedLimits
    {
        public JobBasicLimits Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll")]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);
    [DllImport("kernel32.dll")]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
}
