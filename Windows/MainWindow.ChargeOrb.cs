using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VNotch.Models;
using VNotch.Services;

namespace VNotch;

/// <summary>
/// Charging in the orb (the "Kabel" design, picked 2026-11-09). Plugging in buds the orb out and a cable shoots
/// from it across the gap into the notch, which gives a small jolt. While the battery charges, a ring around the
/// bolt shows the level and every few seconds a light runs from the bolt along the cable into the notch, whose
/// edge glows a moment. Unplugging pulls the cable taut until it snaps: the ends whip back and the recoil pushes
/// the orb away before it goes. Full (or held by battery care), the bolt turns into a tick and the cable draws back.
/// The quiet part only takes a free orb; plugging in and out push in front of whatever has it, for a moment.
/// </summary>
public partial class MainWindow
{
    private enum ChargeMoment { None, PlugIn, Unplug, Full }

    private static readonly TimeSpan ChargePulseEvery = TimeSpan.FromSeconds(2.5);
    // Charging that starts without a plug (battery care topping up) only shows once it has gone on this long.
    private static readonly TimeSpan ChargeQuietFor = TimeSpan.FromMinutes(2);
    private const int ChargeGlowFps = 30;
    private const int ChargeGlintFps = 60;
    private const int ChargeDoneAt = 75;
    private const double ChargeCableUnderNotch = 6;
    private const double ChargeCableIntoOrb = 8;
    private const double ChargeCableRestSag = 0.6;
    private static readonly Color ChargeGrey = Color.FromRgb(0x7C, 0x7C, 0x84);
    private static readonly Color ChargeCableGrey = Color.FromRgb(0x6F, 0x6F, 0x79);

    private static readonly DependencyProperty ChargeRingLevelProperty = DependencyProperty.Register(
        "ChargeRingLevel", typeof(double), typeof(MainWindow),
        new PropertyMetadata(0d, (d, e) => ((MainWindow)d).OnChargeRingLevelChanged((double)e.NewValue)));

    private static readonly DependencyProperty ChargeCableSagProperty = DependencyProperty.Register(
        "ChargeCableSag", typeof(double), typeof(MainWindow),
        new PropertyMetadata(ChargeCableRestSag, (d, e) => ((MainWindow)d).OnChargeCableSagChanged((double)e.NewValue)));

    private BatteryInfo? _chargeBattery;
    // Kept as values: the service may hand out the same BatteryInfo again with new contents.
    private bool _chargePlugged;
    private bool _chargeCharging;
    private bool _chargeFromPlug;
    private bool _chargeDone;
    private DateTime _chargingSinceUtc;
    private ChargeMoment _chargeMoment;
    private bool _chargeConnected;
    private bool _chargeHooked;
    private int _chargeToken;
    private double _chargeCableLength = 34;
    private Color _chargeTintColor = ChargeTintFor(100);
    private DispatcherTimer? _chargeMomentTimer;
    private DispatcherTimer? _chargePulseTimer;
    private DispatcherTimer? _chargeQuietTimer;
    private readonly SolidColorBrush _chargeTint = new(ChargeTintFor(100));
    private readonly SolidColorBrush _chargeTrack = new(Colors.Transparent);
    private readonly SolidColorBrush _chargeCableBrush = new(ChargeCableGrey);
    private readonly SolidColorBrush _chargeSparkBrush = new(Colors.White);
    private readonly SolidColorBrush _chargeHaloBrush = new(Colors.Transparent);

    /// <summary>Muted, from the battery: rose when low, sand in the middle, mint above half.</summary>
    internal static Color ChargeTintFor(int percent) => percent switch
    {
        <= 20 => Color.FromRgb(0xD9, 0x87, 0x7E),
        <= 50 => Color.FromRgb(0xD6, 0xB0, 0x7A),
        _ => Color.FromRgb(0x8E, 0xCF, 0xA6)
    };

    internal static string FormatChargeTime(int minutes) => minutes < 60
        ? $"{minutes} min"
        : minutes % 60 == 0 ? $"{minutes / 60} h" : $"{minutes / 60} h {minutes % 60} min";

    private int ChargeLevel => _chargeBattery is { Percentage: >= 0 } battery ? Math.Clamp(battery.Percentage, 0, 100) : 0;

    /// <summary>
    /// The quiet charging face wants the orb: plugged in after a plug event (until it is full), or charging on its
    /// own for a while (battery care topping up shows only when it lasts).
    /// </summary>
    private bool WantsChargingOrb
    {
        get
        {
            if (!_chargePlugged) return false;
            if (_chargeFromPlug && !_chargeDone) return true;
            return _chargeCharging && DateTime.UtcNow - _chargingSinceUtc >= ChargeQuietFor;
        }
    }

