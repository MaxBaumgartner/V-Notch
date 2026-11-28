using System;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using VNotch.Controls;
using VNotch.Controllers;
using VNotch.Presenters;
using VNotch.Services;

namespace VNotch;

public partial class MainWindow
{
    private static string FormatIdleTemperature(double celsius)
    {
        int value = (int)Math.Round(celsius, MidpointRounding.AwayFromZero);
        return (value < 0 ? "\u2212" + (-value).ToString(System.Globalization.CultureInfo.InvariantCulture) : value.ToString(System.Globalization.CultureInfo.InvariantCulture)) + "\u00b0";
    }

    #region Idle notch: slim, with the time and the weather

    // The resting width with nothing live; music, Claude Code and countdowns get the full width back.
    private const double IdleCompactWidth = 150;
    private static readonly TimeSpan IdleWeatherMaxAge = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan IdleWeatherTooOld = TimeSpan.FromHours(3);
    private static readonly TimeSpan IdleSettleRetry = TimeSpan.FromMilliseconds(700);

    private DispatcherTimer? _idleClockTimer;
    private DispatcherTimer? _idleSettleTimer;
    private bool _idleGlanceShown;
    private bool _idleGlanceHooked;
    private CompactSurfaceAttachment? _idleGlanceAttachment;
    private readonly TranslateTransform _idleClockShift = new();
    private readonly ScaleTransform _idleWeatherScale = new();
    private int _idleClockToken;
    private int _idleWeatherToken;
    private string? _idleWeatherKey;

    /// <summary>Nothing live in the closed notch: no music, no Claude Code, no countdown.</summary>
    private bool IsIdleShape =>
        _settings?.SlimIdleNotch == true && !_isMusicCompactMode && _mainActivity == LiveActivity.None && !IsCountdownLive;

    private double FullCompactWidth => _settings.EnableDynamicIslandMode ? _settings.DynamicIslandWidth : _settings.Width;

    /// <summary>Slims the closed notch when nothing is live and widens it again when something is.</summary>
    private void UpdateIdleShape()
    {
        if (_cleanedUp || _settings == null || IdleGlance == null) return;
        HookIdleGlance();
        _collapsedWidth = GetCollapsedWidth();
        ShowIdleGlance(IsIdleShape);
        SettleIdleWidth();
    }

    private void SettleIdleWidth()
    {
        _idleSettleTimer?.Stop();
        if (_idleGlanceAttachment == null || !NotchBorder.IsVisible || !_isNotchVisible || _isHiddenByFullscreen ||
            (!IdleGlance.IsVisible && !AnimationConfig.ReduceMotion)) return;
        // Opening, closing, pills and the volume HUD all end on the resting width themselves.
        if (_isAnimating || _isExpanded || _isMusicExpanded) return;
        if (_isGreetingActive || IsCountdownCompletionVisualActive ||
            _compactPillArbiter.ActiveSlot != CompactPillSlot.None)
        {
            _idleSettleTimer ??= CreateIdleSettleTimer();
            _idleSettleTimer.Start();
            return;
        }
        if (!double.IsFinite(NotchBorder.Width) || !double.IsFinite(NotchBorder.Height)) return;
        if (Math.Abs(NotchBorder.Width - _collapsedWidth) < .5 && !Motion.IsRunning(NotchBorder, WidthProperty))
        {
            FinishIdleGlanceTransition();
            return;
        }
        _notchShellPresenter?.CancelCurrentAnimation();
        _idleGlanceAttachment.MoveTo(_collapsedWidth, _collapsedHeight, Motion.Shift, _idleGlanceShown,
            FinishIdleGlanceTransition);
        if (_privacyIndicatorsVisible) UpdatePrivacyDotPosition(animate: AmbientAnimationsAllowed);
    }

