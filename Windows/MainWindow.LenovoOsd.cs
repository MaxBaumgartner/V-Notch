using System;
using System.Diagnostics;
using System.Text;
using System.Windows.Threading;
using VNotch.Services;

namespace VNotch;

public partial class MainWindow
{
    #region Lenovo's own Fn-key overlay

    // Lenovo answers Fn+Q (and its other Fn keys) with a square overlay at the bottom of the screen. FnHotkeyUtility
    // draws it in one dialog it keeps and shows again on every press. Like Windows' volume pill, the notch blanks it
    // with an empty region ahead of time while the notch can show the power mode. Vantage writes the new mode about
    // 70 ms before the overlay shows, so that press never flashes; an overlay for another key is let through after a
    // short wait, and after it hid the region goes back for the next press.
    private const string LenovoOsdClass = "#32770";
    private const string LenovoOsdProcess = "FnHotkeyUtility";
    private const int LenovoOsdExStyle =
        Win32Interop.WS_EX_NOACTIVATE | Win32Interop.WS_EX_TOOLWINDOW | Win32Interop.WS_EX_TOPMOST | Win32Interop.WS_EX_LAYERED;

    private IntPtr _lenovoOsd;
    private bool _lenovoOsdSuppressed;
    private uint _lenovoOsdPid;
    private DateTime _powerModeShownUtc = DateTime.MinValue;
    private DispatcherTimer? _lenovoOsdRevealTimer;

    private bool LenovoOsdEnabled => _powerModeWatcher != null;

    private void InitializeLenovoOsdHider()
    {
        if (!LenovoOsdEnabled) return;
        _lenovoOsd = FindLenovoOsd();
        if (_lenovoOsd != IntPtr.Zero && CanShowLevelHud()) SetLenovoOsdSuppressed(true);
    }

    private void DisposeLenovoOsdHider()
    {
        _lenovoOsdRevealTimer?.Stop();
        // Lenovo's overlay must work again once the notch is gone.
        SetLenovoOsdSuppressed(false);
    }

    /// <summary>Show and hide events of Lenovo's overlay; false for every other window.</summary>
    private bool OnLenovoOsdShownOrHidden(uint eventType, IntPtr hwnd)
    {
        if (!LenovoOsdEnabled) return false;
        if (hwnd != _lenovoOsd)
        {
            if (eventType != EventObjectShow || !IsLenovoOsd(hwnd)) return false;
            _lenovoOsd = hwnd;
            _lenovoOsdSuppressed = false;
        }

        if (eventType == EventObjectHide)
        {
            _lenovoOsdRevealTimer?.Stop();
            if (CanShowLevelHud()) SetLenovoOsdSuppressed(true);
            return true;
        }

        if (!CanShowLevelHud())
        {
            SetLenovoOsdSuppressed(false);
            return true;
        }
        if (!_lenovoOsdSuppressed)
        {
            SetLenovoOsdSuppressed(true);
            return true;
        }
        if (IsShowingPowerMode()) return true;
        // Another Fn key (keyboard light, mic, ...), or the mode change is still on its way.
        _lenovoOsdRevealTimer ??= CreateLenovoOsdRevealTimer();
        _lenovoOsdRevealTimer.Stop();
        _lenovoOsdRevealTimer.Start();
        return true;
    }

    private DispatcherTimer CreateLenovoOsdRevealTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = WindowsOsdOtherWait };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!IsShowingPowerMode()) SetLenovoOsdSuppressed(false);
        };
        return timer;
    }

    /// <summary>Right after the notch showed a new power mode.</summary>
    private void HideLenovoOsdForPowerMode()
    {
        if (!LenovoOsdEnabled) return;
        _powerModeShownUtc = DateTime.UtcNow;
        _lenovoOsdRevealTimer?.Stop();
        if (_lenovoOsd == IntPtr.Zero || !IsWindow(_lenovoOsd))
        {
            _lenovoOsd = FindLenovoOsd();
            _lenovoOsdSuppressed = false;
        }
        SetLenovoOsdSuppressed(true);
    }

    private bool IsShowingPowerMode() =>
        _isVolumeIndicatorActive && _levelHudKind == LevelHudKind.PowerMode &&
        DateTime.UtcNow - _powerModeShownUtc < WindowsOsdPairing;

    private void SetLenovoOsdSuppressed(bool suppressed)
    {
        if (_lenovoOsd == IntPtr.Zero || _lenovoOsdSuppressed == suppressed) return;
        if (!IsWindow(_lenovoOsd))
        {
            _lenovoOsd = IntPtr.Zero;
            _lenovoOsdSuppressed = false;
            return;
        }
        IntPtr region = suppressed ? CreateRectRgn(0, 0, 0, 0) : IntPtr.Zero;
        if (SetWindowRgn(_lenovoOsd, region, true) == 0)
        {
            if (region != IntPtr.Zero) DeleteObject(region);
            RuntimeLog.Warn("LENOVO-KEYS", "Lenovo's overlay region not set");
            return;
        }
        _lenovoOsdSuppressed = suppressed;
    }

    /// <summary>The class is checked first: this runs for every window shown anywhere.</summary>
    private bool IsLenovoOsd(IntPtr hwnd)
    {
        var name = new StringBuilder(16);
        if (Win32Interop.GetClassName(hwnd, name, name.Capacity) <= 0 ||
            !string.Equals(name.ToString(), LenovoOsdClass, StringComparison.Ordinal)) return false;
        if ((Win32Interop.GetWindowLong(hwnd, Win32Interop.GWL_EXSTYLE) & LenovoOsdExStyle) != LenovoOsdExStyle) return false;
        Win32Interop.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return false;
        if (pid == _lenovoOsdPid) return true;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            if (!string.Equals(process.ProcessName, LenovoOsdProcess, StringComparison.OrdinalIgnoreCase)) return false;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
        _lenovoOsdPid = pid;
        return true;
    }

    /// <summary>FnHotkeyUtility keeps the overlay dialog hidden between presses.</summary>
    private IntPtr FindLenovoOsd()
    {
        IntPtr hwnd = IntPtr.Zero;
        for (int i = 0; i < 64; i++)
        {
            hwnd = FindWindowEx(IntPtr.Zero, hwnd, LenovoOsdClass, null);
            if (hwnd == IntPtr.Zero) return IntPtr.Zero;
            if (IsLenovoOsd(hwnd)) return hwnd;
        }
        return IntPtr.Zero;
    }

    #endregion
}
