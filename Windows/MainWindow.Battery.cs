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
    private Storyboard? _chargingPulseStoryboard;
    private bool _chargingPulseWanted;
    private bool? _wasPluggedIn = null;
    private bool _isChargingNotificationVisible = false;
    private int _chargingGlanceToken = 0;
    private UiScheduledTimer? _chargingNotificationDismissTimer;

    // A slow opacity pulse looks the same at 30 fps; a shimmer sweep at 60. Neither needs the display's 165-360 Hz.
    private const int AmbientPulseFps = 30;
    private static int AmbientSweepFps => Math.Min(AnimationConfig.TargetFps, 60);
    private bool _ambientVisibilityHooked;

    /// <summary>The pulse and the title shimmer live in the open notch; they start and stop with their element.</summary>
    private void HookAmbientVisibility()
    {
        if (_ambientVisibilityHooked) return;
        _ambientVisibilityHooked = true;
        BatteryFill.IsVisibleChanged += (_, _) => RefreshAmbientAnimations();
        PrivacyDot.IsVisibleChanged += (_, _) => RefreshPrivacyBreathing();
        UpdateNotificationButton.IsVisibleChanged += (_, _) => StartUpdatePulseAnimation();
        MediaPlayback.TrackTitle.IsVisibleChanged += (_, _) => RefreshAmbientAnimations();
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

        if (_chargingPulseWanted && allowed && BatteryFill.IsVisible) StartChargingPulse();
        else StopChargingPulseInternal();

        if (_shimmerWanted && allowed && MediaPlayback.TrackTitle.IsVisible) StartTitleShimmer();
        else StopTitleShimmerInternal();

        RefreshChargePulse();
        RefreshPrivacyBreathing();
        StartUpdatePulseAnimation();
        ApplyBubbleMusicSpin();
        ApplyClaudeLiveMotion();
        if (!allowed) StopNotificationDrop();
        RefreshCountdownCompleteTextFlash();
        _phoneMirror?.RefreshMotionVisibility();
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
        bool showLightning = false;

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

        // Only a charge the user plugged in for pulses; on permanent mains power the battery just sits still.
        if (battery.IsCharging && (InChargeFromPlug || MotionLabFakesCharging))
        {
            StartChargingPulse();
        }
        else
        {
            StopChargingPulse();
        }
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

        Color accent;
        string statusKey;
        if (kind == ChargingGlanceKind.PluggedIn)
        {
            if (battery.IsFullyCharged)
            {
                statusKey = "battery.fullyCharged";
                accent = Color.FromRgb(0x30, 0xD1, 0x58);
            }
            else
            {
                statusKey = "battery.charging";
                accent = Color.FromRgb(0x30, 0xD1, 0x58);
            }
        }
        else
        {
            statusKey = "battery.onBattery";
            accent = Color.FromRgb(0xFF, 0x95, 0x00);
        }
        ChargingStatusText.Text = Loc.Get(statusKey);
        ChargingPercentText.Foreground = new SolidColorBrush(accent);
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

        ChargingNotification.Visibility = Visibility.Visible;
        ChargingNotification.Opacity = 0;

        PlayChargingBounce();

        var fadeIn = MakeCompactNotificationEntrance(0, 1);
        ChargingNotification.BeginAnimation(OpacityProperty, fadeIn);

        Motion.To(ChargingNotificationTranslate, TranslateTransform.YProperty, 0, Motion.Tile, from: 6);

        Motion.To(ChargingIconScale, ScaleTransform.ScaleXProperty, 1, Motion.TileScale, from: Motion.TileFromScale);
        Motion.To(ChargingIconScale, ScaleTransform.ScaleYProperty, 1, Motion.TileScale, from: Motion.TileFromScale);

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

        AnimateCompactWidth(_collapsedWidth, TimeSpan.FromMilliseconds(400), _easeExpOut6, token);

        var fadeOut = MakeCompactNotificationExit(1, 0);
        fadeOut.Completed += (s, e) =>
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
        };

        ChargingNotification.BeginAnimation(OpacityProperty, fadeOut);

        Motion.To(ChargingNotificationTranslate, TranslateTransform.YProperty, -4, Motion.Exit);
        Motion.To(ChargingIconScale, ScaleTransform.ScaleXProperty, Motion.ExitToScale, Motion.Exit);
        Motion.To(ChargingIconScale, ScaleTransform.ScaleYProperty, Motion.ExitToScale, Motion.Exit);
    }

    private void CancelChargingGlanceImmediate()
    {
        if (!_isChargingNotificationVisible) return;

        StopChargingNotificationDismissTimer();
        _isChargingNotificationVisible = false;
        _chargingGlanceToken = 0;

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
        AnimateCompactWidth(_collapsedWidth + extra, _dur350, _easeSoftSpring, _chargingGlanceToken);
        if (AnimationConfig.ReduceMotion) return;
        foreach (var scale in new[] { NotchScale, NotchShadowScale })
        {
            Motion.To(scale, ScaleTransform.ScaleXProperty, 1, Motion.Press, velocity: 1);
            Motion.To(scale, ScaleTransform.ScaleYProperty, 1, Motion.Press, velocity: -0.8);
        }
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

    private void StartChargingPulse()
    {
        _chargingPulseWanted = true;
        // The battery sits in the open notch's status bar: pulsing it while the notch is closed only redrew the
        // window at the display rate for nothing. BatteryFill.IsVisibleChanged restarts it when it shows.
        if (!AmbientAnimationsAllowed || !BatteryFill.IsVisible) return;
        if (_chargingPulseStoryboard != null) return;

        _chargingPulseStoryboard = new Storyboard
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        Timeline.SetDesiredFrameRate(_chargingPulseStoryboard, AmbientPulseFps);

        var pulseAnimation = new DoubleAnimation
        {
            From = 1.0,
            To = 0.85,
            Duration = TimeSpan.FromMilliseconds(1000),
            AutoReverse = true,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };

        Storyboard.SetTarget(pulseAnimation, BatteryFill);
        Storyboard.SetTargetProperty(pulseAnimation, new PropertyPath("Opacity", Array.Empty<object>()));
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(pulseAnimation, AmbientPulseFps);
        _chargingPulseStoryboard.Children.Add(pulseAnimation);

        _chargingPulseStoryboard.Begin();
    }

    private void StopChargingPulse()
    {
        _chargingPulseWanted = false;
        StopChargingPulseInternal();
    }

    private void StopChargingPulseInternal()
    {
        if (_chargingPulseStoryboard == null) return;

        _chargingPulseStoryboard.Stop();
        _chargingPulseStoryboard = null;

        if (!AmbientAnimationsAllowed || !BatteryFill.IsVisible)
        {
            Motion.Stop(BatteryFill, OpacityProperty, 1);
            return;
        }

        var resetAnimation = new DoubleAnimation
        {
            To = 1.0,
            Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(resetAnimation, VNotch.Services.AnimationConfig.TargetFps);
        BatteryFill.BeginAnimation(OpacityProperty, resetAnimation);
    }

}
