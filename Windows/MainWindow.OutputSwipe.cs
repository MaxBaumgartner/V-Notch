using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

public partial class MainWindow
{
    #region Speaker switch: two-finger swipe on the closed notch

    // A sideways two-finger swipe on the touchpad (or a tilted wheel) over the closed notch makes the next or the
    // previous speaker the default. Dragging the HUD already sets the volume, so the swipe is the free gesture.
    // Whenever the default speaker changes, by the swipe or anywhere else, the volume HUD shows that speaker's name
    // for a moment, sliding in from the swipe's side, then its volume.
    private const int WmMouseHWheel = 0x020E;
    private const int OutputSwipeThreshold = 120;
    private static readonly TimeSpan OutputSwipeQuiet = TimeSpan.FromMilliseconds(450);
    private static readonly TimeSpan SpeakerNameHold = TimeSpan.FromMilliseconds(1500);

    private HwndSource? _outputSwipeSource;
    private int _outputSwipeAccumulator;
    private DateTime _lastOutputSwipeInputUtc = DateTime.MinValue;
    private DateTime _outputSwipeLockedUntilUtc = DateTime.MinValue;
    private bool _outputSwitchRunning;
    private int _pendingSwipeDirection;
    private string? _levelHudLabelOverride;
    private DispatcherTimer? _levelHudLabelTimer;

    private void InitializeOutputSwipe()
    {
        // Runs from the constructor, before the window has a handle.
        if (new WindowInteropHelper(this).Handle != IntPtr.Zero) AttachOutputSwipeHook();
        else SourceInitialized += (_, _) => AttachOutputSwipeHook();
        if (_masterVolumeWatcher != null)
            _masterVolumeWatcher.SpeakerSwitched += (volume, muted) => Task.Run(() =>
            {
                string? name = AudioMixerService.EnumerateOutputDevices().FirstOrDefault(d => d.IsDefault)?.FriendlyName;
                Dispatcher.BeginInvoke(() => ShowSpeakerSwitch(name, volume, muted));
            });
    }

    private void AttachOutputSwipeHook()
    {
        if (_outputSwipeSource != null || _cleanedUp) return;
        _outputSwipeSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _outputSwipeSource?.AddHook(OutputSwipeHook);
    }

    private void DisposeOutputSwipe()
    {
        _outputSwipeSource?.RemoveHook(OutputSwipeHook);
        _outputSwipeSource = null;
        _levelHudLabelTimer?.Stop();
    }

