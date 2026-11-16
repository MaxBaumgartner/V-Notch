using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using VNotch.Services;
using VNotch.Services.Phone;

namespace VNotch;

public partial class MainWindow
{
    #region Phone

    // When the phone connects by adb a short pill says so (name and battery); a tap lets its screen grow out of the
    // notch. scrcpy is started as the pill appears, so the tap opens it at once.
    private const string PhoneGlyph = "\uE8EA";
    private static readonly TimeSpan PhonePillDuration = TimeSpan.FromSeconds(6);
    // A phone that drops off and comes back (cable wiggle, adb restart) is not announced again for a while.
    private static readonly TimeSpan PhoneAnnounceQuiet = TimeSpan.FromMinutes(2);

    private PhoneWatcher? _phoneWatcher;
    private PhoneMirrorWindow? _phoneMirror;
    private readonly Dictionary<string, DateTime> _phoneAnnounced = new();
    private readonly HashSet<string> _phoneSizeAsked = new();
    private bool _swallowNotchUp;

    private void InitializePhone()
    {
        if (!PhoneTools.Available) return;
        _phoneWatcher = new PhoneWatcher();
        _phoneWatcher.Changed += info => Dispatcher.BeginInvoke(() => OnPhoneChanged(info));
        _phoneWatcher.Start();
        NotchWrapper.PreviewMouseLeftButtonDown += NotchWrapper_PullPhoneIn;
        NotchWrapper.PreviewMouseLeftButtonUp += NotchWrapper_SwallowPhoneUp;
        ControlCenter.AttachPhone(() => _phoneWatcher?.Current, OpenPhoneMirror, PrewarmPhoneMirror);
    }

    private void DisposePhone()
    {
        _phoneMirror?.Shutdown();
        _phoneMirror?.Close();
        _phoneMirror = null;
        _phoneWatcher?.Dispose();
        _phoneWatcher = null;
    }

    private void OnPhoneChanged(PhoneInfo? info)
    {
        if (_cleanedUp) return;
        ControlCenter.RefreshPhone();
        if (_phoneMirror?.Serial is { } mirrored && (info == null || info.Serial != mirrored || info.State != PhoneState.Device))
            _phoneMirror.Shutdown();
        if (info == null) return;

        if (info.State == PhoneState.Device) AskPhoneSize(info.Serial);
        // Name and battery come a moment after the connect; the pill waits for them.
        if (info.State == PhoneState.Device && info.Name == null && info.BatteryPercent == null) return;
        string key = $"{info.Serial}|{info.State}";
        DateTime now = DateTime.UtcNow;
        if (_phoneAnnounced.TryGetValue(key, out var last) && now - last < PhoneAnnounceQuiet) return;
        _phoneAnnounced[key] = now;
        if (!CanShowClaudeAttention) return;

        if (info.State == PhoneState.Unauthorized)
        {
            ShowNotchPill(Loc.Get("phone.name"), Loc.Get("phone.allow"), appIcon: null, isClaude: false, tap: null,
                glyph: PhoneGlyph, duration: PhonePillDuration);
            return;
        }
        if (info.State != PhoneState.Device) return;
        ShowNotchPill(info.Name ?? Loc.Get("phone.name"), PhoneStatus(info), appIcon: null, isClaude: false,
            tap: () => { OpenPhoneMirror(); return true; }, glyph: PhoneGlyph, duration: PhonePillDuration);
        PrewarmPhoneMirror();
    }

    internal static string PhoneStatus(PhoneInfo info) =>
        info.BatteryPercent is { } level
            ? $"{level} %{(info.Charging ? " · " + Loc.Get("phone.charging") : "")}"
            : Loc.Get("phone.connected");

