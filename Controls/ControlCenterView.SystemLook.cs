using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using VNotch.Services;
using VNotch.Services.ControlCenter;

namespace VNotch.Controls;

/// <summary>
/// How the system tab looks: one muted colour for the whole tab that comes from the PC's load (mist blue when
/// calm, lilac, sand under real work, rosé at full load), the way the music tab takes its colour from the
/// cover. Labels, bars, switches, the Wi-Fi arcs and the background all wear it and fade along when it
/// changes. Also the small moving parts: sparklines that draw in, the ping radar, arrows that nudge with
/// traffic, Wi-Fi arcs that build up to the signal, battery rings that fill.
/// </summary>
public partial class ControlCenterView
{
    #region Mood

    private const double MoodFadeMs = 1200;

    // Muted on purpose: bright system colours on black were too loud for this tab.
    private static readonly (double Load, Color Color)[] MoodStops =
    [
        (0, Color.FromRgb(0x7F, 0xA6, 0xCC)),
        (45, Color.FromRgb(0xA1, 0x9B, 0xD2)),
        (72, Color.FromRgb(0xD2, 0xAE, 0x7C)),
        (100, Color.FromRgb(0xD2, 0x85, 0x7E))
    ];

    private static readonly Color PingGood = Color.FromRgb(0x8C, 0xC7, 0xBC);
    private static readonly Color PingSlow = Color.FromRgb(0xD9, 0xC2, 0x7E);
    private static readonly Color PingBad = Color.FromRgb(0xD9, 0x8A, 0x84);

    /// <summary>The tab's colour at a load from 0 to 100, blended between the stops.</summary>
    internal static Color MoodAt(double load)
    {
        load = Math.Clamp(double.IsNaN(load) ? 0 : load, 0, 100);
        for (int i = 1; i < MoodStops.Length; i++)
        {
            if (load > MoodStops[i].Load) continue;
            var (fromLoad, from) = MoodStops[i - 1];
            var (toLoad, to) = MoodStops[i];
            return Mix(from, to, (load - fromLoad) / (toLoad - fromLoad));
        }
        return MoodStops[^1].Color;
    }

    /// <summary>What sets the colour: the busiest of CPU and GPU, and the memory once it fills up.</summary>
    internal static double MoodLoad(SystemStats stats) =>
        Math.Max(Math.Max(stats.CpuPercent ?? 0, (stats.GpuPercent ?? 0) * 0.95), stats.RamPercent * 0.7);

    private static Color Mix(Color a, Color b, double t) => Color.FromArgb(
        (byte)Math.Round(a.A + (b.A - a.A) * t),
        (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t));

    private static Color Lift(Color c, double towardWhite) => Mix(c, Colors.White, towardWhite);
    private static Color Dim(Color c, double factor) => Color.FromRgb((byte)(c.R * factor), (byte)(c.G * factor), (byte)(c.B * factor));
    private static Color Alpha(Color c, double alpha) => Color.FromArgb((byte)Math.Round(alpha * 255), c.R, c.G, c.B);

    private double _moodLoad = double.NaN;
    // The tab's colour itself (switches, rings), lifted for text and icons, softer for status words.
    private readonly SolidColorBrush _mood = new(MoodAt(0));
    private readonly SolidColorBrush _moodInk = new(Lift(MoodAt(0), 0.3));
    private readonly SolidColorBrush _moodSoft = new(Alpha(Lift(MoodAt(0), 0.2), 0.88));
    private readonly GradientStop _moodBarFrom = new(MoodAt(0), 0);
    private readonly GradientStop _moodBarTo = new(Dim(MoodAt(0), 0.62), 1);
    private readonly GradientStop _moodAreaTop = new(Alpha(MoodAt(0), 0.16), 0);
    private readonly GradientStop _moodAreaBottom = new(Alpha(MoodAt(0), 0), 1);
    private LinearGradientBrush? _moodBar;
    private LinearGradientBrush? _moodArea;
    private MoodBackdrop? _backdrop;

