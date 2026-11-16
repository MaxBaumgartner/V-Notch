using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

public partial class MainWindow
{
    private const int PrivacyBreathFps = 24;

    #region Privacy Indicators (Mic/Camera In-Use — Single Dot)

    private bool _privacyIndicatorsVisible = false;
    private int _privacyVisibilityToken;
    private PrivacyIndicatorState _lastPrivacyState = PrivacyIndicatorState.Empty;

    private const double PrivacyDotDefaultRightMargin = 27.0;
    private const double PrivacyDotCompactMusicRightMargin = 34.0;
    private const double PrivacyDotTopMargin = 11.2;
    private const double PrivacyDotSize = 7.0;

    private static readonly Color _micColor = Color.FromRgb(0xFF, 0x95, 0x00);
    private static readonly Color _camColor = Color.FromRgb(0x30, 0xD1, 0x58);
    private static readonly Color _locationColor = Color.FromRgb(0x00, 0xAE, 0xEF);
    private static readonly Color _screenRecColor = Color.FromRgb(0xFF, 0x3B, 0x30);

    internal static bool ShouldSuppressPrivacyDot(
        bool isCompactPill,
        bool isAudioView,
        bool isVolumeIndicatorActive,
        bool isBluetoothNotificationVisible,
        bool isClipboardPeekActive)
        => !isCompactPill || isAudioView || isVolumeIndicatorActive ||
           isBluetoothNotificationVisible || isClipboardPeekActive;

    private bool IsCompactPillForPrivacyIndicator =>
        _transitionCoordinator.ShapeState == VNotch.Controllers.NotchShapeState.Collapsed &&
        !_isAnimating &&
        !_isGreetingActive &&
        !_isAppNotificationBannerVisible;

    /// <summary>Something else holds the dot's spot (an open notch, a pill, the audio view…).</summary>
    private bool IsPrivacySpotTaken => ShouldSuppressPrivacyDot(
        IsCompactPillForPrivacyIndicator,
        _isAudioView,
        _isVolumeIndicatorActive,
        _isBluetoothNotificationVisible || _isClaudeNotificationVisible || _appPillToken != 0,
        _isClipboardPeekActive);

    // A muted mic in a call takes the dot's place.
    private bool IsPrivacyDotSuppressed => IsPrivacySpotTaken || _micMutedShown;

    private void SyncPrivacyDotVisibilityForCurrentView()
    {
        ApplyMicMutedPresentation();
        if (!_privacyIndicatorsVisible) return;

        if (IsPrivacyDotSuppressed)
            SuppressPrivacyDot();
        else
            RestorePrivacyDotVisibility();
    }

    private int _privacyPresentationGeneration;
    private bool? _appliedPrivacyIndicatorsEnabled;

    private void PrivacyModule_StateChanged(object? sender, PrivacyIndicatorState state)
    {
        int generation = _privacyPresentationGeneration;
        Dispatcher.BeginInvoke(() =>
        {
            if (!_cleanedUp && generation == _privacyPresentationGeneration)
                UpdatePrivacyIndicators(state);
        });
    }

    private void ApplyPrivacyIndicatorSettings()
    {
        if (_cleanedUp) return;
        if (_appliedPrivacyIndicatorsEnabled != _settings.EnablePrivacyIndicators)
        {
            _appliedPrivacyIndicatorsEnabled = _settings.EnablePrivacyIndicators;
            _privacyPresentationGeneration++;
        }
        if (_settings.EnablePrivacyIndicators)
        {
            if (_coreModulesStarted) _privacyModule.Start();
        }
        else
        {
            _privacyModule.Stop();
            UpdatePrivacyIndicators(PrivacyIndicatorState.Empty);
        }
    }

