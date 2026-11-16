using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

/// <summary>The mic dives out and returns struck through in rose when muted, or plain when switched back on.</summary>
public partial class MainWindow
{
    private static readonly Color MicMuteRose = Color.FromRgb(0xD2, 0x85, 0x7E);
    private MasterVolumeWatcher? _captureVolumeWatcher;
    private bool? _micMuteKnown;
    private bool _micMuteShown;
    private int _micDiveToken;

    internal bool MicMuteIsKnownMuted => _micMuteKnown == true;

    private void InitializeMicMuteHud()
    {
        _captureVolumeWatcher = new MasterVolumeWatcher(capture: true);
        _captureVolumeWatcher.Changed += (volume, muted) => Dispatcher.BeginInvoke(() => OnCaptureLevelChanged(volume, muted));
        _captureVolumeWatcher.SpeakerSwitched += (volume, muted) => Dispatcher.BeginInvoke(() => OnCaptureDefaultChanged(volume, muted));
        _captureVolumeWatcher.EndpointStateChanged += (id, active) =>
        {
            if (!active) Dispatcher.BeginInvoke(() =>
            {
                if (_captureVolumeWatcher?.IsBoundDevice(id) == true) OnCaptureEndpointGone();
            });
        };
        _captureVolumeWatcher.DefaultUnavailable += () => Dispatcher.BeginInvoke(() =>
        {
            if (_captureVolumeWatcher?.HasBoundDevice == false) OnCaptureEndpointGone();
        });
        _captureVolumeWatcher.Start();
    }

    private void DisposeMicMuteHud()
    {
        _fakeMicMuteTimer?.Stop();
        _captureVolumeWatcher?.Dispose();
        CancelMicDive();
    }

    private void OnCaptureLevelChanged(float volume, bool muted)
    {
        if (_cleanedUp || MotionLabFakesMicMute) return;
        OnMicMuteChanged(muted);
    }

    // Initial binding and default-device switches establish the state without playing a mute moment.
    private void OnCaptureDefaultChanged(float volume, bool muted)
    {
        if (_cleanedUp || MotionLabFakesMicMute) return;
        _micMuteKnown = muted;
        UpdateMicRestGlyph();
        _micMuteShown = muted;
        if (_isVolumeIndicatorActive && _levelHudKind == LevelHudKind.MicMute)
        {
            CancelMicDive();
            ApplyMicMuteFace(muted);
        }
    }

    private void OnMicMuteChanged(bool muted)
    {
        bool? previous = _micMuteKnown;
        _micMuteKnown = muted;
        UpdateMicRestGlyph();
        if (previous == muted) return;
        if (previous == null || _cleanedUp || _settings == null || _isGreetingActive || !CanShowClaudeAttention)
        {
            CancelMicDive();
            _micMuteShown = muted;
            if (_isVolumeIndicatorActive && _levelHudKind == LevelHudKind.MicMute) ApplyMicMuteFace(muted);
            return;
        }

        _levelHudLabelTimer?.Stop();
        _levelHudLabelOverride = null;
        ShowLevelIndicator(0f, LevelHudKind.MicMute);
        if (_isVolumeIndicatorActive && _levelHudKind == LevelHudKind.MicMute) PlayMicDive(muted);
        else _micMuteShown = muted;
    }

    private int _micRestToken;
    private bool _micRestShown;

    private void OnCaptureEndpointGone()
    {
        if (_cleanedUp) return;
        _micMuteKnown = null;
        UpdateMicRestGlyph();
    }

