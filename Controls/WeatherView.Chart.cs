using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using VNotch.Models;
using VNotch.Services;

namespace VNotch.Controls;

/// <summary>
/// The temperature curve: the next 24 hours, or a day picked below. It draws itself in from the left with a
/// glowing head, labels pop in as it passes, rain bars rise, and a scrubber follows the pointer.
/// </summary>
public sealed partial class WeatherView
{
    private const double ChartHeight = 146;
    private const double ChartPad = 14;
    private const double CurveTop = 24;
    private const double CurveBottom = 74;
    private const double BarsBottom = 98;
    private const double BarsHeight = 16;
    private const double IconsTop = 102;
    private const double TimesTop = 122;
    private const double RevealMs = 1050;

    private Grid? _chartHeader;
    private Grid? _chartHost;

    private sealed record ChartPoint(DateTime Time, double Temperature, int Chance, double Precipitation, int Code, bool IsDay, double X, double Y);

    /// <summary>Draws the chart for the selected day (or the next 24 hours); a negative delay draws it without the entrance.</summary>
    private void ShowChart(WeatherForecast forecast, double delayMs)
    {
        if (_chartHost == null || _chartHeader == null) return;
        var (from, to, points) = ChartWindow(forecast, _selectedDay);

        _chartHeader.Children.Clear();
        _chartHeader.ColumnDefinitions.Clear();
        _chartHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _chartHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        string title = _selectedDay is { } day
            ? Capitalize(day.ToString("dddd", Loc.GetCulture()))
            : Loc.Get("weather.next24");
        var titleText = Text(title, 11.5, Brushes.White, FontWeights.SemiBold);
        _chartHeader.Children.Add(titleText);
        FrameworkElement side;
        if (_selectedDay != null)
        {
            // Back to the next 24 hours.
            var back = new Border
            {
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(RowHover),
                Padding = new Thickness(7, 1, 8, 1),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center
            };
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            line.Children.Add(new TextBlock { Text = "", FontFamily = SymbolFont, FontSize = 9, Foreground = Soft, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 5, 0) });
            line.Children.Add(Text("24 h", 10.5, Soft));
            back.Child = line;
            back.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                SelectDay(null);
            };
            HoverTint(back);
            PressSquish(back);
            side = back;
            WeatherMotion.Pop(back, 0, 0.6, 320);
        }
        else
        {
            side = Text(forecast.Source, 10, Muted);
        }
        Grid.SetColumn(side, 1);
        _chartHeader.Children.Add(side);
        if (delayMs >= 0) WeatherMotion.Rise(titleText, delayMs * 0.3, dy: 0, dx: 10, ms: 380);

        _chartHost.Children.Clear();
        if (points.Count < 2) return;
        _chartHost.Children.Add(BuildChart(points, from, to, isNext24: _selectedDay == null, delayMs));
    }

    private static (DateTime From, DateTime To, List<WeatherHour> Points) ChartWindow(WeatherForecast forecast, DateTime? day)
    {
        DateTime from, to;
        if (day is { } d)
        {
            from = d.Date;
            to = from.AddDays(1);
            var inDay = forecast.Hours.Where(h => h.Time >= from && h.Time <= to).ToList();
            // A sparse day (six-hour steps) borrows the steps around it so the curve spans the whole width.
            var before = forecast.Hours.LastOrDefault(h => h.Time < from);
            var after = forecast.Hours.FirstOrDefault(h => h.Time > to);
            if (before != null && (inDay.Count == 0 || inDay[0].Time - from > TimeSpan.FromHours(2))) inDay.Insert(0, before);
            if (after != null && (inDay.Count == 0 || to - inDay[^1].Time > TimeSpan.FromHours(2))) inDay.Add(after);
            return (from, to, inDay);
        }
        var now = DateTime.Now;
        from = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Local);
        to = from.AddHours(24);
        var next = forecast.Hours.Where(h => h.Time >= from && h.Time <= to).ToList();
        if (next.Count > 0 && next[0].Time == from)
        {
            // The first point is "now": the measured temperature, not the hourly one.
            var first = next[0];
            next[0] = first with { Temperature = forecast.Now.Temperature, Code = forecast.Now.Code, IsDay = forecast.Now.IsDay };
        }
        return (from, to, next);
    }

    private FrameworkElement BuildChart(List<WeatherHour> hours, DateTime from, DateTime to, bool isNext24, double delayMs)
    {
        bool animate = delayMs >= 0 && !AnimationConfig.ReduceMotion;
        double width = RightWidth;
        double span = (to - from).TotalHours;
        double low = hours.Min(h => h.Temperature), high = hours.Max(h => h.Temperature);
        if (high - low < 4)
        {
            double mid = (high + low) / 2;
            low = mid - 2;
            high = mid + 2;
        }
        double XOf(DateTime t) => ChartPad + Math.Clamp((t - from).TotalHours / span, -0.04, 1.04) * (width - 2 * ChartPad);
        double YOf(double temp) => CurveBottom - (temp - low) / (high - low) * (CurveBottom - CurveTop);
        var points = hours.Select(h => new ChartPoint(h.Time, h.Temperature, h.PrecipitationChance, h.Precipitation, h.Code, h.IsDay, XOf(h.Time), YOf(h.Temperature))).ToList();

        var canvas = new Canvas { Width = width, Height = ChartHeight, ClipToBounds = false, Background = Brushes.Transparent };

        // Freezing line, when the curve crosses it.
        if (low < 0 && high > 0)
        {
            double y0 = YOf(0);
            canvas.Children.Add(new Line
            {
                X1 = ChartPad, X2 = width - ChartPad, Y1 = y0, Y2 = y0,
                Stroke = WeatherIcons.Brush(Color.FromArgb(0x50, 0xA5, 0xB4, 0xFF)),
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 2, 4 }
            });
            var zero = Text("0°", 9, WeatherIcons.Brush(Color.FromArgb(0xA0, 0xA5, 0xB4, 0xFF)));
            Canvas.SetLeft(zero, width - ChartPad + 2);
            Canvas.SetTop(zero, y0 - 7);
            canvas.Children.Add(zero);
        }

        // Rain bars along the bottom.
        double step = points.Count > 1 ? (points[^1].X - points[0].X) / (points.Count - 1) : 16;
        double barWidth = Math.Clamp(step * 0.42, 3, 9);
        var barBrush = WeatherIcons.Brush(Color.FromArgb(0xC0, 0x5A, 0xB0, 0xFF));
        foreach (var p in points)
        {
            double value = p.Chance >= 0 ? p.Chance / 100.0 : Math.Min(1, Math.Sqrt(p.Precipitation / 3.0));
            if (value < 0.08 && p.Precipitation < 0.05) continue;
            double h = Math.Max(2, value * BarsHeight);
            var bar = new Rectangle { Width = barWidth, Height = h, RadiusX = Math.Min(2, barWidth / 2), RadiusY = Math.Min(2, barWidth / 2), Fill = barBrush };
            Canvas.SetLeft(bar, p.X - barWidth / 2);
            Canvas.SetTop(bar, BarsBottom - h);
            canvas.Children.Add(bar);
            if (animate) WeatherMotion.Grow(bar, delayMs + RevealMs * 0.85 * (p.X / width) + 80, horizontal: false, new Point(0.5, 1), 480);
        }

        // The curve with its fading fill, revealed left to right.
        var geometry = MonotoneCurve(points.Select(p => new Point(p.X, p.Y)).ToList());
        var stroke = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(0, 0), EndPoint = new Point(width, 0) };
        var fillBrush = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(0, 0), EndPoint = new Point(width, 0) };
        foreach (var p in points)
        {
            var color = WeatherIcons.TemperatureColor(p.Temperature);
            double offset = Math.Clamp(p.X / width, 0, 1);
            stroke.GradientStops.Add(new GradientStop(color, offset));
            fillBrush.GradientStops.Add(new GradientStop(WeatherIcons.WithAlpha(color, 0x48), offset));
        }
        stroke.Freeze();
        fillBrush.Freeze();
        var area = geometry.Clone();
        if (area.Figures.Count > 0)
        {
            var figure = area.Figures[0];
            figure.Segments.Add(new LineSegment(new Point(points[^1].X, BarsBottom), false));
            figure.Segments.Add(new LineSegment(new Point(points[0].X, BarsBottom), false));
            figure.IsClosed = true;
            figure.IsFilled = true;
        }
        var fade = new LinearGradientBrush(Colors.Black, Color.FromArgb(0, 0, 0, 0), 90);
        fade.Freeze();
        var layer = new Canvas { Width = width, Height = ChartHeight };
        layer.Children.Add(new Path { Data = area, Fill = fillBrush, OpacityMask = fade });
        layer.Children.Add(new Path
        {
            Data = geometry,
            Stroke = stroke,
            StrokeThickness = 2.6,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round
        });
        canvas.Children.Add(layer);
        var samples = SampleCurve(geometry);
        if (animate)
        {
            var clip = new RectangleGeometry(new Rect(0, -20, 0, ChartHeight + 40));
            layer.Clip = clip;
            var reveal = new RectAnimation(new Rect(0, -20, 0, ChartHeight + 40), new Rect(0, -20, width + 10, ChartHeight + 40), TimeSpan.FromMilliseconds(RevealMs))
            {
                BeginTime = TimeSpan.FromMilliseconds(delayMs),
                EasingFunction = WeatherMotion.Out
            };
            Timeline.SetDesiredFrameRate(reveal, AnimationConfig.TargetFps);
            reveal.Completed += (_, _) => layer.Clip = null;
            clip.BeginAnimation(RectangleGeometry.RectProperty, reveal);
            AddRevealHead(canvas, samples, delayMs, width);
        }

        // Labels every three hours: temperature above the curve, icon and time below.
        var labels = new Canvas { IsHitTestVisible = false };
        canvas.Children.Add(labels);
        var labelled = LabelIndexes(points, from);
        string hourFormat = CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern.Contains('H') ? "HH" : "h tt";
        foreach (int i in labelled)
        {
            var p = points[i];
            double labelDelay = delayMs + RevealMs * 0.85 * (p.X / width);
            var temp = Text(Degrees(p.Temperature), 11, Brushes.White, FontWeights.SemiBold);
            var timeText = Text(i == 0 && isNext24 ? Loc.Get("weather.now") : p.Time.ToString(hourFormat, CultureInfo.CurrentCulture), 10, i == 0 && isNext24 ? Soft : Muted,
                i == 0 && isNext24 ? FontWeights.SemiBold : FontWeights.Normal);
            var icon = WeatherIcons.Create(p.Code, p.IsDay, 15);
            Center(labels, temp, p.X, p.Y - 19);
            Center(labels, icon, p.X, IconsTop, 15);
            Center(labels, timeText, p.X, TimesTop);
            if (animate)
            {
                WeatherMotion.Rise(temp, labelDelay + 40, dy: 6, ms: 380);
                WeatherMotion.Pop(icon, labelDelay + 70, 0.4, 420);
                WeatherMotion.Rise(timeText, labelDelay + 90, dy: -4, ms: 380);
            }
        }
        foreach (int i in labelled)
        {
            var dot = new Ellipse { Width = 5, Height = 5, Fill = Brushes.White, Stroke = Brushes.Black, StrokeThickness = 1.2 };
            Canvas.SetLeft(dot, points[i].X - 2.5);
            Canvas.SetTop(dot, points[i].Y - 2.5);
            labels.Children.Add(dot);
            if (animate) WeatherMotion.Pop(dot, delayMs + RevealMs * 0.85 * (points[i].X / width) + 20, 0.2, 360);
        }

        if (isNext24) AddNowPulse(canvas, points[0], animate ? delayMs : 0);
        AddScrubber(canvas, points, labels, width);

        if (animate && delayMs > 0)
        {
            canvas.Opacity = 0;
            canvas.BeginAnimation(OpacityProperty, WeatherMotion.To(0, 1, 260, WeatherMotion.Out, delayMs * 0.4));
        }
        return canvas;
    }

    private static List<int> LabelIndexes(List<ChartPoint> points, DateTime from)
    {
        var result = new List<int>();
        for (int i = 0; i < points.Count; i++)
        {
            var offset = points[i].Time - from;
            if (offset < TimeSpan.Zero || offset > TimeSpan.FromHours(23.5)) continue;
            double hours = offset.TotalHours;
            if (Math.Abs(hours - Math.Round(hours)) < 0.01 && (int)Math.Round(hours) % 3 == 0) result.Add(i);
        }
        if (result.Count >= 3) return result;
        // Six-hour steps: label every step inside the day.
        result.Clear();
        for (int i = 0; i < points.Count; i++)
            if (points[i].Time >= from && points[i].Time - from <= TimeSpan.FromHours(23.5)) result.Add(i);
        return result;
    }

    private static void Center(Canvas canvas, FrameworkElement element, double x, double top, double? knownWidth = null)
    {
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double w = knownWidth ?? element.DesiredSize.Width;
        Canvas.SetLeft(element, x - w / 2);
        Canvas.SetTop(element, top);
        canvas.Children.Add(element);
    }

    /// <summary>
    /// A smooth curve through the points that never overshoots them (Fritsch–Carlson monotone cubic), so a flat
    /// night stays flat and the highest point is the high.
    /// </summary>
    internal static PathGeometry MonotoneCurve(IReadOnlyList<Point> points)
    {
        var geometry = new PathGeometry();
        if (points.Count == 0) return geometry;
        var figure = new PathFigure { StartPoint = points[0], IsFilled = false };
        geometry.Figures.Add(figure);
        int n = points.Count;
        if (n == 1) return geometry;
        var slopes = new double[n - 1];
        for (int i = 0; i < n - 1; i++)
        {
            double dx = points[i + 1].X - points[i].X;
            slopes[i] = dx == 0 ? 0 : (points[i + 1].Y - points[i].Y) / dx;
        }
        var tangents = new double[n];
        tangents[0] = slopes[0];
        tangents[n - 1] = slopes[n - 2];
        for (int i = 1; i < n - 1; i++)
            tangents[i] = slopes[i - 1] * slopes[i] <= 0 ? 0 : (slopes[i - 1] + slopes[i]) / 2;
        for (int i = 0; i < n - 1; i++)
        {
            if (slopes[i] == 0)
            {
                tangents[i] = tangents[i + 1] = 0;
                continue;
            }
            double a = tangents[i] / slopes[i], b = tangents[i + 1] / slopes[i];
            double h = a * a + b * b;
            if (h > 9)
            {
                double t = 3 / Math.Sqrt(h);
                tangents[i] = t * a * slopes[i];
                tangents[i + 1] = t * b * slopes[i];
            }
        }
        for (int i = 0; i < n - 1; i++)
        {
            var p0 = points[i];
            var p1 = points[i + 1];
            double dx = (p1.X - p0.X) / 3;
            figure.Segments.Add(new BezierSegment(
                new Point(p0.X + dx, p0.Y + tangents[i] * dx),
                new Point(p1.X - dx, p1.Y - tangents[i + 1] * dx),
                p1, true));
        }
        return geometry;
    }

    /// <summary>The curve as a dense list of points, to find its height at any x.</summary>
    private static List<Point> SampleCurve(PathGeometry geometry)
    {
        var result = new List<Point>();
        var flat = geometry.GetFlattenedPathGeometry(0.25, ToleranceType.Absolute);
        foreach (var figure in flat.Figures)
        {
            result.Add(figure.StartPoint);
            foreach (var segment in figure.Segments)
            {
                if (segment is PolyLineSegment poly) result.AddRange(poly.Points);
                else if (segment is LineSegment line) result.Add(line.Point);
            }
        }
        return result;
    }

    private static double CurveYAt(List<Point> samples, double x)
    {
        if (samples.Count == 0) return CurveBottom;
        if (x <= samples[0].X) return samples[0].Y;
        for (int i = 1; i < samples.Count; i++)
        {
            if (samples[i].X < x) continue;
            var a = samples[i - 1];
            var b = samples[i];
            double f = b.X == a.X ? 0 : (x - a.X) / (b.X - a.X);
            return a.Y + (b.Y - a.Y) * f;
        }
        return samples[^1].Y;
    }

    /// <summary>A bright point riding the tip of the curve while it draws itself.</summary>
    private static void AddRevealHead(Canvas canvas, List<Point> samples, double delayMs, double width)
    {
        if (samples.Count < 2) return;
        var glowBrush = new RadialGradientBrush();
        glowBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF), 0));
        glowBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1));
        glowBrush.Freeze();
        var head = new Canvas { IsHitTestVisible = false, Opacity = 0 };
        var glow = new Ellipse { Width = 18, Height = 18, Fill = glowBrush };
        Canvas.SetLeft(glow, -9);
        Canvas.SetTop(glow, -9);
        var core = new Ellipse { Width = 6, Height = 6, Fill = Brushes.White };
        Canvas.SetLeft(core, -3);
        Canvas.SetTop(core, -3);
        head.Children.Add(glow);
        head.Children.Add(core);
        var move = new TranslateTransform(samples[0].X, samples[0].Y);
        head.RenderTransform = move;
        canvas.Children.Add(head);

        double startX = samples[0].X, endX = samples[^1].X;
        var tween = new WeatherTween
        {
            Step = p =>
            {
                double x = startX + (width + 10) * p;
                x = Math.Min(x, endX);
                move.X = x;
                move.Y = CurveYAt(samples, x);
            }
        };
        tween.Play(0, 1, RevealMs, WeatherMotion.Out, delayMs);
        var frames = new DoubleAnimationUsingKeyFrames { BeginTime = TimeSpan.FromMilliseconds(delayMs), Duration = TimeSpan.FromMilliseconds(RevealMs + 260) };
        frames.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        frames.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.08)));
        frames.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.72)));
        frames.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
        Timeline.SetDesiredFrameRate(frames, AnimationConfig.TargetFps);
        frames.Completed += (_, _) => canvas.Children.Remove(head);
        head.BeginAnimation(OpacityProperty, frames);
    }

    /// <summary>"Now" on the curve: a dot with a ring that keeps opening.</summary>
    private void AddNowPulse(Canvas canvas, ChartPoint now, double delayMs)
    {
        var color = WeatherIcons.TemperatureColor(now.Temperature);
        var ring = new Ellipse { Width = 8, Height = 8, Stroke = WeatherIcons.Brush(color), StrokeThickness = 1.4, Opacity = 0, IsHitTestVisible = false };
        Canvas.SetLeft(ring, now.X - 4);
        Canvas.SetTop(ring, now.Y - 4);
        var open = new ScaleTransform(1, 1, 4, 4);
        ring.RenderTransform = open;
        canvas.Children.Add(ring);
        var dot = new Ellipse { Width = 8, Height = 8, Fill = Brushes.White, Stroke = WeatherIcons.Brush(color), StrokeThickness = 2, IsHitTestVisible = false };
        Canvas.SetLeft(dot, now.X - 4);
        Canvas.SetTop(dot, now.Y - 4);
        canvas.Children.Add(dot);
        WeatherMotion.Pop(dot, delayMs + 60, 0.2, 480);
        double start = (delayMs + 400) / 1000.0;
        _loops.Run(open, ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 3, TimeSpan.FromSeconds(1.8)) { RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromSeconds(start), EasingFunction = WeatherMotion.Out });
        _loops.Run(open, ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 3, TimeSpan.FromSeconds(1.8)) { RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromSeconds(start), EasingFunction = WeatherMotion.Out });
        _loops.Run(ring, OpacityProperty, new DoubleAnimation(0.9, 0, TimeSpan.FromSeconds(1.8)) { RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromSeconds(start), EasingFunction = WeatherMotion.Out });
    }

    /// <summary>Under the pointer: a hairline and a dot glide to the nearest step, with its time, temperature and rain.</summary>
    private static void AddScrubber(Canvas canvas, List<ChartPoint> points, UIElement labels, double width)
    {
        var lineBrush = new LinearGradientBrush(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF), 90);
        lineBrush.Freeze();
        var hairline = new Rectangle { Width = 1, Height = BarsBottom - 14, Fill = lineBrush, IsHitTestVisible = false };
        Canvas.SetTop(hairline, 14);
        var lineMove = new TranslateTransform();
        hairline.RenderTransform = lineMove;

        var dot = new Ellipse { Width = 10, Height = 10, Fill = Brushes.White, Stroke = Brushes.Black, StrokeThickness = 2, IsHitTestVisible = false };
        var dotMove = new TranslateTransform();
        dot.RenderTransform = dotMove;

        var bubbleText = Text("", 10.5, Brushes.White, FontWeights.SemiBold);
        var bubble = new Border
        {
            Background = WeatherIcons.Brush(Color.FromArgb(0xF0, 0x26, 0x26, 0x28)),
            BorderBrush = WeatherIcons.Brush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(7, 2, 7, 3),
            Child = bubbleText,
            IsHitTestVisible = false
        };
        Canvas.SetTop(bubble, -6);
        var bubbleMove = new TranslateTransform();
        bubble.RenderTransform = bubbleMove;

        var overlay = new Canvas { IsHitTestVisible = false, Opacity = 0 };
        overlay.Children.Add(hairline);
        overlay.Children.Add(dot);
        overlay.Children.Add(bubble);

        var hit = new Rectangle { Width = width, Height = TimesTop, Fill = Brushes.Transparent, Cursor = Cursors.Cross };
        canvas.Children.Add(hit);
        canvas.Children.Add(overlay);

        int current = -1;
        bool placed = false;
        hit.MouseMove += (_, e) =>
        {
            double x = e.GetPosition(canvas).X;
            int index = 0;
            double best = double.MaxValue;
            for (int i = 0; i < points.Count; i++)
            {
                double d = Math.Abs(points[i].X - x);
                if (d < best)
                {
                    best = d;
                    index = i;
                }
            }
            if (index == current) return;
            current = index;
            var p = points[index];
            string rain = p.Chance >= 0
                ? (p.Chance >= 5 ? $"  ·  {p.Chance} %" : "")
                : (p.Precipitation >= 0.05 ? $"  ·  {p.Precipitation.ToString("0.#", CultureInfo.CurrentCulture)} mm" : "");
            bubbleText.Text = $"{p.Time.ToString("t", CultureInfo.CurrentCulture)}  ·  {Degrees(p.Temperature)}{rain}";
            bubble.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double bx = Math.Clamp(p.X - bubble.DesiredSize.Width / 2, 0, width - bubble.DesiredSize.Width);
            double ms = placed ? 170 : 0;
            placed = true;
            Glide(lineMove, TranslateTransform.XProperty, p.X - 0.5, ms);
            Glide(dotMove, TranslateTransform.XProperty, p.X - 5, ms);
            Glide(dotMove, TranslateTransform.YProperty, p.Y - 5, ms);
            Glide(bubbleMove, TranslateTransform.XProperty, bx, ms);
        };
        hit.MouseEnter += (_, _) =>
        {
            overlay.BeginAnimation(OpacityProperty, WeatherMotion.To(overlay.Opacity, 1, 140, WeatherMotion.Out));
            labels.BeginAnimation(OpacityProperty, WeatherMotion.To(labels.Opacity, 0.35, 180, WeatherMotion.Out));
        };
        hit.MouseLeave += (_, _) =>
        {
            current = -1;
            placed = false;
            overlay.BeginAnimation(OpacityProperty, WeatherMotion.To(overlay.Opacity, 0, 200, WeatherMotion.Out));
            labels.BeginAnimation(OpacityProperty, WeatherMotion.To(labels.Opacity, 1, 240, WeatherMotion.Out));
        };
    }

    private static void Glide(TranslateTransform target, DependencyProperty property, double to, double ms)
    {
        if (ms <= 0)
        {
            target.BeginAnimation(property, null);
            target.SetValue(property, to);
            return;
        }
        WeatherMotion.Glide(target, property, to, ms);
    }

    private static string Capitalize(string text) =>
        string.IsNullOrEmpty(text) ? text : char.ToUpper(text[0], CultureInfo.CurrentCulture) + text[1..];
}
