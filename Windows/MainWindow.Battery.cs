using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VNotch.Models;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

public partial class MainWindow
{
    private bool? _wasPluggedIn = null;
    private bool _isChargingNotificationVisible = false;
    private int _chargingGlanceToken = 0;
    private UiScheduledTimer? _chargingNotificationDismissTimer;
    private VNotch.Presenters.CompactSurfaceAttachment? _chargingGlanceAttachment;

    // A title shimmer does not need the display's 165-360 Hz.
    private static int AmbientSweepFps => Math.Min(AnimationConfig.TargetFps, 60);
    private bool _ambientVisibilityHooked;

    /// <summary>Ambient feedback starts and stops with the element that owns it.</summary>
    private void HookAmbientVisibility()
    {
        if (_ambientVisibilityHooked) return;
        _ambientVisibilityHooked = true;
        BatteryFill.IsVisibleChanged += (_, _) => RefreshAmbientAnimations();
        PrivacyDot.IsVisibleChanged += (_, _) => RefreshPrivacyBreathing();
        UpdateNotificationButton.IsVisibleChanged += (_, _) => StartUpdatePulseAnimation();
        MediaPlayback.TrackTitle.IsVisibleChanged += (_, _) => RefreshAmbientAnimations();
        LyricsSearchText.IsVisibleChanged += (_, _) => RefreshLyricsSearchShimmer();
    }

    private bool AmbientAnimationsAllowed =>
        !AnimationConfig.ReduceMotion && _isNotchVisible && !_isHiddenByFullscreen;

    private void RefreshAmbientAnimations()
    {
        HookAmbientVisibility();
        bool allowed = AmbientAnimationsAllowed;

        if (_displayedBattery is { } battery)
        {
            if (!allowed || !BatteryFill.IsVisible)
                Motion.Stop(BatteryFillScale, ScaleTransform.ScaleXProperty, battery.FillRatio);
            if (!allowed || !ChargingBolt.IsVisible) AnimateChargingBolt(battery.IsPowerConnected);
        }

        Motion.Stop(BatteryFill, OpacityProperty, 1);

        if (_shimmerWanted && allowed && MediaPlayback.TrackTitle.IsVisible) StartTitleShimmer();
        else StopTitleShimmerInternal();
        RefreshLyricsSearchShimmer();

        RefreshChargePulse();
        RefreshPrivacyBreathing();
        StartUpdatePulseAnimation();
        ApplyBubbleMusicSpin();
        ApplyClaudeLiveMotion();
        if (!allowed) StopNotificationDrop();
        RefreshMusicMotionVisibility();
        RefreshCountdownCompleteTextFlash();
        _phoneMirror?.RefreshMotionVisibility();
        RefreshAppNotificationMotionVisibility();
    }

    private void OnReduceMotionChanged()
    {
        void ApplyMotionPreference()
        {
            RefreshAmbientAnimations();
            if (!AnimationConfig.ReduceMotion) return;
            SetVolumeFill(VolumeBarScale, (double)VolumeBarScale.GetAnimationBaseValue(ScaleTransform.ScaleXProperty));
            SetVolumeFill(VolumeIndicatorScale, (double)VolumeIndicatorScale.GetAnimationBaseValue(ScaleTransform.ScaleXProperty));
            _volumeScrollSyncAfterUtc = DateTime.MinValue;
        }
        if (Dispatcher.CheckAccess()) ApplyMotionPreference();
        else Dispatcher.BeginInvoke(new Action(ApplyMotionPreference));
    }

    private void HandleBatteryUpdate(BatteryInfo battery)
    {
        _displayedBattery = battery;
        RefreshBatteryAccessibleName();
        LinkedDeviceBatteryIconView.Visibility = string.IsNullOrEmpty(battery.DeviceId) ? Visibility.Collapsed : Visibility.Visible;
        if (_lastBatteryDeviceId != battery.DeviceId)
        {
            _wasPluggedIn = null;
            _lastBatteryDeviceId = battery.DeviceId;
        }
        AnimationConfig.SetReduceMotion(battery.IsBatterySaver);

        if (AmbientAnimationsAllowed && BatteryFill.IsVisible)
            Motion.To(BatteryFillScale, ScaleTransform.ScaleXProperty, battery.FillRatio, Motion.Glide);
        else Motion.Stop(BatteryFillScale, ScaleTransform.ScaleXProperty, battery.FillRatio);

        SolidColorBrush fillBrush;
        SolidColorBrush percentBrush;
        bool showLightning;

        if (battery.HasBattery && battery.Percentage >= 0 && battery.Percentage <= 20 && !battery.IsPowerConnected)
        {
            fillBrush = _brushLowBattery;
            percentBrush = _brushLowBattery;
            showLightning = false;
        }
        else if (battery.IsPowerConnected)
        {
            // Monochrome on mains power (a desktop always reports this); the bolt says it.
            fillBrush = _brushWhite;
            percentBrush = VNotch.Services.UiPalette.PrimaryBrush;
            showLightning = true;
        }
        else
        {
            fillBrush = _brushWhite;
            percentBrush = VNotch.Services.UiPalette.PrimaryBrush;
            showLightning = false;
        }

        AnimateBrushTransition(BatteryFill, fillBrush);
        AnimateBrushTransition(BatteryPercent, percentBrush);
        AnimateChargingBolt(showLightning);

        // Plugging in and out now plays in the orb beside the notch (MainWindow.ChargeOrb), not as a glance in it.
        if (!MotionLabFakesCharging) UpdateChargeOrb(battery);

        UpdateKeepAwake();
    }