    /// <summary>The phone's real screen proportions, so the body around the picture fits it exactly.</summary>
    private async void AskPhoneSize(string serial)
    {
        if (!_phoneSizeAsked.Add(serial)) return;
        string? output = await PhoneTools.RunAdbAsync(["-s", serial, "shell", "wm", "size"]);
        if (output == null) return;
        // "Physical size: 1200x2608", plus "Override size: …" when the user changed the display size.
        var match = Regex.Match(output, @"Override size:\s*(\d+)x(\d+)");
        if (!match.Success) match = Regex.Match(output, @"Physical size:\s*(\d+)x(\d+)");
        if (match.Success) Mirror.SetPhoneSize(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value));
    }

    private PhoneMirrorWindow Mirror
    {
        get
        {
            if (_phoneMirror == null)
            {
                _phoneMirror = new PhoneMirrorWindow(GetNotchAnchor, () => _isNotchVisible && !_isHiddenByFullscreen);
                _phoneMirror.MirrorClosed += () => ControlCenter.RefreshPhone();
            }
            return _phoneMirror;
        }
    }

    private void PrewarmPhoneMirror()
    {
        if (_phoneWatcher?.Current is { State: PhoneState.Device } phone) Mirror.Prewarm(phone.Serial);
    }

    private void OpenPhoneMirror()
    {
        if (_phoneWatcher?.Current is not { State: PhoneState.Device } phone) return;
        if (Mirror.IsOpen)
        {
            Mirror.PullIn();
            return;
        }
        if (!_isExpanded && !_isMusicExpanded && !_isAnimating)
        {
            Mirror.Open(phone.Serial);
            return;
        }
        // The open notch closes first, so the phone grows out of the closed one (its size decides where the phone hangs).
        if (_isExpanded || _isMusicExpanded) CollapseNotch();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var wait = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        wait.Tick += (_, _) =>
        {
            if ((_isExpanded || _isMusicExpanded || _isAnimating) && clock.ElapsedMilliseconds < 1500) return;
            wait.Stop();
            if (!_cleanedUp) Mirror.Open(phone.Serial);
        };
        wait.Start();
    }

    /// <summary>A click on the notch while the phone hangs under it pulls the phone back in (and does nothing else).</summary>
    private void NotchWrapper_PullPhoneIn(object sender, MouseButtonEventArgs e)
    {
        if (_phoneMirror is not { State: PhoneMirrorWindow.MirrorState.Docked } mirror) return;
        e.Handled = true;
        _swallowNotchUp = true;
        mirror.PullIn();
    }

    private void NotchWrapper_SwallowPhoneUp(object sender, MouseButtonEventArgs e)
    {
        if (!_swallowNotchUp) return;
        _swallowNotchUp = false;
        e.Handled = true;
    }

    private NotchAnchor? GetNotchAnchor()
    {
        if (!IsLoaded || NotchBorder.ActualWidth <= 0) return null;
        var source = PresentationSource.FromVisual(NotchBorder);
        if (source?.CompositionTarget == null) return null;
        double scale = source.CompositionTarget.TransformToDevice.M11;
        Point bottom = NotchBorder.PointToScreen(new Point(NotchBorder.ActualWidth / 2, NotchBorder.ActualHeight));
        Point left = NotchBorder.PointToScreen(new Point(0, 0));
        var monitor = Win32Interop.MonitorFromPoint(new Win32Interop.POINT { X = (int)bottom.X, Y = (int)bottom.Y }, 2);
        var info = new Win32Interop.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32Interop.MONITORINFO>() };
        bool known = Win32Interop.GetMonitorInfo(monitor, ref info);
        Int32Rect work = known
            ? new Int32Rect(info.rcWork.Left, info.rcWork.Top, info.rcWork.Right - info.rcWork.Left, info.rcWork.Bottom - info.rcWork.Top)
            : new Int32Rect(0, 0, (int)(SystemParameters.WorkArea.Width * scale), (int)(SystemParameters.WorkArea.Height * scale));
        Int32Rect screen = known
            ? new Int32Rect(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Right - info.rcMonitor.Left, info.rcMonitor.Bottom - info.rcMonitor.Top)
            : new Int32Rect(0, 0, (int)(SystemParameters.PrimaryScreenWidth * scale), (int)(SystemParameters.PrimaryScreenHeight * scale));
        int width = (int)Math.Round((bottom.X - left.X) * 2);
        return new NotchAnchor((int)Math.Round(bottom.X), (int)Math.Round(bottom.Y), width, scale, work, screen);
    }

    #endregion
}
