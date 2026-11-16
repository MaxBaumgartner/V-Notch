using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using VNotch.Controllers;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

public partial class MainWindow
{
    #region System volume and brightness in the volume HUD

    // The HUD that comes up when scrolling on the notch during music also shows the system volume (volume keys,
    // the Windows mixer) and the screen brightness, with or without music. Brightness wears a sun and a warm tint.
    private enum LevelHudKind
    {
        AppVolume,
        SystemVolume,
        Brightness,
        // Lenovo's Fn+Q power mode: four steps, the mode's glyph and name, tinted in the mode's colour.
        PowerMode,
        MicMute
    }

    private const int PowerModeSteps = 4;
    private static readonly VNotch.Services.LegionMode[] PowerModeByStep =
    [
        VNotch.Services.LegionMode.Quiet, VNotch.Services.LegionMode.Balanced,
        VNotch.Services.LegionMode.Performance, VNotch.Services.LegionMode.Extreme
    ];
    private LenovoPowerModeWatcher? _powerModeWatcher;
    private (string Glyph, string Name, Color Glow, int Step) _powerModeLook;
    private int _powerModeIconStep;

    private static readonly Color BrightnessHudTint = Color.FromRgb(0xE2, 0xBE, 0x86);

    private LevelHudKind _levelHudKind = LevelHudKind.AppVolume;
    private bool _levelHudMuted;
    // Without music the HUD covers the resting content (time, weather, Claude), which comes back afterwards.
    private bool _levelHudOverIdle;
    private MasterVolumeWatcher? _masterVolumeWatcher;
    private BrightnessWatcher? _brightnessWatcher;
    private readonly CoalescingActionQueue _levelWrites = new();

    private void InitializeLevelHud()
    {
        _masterVolumeWatcher = new MasterVolumeWatcher();
        _masterVolumeWatcher.Changed += (volume, muted) => Dispatcher.BeginInvoke(() =>
            OnSystemLevelChanged(LevelHudKind.SystemVolume, volume, muted));
        _masterVolumeWatcher.Start();

        InitializeMicMuteHud();

        _brightnessWatcher = new BrightnessWatcher();
        _brightnessWatcher.Changed += percent => Dispatcher.BeginInvoke(() =>
            OnSystemLevelChanged(LevelHudKind.Brightness, percent / 100f, muted: false));
        _brightnessWatcher.Start();

        if (LegionService.IsSupported)
        {
            _powerModeWatcher = new LenovoPowerModeWatcher();
            _powerModeWatcher.Changed += mode => Dispatcher.BeginInvoke(() => OnPowerModeChanged(mode));
            _powerModeWatcher.Start();
        }

        InitializeWindowsOsdHider();
        InitializeOutputSwipe();
        InitializeOutputAuto();
    }

    private void OnPowerModeChanged(int rawMode)
    {
        if (_cleanedUp || _settings == null || _isGreetingActive || !CanShowClaudeAttention) return;
        // While dragging the HUD its own writes come back here.
        if (_isDraggingVolumeIndicator && _levelHudKind == LevelHudKind.PowerMode) return;
        _powerModeLook = VNotch.Controls.ControlCenterView.LegionModeLook(rawMode);
        bool refresh = _isVolumeIndicatorActive && _levelHudKind == LevelHudKind.PowerMode;
        ShowLevelIndicator(_powerModeLook.Step / (float)PowerModeSteps, LevelHudKind.PowerMode);
        // Same kind already on screen: ShowLevelIndicator keeps the look, so the new mode's colour goes on here.
        if (refresh) ApplyLevelHudLook();
        _powerModeRawShown = rawMode;
        // The notch shows it now, so Lenovo's own overlay for this press goes.
        if (_isVolumeIndicatorActive && _levelHudKind == LevelHudKind.PowerMode) HideLenovoOsdForPowerMode();
    }

    private void DisposeLevelHud()
    {
        DisposeMicMuteHud();
        _powerModeWatcher?.Dispose();
        DisposeOutputSwipe();
        DisposeWindowsOsdHider();
        _masterVolumeWatcher?.Dispose();
        _brightnessWatcher?.Dispose();
        _levelWrites.Dispose();
    }