    /// <summary>Bars fade from the tab's colour to a darker shade of it, like the music tab's progress bar.</summary>
    private LinearGradientBrush MoodBar => _moodBar ??= new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0), GradientStops = { _moodBarFrom, _moodBarTo } };

    private LinearGradientBrush MoodArea => _moodArea ??= new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1), GradientStops = { _moodAreaTop, _moodAreaBottom } };

    // What the backdrop shows: the tab's colour and its two neighbours.
    private (Color Main, Color Warmer, Color Cooler)? _moodColors;

    /// <summary>The colour fields behind the whole notch, for the system, home and school tabs.</summary>
    internal void AttachBackdrop(MoodBackdrop backdrop)
    {
        _backdrop = backdrop;
        var (main, warmer, cooler) = _moodColors ?? (MoodAt(0), MoodAt(22), MoodAt(0));
        backdrop.SetMood(main, warmer, cooler, 0);
    }

    /// <summary>The system tab: moves to the colour of <paramref name="load"/>.</summary>
    private void SetMood(double load)
    {
        // Small wobbles would keep the colours sliding for nothing.
        if (!double.IsNaN(_moodLoad) && Math.Abs(load - _moodLoad) < 2) return;
        _moodLoad = load;
        SetMoodColors(MoodAt(load), MoodAt(load + 22), MoodAt(load - 18));
    }

    /// <summary>
    /// Moves the whole tab to <paramref name="main"/> (labels, bars, switches, rings) and the backdrop to it and its
    /// two neighbours; the first call paints at once, later ones fade.
    /// </summary>
    private void SetMoodColors(Color main, Color warmer, Color cooler)
    {
        if (_moodColors is { } now && now.Main == main && now.Warmer == warmer && now.Cooler == cooler) return;
        bool first = _moodColors == null;
        _moodColors = (main, warmer, cooler);
        double ms = first || !IsVisible ? 0 : MoodFadeMs;
        SetOrTint(_mood, main, ms);
        SetOrTint(_moodInk, Lift(main, 0.3), ms);
        SetOrTint(_moodSoft, Alpha(Lift(main, 0.2), 0.88), ms);
        Fade(_moodBarFrom, main, ms);
        Fade(_moodBarTo, Dim(main, 0.62), ms);
        Fade(_moodAreaTop, Alpha(main, 0.16), ms);
        Fade(_moodAreaBottom, Alpha(main, 0), ms);
        _backdrop?.SetMood(main, warmer, cooler, ms);
    }

    private static void Fade(GradientStop stop, Color to, double ms)
    {
        if (ms <= 0 || AnimationConfig.ReduceMotion)
        {
            stop.BeginAnimation(GradientStop.ColorProperty, null);
            stop.Color = to;
            return;
        }
        var fade = new ColorAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = WeatherMotion.Out };
        Timeline.SetDesiredFrameRate(fade, AnimationConfig.TargetFps);
        stop.BeginAnimation(GradientStop.ColorProperty, fade, HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>True while a section of the system tab is being built: its switches follow the tab's colour.</summary>
    private bool AccentIsMood => ReferenceEquals(_accent, _mood);

    #endregion

    #region Sparklines

    private const int SparkPoints = 20; // a minute at one sample every three seconds
    private static readonly TimeSpan SparkStaleAfter = TimeSpan.FromMinutes(2);
    private readonly Dictionary<string, List<double>> _history = new(StringComparer.Ordinal);
    private DateTime _lastSampleUtc;

    /// <summary>A curve of the last minute behind a tile's number; it draws itself in when the tab opens.</summary>
    private sealed class Spark
    {
        public required Grid Host;
        public required Path Line, Area;
        public required ScaleTransform Reveal;
        public required List<double> Points;
    }

    private Spark NewSpark(string metric)
    {
        if (!_history.TryGetValue(metric, out var points)) _history[metric] = points = new List<double>(SparkPoints + 1);
        var reveal = new ScaleTransform(1, 1);
        var clip = new RectangleGeometry(new Rect(0, 0, 1, 1)) { Transform = reveal };
        var line = new Path { Stroke = _moodInk, StrokeThickness = 1.3, Opacity = 0.7, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        var area = new Path { Fill = MoodArea };
        var host = new Grid { Height = 22, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 8), IsHitTestVisible = false, Clip = clip, Children = { area, line } };
        var spark = new Spark { Host = host, Line = line, Area = area, Reveal = reveal, Points = points };
        host.SizeChanged += (_, e) =>
        {
            clip.Rect = new Rect(0, -2, e.NewSize.Width, e.NewSize.Height + 4);
            DrawSpark(spark);
        };
        return spark;
    }

    private void RecordSample(SystemStats stats)
    {
        Push("cpu", stats.CpuPercent);
        Push("gpu", stats.GpuPercent);
        Push("ram", stats.RamPercent);
        _lastSampleUtc = DateTime.UtcNow;

        void Push(string metric, double? value)
        {
            if (value is not { } v) return;
            if (!_history.TryGetValue(metric, out var points)) _history[metric] = points = new List<double>(SparkPoints + 1);
            points.Add(Math.Clamp(v, 0, 100));
            if (points.Count > SparkPoints) points.RemoveAt(0);
        }
    }

    /// <summary>After a long while hidden the curve would join old and new across the gap: start over instead.</summary>
    private void ForgetStaleHistory()
    {
        if (_lastSampleUtc != default && DateTime.UtcNow - _lastSampleUtc > SparkStaleAfter)
            foreach (var points in _history.Values) points.Clear();
    }

    private static void DrawSpark(Spark spark)
    {
        double w = spark.Host.ActualWidth, h = spark.Host.ActualHeight;
        var points = spark.Points;
        if (w <= 0 || h <= 0 || points.Count < 2)
        {
            spark.Line.Data = spark.Area.Data = null;
            return;
        }
        // Always spread over the full minute's width, so a fresh curve grows in from the right.
        double step = w / (SparkPoints - 1);
        double x0 = w - step * (points.Count - 1);
        Point At(int i) => new(x0 + step * i, h - 2 - points[i] / 100 * (h - 4));

        var line = new PathFigure { StartPoint = At(0), IsFilled = false };
        for (int i = 1; i < points.Count; i++)
        {
            Point a = At(i - 1), b = At(i);
            double cx = (a.X + b.X) / 2;
            line.Segments.Add(new BezierSegment(new Point(cx, a.Y), new Point(cx, b.Y), b, true));
        }
        var area = line.Clone();
        area.IsFilled = true;
        area.Segments.Add(new LineSegment(new Point(w, h), false));
        area.Segments.Add(new LineSegment(new Point(x0, h), false));
        area.IsClosed = true;
        var lineGeometry = new PathGeometry { Figures = { line } };
        var areaGeometry = new PathGeometry { Figures = { area } };
        lineGeometry.Freeze();
        areaGeometry.Freeze();
        spark.Line.Data = lineGeometry;
        spark.Area.Data = areaGeometry;
    }

    /// <summary>The curve sweeps in from the left, like the weather tab's charts.</summary>
    private static void DrawSparkIn(Spark spark, double delayMs)
    {
        if (AnimationConfig.ReduceMotion) return;
        spark.Reveal.BeginAnimation(ScaleTransform.ScaleXProperty, WeatherMotion.To(0, 1, 1100, WeatherMotion.OutQuint, delayMs));
    }

    #endregion

    #region Ping radar, arrows, peaks, flashes

    /// <summary>A dot next to "Ping" that sends out a ring with every measurement, coloured by how fast it was.</summary>
    private sealed class PingDot
    {
        public required Grid Host;
        public required SolidColorBrush Ink;
        public required Ellipse Ring;
        public required ScaleTransform RingScale;
    }

    private static PingDot NewPingDot()
    {
        var ink = new SolidColorBrush(PingGood);
        var ringScale = new ScaleTransform(1, 1);
        var ring = new Ellipse { Width = 6, Height = 6, Stroke = ink, StrokeThickness = 1.4, Opacity = 0, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = ringScale };
        var dot = new Ellipse { Width = 6, Height = 6, Fill = ink };
        var host = new Grid { Width = 6, Height = 6, Margin = new Thickness(6, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false, Children = { ring, dot } };
        return new PingDot { Host = host, Ink = ink, Ring = ring, RingScale = ringScale };
    }

    private void Ping(PingDot dot, long? ms)
    {
        dot.Host.Visibility = ms == null ? Visibility.Hidden : Visibility.Visible;
        if (ms is not { } value) return;
        SetOrTint(dot.Ink, value < 50 ? PingGood : value < 100 ? PingSlow : PingBad, IsVisible ? 400 : 0);
        if (AnimationConfig.ReduceMotion || !IsVisible) return;
        dot.Ring.BeginAnimation(OpacityProperty, WeatherMotion.To(0.85, 0, 1000, WeatherMotion.Out));
        dot.RingScale.BeginAnimation(ScaleTransform.ScaleXProperty, WeatherMotion.To(1, 4.2, 1000, WeatherMotion.Out));
        dot.RingScale.BeginAnimation(ScaleTransform.ScaleYProperty, WeatherMotion.To(1, 4.2, 1000, WeatherMotion.Out));
    }

    /// <summary>An arrow hops in its direction when data moves, so traffic is felt without reading the number.</summary>
    private void Nudge(FrameworkElement arrow, double direction)
    {
        if (AnimationConfig.ReduceMotion || !IsVisible) return;
        var shift = arrow.RenderTransform as TranslateTransform ?? new TranslateTransform();
        arrow.RenderTransform = shift;
        var hop = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(480) };
        hop.KeyFrames.Add(new EasingDoubleKeyFrame(2.5 * direction, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(130)), PressEase));
        hop.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(480)), ReleaseSpring));
        Timeline.SetDesiredFrameRate(hop, AnimationConfig.TargetFps);
        shift.BeginAnimation(TranslateTransform.YProperty, hop);
    }

    /// <summary>A big jump (say 20 to 90 %) lights the number up in the tab's colour for a moment.</summary>
    private void Flash(TextBlock value, SolidColorBrush ink)
    {
        if (!IsVisible) return;
        SetOrTint(ink, Lift(_mood.Color, 0.15), 0);
        WeatherMotion.Tint(ink, ((SolidColorBrush)VNotch.Services.UiPalette.PrimaryBrush).Color, 900);
        Bounce(value, 1.12);
    }

    #endregion

    #region Wi-Fi arcs and battery rings

    private static readonly TimeSpan WifiSearchTime = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan BluetoothConnectTime = TimeSpan.FromSeconds(12);
    // Loops of one section each, stopped whenever that section is rebuilt or the tab hides.
    private readonly LoopSet _wifiLoops = new();
    private readonly LoopSet _bluetoothLoops = new();
    private WifiIcon? _wifiIcon;
    private DateTime _wifiSearchUntil;
    // Devices asked to connect, until when their button keeps breathing.
    private readonly Dictionary<string, DateTime> _bluetoothConnecting = new(StringComparer.OrdinalIgnoreCase);

    private bool IsWifiSearching => DateTime.UtcNow < _wifiSearchUntil;

    /// <summary>Puts a drawn icon where a row's glyph would be.</summary>
    private static void SetRowIcon(Grid row, FrameworkElement icon)
    {
        if (row.Children.Count == 0 || row.Children[0] is not StackPanel name || name.Children.Count == 0) return;
        name.Children.RemoveAt(0);
        name.Children.Insert(0, icon);
    }

    /// <summary>The Wi-Fi icon drawn as a dot and three arcs, lit up to the signal strength.</summary>
    private sealed class WifiIcon
    {
        public required Canvas Host;
        public required Path[] Parts; // dot, small, middle, large arc
    }

    private static readonly Geometry[] WifiGeometry =
    [
        Frozen(new EllipseGeometry(new Point(8, 12.2), 1.3, 1.3)),
        Frozen(Geometry.Parse("M5.2,9.3 A4,4 0 0 1 10.8,9.3")),
        Frozen(Geometry.Parse("M2.9,6.9 A7.3,7.3 0 0 1 13.1,6.9")),
        Frozen(Geometry.Parse("M0.7,4.5 A10.4,10.4 0 0 1 15.3,4.5"))
    ];

    private static Geometry Frozen(Geometry geometry)
    {
        geometry.Freeze();
        return geometry;
    }

    /// <param name="bars">0 to 3 arcs lit; the dot is always lit while connected.</param>
    private WifiIcon NewWifiIcon(int bars, bool connected)
    {
        var lit = connected ? (Brush)_moodInk : Muted;
        var parts = new Path[WifiGeometry.Length];
        var host = new Canvas { Width = 16, Height = 14, Margin = new Thickness(0, 2, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        for (int i = 0; i < parts.Length; i++)
        {
            bool on = i == 0 || connected && i <= bars;
            var part = new Path
            {
                Data = WifiGeometry[i],
                Stroke = on ? lit : Track,
                StrokeThickness = i == 0 ? 0 : 1.6,
                Fill = i == 0 ? lit : null,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                RenderTransformOrigin = new Point(0.5, 0.95)
            };
            host.Children.Add(part);
            parts[i] = part;
        }
        return new WifiIcon { Host = host, Parts = parts };
    }

    /// <summary>The arcs pop in one after another, from the dot outwards.</summary>
    private static void ArcsArrive(WifiIcon icon, double delayMs)
    {
        if (AnimationConfig.ReduceMotion) return;
        for (int i = 0; i < icon.Parts.Length; i++)
        {
            var part = icon.Parts[i];
            double delay = delayMs + i * 90;
            var scale = new ScaleTransform(0.4, 0.4);
            part.RenderTransform = scale;
            part.Opacity = 0;
            var fade = WeatherMotion.To(0, 1, 280, WeatherMotion.Out, delay);
            // Hand the arc back at full opacity, so a later search wave that stops leaves it lit.
            fade.Completed += (_, _) =>
            {
                part.BeginAnimation(OpacityProperty, null);
                part.Opacity = 1;
            };
            part.BeginAnimation(OpacityProperty, fade);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, WeatherMotion.To(0.4, 1, 480, WeatherMotion.OutBack, delay));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, WeatherMotion.To(0.4, 1, 480, WeatherMotion.OutBack, delay));
        }
    }

    /// <summary>While Windows connects, a wave runs through the arcs.</summary>
    private void ArcsSearch(WifiIcon icon)
    {
        for (int i = 1; i < icon.Parts.Length; i++)
        {
            icon.Parts[i].Stroke = _moodInk;
            _wifiLoops.Run(icon.Parts[i], OpacityProperty, new DoubleAnimation(1, 0.2, TimeSpan.FromMilliseconds(520))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = WeatherMotion.Sine,
                BeginTime = TimeSpan.FromMilliseconds(i * 170)
            });
        }
    }

    private static int WifiBars(int signal) => signal >= 75 ? 3 : signal >= 50 ? 2 : signal >= 25 ? 1 : 0;

    /// <summary>A small ring that fills to a Bluetooth device's battery level.</summary>
    private FrameworkElement BatteryRing(int percent, double delayMs)
    {
        const double Size = 18, Stroke = 2;
        double r = (Size - Stroke) / 2, c = Size / 2;
        var value = new Path { Stroke = _mood, StrokeThickness = Stroke, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        var ring = new Grid
        {
            Width = Size,
            Height = Size,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = $"{percent} %",
            Children = { new Ellipse { Stroke = Track, StrokeThickness = Stroke }, value }
        };
        var tween = new WeatherTween { Step = t => value.Data = Arc(c, c, r, -90, Math.Min(359.5, 3.6 * t)) };
        bool animate = IsVisible && !AnimationConfig.ReduceMotion;
        tween.Play(animate ? 0 : percent, percent, animate ? 1200 : 0, WeatherMotion.OutQuint, delayMs);
        return ring;
    }

    #endregion
}