    private DispatcherTimer CreateIdleSettleTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = IdleSettleRetry };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!_cleanedUp) SettleIdleWidth();
        };
        return timer;
    }

    /// <summary>The idle face keeps its layout as the compact notch slims or widens.</summary>
    private void ShowIdleGlance(bool show)
    {
        if (show == _idleGlanceShown) return;
        _idleGlanceShown = show;
        _idleGlanceAttachment ??= new(NotchBorder, IdleGlance, () => new Size(FullCompactWidth, _collapsedHeight));
        Motion.Stop(IdleGlance, OpacityProperty, 1);
        if (show)
        {
            RefreshIdleClock(animate: false);
            UpdateIdleWeather();
            if (IdleGlance.Visibility != Visibility.Visible)
            {
                _idleGlanceAttachment.Attach(_collapsedWidth, _collapsedHeight);
                IdleGlance.Visibility = Visibility.Visible;
            }
        }
        else if (IdleGlance.IsVisible && AmbientAnimationsAllowed && !_isExpanded && !_isMusicExpanded)
            _idleGlanceAttachment.Attach(Math.Min(FullCompactWidth, IdleCompactWidth), _collapsedHeight);
        else FinishIdleGlanceTransition();
    }

    private void RefreshIdleGlanceMotionVisibility()
    {
        if (_idleGlanceAttachment == null || AmbientAnimationsAllowed) return;
        StopIdleGlanceDetails();
        if (!_isAnimating && _compactPillArbiter.ActiveSlot == CompactPillSlot.None) _idleGlanceAttachment.Settle();
        FinishIdleGlanceTransition();
    }

    private void FinishIdleGlanceTransition()
    {
        _idleGlanceAttachment?.Detach(_idleGlanceShown);
        if (!_idleGlanceShown) IdleGlance.Visibility = Visibility.Collapsed;
    }

    private void HookIdleGlance()
    {
        if (_idleGlanceHooked) return;
        _idleGlanceHooked = true;
        IdleClock.RenderTransform = _idleClockShift;
        IdleWeather.RenderTransformOrigin = new Point(0.5, 0.5);
        IdleWeather.RenderTransform = _idleWeatherScale;
        // The clock only ticks while it is on screen; hidden (expanded, fullscreen) nothing runs.
        IdleGlance.IsVisibleChanged += (_, _) =>
        {
            if (_cleanedUp) return;
            if (IdleGlance.IsVisible && AmbientAnimationsAllowed)
            {
                RefreshIdleClock(animate: false);
                UpdateIdleWeather();
                ScheduleIdleClock();
                RefreshIdleWeatherIfStale();
            }
            else if (!AmbientAnimationsAllowed) RefreshIdleGlanceMotionVisibility();
            else
            {
                StopIdleGlanceDetails();
                if (!_idleGlanceShown) FinishIdleGlanceTransition();
            }
        };
    }

    private void StopIdleGlanceDetails()
    {
        _idleClockTimer?.Stop();
        _idleSettleTimer?.Stop();
        Motion.Stop(IdleClock, OpacityProperty, 1);
        Motion.Stop(_idleClockShift, TranslateTransform.XProperty, 0);
        Motion.Stop(_idleWeatherScale, ScaleTransform.ScaleXProperty, 1);
        Motion.Stop(_idleWeatherScale, ScaleTransform.ScaleYProperty, 1);
        Motion.Stop(IdleWeather, OpacityProperty, IdleWeather.Visibility == Visibility.Visible ? 1 : 0);
    }

    private void ScheduleIdleClock()
    {
        if (_cleanedUp || !IdleGlance.IsVisible || !AmbientAnimationsAllowed) return;
        _idleClockTimer ??= CreateIdleClockTimer();
        var now = DateTime.Now;
        _idleClockTimer.Interval = TimeSpan.FromSeconds(60 - now.Second) - TimeSpan.FromMilliseconds(now.Millisecond) + TimeSpan.FromMilliseconds(50);
        _idleClockTimer.Start();
    }

    private DispatcherTimer CreateIdleClockTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background);
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_cleanedUp || !IdleGlance.IsVisible || !AmbientAnimationsAllowed) return;
            RefreshIdleClock(animate: true);
            RefreshIdleWeatherIfStale();
            ScheduleIdleClock();
        };
        return timer;
    }

    private void RefreshIdleClock(bool animate)
    {
        string text = DateTime.Now.ToString("t", CultureInfo.CurrentCulture);
        if (IdleClock.Text == text) return;
        int token = ++_idleClockToken;
        if (!animate || AnimationConfig.ReduceMotion || !IdleClock.IsVisible)
        {
            IdleClock.Text = text;
            Motion.Stop(IdleClock, OpacityProperty, 1);
            return;
        }
        Motion.To(IdleClock, OpacityProperty, 0, Motion.Exit, done: () =>
        {
            if (_cleanedUp || token != _idleClockToken) return;
            IdleClock.Text = text;
            Motion.To(IdleClock, OpacityProperty, 1, Motion.Tile);
        });
    }

    private void RefreshIdleWeatherIfStale()
    {
        if (_settings == null || !NetworkPrivacy.Allows(_settings, NetworkFeature.Weather)) return;
        if (_weatherCache is { } cached && DateTime.Now - cached.FetchedAt < IdleWeatherMaxAge) return;
        GetWeatherAsync(IdleWeatherMaxAge, CancellationToken.None).SafeFireAndForget("WEATHER-IDLE");
    }

    /// <summary>The weather beside the clock; without a recent forecast the clock sits alone in the middle.</summary>
    private void UpdateIdleWeather()
    {
        if (IdleWeather == null) return;
        bool allowed = _settings != null && NetworkPrivacy.Allows(_settings, NetworkFeature.Weather);
        var forecast = allowed ? _weatherCache : null;
        int token = ++_idleWeatherToken;
        if (forecast == null || DateTime.Now - forecast.FetchedAt > IdleWeatherTooOld)
        {
            _idleWeatherKey = null;
            PositionIdleClock(HorizontalAlignment.Center);
            if (!IdleWeather.IsVisible || !AmbientAnimationsAllowed)
            {
                Motion.Stop(IdleWeather, OpacityProperty, 0);
                IdleWeather.Visibility = Visibility.Collapsed;
                return;
            }
            Motion.To(_idleWeatherScale, ScaleTransform.ScaleXProperty, Motion.ExitToScale, Motion.Exit);
            Motion.To(_idleWeatherScale, ScaleTransform.ScaleYProperty, Motion.ExitToScale, Motion.Exit);
            Motion.To(IdleWeather, OpacityProperty, 0, Motion.Exit, done: () =>
            {
                if (token == _idleWeatherToken) IdleWeather.Visibility = Visibility.Collapsed;
            });
            return;
        }
        bool wasShown = IdleWeather.Visibility == Visibility.Visible;
        string temperature = FormatIdleTemperature(forecast.Now.Temperature);
        string key = $"{forecast.Now.Code}:{forecast.Now.IsDay}:{temperature}";
        PositionIdleClock(HorizontalAlignment.Left);
        void Reveal()
        {
            if (_cleanedUp || token != _idleWeatherToken) return;
            if (_idleWeatherKey != key)
            {
                IdleWeatherIcon.Content = WeatherIcons.Create(forecast.Now.Code, forecast.Now.IsDay, 15);
                IdleWeatherTemp.Text = temperature;
                _idleWeatherKey = key;
            }
            IdleWeather.Visibility = Visibility.Visible;
            if (!IdleWeather.IsVisible || !AmbientAnimationsAllowed)
            {
                Motion.Stop(IdleWeather, OpacityProperty, 1);
                Motion.Stop(_idleWeatherScale, ScaleTransform.ScaleXProperty, 1);
                Motion.Stop(_idleWeatherScale, ScaleTransform.ScaleYProperty, 1);
                return;
            }
            Motion.To(IdleWeather, OpacityProperty, 1, Motion.Tile, from: wasShown ? null : 0);
            Motion.To(_idleWeatherScale, ScaleTransform.ScaleXProperty, 1, Motion.TileScale, from: wasShown ? null : Motion.TileFromScale);
            Motion.To(_idleWeatherScale, ScaleTransform.ScaleYProperty, 1, Motion.TileScale, from: wasShown ? null : Motion.TileFromScale);
        }
        if (wasShown && _idleWeatherKey != key && IdleWeather.IsVisible)
            Motion.To(IdleWeather, OpacityProperty, 0, Motion.Exit, done: Reveal);
        else Reveal();
    }

    private void PositionIdleClock(HorizontalAlignment alignment)
    {
        if (IdleClock.HorizontalAlignment == alignment) return;
        bool animate = IdleClock.IsVisible && AmbientAnimationsAllowed;
        double before = animate ? IdleClock.TranslatePoint(new Point(), IdleGlance).X : 0;
        IdleClock.HorizontalAlignment = alignment;
        if (!animate) return;
        IdleGlance.UpdateLayout();
        double after = IdleClock.TranslatePoint(new Point(), IdleGlance).X;
        double velocity = Motion.VelocityOf(_idleClockShift, TranslateTransform.XProperty);
        Motion.Stop(_idleClockShift, TranslateTransform.XProperty, _idleClockShift.X + before - after);
        Motion.To(_idleClockShift, TranslateTransform.XProperty, 0, Motion.Glide,
            velocity: velocity);
    }

    #endregion
}