    private void OnSystemLevelChanged(LevelHudKind kind, float level, bool muted)
    {
        if (_cleanedUp || _settings == null || _isGreetingActive || !CanShowClaudeAttention) return;
        // While dragging the HUD its own writes come back here.
        if (_isDraggingVolumeIndicator) return;
        // The scroll HUD shows the player's volume right now; leave it to that.
        if (kind == LevelHudKind.SystemVolume && _isVolumeIndicatorActive && _levelHudKind == LevelHudKind.AppVolume) return;
        // A real volume press wants its number at once, not the speaker's name from a moment ago.
        _levelHudLabelOverride = null;
        _levelHudMuted = muted;
        ShowLevelIndicator(muted ? 0f : level, kind);
        // The notch shows it now, so Windows' own pill for the same key goes.
        if (_isVolumeIndicatorActive && _levelHudKind == kind) HideWindowsOsdForLevelHud();
    }

    /// <summary>Dragging the HUD sets what it shows: the system volume or the brightness.</summary>
    private void WriteSystemLevel(float level)
    {
        switch (_levelHudKind)
        {
            case LevelHudKind.SystemVolume when _masterVolumeWatcher is { } volume:
                if (level > 0) _levelHudMuted = false;
                _levelWrites.Post("volume", () => volume.SetVolume(level));
                break;
            case LevelHudKind.Brightness when _brightnessWatcher is { } brightness:
                int percent = (int)Math.Round(level * 100);
                _levelWrites.Post("brightness", () => brightness.SetBrightness(percent));
                break;
            case LevelHudKind.PowerMode:
                var mode = PowerModeByStep[PowerModeStep(level) - 1];
                if ((int)mode == _powerModeRawShown) break;
                _powerModeRawShown = (int)mode;
                _powerModeLook = VNotch.Controls.ControlCenterView.LegionModeLook((int)mode);
                ApplyLevelHudLook();
                UpdateVolumeIndicatorHud(level, animate: true);
                _levelWrites.Post("powermode", () => LegionService.SetModeAsync(mode).GetAwaiter().GetResult());
                break;
        }
    }

    private int _powerModeRawShown = -1;

    /// <summary>A drag position or level as one of the four steps (any touch of the bar picks at least Quiet).</summary>
    private static int PowerModeStep(double level) =>
        Math.Clamp((int)Math.Ceiling(level * PowerModeSteps - 0.001), 1, PowerModeSteps);

    /// <summary>The music's colour for volume while music shows, warm sand for brightness, else white.</summary>
    private (Color Color, bool Has) EffectiveHudTint() => _levelHudKind switch
    {
        LevelHudKind.Brightness => (BrightnessHudTint, true),
        LevelHudKind.PowerMode => (_powerModeLook.Glow, true),
        LevelHudKind.SystemVolume or LevelHudKind.MicMute when !_isMusicCompactMode => (Colors.White, false),
        _ => (_hasVolumeHudMediaTint ? _volumeHudMediaColor : Colors.White, _hasVolumeHudMediaTint)
    };

    private bool IsLevelHudMuted(double volume) => volume <= 0.0001 && _levelHudKind switch
    {
        LevelHudKind.SystemVolume => _levelHudMuted,
        LevelHudKind.AppVolume => _viewModel?.AudioMixer?.IsMuted == true,
        _ => false
    };