    private void UpdatePrivacyIndicators(PrivacyIndicatorState state)
    {
        if (!_settings.EnablePrivacyIndicators)
        {
            HidePrivacyIndicators();
            _lastPrivacyState = state;
            return;
        }

        if (_isCameraActive && state.CameraInUse)
        {
            state = state with
            {
                CameraInUse = false,
                CameraConsumers = Array.Empty<string>()
            };
        }

        bool shouldShow = state.AnyInUse;
        bool wasShowing = _privacyIndicatorsVisible;

        _lastPrivacyState = state;

        if (shouldShow && !wasShowing)
        {
            ShowPrivacyDot(state);
        }
        else if (!shouldShow && wasShowing)
        {
            HidePrivacyIndicators();
        }
        else if (shouldShow)
        {
            if (IsPrivacyDotSuppressed)
            {
                SuppressPrivacyDot();
            }
            else if (PrivacyIndicatorPanel.Visibility != Visibility.Visible)
            {
                ShowPrivacyDot(state);
            }
            else
            {
                ApplyDotColor(state, animate: true);
            }
        }
    }

    private void ShowPrivacyDot(PrivacyIndicatorState state)
    {
        HookAmbientVisibility();
        _privacyIndicatorsVisible = true;

        if (IsPrivacyDotSuppressed)
            return;

        UpdatePrivacyDotPosition();

        ApplyDotColor(state, animate: false);

        FadePrivacyDot(show: true);

        StartBreathingAnimation(PrivacyDot);

        RuntimeLog.Log("PRIVACY",
            $"Dot shown — Mic: {state.MicrophoneInUse}, Cam: {state.CameraInUse}, Screen: {state.ScreenRecordingActive}, Location: {state.LocationInUse}");
    }

    private void HidePrivacyIndicators()
    {
        if (!_privacyIndicatorsVisible) return;
        _privacyIndicatorsVisible = false;

        StopBreathingAnimation(PrivacyDot);

        FadePrivacyDot(show: false);

        RuntimeLog.Log("PRIVACY", "Dot hidden");
    }

    private void SuppressPrivacyDot()
    {
        ApplyMicMutedPresentation();
        if (!_privacyIndicatorsVisible) return;

        StopBreathingAnimation(PrivacyDot);
        FadePrivacyDot(show: false);
    }

    private void RestorePrivacyDotVisibility()
    {
        ApplyMicMutedPresentation();
        if (!_privacyIndicatorsVisible) return;
        if (IsPrivacyDotSuppressed) return;

        UpdatePrivacyDotPosition();

        ApplyDotColor(_lastPrivacyState, animate: false);

        FadePrivacyDot(show: true);

        StartBreathingAnimation(PrivacyDot);
    }

    private void UpdatePrivacyDotPosition(bool animate = false)
    {
        bool compact = _isMusicCompactMode && !_isExpanded;
        double right = compact ? PrivacyDotCompactMusicRightMargin : PrivacyDotDefaultRightMargin;

        double top = PrivacyDotTopMargin;
        bool isCollapsed = !_isExpanded && !_isMusicExpanded;
        if (isCollapsed && _settings.EnableDynamicIslandMode)
        {
            double h = NotchBorder?.ActualHeight > 0
                ? NotchBorder.ActualHeight
                : GetCollapsedHeight();
            if (h > 0) top = Math.Max(0, (h - PrivacyDotSize) / 2.0);
        }

        var target = new Thickness(0, top, right, 0);
        if (animate && PrivacyIndicatorPanel.IsVisible) Motion.MarginTo(PrivacyIndicatorPanel, target, Motion.Glide);
        else PrivacyIndicatorPanel.Margin = target;
    }

    // A single dot prioritizes capture, mic, camera, then location.
    internal static Color GetPrivacyDotColor(PrivacyIndicatorState state)
    {
        if (state.ScreenRecordingActive)
            return _screenRecColor;
        if (state.MicrophoneInUse)
            return _micColor;
        if (state.CameraInUse)
            return _camColor;
        return state.LocationInUse ? _locationColor : Colors.Transparent;
    }

