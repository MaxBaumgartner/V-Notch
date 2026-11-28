using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using VNotch.Interop;
using VNotch.Models;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;
using static VNotch.Services.Win32Interop;

namespace VNotch;

public partial class MainWindow
{
    private const string MediaCtrlLogTag = "MEDIA-CTRL";
    private bool _isPlaying = true;

    private async Task TogglePlayPauseInstantAsync()
    {
        try
        {
            if ((DateTime.UtcNow - _lastMediaActionTime).TotalMilliseconds < 250) return;
            _lastMediaActionTime = DateTime.UtcNow;

            WakeFromIdle();

            if (_isVolumeIndicatorActive)
            {
                DismissVolumeIndicatorImmediate(restoreMedia: true);
            }

            _isPlaying = !_viewModel.Media.IsPlaying;
            UpdatePlayPauseIcon();

            if (!_isExpanded && !_isAnimating &&
                !_transitionCoordinator.IsTransitionActive &&
                _transitionCoordinator.CurrentView == NotchView.Compact)
            {
                PlayGestureMiddleClickFeedback();
            }

            if (_isExpanded && MediaPlayback.PlayPauseButton != null && MediaPlayback.PlayPauseButton.Visibility == Visibility.Visible)
            {
                PlayGentleButtonPressAnimation(MediaPlayback.PlayPauseButton);
            }
            else if (_isExpanded && InlinePlayPauseButton != null && InlinePlayPauseButton.Visibility == Visibility.Visible)
            {
                PlayGentleButtonPressAnimation(InlinePlayPauseButton);
            }

            _progressEngine.NotifyUserPlayPause(_isPlaying);
            await _viewModel.Media.PlayPauseCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            RuntimeLog.Error(MediaCtrlLogTag, ex, "MiddleClick/PlayPause failed");
        }
    }

    private void PlayPauseButton_Click(object sender, MouseButtonEventArgs e) => MediaPlayback.PlayPauseButton_Click(sender, e);

    private void NextButton_Click(object sender, MouseButtonEventArgs e) => MediaPlayback.NextButton_Click(sender, e);

    private void PrevButton_Click(object sender, MouseButtonEventArgs e) => MediaPlayback.PrevButton_Click(sender, e);

    private bool? _presentedInlinePlaying;
    private static readonly DependencyProperty InlinePlaybackMorphProperty = DependencyProperty.Register(
        "InlinePlaybackMorph", typeof(double), typeof(MainWindow), new PropertyMetadata(0d,
            (target, args) => ((MainWindow)target).RenderInlinePlaybackMorph((double)args.NewValue)));

    private void RenderInlinePlaybackMorph(double progress)
    {
        InlinePauseIconPath.Stretch = Stretch.None;
        InlinePauseIconPath.Data = VNotch.Controls.MediaPlaybackView.PlaybackGeometry(progress, 16);
        InlinePauseIcon.Visibility = Visibility.Visible;
        InlinePlayIcon.Visibility = Visibility.Collapsed;
    }

    private void OnViewModelPlayPauseToggled(object? sender, bool isPlaying)
    {
        _lastMediaActionTime = DateTime.UtcNow;
        _isPlaying = isPlaying;
        UpdatePlayPauseIcon();
    }

    private void UpdatePlayPauseIcon()
    {
        RefreshPlaybackAccessibleNames();
        MediaPlayback.PresentPlayback(_isPlaying);
        if (_presentedInlinePlaying == _isPlaying) return;
        _presentedInlinePlaying = _isPlaying;
        RenderInlinePlaybackMorph((double)GetValue(InlinePlaybackMorphProperty));
        if (InlinePlayPauseButton.IsVisible) Motion.To(this, InlinePlaybackMorphProperty, _isPlaying ? 0 : 1, Motion.Glide);
        else Motion.Stop(this, InlinePlaybackMorphProperty, _isPlaying ? 0 : 1);
    }