    /// <summary>Speaker or sun, and the glow in the HUD's tint.</summary>
    private void ApplyLevelHudLook()
    {
        bool sun = _levelHudKind == LevelHudKind.Brightness;
        bool power = _levelHudKind == LevelHudKind.PowerMode;
        bool mic = _levelHudKind == LevelHudKind.MicMute;
        var speaker = sun || power || mic ? Visibility.Collapsed : Visibility.Visible;
        VolumeIndicatorSpeakerCone.Visibility = speaker;
        VolumeIndicatorWave1.Visibility = speaker;
        VolumeIndicatorWave2.Visibility = speaker;
        ApplyPowerModeGlyph(power);
        if (sun || power || mic)
        {
            VolumeIndicatorMuteSlash.Visibility = Visibility.Collapsed;
            VolumeIndicatorMuteSlashGap.Visibility = Visibility.Collapsed;
        }
        VolumeIndicatorSun.Visibility = sun ? Visibility.Visible : Visibility.Collapsed;
        VolumeIndicatorSunRays.Visibility = sun ? Visibility.Visible : Visibility.Collapsed;
        VolumeIndicatorMic.Visibility = mic ? Visibility.Visible : Visibility.Collapsed;
        VolumeIndicatorMicWord.Visibility = mic ? Visibility.Visible : Visibility.Collapsed;
        VolumeIndicatorTicksHost.Visibility = mic ? Visibility.Collapsed : Visibility.Visible;
        VolumeIndicatorTicksRow.Height = new GridLength(mic ? 0 : 14);
        VolumeIndicatorPercentText.Visibility = mic ? Visibility.Collapsed : Visibility.Visible;
        VolumeIndicatorContainer.IsHitTestVisible = !mic;
        if (mic)
        {
            Motion.Stop(VolumeIndicatorIconScale, ScaleTransform.ScaleXProperty, 1);
            Motion.Stop(VolumeIndicatorIconScale, ScaleTransform.ScaleYProperty, 1);
            // A tint refresh must not reveal the pending face before the dive's hidden midpoint.
            if (!Motion.IsRunning(VolumeIndicatorMicShift, TranslateTransform.YProperty)) ApplyMicMuteFace(_micMuteShown);
        }

        var (tint, has) = EffectiveHudTint();
        Color glow = has ? tint : Colors.White;
        VolumeIndicatorTicksGlow.Color = glow;
        VolumeIndicatorIconGlow.Color = glow;
    }

    /// <summary>The sun's rays grow and brighten with the level, and the icon hops when a tick lights or goes out.</summary>
    private void UpdateSunIconState(double level, Color accent, bool animate)
    {
        TintPath(VolumeIndicatorSun, accent, fill: true);
        TintPath(VolumeIndicatorSunRays, accent, fill: false);
        double t = Math.Clamp(level, 0, 1);
        double rayScale = 0.78 + 0.22 * t;
        if (animate)
        {
            Motion.To(VolumeIndicatorSunRaysScale, ScaleTransform.ScaleXProperty, rayScale, Motion.Glide);
            Motion.To(VolumeIndicatorSunRaysScale, ScaleTransform.ScaleYProperty, rayScale, Motion.Glide);
            Motion.To(VolumeIndicatorSunRays, OpacityProperty, 0.5 + 0.5 * t, Motion.Glide);
            Motion.To(VolumeIndicatorIconScale, ScaleTransform.ScaleXProperty, 1, Motion.Press,
                velocity: Motion.VelocityOf(VolumeIndicatorIconScale, ScaleTransform.ScaleXProperty) + 2.5);
            Motion.To(VolumeIndicatorIconScale, ScaleTransform.ScaleYProperty, 1, Motion.Press,
                velocity: Motion.VelocityOf(VolumeIndicatorIconScale, ScaleTransform.ScaleYProperty) + 2.5);
        }
        else
        {
            Motion.Stop(VolumeIndicatorSunRaysScale, ScaleTransform.ScaleXProperty, rayScale);
            Motion.Stop(VolumeIndicatorSunRaysScale, ScaleTransform.ScaleYProperty, rayScale);
            Motion.Stop(VolumeIndicatorSunRays, OpacityProperty, 0.5 + 0.5 * t);
        }
    }

    private ScaleTransform? _powerModeIconScale;

