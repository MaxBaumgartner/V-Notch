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

/// <summary>The seven days with their range bars, and the wind, humidity, UV and sun tiles.</summary>
public sealed partial class WeatherView
{
    private const double DayRowHeight = 20;
    private const double TileWidth = 124;
    private const double TileHeight = 64;

    private readonly List<(DateTime Date, Border Row)> _dayRows = new();

    #region Days

    private FrameworkElement BuildDays(WeatherForecast forecast, bool entrance)
    {
        _dayRows.Clear();
        var stack = new StackPanel();
        var days = forecast.Days.Where(d => d.Date >= DateTime.Today).Take(7).ToList();
        if (days.Count == 0) return stack;
        double weekLow = days.Min(d => d.Low), weekHigh = days.Max(d => d.High);
        if (weekHigh - weekLow < 1) weekHigh = weekLow + 1;
        const double nameWidth = 46, iconWidth = 24, chanceWidth = 40, tempWidth = 32, barMargin = 9;
        double track = RightWidth - nameWidth - iconWidth - chanceWidth - 2 * tempWidth - 2 * barMargin - 8;
        var culture = Loc.GetCulture();

        for (int i = 0; i < days.Count; i++)
        {
            var day = days[i];
            bool today = day.Date == DateTime.Today;
            var row = new Border
            {
                Height = DayRowHeight,
                CornerRadius = new CornerRadius(7),
                Background = new SolidColorBrush(day.Date == _selectedDay ? RowSelected : RowIdle),
                Padding = new Thickness(4, 0, 4, 0),
                Cursor = Cursors.Hand
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(nameWidth) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(iconWidth) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(chanceWidth) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(tempWidth) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(tempWidth) });
            row.Child = grid;

            string name = today ? Loc.Get("weather.today") : Capitalize(culture.DateTimeFormat.GetAbbreviatedDayName(day.Date.DayOfWeek).TrimEnd('.'));
            grid.Children.Add(Text(name, 11.5, Brushes.White, today ? FontWeights.SemiBold : FontWeights.Medium, trim: true));

            var icon = WeatherIcons.Create(day.Code, isDay: true, 15);
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            Grid.SetColumn(icon, 1);
            grid.Children.Add(icon);

            string chance = day.PrecipitationChance >= 20
                ? $"{day.PrecipitationChance} %"
                : day.PrecipitationChance < 0 && day.PrecipitationSum >= 0.3
                    ? $"{day.PrecipitationSum.ToString(day.PrecipitationSum < 10 ? "0.#" : "0", CultureInfo.CurrentCulture)} mm"
                    : "";
            var chanceText = Text(chance, 10, RainBrush, FontWeights.SemiBold);
            chanceText.HorizontalAlignment = HorizontalAlignment.Center;
            Grid.SetColumn(chanceText, 2);
            grid.Children.Add(chanceText);

            var lowText = Text(Degrees(day.Low), 11, Muted);
            lowText.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(lowText, 3);
            grid.Children.Add(lowText);

            var bar = new Canvas { Height = 6, Margin = new Thickness(barMargin, 0, barMargin, 0), VerticalAlignment = VerticalAlignment.Center };
            var trackShape = new Rectangle { Width = track, Height = 5, RadiusX = 2.5, RadiusY = 2.5, Fill = TrackFill, Opacity = 0.6 };
            Canvas.SetTop(trackShape, 0.5);
            bar.Children.Add(trackShape);
            double left = (day.Low - weekLow) / (weekHigh - weekLow) * track;
            double width = Math.Max(6, (day.High - day.Low) / (weekHigh - weekLow) * track);
            left = Math.Min(left, track - width);
            var fill = new LinearGradientBrush(WeatherIcons.TemperatureColor(day.Low), WeatherIcons.TemperatureColor(day.High), 0);
            fill.Freeze();
            var segment = new Rectangle { Width = width, Height = 5, RadiusX = 2.5, RadiusY = 2.5, Fill = fill };
            Canvas.SetLeft(segment, left);
            Canvas.SetTop(segment, 0.5);
            bar.Children.Add(segment);
            Grid.SetColumn(bar, 4);
            grid.Children.Add(bar);

            var highText = Text(Degrees(day.High), 11, Brushes.White, FontWeights.SemiBold);
            highText.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(highText, 5);
            grid.Children.Add(highText);

            double rowDelay = 200 + i * 55;
            if (entrance)
            {
                WeatherMotion.Rise(row, rowDelay, dy: 0, dx: 16, ms: 480);
                WeatherMotion.Grow(segment, rowDelay + 120, horizontal: true, new Point(0, 0.5), 720);
            }

            if (today)
            {
                // Where today stands right now on its own bar.
                double nowX = Math.Clamp((forecast.Now.Temperature - weekLow) / (weekHigh - weekLow) * track, left, left + width);
                var mark = new Ellipse { Width = 7, Height = 7, Fill = Brushes.White, Stroke = Brushes.Black, StrokeThickness = 1.6 };
                Canvas.SetLeft(mark, nowX - 3.5);
                Canvas.SetTop(mark, -0.5);
                // The breathing scale sits on a holder so the pop on the mark stays its own.
                var breathe = new ScaleTransform(1, 1, nowX, 3);
                var holder = new Canvas { RenderTransform = breathe };
                holder.Children.Add(mark);
                bar.Children.Add(holder);
                if (entrance) WeatherMotion.Pop(mark, rowDelay + 620, 0.2, 420);
                _loops.Swing(breathe, ScaleTransform.ScaleXProperty, 1, 1.28, 1.4, entrance ? 1.2 : 0);
                _loops.Swing(breathe, ScaleTransform.ScaleYProperty, 1, 1.28, 1.4, entrance ? 1.2 : 0);
            }

            var date = day.Date;
            HoverTint(row, () => date == _selectedDay);
            PressSquish(row);
            row.MouseEnter += (_, _) => Bump(icon, 1.22);
            row.MouseLeave += (_, _) => Bump(icon, 1);
            row.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                // Today is what the next 24 hours already show; another tap on the picked day goes back too.
                SelectDay(today || date == _selectedDay ? null : date);
            };
            _dayRows.Add((date, row));
            stack.Children.Add(row);
        }
        return stack;
    }

    /// <summary>Shows a day's curve (null: the next 24 hours); the old curve slides away first.</summary>
    private void SelectDay(DateTime? day)
    {
        if (_forecast == null || _chartHost == null || day == _selectedDay) return;
        _selectedDay = day;
        foreach (var (date, row) in _dayRows)
            if (row.Background is SolidColorBrush brush)
                WeatherMotion.Tint(brush, date == day ? RowSelected : row.IsMouseOver ? RowHover : RowIdle, 220);

        var old = _chartHost.Children.Count > 0 ? _chartHost.Children[0] as FrameworkElement : null;
        if (old == null || AnimationConfig.ReduceMotion)
        {
            ShowChart(_forecast, 0);
            return;
        }
        // The chart's own pulse loops stay in _loops; the old one is about to be removed anyway.
        old.IsHitTestVisible = false;
        var slide = new TranslateTransform();
        old.RenderTransform = slide;
        double direction = day == null ? 1 : -1;
        var fade = WeatherMotion.To(old.Opacity, 0, 200, WeatherMotion.Out);
        old.BeginAnimation(OpacityProperty, fade);
        slide.BeginAnimation(TranslateTransform.XProperty, WeatherMotion.To(0, 24 * direction, 220, WeatherMotion.Out));
        var forecast = _forecast;
        fade.Completed += (_, _) =>
        {
            if (_forecast != forecast || _chartHost == null) return;
            ShowChart(forecast, 0);
            if (_chartHost.Children.Count > 0 && _chartHost.Children[0] is FrameworkElement fresh)
                WeatherMotion.Rise(fresh, 0, dy: 0, dx: -18 * direction, ms: 420);
        };
    }

    #endregion

    #region Tiles

    private static readonly Color TileIdle = Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF);
    private static readonly Color TileHover = Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF);

    /// <summary>Cards rise a little toward the pointer and light up.</summary>
    private static void HoverLift(Border tile)
    {
        tile.MouseEnter += (_, _) =>
        {
            Bump(tile, 1.035);
            if (tile.Background is SolidColorBrush brush) WeatherMotion.Tint(brush, TileHover);
        };
        tile.MouseLeave += (_, _) =>
        {
            Bump(tile, 1);
            if (tile.Background is SolidColorBrush brush) WeatherMotion.Tint(brush, TileIdle, 260);
        };
    }

    /// <summary>Springs an element to a scale from wherever its entrance left it.</summary>
    private static readonly IEasingFunction PressRelease = CreatePressRelease();

    private static IEasingFunction CreatePressRelease()
    {
        var ease = new ElasticEase { Oscillations = 1, Springiness = 7, EasingMode = EasingMode.EaseOut };
        ease.Freeze();
        return ease;
    }

    /// <summary>Like the other tabs' cards: sinks a little while pressed (wider than tall) and springs back.</summary>
    private static void PressSquish(FrameworkElement element, double restScale = 1)
    {
        element.PreviewMouseLeftButtonDown += (_, _) =>
        {
            if (AnimationConfig.ReduceMotion) return;
            Bump(element, restScale); // makes sure there is a live scale to squeeze
            var scale = (ScaleTransform)element.RenderTransform;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, WeatherMotion.To(scale.ScaleX, restScale * 0.97, 100, WeatherMotion.Out));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, WeatherMotion.To(scale.ScaleY, restScale * 0.9, 100, WeatherMotion.Out));
        };
        element.PreviewMouseLeftButtonUp += (_, _) =>
        {
            if (AnimationConfig.ReduceMotion || element.RenderTransform is not ScaleTransform scale) return;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, WeatherMotion.To(scale.ScaleX, restScale, 520, PressRelease));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, WeatherMotion.To(scale.ScaleY, restScale, 520, PressRelease));
        };
    }

    private static void Bump(FrameworkElement element, double scale)
    {
        if (element.RenderTransform is not ScaleTransform transform || transform.IsFrozen)
        {
            transform = new ScaleTransform(1, 1);
            element.RenderTransformOrigin = new Point(0.5, 0.5);
            element.RenderTransform = transform;
        }
        WeatherMotion.Glide(transform, ScaleTransform.ScaleXProperty, scale, 220);
        WeatherMotion.Glide(transform, ScaleTransform.ScaleYProperty, scale, 220);
    }

    private FrameworkElement BuildTiles(WeatherForecast forecast, bool entrance)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(TileWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(TileWidth) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(TileHeight) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(TileHeight) });

        var tiles = new[]
        {
            WindTile(forecast, entrance),
            HumidityTile(forecast, entrance),
            UvTile(forecast, entrance),
            SunTile(forecast, entrance)
        };
        for (int i = 0; i < tiles.Length; i++)
        {
            Grid.SetColumn(tiles[i], i % 2 == 0 ? 0 : 2);
            Grid.SetRow(tiles[i], i < 2 ? 0 : 2);
            grid.Children.Add(tiles[i]);
            if (entrance) WeatherMotion.Pop(tiles[i], 220 + i * 70, 0.86, 520);
            HoverLift(tiles[i]);
        }
        return grid;
    }

    private static (Border Tile, Grid Body, StackPanel Text) Tile(string label, string value, string? detail, double reserveRight = 0)
    {
        var tile = new Border { CornerRadius = new CornerRadius(12), Background = new SolidColorBrush(TileIdle), Padding = new Thickness(10, 7, 8, 7) };
        var body = new Grid();
        tile.Child = body;
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, reserveRight, 0) };
        text.Children.Add(Text(label, 10, Muted, trim: true));
        text.Children.Add(Text(value, 15, Brushes.White, FontWeights.SemiBold, trim: true, margin: new Thickness(0, 1, 0, 0)));
        if (!string.IsNullOrEmpty(detail)) text.Children.Add(Text(detail, 10, Soft, trim: true));
        body.Children.Add(text);
        return (tile, body, text);
    }

    private Border WindTile(WeatherForecast forecast, bool entrance)
    {
        var now = forecast.Now;
        string? gusts = now.GustKmh is { } g && g >= now.WindKmh + 5 ? Loc.Get("weather.gusts", (int)Math.Round(g)) : null;
        var (tile, body, _) = Tile(Loc.Get("weather.wind"), $"{(int)Math.Round(now.WindKmh)} km/h", gusts, reserveRight: 38);

        var dial = new Canvas { Width = 34, Height = 34, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        dial.Children.Add(new Ellipse { Width = 34, Height = 34, Stroke = WeatherIcons.Brush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), StrokeThickness = 1.2 });
        for (int i = 0; i < 4; i++)
        {
            double a = i * Math.PI / 2;
            dial.Children.Add(new Line
            {
                X1 = 17 + Math.Sin(a) * 13.5, Y1 = 17 - Math.Cos(a) * 13.5,
                X2 = 17 + Math.Sin(a) * 16, Y2 = 17 - Math.Cos(a) * 16,
                Stroke = i == 0 ? Brushes.White : Muted,
                StrokeThickness = i == 0 ? 1.6 : 1.1,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            });
        }
        // The arrow points where the wind blows to.
        var arrow = new Path
        {
            Data = Geometry.Parse("M17,6 L21.6,15.4 L18.1,14.2 L18.1,27.5 L15.9,27.5 L15.9,14.2 L12.4,15.4 Z"),
            Fill = Brushes.White
        };
        var aim = new RotateTransform(0, 17, 17);
        var gust = new RotateTransform(0, 17, 17);
        var group = new TransformGroup();
        group.Children.Add(gust);
        group.Children.Add(aim);
        arrow.RenderTransform = group;
        dial.Children.Add(arrow);
        body.Children.Add(dial);

        double target = (now.WindFrom + 180) % 360;
        if (entrance && !AnimationConfig.ReduceMotion)
            aim.BeginAnimation(RotateTransform.AngleProperty, WeatherMotion.To(target - 150, target, 1400, WeatherMotion.Spring, 380));
        else aim.Angle = target;
        double gustiness = now.GustKmh is { } gk ? Math.Clamp((gk - now.WindKmh) / 4, 0, 9) : 2;
        double swing = 3 + gustiness + Math.Min(now.WindKmh, 40) / 10;
        _loops.Swing(gust, RotateTransform.AngleProperty, -swing, swing, Math.Max(0.6, 1.6 - now.WindKmh / 60), entrance ? 1.8 : 0);
        return tile;
    }

    private Border HumidityTile(WeatherForecast forecast, bool entrance)
    {
        int humidity = Math.Clamp(forecast.Now.Humidity, 0, 100);
        var (tile, body, _) = Tile(Loc.Get("weather.humidity"), $"{humidity} %", null, reserveRight: 38);

        var ring = new Grid { Width = 34, Height = 34, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        const double thickness = 3.6, size = 32;
        ring.Children.Add(new Ellipse { Width = size, Height = size, Stroke = TrackFill, StrokeThickness = thickness });
        double units = Math.PI * (size - thickness) / thickness; // circumference in stroke widths
        var progress = new Ellipse
        {
            Width = size,
            Height = size,
            Stroke = RainBrush,
            StrokeThickness = thickness,
            StrokeDashArray = new DoubleCollection { units, units },
            StrokeDashCap = PenLineCap.Round,
            StrokeDashOffset = units * (1 - humidity / 100.0),
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new RotateTransform(-90)
        };
        ring.Children.Add(progress);
        if (entrance && !AnimationConfig.ReduceMotion)
            progress.BeginAnimation(Shape.StrokeDashOffsetProperty, WeatherMotion.To(units, units * (1 - humidity / 100.0), 1200, WeatherMotion.OutQuint, 420));

        var drop = new Path
        {
            Data = Geometry.Parse("M5,0 C5,0 10,6.2 10,9.2 A5,5 0 0 1 0,9.2 C0,6.2 5,0 5,0 Z"),
            Fill = RainBrush,
            Width = 10,
            Height = 14.2,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var bob = new TranslateTransform();
        var dropGroup = new TransformGroup();
        dropGroup.Children.Add(new ScaleTransform(0.85, 0.85, 5, 7));
        dropGroup.Children.Add(bob);
        drop.RenderTransform = dropGroup;
        ring.Children.Add(drop);
        _loops.Swing(bob, TranslateTransform.YProperty, -1.4, 1.2, 1.7);
        body.Children.Add(ring);
        return tile;
    }

    private Border UvTile(WeatherForecast forecast, bool entrance)
    {
        var today = forecast.Days.FirstOrDefault(d => d.Date == DateTime.Today) ?? forecast.Days[0];
        double uv = Math.Max(today.UvMax, forecast.Now.Uv);
        int rounded = (int)Math.Round(uv);
        string level = rounded switch
        {
            <= 2 => Loc.Get("weather.uv.low"),
            <= 5 => Loc.Get("weather.uv.moderate"),
            <= 7 => Loc.Get("weather.uv.high"),
            <= 10 => Loc.Get("weather.uv.veryHigh"),
            _ => Loc.Get("weather.uv.extreme")
        };
        var (tile, body, _) = Tile(Loc.Get("weather.uv"), $"{rounded} · {level}", null);

        const double barWidth = TileWidth - 18;
        var bar = new Canvas { Width = barWidth, Height = 8, VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 2) };
        var scale = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        scale.GradientStops.Add(new GradientStop(Color.FromRgb(0x3F, 0xD1, 0x5B), 0));
        scale.GradientStops.Add(new GradientStop(Color.FromRgb(0xFF, 0xD6, 0x0A), 0.3));
        scale.GradientStops.Add(new GradientStop(Color.FromRgb(0xFF, 0x9F, 0x0A), 0.55));
        scale.GradientStops.Add(new GradientStop(Color.FromRgb(0xFF, 0x45, 0x3A), 0.8));
        scale.GradientStops.Add(new GradientStop(Color.FromRgb(0xBF, 0x5A, 0xF2), 1));
        scale.Freeze();
        var strip = new Rectangle { Width = barWidth, Height = 4, RadiusX = 2, RadiusY = 2, Fill = scale };
        Canvas.SetTop(strip, 2);
        bar.Children.Add(strip);
        var knob = new Ellipse { Width = 8, Height = 8, Fill = Brushes.White, Stroke = Brushes.Black, StrokeThickness = 1.6 };
        var slide = new TranslateTransform();
        knob.RenderTransform = slide;
        bar.Children.Add(knob);
        double x = Math.Clamp(uv / 11.0, 0, 1) * (barWidth - 8);
        if (entrance && !AnimationConfig.ReduceMotion)
            slide.BeginAnimation(TranslateTransform.XProperty, WeatherMotion.To(0, x, 1000, WeatherMotion.OutBack, 480));
        else slide.X = x;
        body.Children.Add(bar);
        return tile;
    }

    private Border SunTile(WeatherForecast forecast, bool entrance)
    {
        var now = DateTime.Now;
        var today = SunTimes.For(now, forecast.Latitude, forecast.Longitude);
        var tomorrow = SunTimes.For(now.AddDays(1), forecast.Latitude, forecast.Longitude);
        var yesterday = SunTimes.For(now.AddDays(-1), forecast.Latitude, forecast.Longitude);
        bool day;
        DateTime from, to;
        string label, value, detail;
        string Clock(DateTime t) => t.ToString("t", CultureInfo.CurrentCulture);
        if (today is { } s && now >= s.Rise && now < s.Set)
        {
            day = true;
            (from, to) = (s.Rise, s.Set);
            label = Loc.Get("weather.sunset");
            value = Clock(s.Set);
            detail = "↑ " + Clock(s.Rise);
        }
        else if (today is { } s2)
        {
            day = false;
            // Night: from the last sunset to the next sunrise.
            if (now >= s2.Set) (from, to) = (s2.Set, tomorrow?.Rise ?? s2.Rise.AddDays(1));
            else (from, to) = (yesterday?.Set ?? s2.Set.AddDays(-1), s2.Rise);
            label = Loc.Get("weather.sunrise");
            value = Clock(to);
            detail = "↓ " + Clock(from);
        }
        else
        {
            var polar = Tile(Loc.Get("weather.sunrise"), "—", null);
            return polar.Tile;
        }
        var (tile, body, _) = Tile(label, value, detail, reserveRight: 54);

        const double w = 52, h = 30, r = 22;
        var arc = new Canvas { Width = w, Height = h, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
        var path = new PathGeometry();
        var figure = new PathFigure { StartPoint = new Point(w / 2 - r, h - 3) };
        figure.Segments.Add(new ArcSegment(new Point(w / 2 + r, h - 3), new Size(r, r), 0, false, SweepDirection.Clockwise, true));
        path.Figures.Add(figure);
        arc.Children.Add(new Path { Data = path, Stroke = WeatherIcons.Brush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)), StrokeThickness = 1.2, StrokeDashArray = new DoubleCollection { 1.2, 2.4 }, StrokeDashCap = PenLineCap.Round });
        arc.Children.Add(new Line { X1 = 0, X2 = w, Y1 = h - 3, Y2 = h - 3, Stroke = WeatherIcons.Brush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF)), StrokeThickness = 1 });

        var color = day ? WeatherIcons.SunColor : WeatherIcons.MoonColor;
        var glowBrush = new RadialGradientBrush();
        glowBrush.GradientStops.Add(new GradientStop(WeatherIcons.WithAlpha(color, 0x90), 0));
        glowBrush.GradientStops.Add(new GradientStop(WeatherIcons.WithAlpha(color, 0x00), 1));
        glowBrush.Freeze();
        var body2 = new Canvas();
        var glow = new Ellipse { Width = 16, Height = 16, Fill = glowBrush };
        Canvas.SetLeft(glow, -8);
        Canvas.SetTop(glow, -8);
        var orb = new Ellipse { Width = 8, Height = 8, Fill = WeatherIcons.Brush(color) };
        Canvas.SetLeft(orb, -4);
        Canvas.SetTop(orb, -4);
        body2.Children.Add(glow);
        body2.Children.Add(orb);
        var place = new TranslateTransform();
        body2.RenderTransform = place;
        arc.Children.Add(body2);
        _loops.Swing(glow, OpacityProperty, 0.5, 1, 1.6);

        double fraction = Math.Clamp((now - from).TotalMinutes / Math.Max(1, (to - from).TotalMinutes), 0, 1);
        var tween = new WeatherTween
        {
            Step = f =>
            {
                double angle = Math.PI * (1 - f);
                place.X = w / 2 + Math.Cos(angle) * r;
                place.Y = h - 3 - Math.Sin(angle) * r;
            }
        };
        if (entrance) tween.Play(0, fraction, 1500, WeatherMotion.OutQuint, 520);
        else tween.Play(fraction, fraction, 0, WeatherMotion.Out);
        body.Children.Add(arc);
        return tile;
    }

    #endregion
}