    #region Battery events

    /// <summary>Every battery update: notices plugging in and out and a full battery, and follows the level.</summary>
    private void UpdateChargeOrb(BatteryInfo battery)
    {
        bool known = _wasPluggedIn != null;
        bool plugged = battery.IsPowerConnected;
        bool charging = battery.HasBattery && battery.IsCharging;
        bool wasCharging = _chargeCharging;
        bool wanted = WantsChargingOrb;
        _chargeBattery = battery;

        var moment = ChargeMoment.None;
        if (!known)
        {
            // Already charging when the notch starts: show it like after a plug.
            _chargeFromPlug = plugged && charging;
        }
        else if (plugged && _wasPluggedIn == false)
        {
            moment = ChargeMoment.PlugIn;
            _chargeFromPlug = true;
            _chargeDone = false;
        }
        else if (!plugged && _wasPluggedIn == true)
        {
            moment = ChargeMoment.Unplug;
            _chargeFromPlug = false;
            _chargeDone = false;
        }
        else if (wasCharging && !charging && plugged && battery.Percentage >= ChargeDoneAt)
        {
            moment = ChargeMoment.Full;
            _chargeDone = true;
        }
        _wasPluggedIn = plugged;
        _chargePlugged = battery.HasBattery && plugged;

        if (charging && !wasCharging)
        {
            _chargingSinceUtc = DateTime.UtcNow;
            if (!_chargeFromPlug || _chargeDone) RestartChargeQuietTimer();
        }
        _chargeCharging = charging;
        if (!battery.HasBattery) return;

        bool faceUp = _bubbleShown && _bubbleActivity == LiveActivity.Charging;
        if (faceUp && moment is ChargeMoment.None or ChargeMoment.PlugIn)
        {
            var tint = ChargeTintFor(battery.Percentage);
            if (tint != _chargeTintColor) ApplyChargeTint(tint, animate: true);
            if (_chargeConnected && moment == ChargeMoment.None) AnimateChargeRing(ChargeLevel, TimeSpan.FromMilliseconds(400));
        }

        if (moment != ChargeMoment.None) BeginChargeMoment(moment);
        else if (WantsChargingOrb != wanted) ArrangeLiveActivities(_currentMediaInfo, applyCompact: true);
        else if (faceUp) UpdateBubbleContent();
    }

    /// <summary>
    /// Plugging in or out (or a full battery) takes the orb for a moment, even from the music or Claude; the next
    /// arrangement after it gives the orb back.
    /// </summary>
    private void BeginChargeMoment(ChargeMoment kind)
    {
        bool faceUp = _bubbleShown && _bubbleActivity == LiveActivity.Charging && ActivityBubble.Visibility == Visibility.Visible;
        bool connected = faceUp && _chargeConnected;
        if (kind == ChargeMoment.Full && !connected)
        {
            // Nothing on screen to celebrate on: just let the orb go (or not come).
            ArrangeLiveActivities(_currentMediaInfo, applyCompact: true);
            return;
        }

        _chargeMoment = kind;
        TimeSpan length = kind switch
        {
            ChargeMoment.Unplug => TimeSpan.FromMilliseconds(connected ? 1050 : faceUp ? 1900 : 2500),
            ChargeMoment.Full => TimeSpan.FromMilliseconds(3500),
            _ => TimeSpan.FromMilliseconds(3400)
        };
        if (_chargeMomentTimer == null)
        {
            _chargeMomentTimer = new DispatcherTimer(DispatcherPriority.Background);
            _chargeMomentTimer.Tick += (_, _) => EndChargeMoment();
        }
        _chargeMomentTimer.Stop();
        _chargeMomentTimer.Interval = length;
        _chargeMomentTimer.Start();
        StopChargePulse();

        if (connected)
        {
            switch (kind)
            {
                case ChargeMoment.PlugIn: PlayChargeArrival(plugged: true); break;
                case ChargeMoment.Unplug: PlayChargeUnplug(); break;
                case ChargeMoment.Full: PlayChargeFull(); break;
            }
        }
        // Brings the face up if it is not there yet; it plays the moment once it is (see ChargeFaceReady).
        ArrangeLiveActivities(_currentMediaInfo, applyCompact: true);
    }