    private BatteryInfo? _displayedBattery;
    private string? _lastBatteryDeviceId;

    private void RefreshBatteryAccessibleName()
    {
        var battery = _displayedBattery;
        string name = battery == null || string.IsNullOrEmpty(battery.DeviceId)
            ? Loc.Get("settings.batteryDevice.system")
            : battery.HasBattery ? battery.DeviceName : Loc.Get("settings.batteryDevice.unavailable", battery.DeviceName);
        string description = battery == null ? name : $"{name}, {battery.GetPercentageText()}";
        System.Windows.Automation.AutomationProperties.SetName(BatterySection, description);
        BatterySection.ToolTip = description;
    }

    private enum ChargingGlanceKind
    {
        PluggedIn,
        Unplugged
    }

    private void ShowChargingGlance(BatteryInfo battery, ChargingGlanceKind kind)
    {
        if (_isExpanded || _isAnimating || _isGreetingActive)
            return;

        if (!TryAcquireCompactSlot(VNotch.Controllers.CompactPillSlot.Charging, out int token))
            return;

        _chargingGlanceToken = token;
        _isChargingNotificationVisible = true;

        ChargingPercentText.Text = battery.GetPercentageText();

        bool plugged = kind == ChargingGlanceKind.PluggedIn;
        Color accent = plugged ? ChargeTintFor(battery.Percentage) : BrightnessHudTint;
        string statusKey = plugged ? battery.IsFullyCharged ? "battery.fullyCharged" : "battery.charging" : "battery.onBattery";
        ChargingStatusText.Text = Loc.Get(statusKey);
        ChargingPercentText.Foreground = UiPalette.PrimaryBrush;
        ChargingBatteryFill.Background = new SolidColorBrush(accent);

        if (battery.HasPowerRate && Math.Abs(battery.PowerWatts) >= 0.1)
        {
            double watts = Math.Abs(battery.PowerWatts);
            string formatted = watts >= 10
                ? $"{watts:0} W"
                : $"{watts:0.0} W";
            ChargingWattText.Text = formatted;
            ChargingWattText.Visibility = Visibility.Visible;
        }
        else
        {
            ChargingWattText.Visibility = Visibility.Collapsed;
            ChargingWattText.Text = string.Empty;
        }

        ChargingBatteryFillScale.ScaleX = battery.FillRatio;

        HideCompactSurface(CollapsedContent);
        HideCompactSurface(MusicCompactContent);

        bool entering = ChargingNotification.Visibility != Visibility.Visible;
        if (entering) _chargingGlanceAttachment?.Detach(false);
        ChargingNotification.Visibility = Visibility.Visible;
        Motion.Stop(ChargingNotification, OpacityProperty, 1);
        Motion.Stop(ChargingNotificationTranslate, TranslateTransform.YProperty, 0);
        PlayChargingBounce();
        Motion.To(ChargingIconScale, ScaleTransform.ScaleXProperty, 1, Motion.Pop, from: entering ? .6 : null);
        Motion.To(ChargingIconScale, ScaleTransform.ScaleYProperty, 1, Motion.Pop, from: entering ? .6 : null);
        StopChargingNotificationDismissTimer();

        int dismissToken = _chargingGlanceToken;
        var dismissTimer = new UiScheduledTimer
        {
            Interval = TimeSpan.FromMilliseconds(3000)
        };
        _chargingNotificationDismissTimer = dismissTimer;
        dismissTimer.Tick += (s, e) =>
        {
            dismissTimer.Stop();
            dismissTimer.Dispose();
            if (!ReferenceEquals(_chargingNotificationDismissTimer, dismissTimer) ||
                dismissToken != _chargingGlanceToken)
            {
                return;
            }

            _chargingNotificationDismissTimer = null;
            DismissChargingNotification();
        };
        dismissTimer.Start();
    }