    private void ApplyDotColor(PrivacyIndicatorState state, bool animate)
    {
        Color targetColor = GetPrivacyDotColor(state);

        if (animate && !AnimationConfig.ReduceMotion)
        {
            var colorAnim = new ColorAnimation
            {
                To = targetColor,
                Duration = new Duration(TimeSpan.FromMilliseconds(180)),
                EasingFunction = _easeSineInOut
            };
            Timeline.SetDesiredFrameRate(colorAnim, VNotch.Services.AnimationConfig.TargetFps);
            PrivacyDotBrush.BeginAnimation(SolidColorBrush.ColorProperty, colorAnim);
        }
        else
        {
            PrivacyDotBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            PrivacyDotBrush.Color = targetColor;
        }
    }

    private void FadePrivacyDot(bool show)
    {
        int token = ++_privacyVisibilityToken;
        bool entering = PrivacyIndicatorPanel.Visibility != Visibility.Visible;
        if (!show && !PrivacyIndicatorPanel.IsVisible)
        {
            Motion.Stop(PrivacyIndicatorPanel, OpacityProperty, 0);
            Motion.Stop(PrivacyDotScale, ScaleTransform.ScaleXProperty, Motion.ExitToScale);
            Motion.Stop(PrivacyDotScale, ScaleTransform.ScaleYProperty, Motion.ExitToScale);
            PrivacyIndicatorPanel.Visibility = Visibility.Collapsed;
            PrivacyDot.Visibility = Visibility.Collapsed;
            return;
        }
        if (show)
        {
            PrivacyDot.Visibility = Visibility.Visible;
            PrivacyIndicatorPanel.Visibility = Visibility.Visible;
        }
        Motion.To(PrivacyDotScale, ScaleTransform.ScaleXProperty, show ? 1 : Motion.ExitToScale,
            show ? Motion.TileScale : Motion.Exit, from: show && entering ? Motion.TileFromScale : null);
        Motion.To(PrivacyDotScale, ScaleTransform.ScaleYProperty, show ? 1 : Motion.ExitToScale,
            show ? Motion.TileScale : Motion.Exit, from: show && entering ? Motion.TileFromScale : null);
        Motion.To(PrivacyIndicatorPanel, OpacityProperty, show ? 1 : 0, show ? Motion.Tile : Motion.Exit,
            from: show && entering ? 0 : null, done: () =>
            {
                if (token != _privacyVisibilityToken || show) return;
                Motion.Stop(PrivacyDotScale, ScaleTransform.ScaleXProperty);
                Motion.Stop(PrivacyDotScale, ScaleTransform.ScaleYProperty);
                PrivacyIndicatorPanel.Visibility = Visibility.Collapsed;
                PrivacyDot.Visibility = Visibility.Collapsed;
            });
    }

    private bool _privacyBreathing;

    private void RefreshPrivacyBreathing()
    {
        if (_privacyIndicatorsVisible && !IsPrivacyDotSuppressed && AmbientAnimationsAllowed && PrivacyDot.IsVisible)
            StartBreathingAnimation(PrivacyDot);
        else StopBreathingAnimation(PrivacyDot);
    }

    private void StartBreathingAnimation(FrameworkElement dot)
    {
        if (_privacyBreathing || !AmbientAnimationsAllowed || !dot.IsVisible) return;
        _privacyBreathing = true;
        var breathing = new DoubleAnimation
        {
            To = 0.4,
            Duration = new Duration(TimeSpan.FromMilliseconds(1400)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = _easeSineInOut
        };
        // A slow opacity breath looks the same at 24 fps; at the display rate (165 Hz here) it kept the whole
        // notch window repainting for as long as an app holds the mic, which for an always-on mixer is always.
        Timeline.SetDesiredFrameRate(breathing, PrivacyBreathFps);
        dot.BeginAnimation(OpacityProperty, breathing);
    }

    private void StopBreathingAnimation(FrameworkElement dot)
    {
        _privacyBreathing = false;
        double opacity = dot.IsVisible && AmbientAnimationsAllowed ? dot.Opacity : 1;
        dot.BeginAnimation(OpacityProperty, null);
        // The panel owns the exit fade; resetting a breathing dot first would brighten it for one frame.
        dot.Opacity = opacity;
    }

    #endregion
}