    private void InlinePlayPauseButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ExecuteInlinePlayPauseAsync().SafeFireAndForget(MediaCtrlLogTag);
    }

    private async Task ExecuteInlinePlayPauseAsync()
    {
        try
        {
            _lastMediaActionTime = DateTime.UtcNow;
            _isPlaying = !_viewModel.Media.IsPlaying;
            UpdatePlayPauseIcon();
            PlayButtonPressAnimation(InlinePlayPauseButton);

            _progressEngine.NotifyUserPlayPause(_isPlaying);

            await _viewModel.Media.PlayPauseCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            RuntimeLog.Error(MediaCtrlLogTag, ex, "InlinePlayPause failed");
        }
    }

    private void InlineNextButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ExecuteInlineNextAsync().SafeFireAndForget(MediaCtrlLogTag);
    }

    private async Task ExecuteInlineNextAsync()
    {
        try
        {
            PlayButtonPressAnimation(InlineNextButton);
            PlayNextSkipAnimation(InlineNextArrow0, InlineNextArrow1);

            OptimisticPrepareForNextTrack();
            await _viewModel.Media.NextTrackCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            RuntimeLog.Error(MediaCtrlLogTag, ex, "InlineNext failed");
        }
    }

    private void InlinePrevButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ExecuteInlinePrevAsync().SafeFireAndForget(MediaCtrlLogTag);
    }

    private async Task ExecuteInlinePrevAsync()
    {
        try
        {
            PlayButtonPressAnimation(InlinePrevButton);
            PlayPrevSkipAnimation(InlinePrevArrow0, InlinePrevArrow2);

            PrepareForPreviousTrackRequest();
            await _viewModel.Media.PreviousTrackCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            RuntimeLog.Error(MediaCtrlLogTag, ex, "InlinePrev failed");
        }
    }

    private void PrepareForPreviousTrackRequest() => MediaPlayback.PrepareForPreviousTrackRequest();
    private void OptimisticPrepareForNextTrack() => MediaPlayback.OptimisticPrepareForNextTrack();

    private void ThumbnailBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        OpenCurrentMediaSourceFromThumbnail();
    }

    private void CompactThumbnailBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_settings.EnableGestureControls && !_isExpanded && !_isMusicExpanded &&
            _currentMediaInfo != null && _currentMediaInfo.IsAnyMediaPlaying && !_isAnimating)
        {
            return;
        }

        e.Handled = true;
        if (!_isExpanded && !_isAnimating)
        {
            ExpandNotch();
        }
    }

    private void OpenCurrentMediaSourceFromThumbnail()
    {
        var info = _currentMediaInfo;
        if (info == null || !info.IsAnyMediaPlaying) return;
        Task.Run(() => MediaWindowActivator.TryActivateForMedia(info), System.Threading.CancellationToken.None)
            .SafeFireAndForget("MEDIA-ACTIVATE");
    }

    private void MediaButton_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Border button && button.RenderTransform is ScaleTransform transform)
        {
            button.CacheMode = null;

            var animX = new DoubleAnimation(transform.ScaleX, 1.18, _dur150) { EasingFunction = _easeQuadOut };
            var animY = new DoubleAnimation(transform.ScaleY, 1.18, _dur150) { EasingFunction = _easeQuadOut };
            Timeline.SetDesiredFrameRate(animX, VNotch.Services.AnimationConfig.TargetFps);
            Timeline.SetDesiredFrameRate(animY, VNotch.Services.AnimationConfig.TargetFps);
            transform.BeginAnimation(ScaleTransform.ScaleXProperty, animX);
            transform.BeginAnimation(ScaleTransform.ScaleYProperty, animY);
        }
    }

    private void MediaButton_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Border button && button.RenderTransform is ScaleTransform transform)
        {
            var animX = new DoubleAnimation(transform.ScaleX, 1.0, _dur200) { EasingFunction = _easeQuadOut };
            var animY = new DoubleAnimation(transform.ScaleY, 1.0, _dur200) { EasingFunction = _easeQuadOut };
            Timeline.SetDesiredFrameRate(animX, VNotch.Services.AnimationConfig.TargetFps);
            Timeline.SetDesiredFrameRate(animY, VNotch.Services.AnimationConfig.TargetFps);

            animX.Completed += (_, _) =>
            {
                if (!button.IsMouseOver)
                    button.CacheMode = null;
            };

            transform.BeginAnimation(ScaleTransform.ScaleXProperty, animX);
            transform.BeginAnimation(ScaleTransform.ScaleYProperty, animY);
        }
    }

    private float _currentVolume = 0.5f;
    private const float VolumeScrollStep = 0.05f;
    private const double CompactVolumeClickSuppressMs = 600.0;
    private UiScheduledTimer? _volumeIndicatorHideTimer;
    private bool _isVolumeIndicatorActive = false;
    private bool _volumeSynced = false;
    private int _volumeIndicatorToken = 0;
    private double _compactVolumeWheelAccumulator = 0;
    private DateTime _lastCompactVolumeWheelUtc = DateTime.MinValue;
    private DateTime _suppressCompactVolumeWheelUntilUtc = DateTime.MinValue;
    private bool _isCompactVolumeWheelActive = false;

    private void SuppressCompactVolumeWheelForClick()
    {
        _suppressCompactVolumeWheelUntilUtc = DateTime.UtcNow.AddMilliseconds(CompactVolumeClickSuppressMs);
        ResetCompactVolumeWheelIntent();
    }

    private void ResetCompactVolumeWheelIntent()
    {
        ++_volumeBaselineVersion;
        _volumeBaselinePending = false;
        _compactVolumeWheelAccumulator = 0;
        _lastCompactVolumeWheelUtc = DateTime.MinValue;
        _isCompactVolumeWheelActive = false;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnVolumeWheelState
    {
        public double Accumulator;
        public long LastWheelUtcTicks;
        public long SuppressUntilUtcTicks;
        public byte IsWheelActive;
        public byte Reserved0;
        public byte Reserved1;
        public byte Reserved2;
    }

    [LibraryImport(NativeAbi.LibraryName)]
    private static partial int vn_mainwindow_step_volume_wheel(
        ref VnVolumeWheelState ioState,
        int rawDelta,
        long nowUtcTicks,
        byte isIndicatorActive,
        out int outVolumeDelta,
        out byte outAccepted,
        out byte outSuppressed);

    private bool TryGetCompactVolumeWheelDelta(int rawDelta, out int volumeDelta)
    {
        volumeDelta = 0;
        if (rawDelta == 0) return false;

        var state = new VnVolumeWheelState
        {
            Accumulator = _compactVolumeWheelAccumulator,
            LastWheelUtcTicks = _lastCompactVolumeWheelUtc.Ticks,
            SuppressUntilUtcTicks = _suppressCompactVolumeWheelUntilUtc.Ticks,
            IsWheelActive = _isCompactVolumeWheelActive ? (byte)1 : (byte)0
        };

        if (vn_mainwindow_step_volume_wheel(
                ref state,
                rawDelta,
                DateTime.UtcNow.Ticks,
                _isVolumeIndicatorActive ? (byte)1 : (byte)0,
                out volumeDelta,
                out byte accepted,
                out byte suppressed) != 0)
        {
            return false;
        }

        if (suppressed != 0)
        {
            ResetCompactVolumeWheelIntent();
            return false;
        }

        _compactVolumeWheelAccumulator = state.Accumulator;
        _lastCompactVolumeWheelUtc = state.LastWheelUtcTicks <= 0
            ? DateTime.MinValue
            : new DateTime(state.LastWheelUtcTicks, DateTimeKind.Utc);
        _isCompactVolumeWheelActive = state.IsWheelActive != 0;
        return accepted != 0;
    }

    private bool _volumeBaselinePending;
    private int _volumeBaselineVersion;
    private DateTime _volumeScrollSyncAfterUtc = DateTime.MinValue;

    private const int VolumeHudTickCount = 40;
    private const double VolumeHudExtraWidth = 44.0;
    private const double VolumeHudExtraHeight = 20.0;



    private Border[]? _volumeHudTickBorders;
    private SolidColorBrush[]? _volumeHudTickBrushes;
    private ScaleTransform[]? _volumeHudTickScales;
    private Color _volumeHudMediaColor = Colors.White;
    private bool _hasVolumeHudMediaTint;
    private int _lastVolumeHudActiveTicks = -1;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnVolumeHudLayoutOut
    {
        public int Percentage;
        public int ActiveTicks;
        public int HighlightStartTick;
        public int IconCodepoint;
        public double LabelOffsetX;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnVolumeHudOverscrollOut
    {
        public double ScaleX;
        public double ScaleY;
        public double TranslateX;
        public double OriginX;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnVolumeHudTransitionParamsOut
    {
        public double HudFromScaleX;
        public double HudFromScaleY;
        public double HudToScaleX;
        public double HudToScaleY;
        public double HudFromTranslateY;
        public double HudToTranslateY;
        public double HudFromBlur;
        public double HudToBlur;
        public double MediaToScale;
        public double MediaToBlur;
        public double TargetWidth;
        public double TargetHeight;
        public double TargetCornerRadius;
        public int MorphDurationMs;
        public int FadeDurationMs;
    }

    [LibraryImport(NativeAbi.LibraryName)]
    private static unsafe partial int vn_animation_compute_volume_hud_state(
        double volume,
        byte muted,
        int tickCount,
        double trackWidth,
        double labelWidth,
        double minLeftX,
        out VnVolumeHudLayoutOut outLayout,
        byte* outTickStates,
        int tickCap);

    [LibraryImport(NativeAbi.LibraryName)]
    private static unsafe partial int vn_animation_compute_volume_hud_palette(
        int tickCount,
        int activeTicks,
        int highlightStartTick,
        byte mediaR,
        byte mediaG,
        byte mediaB,
        byte hasMediaTint,
        uint* outArgb,
        double* outScaleY,
        int tickCap);

    [LibraryImport(NativeAbi.LibraryName)]
    private static partial int vn_animation_compute_volume_overscroll_spring(
        double currentVolume,
        double stepDelta,
        out VnVolumeHudOverscrollOut outOverscroll);

    [LibraryImport(NativeAbi.LibraryName)]
    private static partial int vn_animation_compute_volume_hud_transition(
        byte entering,
        byte islandMode,
        byte enableBlur,
        double collapsedWidth,
        double collapsedHeight,
        double collapsedRadius,
        out VnVolumeHudTransitionParamsOut outParams);

    private void ClearVolumeIndicatorTransitionEffects()
    {
        if (VolumeIndicatorContainer == null) return;
        VolumeIndicatorContainer.Effect = null;
        VolumeIndicatorContainer.CacheMode = null;
    }

    private void SetVolumeHudMediaTint(Color vibrantColor, bool hasMediaTint)
    {
        _volumeHudMediaColor = vibrantColor;
        _hasVolumeHudMediaTint = hasMediaTint;
        var (tint, hasTint) = EffectiveHudTint();
        Color glowColor = hasTint ? tint : Colors.White;
        if (VolumeIndicatorTicksGlow != null) VolumeIndicatorTicksGlow.Color = glowColor;
        if (VolumeIndicatorIconGlow != null) VolumeIndicatorIconGlow.Color = glowColor;
        if (_isVolumeIndicatorActive && VolumeIndicatorScale != null)
        {
            UpdateVolumeIndicatorHud(VolumeIndicatorScale.ScaleX, animate: false);
        }
    }

    private void EnsureVolumeIndicatorTicks()
    {
        if (_volumeHudTickBorders != null || VolumeIndicatorTicksHost == null) return;
        VolumeIndicatorTicksHost.Children.Clear();
        _volumeHudTickBorders = new Border[VolumeHudTickCount];
        _volumeHudTickBrushes = new SolidColorBrush[VolumeHudTickCount];
        _volumeHudTickScales = new ScaleTransform[VolumeHudTickCount];
        for (int i = 0; i < VolumeHudTickCount; i++)
        {
            var brush = new SolidColorBrush(Color.FromArgb(0x48, 0xFF, 0xFF, 0xFF));
            var scale = new ScaleTransform(1.0, 0.86);
            var tick = new Border
            {
                Width = 2.4,
                Height = 9.2,
                CornerRadius = new CornerRadius(1.2),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Background = brush,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = scale,
                SnapsToDevicePixels = false
            };
            _volumeHudTickBorders[i] = tick;
            _volumeHudTickBrushes[i] = brush;
            _volumeHudTickScales[i] = scale;
            VolumeIndicatorTicksHost.Children.Add(tick);
        }
    }

    private void UpdateSolidSpeakerIconState(int iconCodepoint, Color accentColor, bool animate)
    {
        if (VolumeIndicatorSpeakerCone == null) return;
        bool isMuted = iconCodepoint == 0xE74F;
        bool hasWave1 = !isMuted && iconCodepoint >= 0xE993;
        bool hasWave2 = !isMuted && iconCodepoint >= 0xE995;

        AnimateSpeakerParts(isMuted, isMuted ? 0 : hasWave1 ? 1.0 : 0.22,
            isMuted ? 0 : hasWave2 ? 1.0 : iconCodepoint == 0xE994 ? 0.32 : 0.15, animate);
        if (VolumeIndicatorMuteSlash != null)
        {
            if (VolumeIndicatorMuteSlash.Stroke is SolidColorBrush slashBrush && !slashBrush.IsFrozen)
                slashBrush.Color = accentColor;
            else
                VolumeIndicatorMuteSlash.Stroke = new SolidColorBrush(accentColor);
        }
        if (VolumeIndicatorSpeakerCone.Fill is SolidColorBrush coneBrush && !coneBrush.IsFrozen)
            coneBrush.Color = accentColor;
        else
            VolumeIndicatorSpeakerCone.Fill = new SolidColorBrush(accentColor);

        if (VolumeIndicatorSpeakerCone.Stroke is SolidColorBrush coneStroke && !coneStroke.IsFrozen)
            coneStroke.Color = accentColor;
        else
            VolumeIndicatorSpeakerCone.Stroke = new SolidColorBrush(accentColor);

        if (VolumeIndicatorWave1?.Stroke is SolidColorBrush w1Brush && !w1Brush.IsFrozen)
            w1Brush.Color = accentColor;
        else if (VolumeIndicatorWave1 != null)
            VolumeIndicatorWave1.Stroke = new SolidColorBrush(accentColor);

        if (VolumeIndicatorWave2?.Stroke is SolidColorBrush w2Brush && !w2Brush.IsFrozen)
            w2Brush.Color = accentColor;
        else if (VolumeIndicatorWave2 != null)
            VolumeIndicatorWave2.Stroke = new SolidColorBrush(accentColor);

        // The whole speaker still gives its small hop on every step, as before the waves learned to fade.
        if (animate && !AnimationConfig.ReduceMotion && VolumeIndicatorIconScale != null)
        {
            var pop = new DoubleAnimationUsingKeyFrames();
            pop.KeyFrames.Add(new EasingDoubleKeyFrame(1.16, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(75)), _easeQuadOut));
            pop.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(220)), _easeSoftSpring));
            Timeline.SetDesiredFrameRate(pop, AnimationConfig.TargetFps);
            VolumeIndicatorIconScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            VolumeIndicatorIconScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        }
    }

    private bool? _speakerPartsMuted;
    private int _speakerSlashToken;

    /// <summary>The speaker waves fade with volume and the mute slash draws across the icon.</summary>
    private void AnimateSpeakerParts(bool muted, double wave1, double wave2, bool animate)
    {
        bool motion = animate && !AnimationConfig.ReduceMotion;
        AnimateSpeakerWave(VolumeIndicatorWave1, wave1, motion);
        AnimateSpeakerWave(VolumeIndicatorWave2, wave2, motion);
        if (_speakerPartsMuted == muted && motion) return;
        _speakerPartsMuted = muted;
        int token = ++_speakerSlashToken;
        var slash = VolumeIndicatorMuteSlash;
        var gap = VolumeIndicatorMuteSlashGap;
        if (slash == null || gap == null) return;
        // Nothing to wipe away: a slash that is not there must not flash up just to leave again.
        if (motion && !muted && slash.Visibility != Visibility.Visible) return;
        if (!motion)
        {
            slash.BeginAnimation(System.Windows.Shapes.Shape.StrokeDashOffsetProperty, null);
            gap.BeginAnimation(System.Windows.Shapes.Shape.StrokeDashOffsetProperty, null);
            slash.StrokeDashOffset = gap.StrokeDashOffset = 0;
            slash.Visibility = gap.Visibility = muted ? Visibility.Visible : Visibility.Collapsed;
            return;
        }
        const double length = 30;
        slash.BeginAnimation(System.Windows.Shapes.Shape.StrokeDashOffsetProperty, null);
        gap.BeginAnimation(System.Windows.Shapes.Shape.StrokeDashOffsetProperty, null);
        slash.StrokeDashOffset = gap.StrokeDashOffset = muted ? length : 0;
        slash.Visibility = gap.Visibility = Visibility.Visible;
        var duration = TimeSpan.FromMilliseconds(muted ? 180 : 160);
        var stroke = MakeAnim(muted ? length : 0, muted ? 0 : -length, duration, _easeQuadOut);
        stroke.Completed += (_, _) =>
        {
            if (token == _speakerSlashToken && !muted)
                slash.Visibility = gap.Visibility = Visibility.Collapsed;
        };
        slash.BeginAnimation(System.Windows.Shapes.Shape.StrokeDashOffsetProperty, stroke);
        gap.BeginAnimation(System.Windows.Shapes.Shape.StrokeDashOffsetProperty,
            MakeAnim(muted ? length : 0, muted ? 0 : -length, duration, _easeQuadOut));
    }

    private void AnimateSpeakerWave(System.Windows.Shapes.Path wave, double target, bool animate)
    {
        if (wave == null) return;
        double current = wave.Opacity;
        wave.BeginAnimation(OpacityProperty, null);
        wave.Opacity = animate ? current : target;
        if (!animate)
        {
            if (wave.RenderTransform is ScaleTransform existingScale)
            {
                existingScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                existingScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                existingScale.ScaleX = existingScale.ScaleY = 1;
            }
            return;
        }
        wave.BeginAnimation(OpacityProperty, MakeAnim(current, target, TimeSpan.FromMilliseconds(140), _easeQuadOut));
        if (target <= current || target < 1) return;
        if (wave.RenderTransform is not ScaleTransform scale)
        {
            scale = new ScaleTransform(1, 1);
            wave.RenderTransformOrigin = new Point(0.5, 0.5);
            wave.RenderTransform = scale;
        }
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, MakeAnim(0.85, 1, TimeSpan.FromMilliseconds(140), _easeQuadOut));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, MakeAnim(0.85, 1, TimeSpan.FromMilliseconds(140), _easeQuadOut));
    }

    private unsafe void UpdateVolumeIndicatorHud(double volume, bool animate = false)
    {
        if (_levelHudKind == LevelHudKind.MicMute)
        {
            ApplyMicMuteFace(_micMuteShown);
            return;
        }
        if (VolumeIndicatorTicksHost == null) return;
        EnsureVolumeIndicatorTicks();
        if (_volumeHudTickBorders == null || _volumeHudTickBrushes == null || _volumeHudTickScales == null) return;

        byte* tickStates = stackalloc byte[VolumeHudTickCount];
        uint* tickArgb = stackalloc uint[VolumeHudTickCount];
        double* tickScaleY = stackalloc double[VolumeHudTickCount];

        double trackWidth = VolumeIndicatorContainer.ActualWidth > 0
            ? VolumeIndicatorContainer.ActualWidth
            : Math.Max(120.0, (_collapsedWidth + VolumeHudExtraWidth) - 28.0);
        bool isMuted = IsLevelHudMuted(volume);
        var (hudTint, hasHudTint) = EffectiveHudTint();

        double approxLabelWidth = VolumeIndicatorPercentText?.ActualWidth > 0
            ? VolumeIndicatorPercentText.ActualWidth
            : 28.0;

        if (vn_animation_compute_volume_hud_state(
                volume,
                isMuted ? (byte)1 : (byte)0,
                VolumeHudTickCount,
                trackWidth,
                approxLabelWidth,
                30.0,
                out var layout,
                tickStates,
                VolumeHudTickCount) != 0)
        {
            return;
        }

        if (VolumeIndicatorIcon != null && _levelHudKind != LevelHudKind.PowerMode)
        {
            string glyph = ((char)layout.IconCodepoint).ToString();
            if (!string.Equals(VolumeIndicatorIcon.Text, glyph, StringComparison.Ordinal))
                VolumeIndicatorIcon.Text = glyph;
        }

        if (VolumeIndicatorPercentText != null)
        {
            string pctText = _levelHudKind switch
            {
                LevelHudKind.PowerMode => _powerModeLook.Name ?? "",
                LevelHudKind.SystemVolume when _levelHudLabelOverride is { Length: > 0 } name => name,
                _ => $"{layout.Percentage}%"
            };
            if (!string.Equals(VolumeIndicatorPercentText.Text, pctText, StringComparison.Ordinal))
            {
                VolumeIndicatorPercentText.Text = pctText;
                VolumeIndicatorPercentText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            }
            double desiredLabelW = VolumeIndicatorPercentText.DesiredSize.Width > 0 ? VolumeIndicatorPercentText.DesiredSize.Width : approxLabelWidth;
            double measuredLabelW = VolumeIndicatorPercentText.ActualWidth > 0 ? VolumeIndicatorPercentText.ActualWidth : desiredLabelW;

            if (Math.Abs(measuredLabelW - approxLabelWidth) > 0.5)
            {
                _ = vn_animation_compute_volume_hud_state(
                    volume,
                    isMuted ? (byte)1 : (byte)0,
                    VolumeHudTickCount,
                    trackWidth,
                    measuredLabelW,
                    30.0,
                    out layout,
                    tickStates,
                    VolumeHudTickCount);
            }
        }

        if (VolumeIndicatorPercentTranslate != null)
        {
            double currentX = VolumeIndicatorPercentTranslate.X;
            VolumeIndicatorPercentTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            VolumeIndicatorPercentTranslate.X = layout.LabelOffsetX;
            if (animate && !AnimationConfig.ReduceMotion && Math.Abs(currentX - layout.LabelOffsetX) > 0.5)
            {
                var moveAnim = MakeAnim(currentX, layout.LabelOffsetX, _dur150, _easeAppleOut);
                moveAnim.FillBehavior = FillBehavior.Stop;
                VolumeIndicatorPercentTranslate.BeginAnimation(TranslateTransform.XProperty, moveAnim, HandoffBehavior.SnapshotAndReplace);
            }
        }

        if (vn_animation_compute_volume_hud_palette(
                VolumeHudTickCount,
                layout.ActiveTicks,
                layout.HighlightStartTick,
                hudTint.R,
                hudTint.G,
                hudTint.B,
                hasHudTint ? (byte)1 : (byte)0,
                tickArgb,
                tickScaleY,
                VolumeHudTickCount) == 0)
        {
            bool activeChanged = _lastVolumeHudActiveTicks != layout.ActiveTicks;
            _lastVolumeHudActiveTicks = layout.ActiveTicks;

            Color fallbackAccent = hasHudTint ? hudTint : Color.FromArgb(0xE8, 0xFF, 0xFF, 0xFF);
            Color startAccent = layout.ActiveTicks > 0
                ? Color.FromArgb(
                    (byte)(tickArgb[0] >> 24),
                    (byte)(tickArgb[0] >> 16),
                    (byte)(tickArgb[0] >> 8),
                    (byte)tickArgb[0])
                : fallbackAccent;
            if (_levelHudKind == LevelHudKind.Brightness)
                UpdateSunIconState(volume, startAccent, animate && activeChanged);
            else if (_levelHudKind == LevelHudKind.PowerMode)
                UpdatePowerModeIconState(animate);
            else
                UpdateSolidSpeakerIconState(layout.IconCodepoint, startAccent, animate && activeChanged);

            for (int i = 0; i < VolumeHudTickCount; i++)
            {
                uint argb = tickArgb[i];
                var targetColor = Color.FromArgb(
                    (byte)(argb >> 24),
                    (byte)(argb >> 16),
                    (byte)(argb >> 8),
                    (byte)argb);

                var brush = _volumeHudTickBrushes[i];
                if (brush.Color != targetColor)
                {
                    brush.Color = targetColor;
                }

                var scale = _volumeHudTickScales[i];
                double targetSy = tickScaleY[i];
                double currentSy = scale.ScaleY;
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                scale.ScaleY = targetSy;
                if (animate && !AnimationConfig.ReduceMotion && Math.Abs(currentSy - targetSy) > 0.01)
                {
                    var syAnim = MakeAnim(currentSy, targetSy, _dur150, _easeAppleOut);
                    syAnim.FillBehavior = FillBehavior.Stop;
                    scale.BeginAnimation(ScaleTransform.ScaleYProperty, syAnim, HandoffBehavior.SnapshotAndReplace);
                }
            }
        }
    }

    private void PlayVolumeOverscrollSpring(double currentVolume, double stepDelta)
    {
        if (AnimationConfig.ReduceMotion || VolumeIndicatorContainer == null) return;
        if (vn_animation_compute_volume_overscroll_spring(currentVolume, stepDelta, out var over) != 0) return;
        if (Math.Abs(over.TranslateX) < 0.01) return;

        VolumeIndicatorContainer.RenderTransformOrigin = new Point(over.OriginX, 0.5);
        if (_isDraggingVolumeIndicator)
        {
            double width = Math.Max(1, VolumeIndicatorContainer.ActualWidth);
            double offset = Math.Sign(stepDelta) * ShellRubber(Math.Abs(stepDelta) * width, 18);
            SetVolumeIndicatorRubber(offset, DateTime.UtcNow);
            return;
        }
        // A wheel detent gives the small face an impulse; repeated detents inherit the moving spring.
        Motion.To(VolumeIndicatorContentScale, ScaleTransform.ScaleXProperty, 1, Motion.Press,
            velocity: Math.Clamp(Motion.VelocityOf(VolumeIndicatorContentScale, ScaleTransform.ScaleXProperty) + (over.ScaleX - 1) * 20, -4, 4));
        Motion.To(VolumeIndicatorContentScale, ScaleTransform.ScaleYProperty, 1, Motion.Press,
            velocity: Math.Clamp(Motion.VelocityOf(VolumeIndicatorContentScale, ScaleTransform.ScaleYProperty) + (over.ScaleY - 1) * 20, -4, 4));
        Motion.To(VolumeIndicatorContentTranslate, TranslateTransform.XProperty, 0, Motion.Press,
            velocity: Math.Clamp(Motion.VelocityOf(VolumeIndicatorContentTranslate, TranslateTransform.XProperty) + over.TranslateX * 20, -120, 120));
    }

    private DateTime _volumeHudRubberAt;
    private double _volumeHudRubberVelocity;

    private void SetVolumeIndicatorRubber(double offset, DateTime now)
    {
        double dt = (now - _volumeHudRubberAt).TotalSeconds;
        _volumeHudRubberVelocity = dt > 0 && dt < .1 ? (offset - VolumeIndicatorContentTranslate.X) / dt : 0;
        _volumeHudRubberAt = now;
        Motion.Stop(VolumeIndicatorContentTranslate, TranslateTransform.XProperty, offset);
    }

    private void ReleaseVolumeIndicatorRubber()
    {
        double velocity = (DateTime.UtcNow - _volumeHudRubberAt).TotalMilliseconds < 80 ? _volumeHudRubberVelocity : 0;
        Motion.To(VolumeIndicatorContentTranslate, TranslateTransform.XProperty, 0, Motion.Press, velocity: velocity);
        Motion.To(VolumeIndicatorContentScale, ScaleTransform.ScaleXProperty, 1, Motion.Press);
        Motion.To(VolumeIndicatorContentScale, ScaleTransform.ScaleYProperty, 1, Motion.Press);
    }
    private void VolumeIndicatorContainer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (VolumeIndicatorScale != null)
            UpdateVolumeIndicatorHud(VolumeIndicatorScale.ScaleX, animate: false);
    }

    private void SetVolumeFill(ScaleTransform scale, double volume, bool animate = false)
    {
        double target = Math.Clamp(volume, 0, 1);
        double current = scale.ScaleX;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.ScaleX = target;
        if (ReferenceEquals(scale, VolumeIndicatorScale))
        {
            UpdateVolumeIndicatorHud(target, animate);
        }
        if (!animate || AnimationConfig.ReduceMotion || Math.Abs(current - target) < 0.0001) return;

        // Retarget from the visible fill while keeping the final value on the
        // base property, so a drag or keyboard update can take over immediately.
        var animation = MakeAnim(current, target, _dur150, _easeAppleOut);
        animation.FillBehavior = FillBehavior.Stop;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private void AdjustVolumeByScroll(int delta)
    {
        if (!_volumeSynced)
        {
            // Query baseline volume off UI thread to avoid cold COM query stalls,
            // applying volume state once known to prevent flicker.
            if (_volumeBaselinePending) return;
            _volumeBaselinePending = true;
            int version = ++_volumeBaselineVersion;
            var target = CurrentAppVolumeTarget();

            _mediaVolumeWrites.Post("volume", () =>
            {
                bool ok = TryReadAppVolume(target, out float vol, out bool _);
                Dispatcher.BeginInvoke(() =>
                {
                    if (version != _volumeBaselineVersion) return;
                    _volumeBaselinePending = false;
                    if (_isExpanded || _isAnimating ||
                        DateTime.UtcNow < _suppressCompactVolumeWheelUntilUtc) return;
                    if (!ok)
                    {
                        _volumeSynced = false;
                        ResetCompactVolumeWheelIntent();
                        RuntimeLog.Log(MediaCtrlLogTag, "No audio session found for wheel volume control.");
                        return;
                    }
                    _currentVolume = vol;
                    _volumeSynced = true;
                    ApplyVolumeStep(delta);
                });
            });
            return;
        }

        ApplyVolumeStep(delta);
    }

    private void ApplyVolumeStep(int delta)
    {
        _volumeInteractionVersion++;
        _volumeScrollSyncAfterUtc = AnimationConfig.ReduceMotion ? DateTime.MinValue : DateTime.UtcNow.Add(_dur150.TimeSpan);
        float step = (delta / 120f) * VolumeScrollStep;
        double priorVolume = _currentVolume;
        float newVolume = Math.Clamp(_currentVolume + step, 0f, 1f);
        _currentVolume = newVolume;

        ShowVolumeIndicator(newVolume);
        if ((priorVolume + step > 1.0001) || (priorVolume + step < -0.0001))
        {
            PlayVolumeOverscrollSpring(priorVolume, step);
        }

        QueueSessionVolumeWrite(newVolume, (float)priorVolume);

        if (_isExpanded)
        {
            SetVolumeFill(VolumeBarScale, newVolume, animate: true);
            UpdateVolumeIcon(newVolume, false);
        }
    }

    private void QueueSessionVolumeWrite(float volume, float fallbackVolume)
    {
        int version = _volumeInteractionVersion;
        var target = CurrentAppVolumeTarget();
        _mediaVolumeWrites.Post("volume", () =>
        {
            if (TryWriteAppVolume(target, volume)) return;
            Dispatcher.BeginInvoke(() =>
            {
                if (version != _volumeInteractionVersion) return;
                _currentVolume = fallbackVolume;
                SetVolumeFill(VolumeBarScale, fallbackVolume);
                UpdateVolumeIcon(fallbackVolume, false);
                DismissVolumeIndicatorImmediate(restoreMedia: true);
                RuntimeLog.Log(MediaCtrlLogTag, "Session volume write failed; refreshing audio state.");
                _pendingMuteRefreshVersion = version;
                _ = SyncVolumeFromActiveSession();
            });
        });
    }

    private void ShowVolumeIndicator(float volume) => ShowLevelIndicator(volume, LevelHudKind.AppVolume);

    private VNotch.Presenters.CompactSurfaceAttachment? _levelHudAttachment;

    /// <summary>The scroll HUD for the player's volume, the system volume or the brightness.</summary>
    private void ShowLevelIndicator(float volume, LevelHudKind kind)
    {
        if (VolumeIndicatorContainer == null || VolumeIndicatorFill == null) return;
        // The player's own volume only exists with music; system volume and brightness show either way.
        if (!_isMusicCompactMode && kind == LevelHudKind.AppVolume) return;
        if (_isExpanded || _isAnimating) return;

        bool animateFill = _isVolumeIndicatorActive;
        if (!_hasVolumeHudMediaTint && _currentMediaInfo?.Thumbnail != null &&
            (_currentMediaInfo.IsAnyMediaPlaying || !string.IsNullOrEmpty(_currentMediaInfo.CurrentTrack)))
        {
            UpdateMediaBackground(_currentMediaInfo, forceRefresh: true);
        }

        if (!_isVolumeIndicatorActive)
        {
            if (!TryAcquireCompactSlot(VNotch.Controllers.CompactPillSlot.Volume, out int token))
            {
                return;
            }
            _volumeIndicatorToken = token;
        }

        if (!_isVolumeIndicatorActive || kind != _levelHudKind)
        {
            CancelMicDive();
            if (kind == LevelHudKind.MicMute)
            {
                _isDraggingVolumeIndicator = false;
                VolumeIndicatorContainer.ReleaseMouseCapture();
                _levelHudLabelTimer?.Stop();
                _levelHudLabelOverride = null;
            }
            _levelHudKind = kind;
            ApplyLevelHudLook();
        }

        if (!_isVolumeIndicatorActive)
        {
            _isVolumeIndicatorActive = true;
            ++_volumeIndicatorExitVersion;
            _isVolumeIndicatorExiting = false;
            VolumeIndicatorContainer.IsHitTestVisible = kind != LevelHudKind.MicMute;
            int presentationToken = _volumeIndicatorToken;

            _hoverThumbnailDelayTimer.Stop();
            _compactThumbnailHoverLeaveTimer.Stop();

            // Cancel any hover stretch on the pill so the volume HUD stays crisp and unstretched
            NotchScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            NotchScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            NotchShadowScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            NotchShadowScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            NotchScale.ScaleX = NotchScale.ScaleY = 1.0;
            NotchShadowScale.ScaleX = NotchShadowScale.ScaleY = 1.0;
            HoverGlow.BeginAnimation(OpacityProperty, null);
            HoverGlow.Opacity = 0;

            SuppressPrivacyDot();

            bool enableBlur = _settings.EnableBlurEffects && !AnimationConfig.ReduceMotion;
            if (vn_animation_compute_volume_hud_transition(
                    1,
                    _settings.EnableDynamicIslandMode ? (byte)1 : (byte)0,
                    enableBlur ? (byte)1 : (byte)0,
                    _collapsedWidth,
                    _collapsedHeight,
                    _cornerRadiusCollapsed,
                    out var trans) != 0)
            {
                trans = new VnVolumeHudTransitionParamsOut
                {
                    HudFromScaleX = 0.86,
                    HudFromScaleY = 0.78,
                    HudToScaleX = 1.0,
                    HudToScaleY = 1.0,
                    HudFromTranslateY = -6.0,
                    HudToTranslateY = 0.0,
                    HudFromBlur = enableBlur ? 10.0 : 0.0,
                    HudToBlur = 0.0,
                    MediaToScale = 0.76,
                    MediaToBlur = enableBlur ? 7.0 : 0.0,
                    TargetWidth = _collapsedWidth + VolumeHudExtraWidth,
                    TargetHeight = _collapsedHeight + VolumeHudExtraHeight,
                    TargetCornerRadius = _settings.EnableDynamicIslandMode ? (_collapsedHeight + VolumeHudExtraHeight) * 0.5 : Math.Max(_cornerRadiusCollapsed, 18.0),
                    MorphDurationMs = 350,
                    FadeDurationMs = 240
                };
            }

            double targetHeight = trans.TargetHeight;
            double targetWidth = LevelHudWidth(trans.TargetWidth);
            var morphDur = new Duration(TimeSpan.FromMilliseconds(trans.MorphDurationMs));


            _notchShellPresenter?.CancelCurrentAnimation();
            if (_isCompactThumbnailHovered)
            {
                _isCompactThumbnailHovered = false;
                Motion.To(CompactHoverInfo, OpacityProperty, 0, Motion.Exit,
                    done: () => CompactHoverInfo.Visibility = Visibility.Collapsed);
            }
            AnimateCornerRadius(trans.TargetCornerRadius, morphDur.TimeSpan);
            if (!_isMusicCompactMode)
            {
                HideIdleUnderHud();
            }
            else
            {
                MusicViz.BeginAnimation(OpacityProperty, null);
                var vizOut = MakeAnim(1.0, 0.0, _dur200, _easeQuadOut);
                vizOut.Completed += (s, e) =>
                {
                    if (_isVolumeIndicatorActive && !IsCompactSlotStale(presentationToken))
                        MusicViz.Visibility = Visibility.Collapsed;
                };
                MusicViz.BeginAnimation(OpacityProperty, vizOut);

                CompactThumbnailBorder.BeginAnimation(OpacityProperty, null);
                if (!AnimationConfig.ReduceMotion)
                {
                    var thumbShrink = MakeAnim(trans.MediaToScale, _dur200, _easeQuadOut, VNotch.Services.AnimationConfig.TargetFps);
                    CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleXProperty, thumbShrink);
                    CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleYProperty, thumbShrink);
                    if (enableBlur && MusicCompactContentBlur != null)
                    {
                        var thumbBlurOut = MakeAnim(0.0, trans.MediaToBlur, _dur200, _easeQuadOut, VNotch.Services.AnimationConfig.TargetFps);
                        MusicCompactContentBlur.BeginAnimation(BlurEffect.RadiusProperty, thumbBlurOut);
                    }
                }
                var thumbOut = MakeAnim(1.0, 0.0, _dur200, _easeQuadOut);
                thumbOut.Completed += (s, e) =>
                {
                    if (_isVolumeIndicatorActive && !IsCompactSlotStale(presentationToken))
                    {
                        CompactThumbnailBorder.Visibility = Visibility.Collapsed;
                        if (MusicCompactContentBlur != null)
                        {
                            MusicCompactContentBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
                            MusicCompactContentBlur.Radius = 0;
                        }
                    }
                };
                CompactThumbnailBorder.BeginAnimation(OpacityProperty, thumbOut);
            }

            ClearVolumeIndicatorTransitionEffects();
            Motion.Stop(VolumeIndicatorContentScale, ScaleTransform.ScaleXProperty, 1);
            Motion.Stop(VolumeIndicatorContentScale, ScaleTransform.ScaleYProperty, 1);
            Motion.Stop(VolumeIndicatorContentTranslate, TranslateTransform.XProperty, 0);
            Motion.Stop(VolumeIndicatorContentTranslate, TranslateTransform.YProperty, 0);
            VolumeIndicatorContainer.RenderTransformOrigin = new Point(.5, .5);
            _levelHudAttachment ??= new(NotchBorder, VolumeIndicatorContainer,
                () => new Size(_collapsedWidth, _collapsedHeight));
            Motion.Stop(VolumeIndicatorContainer, OpacityProperty, 1);
            VolumeIndicatorContainer.Visibility = Visibility.Visible;
            _levelHudAttachment.MoveTo(targetWidth, targetHeight, Motion.Pop, shown: true);
            EnsureVolumeIndicatorTicks();
        }
        SetVolumeFill(VolumeIndicatorScale, volume, animate: animateFill);

        if (_volumeIndicatorHideTimer == null)
        {
            _volumeIndicatorHideTimer = new UiScheduledTimer
            {
                Interval = TimeSpan.FromMilliseconds(1200)
            };
            _volumeIndicatorHideTimer.Tick += (s, e) =>
            {
                _volumeIndicatorHideTimer.Stop();
                ResetCompactVolumeWheelIntent();
                HideVolumeIndicator();
            };
        }
        _volumeIndicatorHideTimer.Stop();
        _volumeIndicatorHideTimer.Interval = TimeSpan.FromMilliseconds(kind == LevelHudKind.MicMute ? 1400 : 1200);
        _volumeIndicatorHideTimer.Start();
    }
    private void HideVolumeIndicator()
    {
        if (VolumeIndicatorContainer == null) return;
        if (!_isVolumeIndicatorActive || IsCompactSlotStale(_volumeIndicatorToken)) return;
        CancelMicDive();
        ResetCompactVolumeWheelIntent();
        int token = _volumeIndicatorToken;
        int exitVersion = ++_volumeIndicatorExitVersion;

        _isVolumeIndicatorActive = false;
        _volumeSynced = false;
        _compactPillArbiter.Release(token);
        _volumeIndicatorToken = 0;

        RestorePrivacyDotVisibility();

        if (_isExpanded || _isAnimating)
        {
            _isVolumeIndicatorExiting = false;
            _levelHudOverIdle = false;
            ClearVolumeIndicatorTransitionEffects();
            VolumeIndicatorContainer.BeginAnimation(OpacityProperty, null);
            VolumeIndicatorContainer.Opacity = 0;
            VolumeIndicatorContainer.Visibility = Visibility.Collapsed;
            return;
        }

        _isVolumeIndicatorExiting = true;
        VolumeIndicatorContainer.IsHitTestVisible = false;

        bool enableBlur = _settings.EnableBlurEffects && !AnimationConfig.ReduceMotion;
        if (vn_animation_compute_volume_hud_transition(
                0,
                _settings.EnableDynamicIslandMode ? (byte)1 : (byte)0,
                enableBlur ? (byte)1 : (byte)0,
                _collapsedWidth,
                _collapsedHeight,
                _cornerRadiusCollapsed,
                out var trans) != 0)
        {
            trans = new VnVolumeHudTransitionParamsOut
            {
                HudFromScaleX = 1.0,
                HudFromScaleY = 1.0,
                HudToScaleX = 0.88,
                HudToScaleY = 0.76,
                HudFromTranslateY = 0.0,
                HudToTranslateY = -5.5,
                HudFromBlur = 0.0,
                HudToBlur = enableBlur ? 9.0 : 0.0,
                MediaToScale = 1.0,
                MediaToBlur = 0.0,
                TargetWidth = _collapsedWidth,
                TargetHeight = _collapsedHeight,
                TargetCornerRadius = _cornerRadiusCollapsed,
                MorphDurationMs = 340,
                FadeDurationMs = 220
            };
        }

        var morphDur = new Duration(TimeSpan.FromMilliseconds(trans.MorphDurationMs));


        _notchShellPresenter?.CancelCurrentAnimation();
        AnimateCornerRadius(trans.TargetCornerRadius, morphDur.TimeSpan);
        _levelHudAttachment!.MoveTo(trans.TargetWidth, trans.TargetHeight, Motion.PopBack, shown: false, done: () =>
        {
            if (exitVersion != _volumeIndicatorExitVersion || _isVolumeIndicatorActive) return;
            _isVolumeIndicatorExiting = false;
            ClearVolumeIndicatorTransitionEffects();
            VolumeIndicatorContainer.Visibility = Visibility.Collapsed;
            VolumeIndicatorContainer.IsHitTestVisible = true;
        });
        // Without music the resting content (time, weather, Claude) comes back instead of cover and bars.
        if (_levelHudOverIdle || !_isMusicCompactMode)
        {
            RestoreIdleUnderHud();
            return;
        }

        if (!_isClipboardPeekActive)
        {
            CompactThumbnailBorder.Visibility = Visibility.Visible;
            CompactThumbnailBorder.Opacity = 0;
            CompactThumbnailBorder.BeginAnimation(OpacityProperty, null);
            var thumbIn = MakeAnim(0.0, 1.0, _dur250, _easeExpOut6);
            CompactThumbnailBorder.BeginAnimation(OpacityProperty, thumbIn);
            if (!AnimationConfig.ReduceMotion)
            {
                var thumbGrow = MakeAnim(0.76, 1.0, morphDur, _easeSoftSpring, VNotch.Services.AnimationConfig.TargetFps);
                CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleXProperty, thumbGrow);
                CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleYProperty, thumbGrow);
                if (enableBlur && MusicCompactContentBlur != null)
                {
                    var thumbBlurIn = MakeAnim(6.0, 0.0, _dur250, _easeExpOut6, VNotch.Services.AnimationConfig.TargetFps);
                    thumbBlurIn.Completed += (_, _) =>
                    {
                        MusicCompactContentBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
                        MusicCompactContentBlur.Radius = 0;
                    };
                    MusicCompactContentBlur.BeginAnimation(BlurEffect.RadiusProperty, thumbBlurIn);
                }
            }
            else
            {
                CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                CompactThumbnailScale.ScaleX = CompactThumbnailScale.ScaleY = 1.0;
            }
        }

        if (!_isClipboardPeekActive)
        {
            ShowMusicVisualizer(duration: _dur150);
        }
    }

    private bool _isVolumeIndicatorExiting;
    private int _volumeIndicatorExitVersion;

    private void DismissVolumeIndicatorImmediate(bool restoreMedia = false, bool animateExit = false)
    {
        CancelMicDive();
        ++_volumeIndicatorExitVersion;
        _volumeIndicatorHideTimer?.Stop();
        ResetCompactVolumeWheelIntent();
        // The next shell owns geometry; freeze the squeezed face before releasing its layout layer.
        if (animateExit) KeepCompactDeparture(VolumeIndicatorContainer);
        int token = _volumeIndicatorToken;
        _isVolumeIndicatorActive = false;
        _isVolumeIndicatorExiting = false;
        _volumeSynced = false;
        _isDraggingVolumeIndicator = false;
        _compactPillArbiter.Release(token);
        _volumeIndicatorToken = 0;
        if (!animateExit && !_isExpanded && !_isAnimating)
            Motion.Stop(NotchBorder, HeightProperty, _collapsedHeight);
        _levelHudAttachment?.Detach(false);
        VolumeIndicatorContainer.ReleaseMouseCapture();
        ClearVolumeIndicatorTransitionEffects();
        Motion.Stop(VolumeIndicatorContainer, OpacityProperty, 0);
        Motion.Stop(VolumeIndicatorContentScale, ScaleTransform.ScaleXProperty, 1);
        Motion.Stop(VolumeIndicatorContentScale, ScaleTransform.ScaleYProperty, 1);
        Motion.Stop(VolumeIndicatorContentTranslate, TranslateTransform.XProperty, 0);
        Motion.Stop(VolumeIndicatorContentTranslate, TranslateTransform.YProperty, 0);
        VolumeIndicatorContainer.Visibility = Visibility.Collapsed;
        VolumeIndicatorContainer.IsHitTestVisible = true;
        CompactThumbnailBorder.BeginAnimation(OpacityProperty, null);
        MusicViz.BeginAnimation(OpacityProperty, null);
        RestorePrivacyDotVisibility();
        ReleaseIdleUnderHud();
        if (restoreMedia) RestoreCompactMediaPresentation();
    }

    private bool _isDraggingVolumeIndicator = false;

    private void VolumeIndicator_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_levelHudKind == LevelHudKind.MicMute) return;
        e.Handled = true;
        _isDraggingVolumeIndicator = true;
        _volumeHudRubberAt = DateTime.MinValue;
        VolumeIndicatorContainer.CaptureMouse();
        SetVolumeFromIndicatorPosition(e);

        _volumeIndicatorHideTimer?.Stop();
    }

    private void VolumeIndicator_MouseMove(object sender, MouseEventArgs e)
    {
        if (_levelHudKind == LevelHudKind.MicMute || !_isDraggingVolumeIndicator || e.LeftButton != MouseButtonState.Pressed) return;
        e.Handled = true;
        SetVolumeFromIndicatorPosition(e);
    }

    private void VolumeIndicator_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDraggingVolumeIndicator) return;
        e.Handled = true;
        _isDraggingVolumeIndicator = false;
        VolumeIndicatorContainer.ReleaseMouseCapture();
        ReleaseVolumeIndicatorRubber();

        _volumeIndicatorHideTimer?.Stop();
        _volumeIndicatorHideTimer?.Start();
    }

    private void VolumeIndicator_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (!_isDraggingVolumeIndicator || !ReferenceEquals(e.OriginalSource, VolumeIndicatorContainer)) return;
        _isDraggingVolumeIndicator = false;
        ReleaseVolumeIndicatorRubber();
        _volumeIndicatorHideTimer?.Stop();
        _volumeIndicatorHideTimer?.Start();
    }

    private void SetVolumeFromIndicatorPosition(MouseEventArgs e)
    {
        if (_levelHudKind == LevelHudKind.MicMute) return;
        _volumeInteractionVersion++;
        var pos = e.GetPosition(VolumeIndicatorContainer);
        double containerWidth = VolumeIndicatorContainer.ActualWidth;
        if (containerWidth <= 0) containerWidth = (_collapsedWidth + VolumeHudExtraWidth) - 28;

        double position = FlowDirection == FlowDirection.RightToLeft ? containerWidth - pos.X : pos.X;
        double rawRatio = position / containerWidth;
        if (rawRatio > 1.02 || rawRatio < -0.02)
        {
            PlayVolumeOverscrollSpring(Math.Clamp(rawRatio, 0.0, 1.0), rawRatio > 1.0 ? (rawRatio - 1.0) : rawRatio);
        }
        else SetVolumeIndicatorRubber(0, DateTime.UtcNow);
        float newVolume = (float)Math.Clamp(rawRatio, 0.0, 1.0);
        // The power mode has four steps: the bar snaps to the one under the finger.
        if (_levelHudKind == LevelHudKind.PowerMode) newVolume = PowerModeStep(newVolume) / (float)PowerModeSteps;
        if (_levelHudKind != LevelHudKind.AppVolume)
        {
            SetVolumeFill(VolumeIndicatorScale, newVolume, animate: true);
            WriteSystemLevel(newVolume);
            return;
        }
        float priorVolume = _currentVolume;
        _currentVolume = newVolume;
        SetVolumeFill(VolumeIndicatorScale, newVolume, animate: true);

        QueueSessionVolumeWrite(newVolume, priorVolume);

        if (_isExpanded)
        {
            SetVolumeFill(VolumeBarScale, newVolume);
            UpdateVolumeIcon(newVolume, false);
        }
    }

    private void VolumeIcon_MouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        int request = ++_volumeInteractionVersion;
        // Every click is a toggle, so retain each one while serializing driver
        // calls. Concurrent read/modify/write operations can otherwise lose clicks.
        _mediaVolumeWrites.Post($"mute:{request}", () =>
        {
            if (!_mediaService.TryToggleCurrentSessionMute()) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _volumeInteractionVersion++;
                if (_volumeReadInFlight)
                    _pendingMuteRefreshVersion = _volumeInteractionVersion;
                else
                    _ = SyncVolumeFromActiveSession();
            }));
        });
    }

    private void VolumeIcon_MouseEnter(object sender, MouseEventArgs e)
    {
        var animX = new DoubleAnimation(1, 1.2, _dur150) { EasingFunction = _easeQuadOut };
        var animY = new DoubleAnimation(1, 1.2, _dur150) { EasingFunction = _easeQuadOut };
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(animX, VNotch.Services.AnimationConfig.TargetFps);
        VolumeIconScale.BeginAnimation(ScaleTransform.ScaleXProperty, animX);
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(animY, VNotch.Services.AnimationConfig.TargetFps);
        VolumeIconScale.BeginAnimation(ScaleTransform.ScaleYProperty, animY);
    }

    private void VolumeIcon_MouseLeave(object sender, MouseEventArgs e)
    {
        var animX = new DoubleAnimation(VolumeIconScale.ScaleX, 1, _dur150) { EasingFunction = _easeQuadOut };
        var animY = new DoubleAnimation(VolumeIconScale.ScaleY, 1, _dur150) { EasingFunction = _easeQuadOut };
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(animX, VNotch.Services.AnimationConfig.TargetFps);
        VolumeIconScale.BeginAnimation(ScaleTransform.ScaleXProperty, animX);
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(animY, VNotch.Services.AnimationConfig.TargetFps);
        VolumeIconScale.BeginAnimation(ScaleTransform.ScaleYProperty, animY);
    }

    private void VolumeBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _isDraggingVolume = true;
        VolumeBarContainer.CaptureMouse();
        SetVolumeFromMousePosition(e);
    }

    private void VolumeBar_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (e.Delta == 0 || _isDraggingVolume || _isAnimating) return;
        ApplyVolumeStep(e.Delta);
    }

    private void VolumeBar_MouseMove(object sender, MouseEventArgs e)
    {
        if (_isDraggingVolume && e.LeftButton == MouseButtonState.Pressed)
        {
            SetVolumeFromMousePosition(e);
        }
    }

    private void VolumeBar_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDraggingVolume)
        {
            _isDraggingVolume = false;
            VolumeBarContainer.ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    private void SetVolumeFromMousePosition(MouseEventArgs e)
    {
        _volumeInteractionVersion++;
        const double volumeBarWidth = 100.0;
        var pos = e.GetPosition(VolumeBarContainer);
        float newVolume = (float)Math.Clamp(pos.X / volumeBarWidth, 0.0, 1.0);

        float priorVolume = _currentVolume;
        _currentVolume = newVolume;
        SetVolumeFill(VolumeBarScale, newVolume);
        UpdateVolumeIcon(newVolume, false);

        QueueSessionVolumeWrite(newVolume, priorVolume);
    }

    private bool _volumeReadInFlight;
    private int? _pendingMuteRefreshVersion;
    private int _volumeInteractionVersion;

    private async Task SyncVolumeFromActiveSession()
    {
        if (_isDraggingVolume || _volumeReadInFlight) return;

        _volumeReadInFlight = true;
        int version = _volumeInteractionVersion;
        try
        {
            var result = await Task.Run(() =>
            {
                bool success = _mediaService.TryGetCurrentSessionVolume(out float volume, out bool muted);
                return (success, volume, muted);
            });
            if (_isDraggingVolume || version != _volumeInteractionVersion) return;

            if (result.success)
            {
                _currentVolume = result.volume;
                SetVolumeFill(VolumeBarScale, _currentVolume);
                UpdateVolumeIcon(_currentVolume, result.muted);
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Error(MediaCtrlLogTag, ex, "Read session volume failed");
        }
        finally
        {
            _volumeReadInFlight = false;
            int? pendingVersion = _pendingMuteRefreshVersion;
            _pendingMuteRefreshVersion = null;
            if (pendingVersion == _volumeInteractionVersion)
                _ = SyncVolumeFromActiveSession();
        }
    }

    private void UpdateVolumeIcon(float volume, bool isMuted)
    {
        _viewModel.AudioMixer.UpdateState(volume, isMuted);
    }
}
