using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using VNotch.Services;

namespace VNotch;

public partial class MainWindow
{
    #region Windows' own volume and brightness pill

    // Windows answers the volume and brightness keys with its own pill at the bottom of the screen: one explorer
    // window (XamlExplorerHostIslandWindow, no-activate tool window, topmost) that it shows on every press. Hiding it
    // after it appeared still flashed, so while the notch can show the HUD that window gets an empty region ahead of
    // time: Windows shows it, but there is nothing to see. Over a fullscreen app or with the notch open the region is
    // lifted and Windows' pill comes as usual; any other pill that window shows is lifted after a short wait.
    private const uint EventObjectShow = 0x8002;
    private const uint EventObjectHide = 0x8003;
    private const string WindowsOsdClass = "XamlExplorerHostIslandWindow";
    private const int WindowsOsdExStyle =
        Win32Interop.WS_EX_NOACTIVATE | Win32Interop.WS_EX_TOOLWINDOW | Win32Interop.WS_EX_TOPMOST;
    // The key press reaches Windows' pill and the notch in either order (brightness comes through WMI, slower).
    private static readonly TimeSpan WindowsOsdPairing = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan WindowsOsdOtherWait = TimeSpan.FromMilliseconds(350);

    private Win32Interop.WinEventHook? _windowShownHook;
    private DispatcherTimer? _windowsOsdRevealTimer;
    private IntPtr _windowsOsd;
    private bool _windowsOsdSuppressed;
    private DateTime _levelHudShownUtc = DateTime.MinValue;

    private void InitializeWindowsOsdHider()
    {
        _windowShownHook = new Win32Interop.WinEventHook(EventObjectShow, EventObjectHide, OnWindowShownOrHidden);
        if (!_windowShownHook.Install())
        {
            RuntimeLog.Warn("LEVEL-HUD", "Windows' volume pill stays: no show hook");
            return;
        }
        // Explorer creates the window on the first press after it started; until then the first show finds it.
        _windowsOsd = FindWindowsOsd();
        if (_windowsOsd != IntPtr.Zero && CanShowLevelHud()) SetWindowsOsdSuppressed(true);
        InitializeLenovoOsdHider();
    }

    private void DisposeWindowsOsdHider()
    {
        DisposeLenovoOsdHider();
        _windowsOsdRevealTimer?.Stop();
        _windowShownHook?.Dispose();
        _windowShownHook = null;
        // Windows' pill must work again once the notch is gone.
        SetWindowsOsdSuppressed(false);
    }

    private void OnWindowShownOrHidden(uint eventType, IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || _cleanedUp) return;
        if (OnLenovoOsdShownOrHidden(eventType, hwnd)) return;
        if (hwnd != _windowsOsd)
        {
            if (eventType != EventObjectShow || !IsWindowsOsd(hwnd)) return;
            _windowsOsd = hwnd;
            _windowsOsdSuppressed = false;
        }

        if (eventType == EventObjectHide)
        {
            // Gone by itself: blank it again for the next press.
            _windowsOsdRevealTimer?.Stop();
            if (CanShowLevelHud()) SetWindowsOsdSuppressed(true);
            return;
        }

        if (!CanShowLevelHud())
        {
            SetWindowsOsdSuppressed(false);
            return;
        }
        if (!_windowsOsdSuppressed)
        {
            // First press after explorer (re)created it, or the region was lifted: from now on ahead of time.
            SetWindowsOsdSuppressed(true);
            return;
        }
        if (IsShowingSystemLevel()) return;
        // Shown without a volume or brightness change yet: either that change is still on its way, or Windows
        // shows something else in its pill, which then gets through after a moment.
        _windowsOsdRevealTimer ??= CreateWindowsOsdRevealTimer();
        _windowsOsdRevealTimer.Stop();
        _windowsOsdRevealTimer.Start();
    }

    private DispatcherTimer CreateWindowsOsdRevealTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = WindowsOsdOtherWait };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!IsShowingSystemLevel()) SetWindowsOsdSuppressed(false);
        };
        return timer;
    }

    /// <summary>Right after the notch showed a system volume or brightness change.</summary>
    private void HideWindowsOsdForLevelHud()
    {
        _levelHudShownUtc = DateTime.UtcNow;
        _windowsOsdRevealTimer?.Stop();
        if (_windowsOsd == IntPtr.Zero || !IsWindow(_windowsOsd))
        {
            _windowsOsd = FindWindowsOsd();
            _windowsOsdSuppressed = false;
        }
        SetWindowsOsdSuppressed(true);
    }

    private bool CanShowLevelHud() =>
        !_cleanedUp && _settings != null && !_isGreetingActive && !_isExpanded && !_isAnimating && CanShowClaudeAttention;

    private bool IsShowingSystemLevel() =>
        _isVolumeIndicatorActive && _levelHudKind != LevelHudKind.AppVolume &&
        DateTime.UtcNow - _levelHudShownUtc < WindowsOsdPairing;

    /// <summary>An empty window region blanks the pill without touching how explorer shows and hides it.</summary>
    private void SetWindowsOsdSuppressed(bool suppressed)
    {
        if (_windowsOsd == IntPtr.Zero || _windowsOsdSuppressed == suppressed) return;
        if (!IsWindow(_windowsOsd))
        {
            _windowsOsd = IntPtr.Zero;
            _windowsOsdSuppressed = false;
            return;
        }
        IntPtr region = suppressed ? CreateRectRgn(0, 0, 0, 0) : IntPtr.Zero;
        // On success the system owns the region; on failure it is still ours to delete.
        if (SetWindowRgn(_windowsOsd, region, true) == 0)
        {
            if (region != IntPtr.Zero) DeleteObject(region);
            RuntimeLog.Warn("LEVEL-HUD", "Windows' volume pill region not set");
            return;
        }
        _windowsOsdSuppressed = suppressed;
    }

    private static bool IsWindowsOsd(IntPtr hwnd)
    {
        if ((Win32Interop.GetWindowLong(hwnd, Win32Interop.GWL_EXSTYLE) & WindowsOsdExStyle) != WindowsOsdExStyle) return false;
        var name = new StringBuilder(64);
        return Win32Interop.GetClassName(hwnd, name, name.Capacity) > 0 &&
               string.Equals(name.ToString(), WindowsOsdClass, StringComparison.Ordinal);
    }

    /// <summary>Explorer keeps the pill's window between presses; the other windows of that class fill the screen.</summary>
    private static IntPtr FindWindowsOsd()
    {
        IntPtr hwnd = IntPtr.Zero;
        for (int i = 0; i < 32; i++)
        {
            hwnd = FindWindowEx(IntPtr.Zero, hwnd, WindowsOsdClass, null);
            if (hwnd == IntPtr.Zero) return IntPtr.Zero;
            if (IsWindowsOsd(hwnd)) return hwnd;
        }
        return IntPtr.Zero;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "FindWindowExW")]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string? windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    #endregion
}
