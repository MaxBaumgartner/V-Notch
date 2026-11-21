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

/// <summary>Charging uses a quiet connection and a data-driven level ring beside the notch.</summary>
public partial class MainWindow
{
    private enum ChargeMoment { None, PlugIn, Unplug, Full }

    private const int ChargeGlowFps = 30;
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
    private ChargeMoment _chargeMoment;
    private bool _chargeConnected;
    private bool _chargeHooked;
    private int _chargeToken;
    private double _chargeCableLength = 34;
    private Color _chargeTintColor = ChargeTintFor(100);
    private DispatcherTimer? _chargeMomentTimer;
    private readonly TranslateTransform _chargeRimShift = new(RimStreakBefore, 0);
    // Where the rim streak's head is, in notch widths: just off the right edge, past the middle, past the left edge.
    private const double RimStreakBefore = 0.6;
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
    /// The quiet charging face wants the orb only after a real plug-in, until it is full. Charging while the laptop
    /// stays on the cable (battery care topping up, or plugged in since start-up) shows nothing.
    /// </summary>
    private bool WantsChargingOrb => _chargePlugged && _chargeFromPlug && !_chargeDone;


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
            // Already on the cable when the notch starts: the laptop mostly lives on it, so that is no charge to show.
            _chargeFromPlug = false;
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
            ChargeMoment.Unplug => TimeSpan.FromMilliseconds(700),
            ChargeMoment.Full => TimeSpan.FromMilliseconds(1400),
            _ => TimeSpan.FromMilliseconds(1800)
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

        if (faceUp)
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