    private IntPtr OutputSwipeHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmMouseHWheel || _cleanedUp || _isGreetingActive) return IntPtr.Zero;
        if (_isExpanded)
        {
            handled = MusicWheelInput(unchecked((short)((long)wParam >> 16)));
            return IntPtr.Zero;
        }
        if (_isAnimating) return IntPtr.Zero;
        handled = true;
        int delta = unchecked((short)((long)wParam >> 16));
        OnOutputSwipeInput(delta);
        return IntPtr.Zero;
    }

    /// <summary>A trackpad fling sends many small deltas: one swipe is one step, then it rests a moment.</summary>
    private void OnOutputSwipeInput(int delta)
    {
        DateTime now = DateTime.UtcNow;
        if (now < _outputSwipeLockedUntilUtc)
        {
            _lastOutputSwipeInputUtc = now;
            return;
        }
        if (now - _lastOutputSwipeInputUtc > OutputSwipeQuiet) _outputSwipeAccumulator = 0;
        _lastOutputSwipeInputUtc = now;
        _outputSwipeAccumulator += delta;
        if (Math.Abs(_outputSwipeAccumulator) < OutputSwipeThreshold) return;

        int direction = Math.Sign(_outputSwipeAccumulator);
        _outputSwipeAccumulator = 0;
        _outputSwipeLockedUntilUtc = now + OutputSwipeQuiet;
        SwitchSpeaker(direction);
    }

    private void SwitchSpeaker(int direction)
    {
        if (_outputSwitchRunning) return;
        _outputSwitchRunning = true;
        _pendingSwipeDirection = direction;
        Task.Run(() =>
        {
            // Only real speakers and headphones, not the virtual mixes (Wave Link) or hands-free profiles.
            var devices = VNotch.Services.ControlCenter.OutputDevices.List(Array.Empty<string>());
            bool switched = false;
            if (devices.Count > 1)
            {
                int current = Math.Max(0, devices.FindIndex(d => d.IsDefault));
                var next = devices[(current + direction + devices.Count) % devices.Count];
                switched = AudioMixerService.SwitchDefaultOutputDevice(next.Id);
            }
            Dispatcher.BeginInvoke(() =>
            {
                _outputSwitchRunning = false;
                if (switched) return; // SpeakerSwitched shows it
                _pendingSwipeDirection = 0;
                // Only one speaker: the HUD leans into the swipe and springs back.
                if (_isVolumeIndicatorActive) PlayVolumeOverscrollSpring(direction > 0 ? 1 : 0, direction * 0.06);
            });
        });
    }

    private void ShowSpeakerSwitch(string? friendlyName, float volume, bool muted)
    {
        if (_cleanedUp || _settings == null || _isGreetingActive || !CanShowClaudeAttention) return;
        int direction = _pendingSwipeDirection;
        _pendingSwipeDirection = 0;
        _levelHudLabelOverride = ShortSpeakerName(friendlyName);
        _levelHudMuted = muted;
        bool wasShowing = _isVolumeIndicatorActive && _levelHudKind == LevelHudKind.SystemVolume;
        ShowLevelIndicator(muted ? 0f : volume, LevelHudKind.SystemVolume);
        if (!_isVolumeIndicatorActive || _levelHudKind != LevelHudKind.SystemVolume) return;
        if (wasShowing) SetVolumeFill(VolumeIndicatorScale, muted ? 0 : volume, animate: true);
        HideWindowsOsdForLevelHud();
        PlaySpeakerNameEntrance(direction);

        _levelHudLabelTimer ??= CreateLevelHudLabelTimer();
        _levelHudLabelTimer.Stop();
        _levelHudLabelTimer.Start();
        // The name needs its moment: the HUD stays up at least as long.
        _volumeIndicatorHideTimer?.Stop();
        _volumeIndicatorHideTimer?.Start();
    }

    private DispatcherTimer CreateLevelHudLabelTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = SpeakerNameHold };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_levelHudLabelOverride == null) return;
            _levelHudLabelOverride = null;
            if (_isVolumeIndicatorActive) UpdateVolumeIndicatorHud(VolumeIndicatorScale.ScaleX, animate: true);
        };
        return timer;
    }

    /// <summary>"Lautsprecher (Realtek(R) Audio)" becomes "Lautsprecher".</summary>
    internal static string ShortSpeakerName(string? friendlyName)
    {
        string name = (friendlyName ?? "").Trim();
        int bracket = name.IndexOf(" (", StringComparison.Ordinal);
        if (bracket > 0) name = name[..bracket];
        return name.Length > 22 ? name[..21].TrimEnd() + "…" : name;
    }

    /// <summary>The name slides in from where the fingers came from, and the whole HUD gives a little lean.</summary>
    private void PlaySpeakerNameEntrance(int direction)
    {
        if (VolumeIndicatorPercentText == null) return;
        Motion.To(VolumeIndicatorPercentText, OpacityProperty, 1, Motion.Tile);
        if (VolumeIndicatorPercentScale != null)
        {
            Motion.To(VolumeIndicatorPercentScale, ScaleTransform.ScaleXProperty, 1, Motion.Press,
                velocity: Motion.VelocityOf(VolumeIndicatorPercentScale, ScaleTransform.ScaleXProperty) + 1.5);
            Motion.To(VolumeIndicatorPercentScale, ScaleTransform.ScaleYProperty, 1, Motion.Press,
                velocity: Motion.VelocityOf(VolumeIndicatorPercentScale, ScaleTransform.ScaleYProperty) + 1.5);
        }
        if (direction == 0 || VolumeIndicatorContentTranslate == null) return;
        Motion.To(VolumeIndicatorContentTranslate, TranslateTransform.XProperty, 0, Motion.Press,
            velocity: Motion.VelocityOf(VolumeIndicatorContentTranslate, TranslateTransform.XProperty) - direction * 70);
    }

    #endregion
}