    /// <summary>The power mode wears its own glyph (moon, balance, gauge, bolt) instead of speaker or sun.</summary>
    private void ApplyPowerModeGlyph(bool power)
    {
        VolumeIndicatorIcon.Visibility = power ? Visibility.Visible : Visibility.Collapsed;
        if (!power) return;
        if (_powerModeIconScale == null)
        {
            _powerModeIconScale = new ScaleTransform(1, 1);
            VolumeIndicatorIcon.FontSize = 15;
            VolumeIndicatorIcon.HorizontalAlignment = HorizontalAlignment.Left;
            VolumeIndicatorIcon.VerticalAlignment = VerticalAlignment.Center;
            VolumeIndicatorIcon.Margin = new Thickness(1, 0, 0, 0);
            VolumeIndicatorIcon.RenderTransformOrigin = new Point(0.5, 0.5);
            VolumeIndicatorIcon.RenderTransform = _powerModeIconScale;
        }
        VolumeIndicatorIcon.Text = _powerModeLook.Glyph;
        VolumeIndicatorIcon.Foreground = new SolidColorBrush(_powerModeLook.Glow);
    }

    /// <summary>The glyph hops when the mode steps up or down, a touch more going up.</summary>
    private void UpdatePowerModeIconState(bool animate)
    {
        int step = _powerModeLook.Step;
        int previous = _powerModeIconStep;
        _powerModeIconStep = step;
        if (!animate || previous == 0 || previous == step || _powerModeIconScale == null) return;
        double impulse = step > previous ? 3.5 : 1.9;
        Motion.To(_powerModeIconScale, ScaleTransform.ScaleXProperty, 1, Motion.Press,
            velocity: Motion.VelocityOf(_powerModeIconScale, ScaleTransform.ScaleXProperty) + impulse);
        Motion.To(_powerModeIconScale, ScaleTransform.ScaleYProperty, 1, Motion.Press,
            velocity: Motion.VelocityOf(_powerModeIconScale, ScaleTransform.ScaleYProperty) + impulse);
    }

    private static void TintPath(Shape path, Color color, bool fill)
    {
        var current = fill ? path.Fill : path.Stroke;
        if (current is SolidColorBrush brush && !brush.IsFrozen)
        {
            brush.Color = color;
            return;
        }
        if (fill) path.Fill = new SolidColorBrush(color);
        else path.Stroke = new SolidColorBrush(color);
    }

    /// <summary>As wide as the HUD over music, also when the notch rests slim.</summary>
    private double LevelHudWidth(double transitionWidth)
    {
        if (_isMusicCompactMode) return transitionWidth;
        double resting = _settings.EnableDynamicIslandMode ? _settings.DynamicIslandWidth : _settings.Width;
        return Math.Max(transitionWidth, resting + VolumeHudExtraWidth);
    }

    private void HideIdleUnderHud()
    {
        _levelHudOverIdle = true;
        int token = _volumeIndicatorToken;
        CollapsedContent.BeginAnimation(OpacityProperty, null);
        var fade = MakeAnim(CollapsedContent.Opacity, 0.0, _dur150, _easeQuadOut);
        fade.Completed += (_, _) =>
        {
            if (_isVolumeIndicatorActive && _levelHudOverIdle && !IsCompactSlotStale(token))
                CollapsedContent.Visibility = Visibility.Collapsed;
        };
        CollapsedContent.BeginAnimation(OpacityProperty, fade);
    }

    private void RestoreIdleUnderHud()
    {
        _levelHudOverIdle = false;
        if (_isMusicCompactMode && _currentMediaInfo != null) return;
        CollapsedContent.Visibility = Visibility.Visible;
        CollapsedContent.BeginAnimation(OpacityProperty, MakeCompactContentRestore());
    }

    /// <summary>The HUD was dropped at once (the notch opens, another alert takes over).</summary>
    private void ReleaseIdleUnderHud()
    {
        if (!_levelHudOverIdle) return;
        _levelHudOverIdle = false;
        // Whoever took the closed notch, or the opening notch, decides what shows now.
        if (_isExpanded || _isAnimating || _compactPillArbiter.ActiveSlot != CompactPillSlot.None) return;
        if (_isMusicCompactMode && _currentMediaInfo != null) return;
        CollapsedContent.BeginAnimation(OpacityProperty, null);
        CollapsedContent.Opacity = 1;
        CollapsedContent.Visibility = Visibility.Visible;
    }

    #endregion
}