    private void DismissChargingNotification()
    {
        if (!_isChargingNotificationVisible) return;

        int token = _chargingGlanceToken;

        StopChargingNotificationDismissTimer();
        _chargingGlanceAttachment!.MoveTo(_collapsedWidth, _collapsedHeight, Motion.PopBack, shown: false, done: () =>
        {
            if (token != _chargingGlanceToken) return;
            if (IsCompactSlotStale(token)) return;

            ChargingNotification.Visibility = Visibility.Collapsed;
            _isChargingNotificationVisible = false;
            _compactPillArbiter.Release(token);
            RestoreCompactMediaPresentation();
            _chargingGlanceToken = 0;

            if (_isMusicCompactMode && _currentMediaInfo != null)
            {
                MusicCompactContent.Visibility = Visibility.Visible;
                MusicCompactContent.Opacity = 0;
                var fadeInMusic = MakeCompactContentRestore();
                MusicCompactContent.BeginAnimation(OpacityProperty, fadeInMusic);
            }
            else
            {
                CollapsedContent.Visibility = Visibility.Visible;
                CollapsedContent.Opacity = 0;
                var fadeInCollapsed = MakeCompactContentRestore();
                CollapsedContent.BeginAnimation(OpacityProperty, fadeInCollapsed);
            }
        });
    }

    private void CancelChargingGlanceImmediate()
    {
        if (!_isChargingNotificationVisible) return;

        StopChargingNotificationDismissTimer();
        _isChargingNotificationVisible = false;
        _compactPillArbiter.Release(_chargingGlanceToken);
        _chargingGlanceToken = 0;

        _chargingGlanceAttachment?.Detach(false);
        Motion.Stop(ChargingNotification, OpacityProperty, 0);
        Motion.Stop(ChargingNotificationTranslate, TranslateTransform.YProperty, 0);
        Motion.Stop(ChargingIconScale, ScaleTransform.ScaleXProperty, 1);
        Motion.Stop(ChargingIconScale, ScaleTransform.ScaleYProperty, 1);
        ChargingNotification.Visibility = Visibility.Collapsed;
    }

    private void StopChargingNotificationDismissTimer()
    {
        var timer = _chargingNotificationDismissTimer;
        _chargingNotificationDismissTimer = null;
        timer?.Stop();
        timer?.Dispose();
    }

    private void PlayChargingBounce()
    {
        if (_isExpanded || _isAnimating) return;
        double extra = ChargingWattText.Visibility == Visibility.Visible ? 56 : 28;
        _notchShellPresenter?.CancelCurrentAnimation();
        _chargingGlanceAttachment ??= new(NotchBorder, ChargingNotification,
            () => new Size(_collapsedWidth, _collapsedHeight));
        _chargingGlanceAttachment.MoveTo(_collapsedWidth + extra, _collapsedHeight, Motion.Pop, shown: true);
    }
    private void AnimateBrushTransition(FrameworkElement element, SolidColorBrush targetBrush)
    {
        SolidColorBrush? currentBrush = element switch
        {
            TextBlock tb => tb.Foreground as SolidColorBrush,
            Border border => border.Background as SolidColorBrush,
            _ => null
        };

        if (!AmbientAnimationsAllowed || !element.IsVisible || currentBrush == null || currentBrush.Color == targetBrush.Color)
        {
            if (element is TextBlock textBlock)
                textBlock.Foreground = targetBrush;
            else if (element is Border borderElement)
                borderElement.Background = targetBrush;
            return;
        }

        var colorAnimation = new ColorAnimation
        {
            To = targetBrush.Color,
            Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };

        var animatedBrush = new SolidColorBrush(currentBrush.Color);
        if (element is TextBlock textBlockElement)
            textBlockElement.Foreground = animatedBrush;
        else if (element is Border borderElement2)
            borderElement2.Background = animatedBrush;

        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(colorAnimation, VNotch.Services.AnimationConfig.TargetFps);
        animatedBrush.BeginAnimation(SolidColorBrush.ColorProperty, colorAnimation);
    }

    private void AnimateChargingBolt(bool show)
    {
        if (!AmbientAnimationsAllowed || !ChargingBolt.IsVisible)
        {
            Motion.Stop(ChargingBolt, OpacityProperty, show ? 1 : 0);
            Motion.Stop(ChargingBoltScale, ScaleTransform.ScaleXProperty, show ? 1 : Motion.ExitToScale);
            Motion.Stop(ChargingBoltScale, ScaleTransform.ScaleYProperty, show ? 1 : Motion.ExitToScale);
            return;
        }
        Motion.To(ChargingBolt, OpacityProperty, show ? 1 : 0, show ? Motion.Tile : Motion.Exit);
        Motion.To(ChargingBoltScale, ScaleTransform.ScaleXProperty, show ? 1 : Motion.ExitToScale, show ? Motion.TileScale : Motion.Exit);
        Motion.To(ChargingBoltScale, ScaleTransform.ScaleYProperty, show ? 1 : Motion.ExitToScale, show ? Motion.TileScale : Motion.Exit);
    }

}