    private void StopChargeTimers()
    {
        _chargeMomentTimer?.Stop();
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
        Tint(_chargeCableBrush, Mix(color, ChargeCableGrey, 0.3), ChargeCable.IsVisible);
        _chargeSparkBrush.Color = Mix(color, Colors.White, 0.35);
        _chargeHaloBrush.Color = Color.FromArgb(0x8C, color.R, color.G, color.B);

        // The glow spans the whole notch now, so its reach is a share of the width: about the right quarter.
        var glow = new RadialGradientBrush
        {
            Center = new Point(1, 0.5),
            GradientOrigin = new Point(1, 0.5),
            RadiusX = 0.28,
            RadiusY = 1.1
        };
        glow.GradientStops.Add(new GradientStop(Color.FromArgb(0x73, color.R, color.G, color.B), 0));
        glow.GradientStops.Add(new GradientStop(Color.FromArgb(0x2B, color.R, color.G, color.B), 0.5));
        glow.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, color.R, color.G, color.B), 1));
        glow.Freeze();
        ChargeNotchGlow.Background = glow;

        // The streak on the rim: a bright head on its left (it travels right to left), a tail fading out behind.
        // Moved by _chargeRimShift in units of the notch's width.
        var rim = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0), RelativeTransform = _chargeRimShift };
        rim.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, color.R, color.G, color.B), 0.38));
        rim.GradientStops.Add(new GradientStop(Mix(color, Colors.White, 0.2), 0.44));
        rim.GradientStops.Add(new GradientStop(Color.FromArgb(0xA6, color.R, color.G, color.B), 0.6));
        rim.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, color.R, color.G, color.B), 0.94));
        ChargeNotchRim.BorderBrush = rim;

        void Tint(SolidColorBrush brush, Color to, bool visible = true)
        {
            if (!animate || !visible || !AmbientAnimationsAllowed || !ActivityBubble.IsVisible)
            {
                brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
                brush.Color = to;
                return;
            }
            var change = new ColorAnimation(to, new Duration(TimeSpan.FromMilliseconds(180)));
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
        Still(ChargeNotchRim, OpacityProperty, 0);
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
        Motion.Stop((DependencyObject)target, property, value);
    }

    /// <summary>The orb shows something else (or goes): the cable draws back into it and the light stops.</summary>
    private void LeaveChargeFace(bool fast)
    {
        _chargeToken++;
        _chargeConnected = false;
        StopChargePulse();
        Still(ChargeNotchGlow, OpacityProperty, 0);
        Still(ChargeNotchRim, OpacityProperty, 0);
        if (ChargeCable.Visibility != Visibility.Visible) return;
        if (AnimationConfig.ReduceMotion || !ChargeCable.IsVisible)
        {
            ChargeCable.Visibility = Visibility.Collapsed;
            return;
        }
        int token = _chargeToken;
        Motion.To(ChargeCableGrow, ScaleTransform.ScaleXProperty, 0, Motion.PopBack, done: () =>
        {
            if (token == _chargeToken) ChargeCable.Visibility = Visibility.Collapsed;
        });
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
            case ChargeMoment.Full:
                PlayChargeFull();
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
            if (token == _chargeToken && !_cleanedUp && ActivityBubble.IsVisible && (AmbientAnimationsAllowed || AnimationConfig.ReduceMotion)) action();
        };
        timer.Start();
    }

    private void AnimateChargeRing(int level, TimeSpan duration)
    {
        Motion.To(this, ChargeRingLevelProperty, level, Motion.Shift);
    }

    #endregion

    #region Motion

    private static TimeSpan Ms(double milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

    /// <summary>The cable grows from the orb; the level and bolt settle together without a jolt.</summary>
    private void PlayChargeArrival(bool plugged)
    {
        ++_chargeToken;
        _chargeConnected = true;
        PositionChargeCable();
        bool entering = ChargeCable.Visibility != Visibility.Visible;
        if (entering) ResetChargeCableHalves();
        else
        {
            Motion.To(ChargeCableLeftScale, ScaleTransform.ScaleXProperty, 1, Motion.Pop);
            Motion.To(ChargeCableRightScale, ScaleTransform.ScaleXProperty, 1, Motion.Pop);
            Motion.To(ChargeCableLeftTurn, RotateTransform.AngleProperty, 0, Motion.Shift);
            Motion.To(ChargeCableRightTurn, RotateTransform.AngleProperty, 0, Motion.Shift);
        }
        ChargeCable.Visibility = Visibility.Visible;
        StopChargePulse();
        Motion.To(ChargeBoltBox, OpacityProperty, 1, Motion.Exit);
        Motion.To(ChargeCheck, OpacityProperty, 0, Motion.Exit);
        Motion.To(ChargeCheckScale, ScaleTransform.ScaleXProperty, .6, Motion.Exit);
        Motion.To(ChargeCheckScale, ScaleTransform.ScaleYProperty, .6, Motion.Exit);
        Motion.To(ChargeCableGrow, ScaleTransform.ScaleXProperty, 1, Motion.Pop, from: entering ? 0 : null);
        Motion.To(this, ChargeCableSagProperty, ChargeCableRestSag, Motion.Shift);
        Motion.To(ChargeBolt, OpacityProperty, 1, Motion.Exit);
        Motion.To(ChargeBoltScale, ScaleTransform.ScaleXProperty, 1, Motion.Pop, from: entering ? .6 : null);
        Motion.To(ChargeBoltScale, ScaleTransform.ScaleYProperty, 1, Motion.Pop, from: entering ? .6 : null);
        Motion.To(ChargeTrack, OpacityProperty, 1, Motion.Exit);
        Motion.To(this, ChargeRingLevelProperty, ChargeLevel, Motion.Shift);
    }

    // Battery data moves the ring. A stationary charging state needs no repeating light or timer.
    private void RefreshChargePulse() => StopChargePulse();
    private void StopChargePulse()
    {
        // A hidden orb must release even finite light sweeps, not just the next pulse timer.
        Motion.Stop(ChargeGlintShift, TranslateTransform.XProperty);
        Motion.Stop(ChargeCableGlintShift, TranslateTransform.XProperty);
        Motion.Stop(_chargeRimShift, TranslateTransform.XProperty);
        Still(ChargeGlint, OpacityProperty, 0);
        Still(ChargeCableGlint, OpacityProperty, 0);
        Still(ChargeNotchGlow, OpacityProperty, 0);
        Still(ChargeNotchRim, OpacityProperty, 0);
    }

    /// <summary>Unplugging withdraws the connection without sparks, recoil or a shell wobble.</summary>
    private void PlayChargeUnplug()
    {
        ++_chargeToken;
        _chargeConnected = false;
        StopChargePulse();
        SnapChargeCable();
    }

    private void SnapChargeCable()
    {
        _chargeConnected = false;
        int token = _chargeToken;
        Motion.To(ChargeCableLeftScale, ScaleTransform.ScaleXProperty, 0, Motion.PopBack);
        Motion.To(ChargeCableRightScale, ScaleTransform.ScaleXProperty, 0, Motion.PopBack, done: () =>
        {
            if (token == _chargeToken && !_chargeConnected) ChargeCable.Visibility = Visibility.Collapsed;
        });
        Motion.To(ChargeBolt, OpacityProperty, 0, Motion.Exit);
        Motion.To(this, ChargeRingLevelProperty, 0, Motion.Exit);
        Motion.To(ChargeTrack, OpacityProperty, 0, Motion.Exit);
        ApplyChargeTint(ChargeGrey, animate: !AnimationConfig.ReduceMotion);
    }

    /// <summary>A full charge confirms once with a small check and draws the cable back into the orb.</summary>
    private void PlayChargeFull()
    {
        ++_chargeToken;
        StopChargePulse();
        ApplyChargeTint(ChargeTintFor(100), animate: true);
        Motion.To(this, ChargeRingLevelProperty, 100, Motion.Shift);
        Motion.To(ChargeBoltBox, OpacityProperty, 0, Motion.Exit);
        Motion.To(ChargeBoltScale, ScaleTransform.ScaleXProperty, .6, Motion.Exit);
        Motion.To(ChargeBoltScale, ScaleTransform.ScaleYProperty, .6, Motion.Exit);
        Motion.To(ChargeCheck, OpacityProperty, 1, Motion.Exit);
        Motion.To(ChargeCheck, System.Windows.Shapes.Shape.StrokeDashOffsetProperty, 0, Motion.Exit);
        Motion.To(ChargeCheckScale, ScaleTransform.ScaleXProperty, 1, Motion.Pop);
        Motion.To(ChargeCheckScale, ScaleTransform.ScaleYProperty, 1, Motion.Pop);
        _chargeConnected = false;
        int token = _chargeToken;
        Motion.To(ChargeCableGrow, ScaleTransform.ScaleXProperty, 0, Motion.PopBack, done: () =>
        {
            if (token == _chargeToken && !_chargeConnected) ChargeCable.Visibility = Visibility.Collapsed;
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