    private void EndChargeMoment()
    {
        _chargeMomentTimer?.Stop();
        var ended = _chargeMoment;
        _chargeMoment = ChargeMoment.None;
        if (_cleanedUp) return;

        // Plugged in already full (or held by battery care): say so with the tick, then go.
        if (ended == ChargeMoment.PlugIn && _chargePlugged && !_chargeCharging && ChargeLevel >= ChargeDoneAt)
        {
            _chargeDone = true;
            if (_bubbleShown && _bubbleActivity == LiveActivity.Charging && _chargeConnected)
            {
                BeginChargeMoment(ChargeMoment.Full);
                return;
            }
        }
        ArrangeLiveActivities(_currentMediaInfo, applyCompact: true);
        RefreshChargePulse();
    }

    private void RestartChargeQuietTimer()
    {
        if (_chargeQuietTimer == null)
        {
            _chargeQuietTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = ChargeQuietFor + TimeSpan.FromSeconds(1) };
            _chargeQuietTimer.Tick += (_, _) =>
            {
                _chargeQuietTimer!.Stop();
                if (!_cleanedUp) ArrangeLiveActivities(_currentMediaInfo, applyCompact: true);
            };
        }
        _chargeQuietTimer.Stop();
        _chargeQuietTimer.Start();
    }

    private void StopChargeTimers()
    {
        _chargeMomentTimer?.Stop();
        _chargePulseTimer?.Stop();
        _chargeQuietTimer?.Stop();
        _chargeToken++;
    }

    #endregion

    #region The face and the cable

    private void HookChargeOrb()
    {
        if (_chargeHooked) return;
        _chargeHooked = true;
        ChargeRing.Stroke = _chargeTint;
        ChargeBolt.Fill = _chargeTint;
        ChargeCheck.Stroke = _chargeTint;
        ChargeTrack.Stroke = _chargeTrack;
        ChargeCableLeft.Stroke = _chargeCableBrush;
        ChargeCableRight.Stroke = _chargeCableBrush;
        ChargeCableGlintHalo.Fill = _chargeHaloBrush;
        foreach (var child in ChargeCableSparks.Children)
        {
            if (child is System.Windows.Shapes.Line line) line.Stroke = _chargeSparkBrush;
        }
        ApplyChargeTint(_chargeTintColor, animate: false);
        OnChargeCableSagChanged((double)GetValue(ChargeCableSagProperty));
    }

    /// <summary>One colour drives the ring, bolt, tick, cable, its light and the notch's glow.</summary>
    private void ApplyChargeTint(Color color, bool animate)
    {
        HookChargeOrb();
        _chargeTintColor = color;
        Tint(_chargeTint, color);
        Tint(_chargeTrack, Color.FromArgb(0x3D, color.R, color.G, color.B));
        Tint(_chargeCableBrush, Mix(color, ChargeCableGrey, 0.3));
        Tint(_chargeSparkBrush, Mix(color, Colors.White, 0.35));
        Tint(_chargeHaloBrush, Color.FromArgb(0x8C, color.R, color.G, color.B));

        var glow = new RadialGradientBrush
        {
            Center = new Point(1, 0.52),
            GradientOrigin = new Point(1, 0.52),
            RadiusX = 1,
            RadiusY = 0.9
        };
        glow.GradientStops.Add(new GradientStop(Color.FromArgb(0x8C, color.R, color.G, color.B), 0));
        glow.GradientStops.Add(new GradientStop(Color.FromArgb(0x33, color.R, color.G, color.B), 0.45));
        glow.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, color.R, color.G, color.B), 1));
        glow.Freeze();
        ChargeNotchGlow.Background = glow;

        void Tint(SolidColorBrush brush, Color to)
        {
            if (!animate || AnimationConfig.ReduceMotion)
            {
                brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
                brush.Color = to;
                return;
            }
            var change = new ColorAnimation(to, new Duration(TimeSpan.FromMilliseconds(400)));
            Timeline.SetDesiredFrameRate(change, ChargeGlowFps);
            brush.BeginAnimation(SolidColorBrush.ColorProperty, change);
        }
    }

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t));

    private void OnChargeRingLevelChanged(double level) => ChargeRing.Data = TimerArcGeometry(level / 100);

    /// <summary>The cable as two halves of one sagging curve, so it can snap in the middle.</summary>
    private void OnChargeCableSagChanged(double sag)
    {
        double length = _chargeCableLength;
        double y = 17, mid = 17 + sag;
        ChargeCableLeft.Data = Curve(new Point(0, y), new Point(length / 4, mid), new Point(length / 2, mid));
        ChargeCableRight.Data = Curve(new Point(length / 2, mid), new Point(length * 3 / 4, mid), new Point(length, y));
        Canvas.SetTop(ChargeCableGlint, sag);
        Canvas.SetTop(ChargeCableSparks, sag);

        static Geometry Curve(Point from, Point control, Point to)
        {
            var figure = new PathFigure { StartPoint = from, IsClosed = false };
            figure.Segments.Add(new QuadraticBezierSegment(control, to, true));
            var geometry = new PathGeometry(new[] { figure });
            geometry.Freeze();
            return geometry;
        }
    }

    /// <summary>From under the notch's edge to just inside the orb, at the orb's middle height.</summary>
    private void PositionChargeCable()
    {
        double ear = LeftEar.Visibility == Visibility.Visible ? LeftEar.Width - 1 : 0;
        double orbLeft = BubbleAnchor.X;
        double left = orbLeft - BubbleGap - ear - ChargeCableUnderNotch;
        double length = Math.Max(12, orbLeft + ChargeCableIntoOrb - left);
        ChargeCableAnchor.X = left;
        ChargeCableAnchor.Y = Math.Round(BubbleSize / 2 - 17);
        if (Math.Abs(length - _chargeCableLength) < 0.05 && Math.Abs(ChargeCable.Width - length) < 0.05) return;
        _chargeCableLength = length;
        ChargeCable.Width = length;
        ChargeCableRightScale.CenterX = length;
        ChargeCableRightTurn.CenterX = length;
        Canvas.SetLeft(ChargeCableSparks, length / 2 - 17);
        OnChargeCableSagChanged((double)GetValue(ChargeCableSagProperty));
    }

    /// <summary>The orb takes the charging face: an unlit bolt, no ring yet, no cable.</summary>
    private void PrepareChargeFace()
    {
        HookChargeOrb();
        _chargeToken++;
        _chargeConnected = false;
        StopChargePulse();
        ApplyChargeTint(ChargeTintFor(ChargeLevel), animate: false);
        Still(ChargeBolt, OpacityProperty, 0);
        Still(ChargeBoltBox, OpacityProperty, 1);
        Still(ChargeBoltScale, ScaleTransform.ScaleXProperty, 1);
        Still(ChargeBoltScale, ScaleTransform.ScaleYProperty, 1);
        Still(ChargeBoltTurn, RotateTransform.AngleProperty, 0);
        Still(ChargeTrack, OpacityProperty, 0);
        Still(ChargeCheck, OpacityProperty, 0);
        Still(ChargeCheck, System.Windows.Shapes.Shape.StrokeDashOffsetProperty, 7);
        Still(ChargeCheckScale, ScaleTransform.ScaleXProperty, 1);
        Still(ChargeCheckScale, ScaleTransform.ScaleYProperty, 1);
        Still(ChargeGlint, OpacityProperty, 0);
        Still(ChargeNotchGlow, OpacityProperty, 0);
        Still(this, ChargeRingLevelProperty, 0);
        Still(this, ChargeCableSagProperty, ChargeCableRestSag);
        Still(ChargeCableGrow, ScaleTransform.ScaleXProperty, 1);
        ResetChargeCableHalves();
        ChargeCable.Visibility = Visibility.Collapsed;
        PositionChargeCable();
    }

    private void ResetChargeCableHalves()
    {
        Still(ChargeCableLeftScale, ScaleTransform.ScaleXProperty, 1);
        Still(ChargeCableRightScale, ScaleTransform.ScaleXProperty, 1);
        Still(ChargeCableLeftTurn, RotateTransform.AngleProperty, 0);
        Still(ChargeCableRightTurn, RotateTransform.AngleProperty, 0);
        Still(ChargeCableGlint, OpacityProperty, 0);
        Still(ChargeCableSparks, OpacityProperty, 0);
    }

    private static void Still(IAnimatable target, DependencyProperty property, double value)
    {
        target.BeginAnimation(property, null);
        ((DependencyObject)target).SetValue(property, value);
    }

    /// <summary>The orb shows something else (or goes): the cable draws back into it and the light stops.</summary>
    private void LeaveChargeFace(bool fast)
    {
        _chargeToken++;
        _chargeConnected = false;
        StopChargePulse();
        Still(ChargeNotchGlow, OpacityProperty, 0);
        if (ChargeCable.Visibility != Visibility.Visible) return;
        if (AnimationConfig.ReduceMotion || !ChargeCable.IsVisible)
        {
            ChargeCable.Visibility = Visibility.Collapsed;
            return;
        }
        int token = _chargeToken;
        var back = new DoubleAnimation(0, new Duration(TimeSpan.FromMilliseconds(fast ? 120 : 180)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        Fps(back);
        back.Completed += (_, _) =>
        {
            if (token == _chargeToken) ChargeCable.Visibility = Visibility.Collapsed;
        };
        ChargeCableGrow.BeginAnimation(ScaleTransform.ScaleXProperty, back);
    }

    /// <summary>The face is on screen (after the bud or the swap): play what the moment asks for.</summary>
    private void ChargeFaceReady()
    {
        if (!_bubbleShown || _bubbleActivity != LiveActivity.Charging) return;
        switch (_chargeMoment)
        {
            case ChargeMoment.PlugIn:
                PlayChargeArrival(plugged: true);
                break;
            case ChargeMoment.Unplug:
                // Pushed in front to say goodbye: connect quietly, then snap.
                PlayChargeArrival(plugged: false);
                ChargeAfter(TimeSpan.FromMilliseconds(700), PlayChargeUnplug);
                break;
            default:
                PlayChargeArrival(plugged: false);
                break;
        }
    }

    private void ChargeAfter(TimeSpan delay, Action action)
    {
        int token = _chargeToken;
        var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (token == _chargeToken && !_cleanedUp) action();
        };
        timer.Start();
    }

    private void AnimateChargeRing(int level, TimeSpan duration)
    {
        if (AnimationConfig.ReduceMotion)
        {
            Still(this, ChargeRingLevelProperty, level);
            return;
        }
        var ring = new DoubleAnimation(level, new Duration(duration)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Fps(ring);
        BeginAnimation(ChargeRingLevelProperty, ring);
    }

    #endregion

    #region Motion

    private static TimeSpan Ms(double milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

    /// <summary>
    /// The cable shoots from the orb into the notch and springs taut; plugged in, the notch and the orb give a
    /// jolt. Then the bolt lights up, the ring runs up to the level and the first light flows.
    /// </summary>
    private void PlayChargeArrival(bool plugged)
    {
        _chargeConnected = true;
        int level = ChargeLevel;
        PositionChargeCable();
        ResetChargeCableHalves();
        ChargeCable.Visibility = Visibility.Visible;

        if (AnimationConfig.ReduceMotion)
        {
            Still(ChargeCableGrow, ScaleTransform.ScaleXProperty, 1);
            Still(this, ChargeCableSagProperty, ChargeCableRestSag);
            Still(ChargeBolt, OpacityProperty, 1);
            Still(ChargeTrack, OpacityProperty, 1);
            Still(this, ChargeRingLevelProperty, level);
            return;
        }

        var grow = new DoubleAnimation(0, 1, new Duration(Ms(190))) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Fps(grow);
        ChargeCableGrow.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        var sag = new DoubleAnimation(5, ChargeCableRestSag, new Duration(Ms(900)))
        {
            EasingFunction = new ElasticEase { Oscillations = 2, Springiness = 4, EasingMode = EasingMode.EaseOut }
        };
        Fps(sag);
        BeginAnimation(ChargeCableSagProperty, sag);

        if (plugged) JoltForCharge(Ms(170));

        var lit = new DoubleAnimation(0, 1, new Duration(Ms(280))) { BeginTime = Ms(330) };
        Fps(lit);
        ChargeBolt.BeginAnimation(OpacityProperty, lit);
        var pop = Keys(Ms(460), (0, 1), (0.4, 1.22), (0.7, 0.95), (1, 1));
        pop.BeginTime = Ms(330);
        ChargeBoltScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        ChargeBoltScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        var track = new DoubleAnimation(0, 1, new Duration(Ms(300))) { BeginTime = Ms(370) };
        Fps(track);
        ChargeTrack.BeginAnimation(OpacityProperty, track);
        var ring = new DoubleAnimation(0, level, new Duration(Ms(800)))
        {
            BeginTime = Ms(430),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Fps(ring);
        BeginAnimation(ChargeRingLevelProperty, ring);

        ChargeAfter(Ms(520), PlayChargePulse);
        ChargeAfter(Ms(1400), RefreshChargePulse);
    }

    /// <summary>The notch squashes a little as the cable lands in it, the orb with it.</summary>
    private void JoltForCharge(TimeSpan delay)
    {
        var squeezeX = Keys(Ms(380), (0, 1), (0.28, 1.05), (0.62, 0.98), (1, 1));
        var squeezeY = Keys(Ms(380), (0, 1), (0.28, 0.95), (0.62, 1.02), (1, 1));
        foreach (var animation in new[] { squeezeX, squeezeY })
        {
            animation.BeginTime = delay;
            animation.FillBehavior = FillBehavior.Stop;
        }
        BubbleScale.BeginAnimation(ScaleTransform.ScaleXProperty, squeezeX);
        BubbleScale.BeginAnimation(ScaleTransform.ScaleYProperty, squeezeY);

        // Only a resting notch: hover and press own its scale otherwise.
        bool resting = !_isExpanded && !_isAnimating &&
                       Math.Abs(NotchScale.ScaleX - 1) < 0.001 && Math.Abs(NotchScale.ScaleY - 1) < 0.001;
        if (!resting) return;
        var joltX = Keys(Ms(460), (0, 1), (0.3, 1.03), (0.64, 0.993), (1, 1));
        var joltY = Keys(Ms(460), (0, 1), (0.3, 0.95), (0.64, 1.012), (1, 1));
        foreach (var animation in new[] { joltX, joltY })
        {
            animation.BeginTime = delay;
            animation.FillBehavior = FillBehavior.Stop;
        }
        NotchScale.BeginAnimation(ScaleTransform.ScaleXProperty, joltX);
        NotchScale.BeginAnimation(ScaleTransform.ScaleYProperty, joltY);
        NotchShadowScale.BeginAnimation(ScaleTransform.ScaleXProperty, joltX);
        NotchShadowScale.BeginAnimation(ScaleTransform.ScaleYProperty, joltY);
    }

    /// <summary>A light leaves the bolt, runs along the cable into the notch, and the notch's edge glows a moment.</summary>
    private void PlayChargePulse()
    {
        if (AnimationConfig.ReduceMotion || !_chargeConnected || _cleanedUp) return;

        var glint = new DoubleAnimation(44, -14, new Duration(Ms(220))) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
        Timeline.SetDesiredFrameRate(glint, ChargeGlintFps);
        ChargeGlintShift.BeginAnimation(TranslateTransform.XProperty, glint);
        ChargeGlint.BeginAnimation(OpacityProperty, Blink(Ms(220), TimeSpan.Zero, ChargeGlintFps));

        var run = new DoubleAnimation(_chargeCableLength - 6, 5, new Duration(Ms(240))) { BeginTime = Ms(190) };
        Timeline.SetDesiredFrameRate(run, ChargeGlintFps);
        ChargeCableGlintShift.BeginAnimation(TranslateTransform.XProperty, run);
        ChargeCableGlint.BeginAnimation(OpacityProperty, Blink(Ms(240), Ms(190), ChargeGlintFps));

        var glow = new DoubleAnimationUsingKeyFrames { Duration = new Duration(Ms(620)), BeginTime = Ms(380) };
        glow.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        glow.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(0.3), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        glow.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1), new QuadraticEase { EasingMode = EasingMode.EaseInOut }));
        Timeline.SetDesiredFrameRate(glow, ChargeGlowFps);
        ChargeNotchGlow.BeginAnimation(OpacityProperty, glow);

        static DoubleAnimationUsingKeyFrames Blink(TimeSpan duration, TimeSpan begin, int fps)
        {
            var blink = new DoubleAnimationUsingKeyFrames { Duration = new Duration(duration), BeginTime = begin };
            blink.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
            blink.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.15)));
            blink.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.85)));
            blink.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
            Timeline.SetDesiredFrameRate(blink, fps);
            return blink;
        }
    }

    private bool ChargePulseAllowed =>
        !_cleanedUp && _chargeConnected && _bubbleShown && _bubbleActivity == LiveActivity.Charging &&
        _chargeMoment is ChargeMoment.None or ChargeMoment.PlugIn && AmbientAnimationsAllowed && ActivityBubble.IsVisible;

    /// <summary>The light runs every few seconds, only while the charging orb is on screen and nothing hides it.</summary>
    private void RefreshChargePulse()
    {
        if (!ChargePulseAllowed)
        {
            StopChargePulse();
            return;
        }
        if (_chargePulseTimer == null)
        {
            _chargePulseTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = ChargePulseEvery };
            _chargePulseTimer.Tick += (_, _) =>
            {
                if (ChargePulseAllowed) PlayChargePulse();
                else _chargePulseTimer!.Stop();
            };
        }
        if (!_chargePulseTimer.IsEnabled) _chargePulseTimer.Start();
    }

    private void StopChargePulse() => _chargePulseTimer?.Stop();

    /// <summary>The cable pulls taut and trembles, then snaps in the middle (see <see cref="SnapChargeCable"/>).</summary>
    private void PlayChargeUnplug()
    {
        StopChargePulse();
        _chargeConnected = false;
        if (AnimationConfig.ReduceMotion)
        {
            ChargeCable.Visibility = Visibility.Collapsed;
            Still(ChargeBolt, OpacityProperty, 0);
            Still(ChargeTrack, OpacityProperty, 0);
            Still(this, ChargeRingLevelProperty, 0);
            ApplyChargeTint(ChargeGrey, animate: false);
            return;
        }
        var taut = new DoubleAnimation(-0.7, new Duration(Ms(180))) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Fps(taut);
        BeginAnimation(ChargeCableSagProperty, taut);
        BubbleSlide.BeginAnimation(TranslateTransform.XProperty, Keys(Ms(300), (0, 0), (0.2, 0.9), (0.4, 0.1), (0.6, 1.1), (0.8, 0.3), (1, 1.4)));
        ChargeAfter(Ms(300), SnapChargeCable);
    }

    /// <summary>
    /// Snap: sparks where it broke, both ends whip back into the notch and the orb, the recoil pushes the orb away,
    /// and the bolt goes out grey.
    /// </summary>
    private void SnapChargeCable()
    {
        var sparks = Keys(Ms(360), (0, 1), (0.4, 1), (1, 0));
        ChargeCableSparks.BeginAnimation(OpacityProperty, sparks);
        var burst = new DoubleAnimation(0.4, 1.4, new Duration(Ms(360))) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Fps(burst);
        ChargeCableSparksScale.BeginAnimation(ScaleTransform.ScaleXProperty, burst);
        ChargeCableSparksScale.BeginAnimation(ScaleTransform.ScaleYProperty, burst);

        var shrink = new DoubleAnimation(1, 0, new Duration(Ms(340))) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Fps(shrink);
        ChargeCableLeftScale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        ChargeCableRightScale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        ChargeCableLeftTurn.BeginAnimation(RotateTransform.AngleProperty, Keys(Ms(340), (0, 0), (0.35, -24), (1, 6)));
        ChargeCableRightTurn.BeginAnimation(RotateTransform.AngleProperty, Keys(Ms(340), (0, 0), (0.35, 26), (1, -8)));

        BubbleSlide.BeginAnimation(TranslateTransform.XProperty, Keys(Ms(620), (0, 1.4), (0.28, 6.5), (0.6, -1.2), (0.82, 0.4), (1, 0)));
        var squashX = Keys(Ms(620), (0, 1), (0.28, 0.93), (0.6, 1.02), (1, 1));
        var squashY = Keys(Ms(620), (0, 1), (0.28, 1.06), (0.6, 0.98), (1, 1));
        squashX.FillBehavior = squashY.FillBehavior = FillBehavior.Stop;
        BubbleScale.BeginAnimation(ScaleTransform.ScaleXProperty, squashX);
        BubbleScale.BeginAnimation(ScaleTransform.ScaleYProperty, squashY);

        var dark = new DoubleAnimation(0, new Duration(Ms(260))) { BeginTime = Ms(60) };
        Fps(dark);
        ChargeBolt.BeginAnimation(OpacityProperty, dark);
        var unwind = new DoubleAnimation(0, new Duration(Ms(360))) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        Fps(unwind);
        BeginAnimation(ChargeRingLevelProperty, unwind);
        var trackOut = new DoubleAnimation(0, new Duration(Ms(300))) { BeginTime = Ms(200) };
        Fps(trackOut);
        ChargeTrack.BeginAnimation(OpacityProperty, trackOut);
        ApplyChargeTint(ChargeGrey, animate: true);
        ChargeAfter(Ms(700), () => ChargeCable.Visibility = Visibility.Collapsed);
    }

    /// <summary>
    /// Full: the ring closes, a few lights hurry through, the bolt turns into a tick with a happy bounce, and the
    /// cable draws back into the orb before it goes.
    /// </summary>
    private void PlayChargeFull()
    {
        StopChargePulse();
        ApplyChargeTint(ChargeTintFor(100), animate: true);
        if (AnimationConfig.ReduceMotion)
        {
            Still(this, ChargeRingLevelProperty, 100);
            Still(ChargeBoltBox, OpacityProperty, 0);
            Still(ChargeCheck, OpacityProperty, 1);
            Still(ChargeCheck, System.Windows.Shapes.Shape.StrokeDashOffsetProperty, 0);
            return;
        }
        var close = new DoubleAnimation(100, new Duration(Ms(850))) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } };
        Fps(close);
        BeginAnimation(ChargeRingLevelProperty, close);
        PlayChargePulse();
        ChargeAfter(Ms(330), PlayChargePulse);
        ChargeAfter(Ms(660), PlayChargePulse);
        ChargeAfter(Ms(900), () =>
        {
            var boltOut = new DoubleAnimation(0, new Duration(Ms(220))) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
            var shrink = new DoubleAnimation(0.3, new Duration(Ms(220))) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
            var turn = new DoubleAnimation(-35, new Duration(Ms(220))) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
            Fps(boltOut);
            Fps(shrink);
            Fps(turn);
            ChargeBoltBox.BeginAnimation(OpacityProperty, boltOut);
            ChargeBoltScale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
            ChargeBoltScale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
            ChargeBoltTurn.BeginAnimation(RotateTransform.AngleProperty, turn);

            var show = new DoubleAnimation(0, 1, new Duration(Ms(80))) { BeginTime = Ms(170) };
            var draw = new DoubleAnimation(7, 0, new Duration(Ms(300))) { BeginTime = Ms(170), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            Fps(show);
            Fps(draw);
            ChargeCheck.BeginAnimation(OpacityProperty, show);
            ChargeCheck.BeginAnimation(System.Windows.Shapes.Shape.StrokeDashOffsetProperty, draw);
            var tickPop = Keys(Ms(420), (0, 0.8), (0.7, 1.12), (1, 1));
            tickPop.BeginTime = Ms(170);
            ChargeCheckScale.BeginAnimation(ScaleTransform.ScaleXProperty, tickPop);
            ChargeCheckScale.BeginAnimation(ScaleTransform.ScaleYProperty, tickPop);

            var bounce = Keys(Ms(680), (0, 1), (0.32, 1.16), (0.6, 0.95), (0.8, 1.03), (1, 1));
            bounce.BeginTime = Ms(150);
            bounce.FillBehavior = FillBehavior.Stop;
            BubbleScale.BeginAnimation(ScaleTransform.ScaleXProperty, bounce);
            BubbleScale.BeginAnimation(ScaleTransform.ScaleYProperty, bounce);
        });
        ChargeAfter(Ms(3000), () =>
        {
            _chargeConnected = false;
            var back = new DoubleAnimation(0, new Duration(Ms(260))) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
            Fps(back);
            ChargeCableGrow.BeginAnimation(ScaleTransform.ScaleXProperty, back);
        });
    }

    #endregion

    #region Words

    /// <summary>The open pill: charging (or full), the time until full and the watts, and the level.</summary>
    private (string Title, string Subtitle, string Trailing) ChargeWords(BatteryInfo battery)
    {
        string title = Loc.Get(!battery.IsPowerConnected || _chargeMoment == ChargeMoment.Unplug ? "battery.onBattery"
            : _chargeCharging ? "battery.charging" : "battery.fullyCharged");
        var parts = new List<string>(2);
        if (_bubbleOpen && _chargeCharging)
        {
            var (minutes, watts) = ChargeReport();
            if (minutes > 0) parts.Add(Loc.Get("battery.fullIn", FormatChargeTime(minutes)));
            double shown = watts > 0 ? watts : battery.HasPowerRate ? Math.Abs(battery.PowerWatts) : 0;
            if (shown >= 0.5) parts.Add(shown >= 10 ? $"{shown:0} W" : $"{shown:0.0} W");
        }
        return (title, string.Join(" · ", parts), battery.GetPercentageText());
    }

    /// <summary>Minutes until full and the charge rate, from Windows' battery report (0 when it does not know).</summary>
    private static (int Minutes, double Watts) ChargeReport()
    {
        try
        {
            var report = global::Windows.Devices.Power.Battery.AggregateBattery.GetReport();
            if (report.ChargeRateInMilliwatts is not int rate || rate <= 0) return (0, 0);
            int minutes = 0;
            if (report.RemainingCapacityInMilliwattHours is int remaining && report.FullChargeCapacityInMilliwattHours is int full && full > remaining)
                minutes = (int)Math.Round((full - remaining) * 60.0 / rate);
            return (minutes is > 0 and <= 24 * 60 ? minutes : 0, rate / 1000.0);
        }
        catch (Exception)
        {
            return (0, 0);
        }
    }

    #endregion
}