    /// <summary>A small rose mic appears beside the idle clock while muted.</summary>
    private void UpdateMicRestGlyph()
    {
        if (MicMutedRestGlyph == null) return;
        bool show = MicMuteIsKnownMuted && !_micMutedShown && _settings?.ShowMutedMicGlyph == true;
        if (show == _micRestShown) return;
        _micRestShown = show;
        int token = ++_micRestToken;
        bool entering = MicMutedRestGlyph.Visibility != Visibility.Visible;
        if (show) MicMutedRestGlyph.Visibility = Visibility.Visible;
        // The clock makes room by gliding, in step with the glyph.
        var clockMargin = show ? new Thickness(17, 0, 0, 0) : new Thickness(0);
        Motion.MarginTo(IdleClock, clockMargin, Motion.Glide);
        Motion.To(MicMutedRestScale, ScaleTransform.ScaleXProperty, show ? 1 : Motion.ExitToScale,
            show ? Motion.TileScale : Motion.Exit, from: show && entering ? Motion.TileFromScale : null);
        Motion.To(MicMutedRestScale, ScaleTransform.ScaleYProperty, show ? 1 : Motion.ExitToScale,
            show ? Motion.TileScale : Motion.Exit, from: show && entering ? Motion.TileFromScale : null);
        Motion.To(MicMutedRestGlyph, OpacityProperty, show ? 1 : 0, show ? Motion.Tile : Motion.Exit,
            from: show && entering ? 0 : null, done: () =>
            {
                if (token != _micRestToken || _micRestShown) return;
                Motion.Stop(MicMutedRestScale, ScaleTransform.ScaleXProperty);
                Motion.Stop(MicMutedRestScale, ScaleTransform.ScaleYProperty);
                MicMutedRestGlyph.Visibility = Visibility.Collapsed;
            });
    }

    private void ApplyMicMuteFace(bool muted)
    {
        _micMuteShown = muted;
        var (accent, hasAccent) = EffectiveHudTint();
        Color tint = muted ? MicMuteRose : hasAccent ? accent : Colors.White;
        foreach (var child in VolumeIndicatorMic.Children)
            if (child is Shape path) TintPath(path, tint, fill: false);
        VolumeIndicatorMicSlash.Visibility = muted ? Visibility.Visible : Visibility.Collapsed;
        VolumeIndicatorMicWord.Text = Loc.Get(muted ? "levelHud.micMuted" : "levelHud.micOn");
        VolumeIndicatorMicWord.Foreground = new SolidColorBrush(tint);
        VolumeIndicatorIconGlow.Color = tint;
    }

    private void CancelMicDive(bool reset = true)
    {
        ++_micDiveToken;
        if (!reset) return;
        Motion.Stop(VolumeIndicatorMicShift, TranslateTransform.YProperty, 0);
        Motion.Stop(VolumeIndicatorMicSquash, ScaleTransform.ScaleYProperty, 1);
        Motion.Stop(VolumeIndicatorMic, OpacityProperty, 1);
        Motion.Stop(VolumeIndicatorMicWord, OpacityProperty, 1);
    }

    /// <summary>Fade the word with the dive, swap at the hidden midpoint, then bring the new mic face back from above.</summary>
    private void PlayMicDive(bool toMuted)
    {
        CancelMicDive(reset: AnimationConfig.ReduceMotion);
        if (AnimationConfig.ReduceMotion)
        {
            ApplyMicMuteFace(toMuted);
            return;
        }
        int token = _micDiveToken;
        Motion.To(VolumeIndicatorMicShift, TranslateTransform.YProperty, 9, Motion.PressDown);
        Motion.To(VolumeIndicatorMicSquash, ScaleTransform.ScaleYProperty, Motion.TileFromScale, Motion.PressDown);
        Motion.To(VolumeIndicatorMicWord, OpacityProperty, 0, Motion.PressDown);
        Motion.To(VolumeIndicatorMic, OpacityProperty, 0, Motion.PressDown, done: () =>
        {
            if (token != _micDiveToken || _cleanedUp || !_isVolumeIndicatorActive || _levelHudKind != LevelHudKind.MicMute) return;
            ApplyMicMuteFace(toMuted);
            // The hidden face changes above the baseline; a reversal never resets a visible spring.
            Motion.To(VolumeIndicatorMicShift, TranslateTransform.YProperty, 0, Motion.Press, from: -9, done: () =>
            {
                if (token == _micDiveToken && Math.Abs(VolumeIndicatorMicShift.Y) < 0.01)
                    Motion.Stop(VolumeIndicatorMicShift, TranslateTransform.YProperty, 0);
            });
            Motion.To(VolumeIndicatorMicSquash, ScaleTransform.ScaleYProperty, 1, Motion.Press);
            Motion.To(VolumeIndicatorMicWord, OpacityProperty, 1, Motion.Tile);
            Motion.To(VolumeIndicatorMic, OpacityProperty, 1, Motion.Tile);
        });
    }
}
