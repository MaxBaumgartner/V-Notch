using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using VNotch.Models;
using VNotch.Services;

namespace VNotch.Controls;

/// <summary>
/// The weather tab: an animated sky for the weather now, the next 24 hours as a temperature curve, seven days
/// with range bars, wind, humidity, UV and the sun's arc. Built in code like the control center pages.
/// Everything that moves only moves while the tab is on screen; hidden, nothing ticks.
/// </summary>
public sealed partial class WeatherView : UserControl, IDisposable
{
    internal const double ContentWidth = 680;
    internal const double ContentHeight = 324;
    private const double LeftWidth = 256;
    private const double RightWidth = 404;
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(15);

    private static readonly Brush Muted = WeatherIcons.Brush(Color.FromRgb(0x8C, 0x8C, 0x8C));
    private static readonly Brush Soft = WeatherIcons.Brush(Color.FromArgb(0xC8, 0xFF, 0xFF, 0xFF));
    private static readonly Brush CardBg = WeatherIcons.Brush(Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF));
    private static readonly Brush RainBrush = WeatherIcons.Brush(WeatherIcons.RainColor);
    private static readonly Color RowIdle = Color.FromArgb(0, 0xFF, 0xFF, 0xFF);
    private static readonly Color RowHover = Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF);
    private static readonly Color RowSelected = Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF);
    private static readonly FontFamily Font = AppFonts.Get("Nirmala UI, Segoe UI");
    private static readonly FontFamily ThinFont = new("Segoe UI");
    private static readonly FontFamily SymbolFont = new("pack://application:,,,/Fonts/#Segoe MDL2 Assets, Segoe Fluent Icons, Segoe MDL2 Assets, Segoe UI Symbol");

    private readonly Grid _root = new() { Width = ContentWidth, Height = ContentHeight, ClipToBounds = false };
    private readonly LoopSet _loops = new();
    private LoopSet _skyLoops = new();
    private readonly DispatcherTimer _refreshTimer;
    private Func<CancellationToken, Task<WeatherForecast?>>? _fetch;
    private Func<bool>? _canGoOnline;
    private WeatherForecast? _forecast;
    private CancellationTokenSource? _loadCts;
    private bool _loading;
    private bool _failed;
    private bool _disposed;
    private DateTime? _selectedDay;
    private double _shownTemperature = double.NaN;
    private RotateTransform? _refreshSpin;
    private FrameworkElement? _sky;
    private string _skyKey = "";

    public WeatherView()
    {
        Content = _root;
        Width = ContentWidth;
        Height = ContentHeight;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = RefreshEvery };
        _refreshTimer.Tick += (_, _) => LoadAsync().SafeFireAndForget("WEATHER");
        IsVisibleChanged += OnIsVisibleChanged;
        Loc.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>
    /// <paramref name="fetch"/> reads the forecast (place from the settings); <paramref name="canGoOnline"/> says whether
    /// the privacy settings allow it at all.
    /// </summary>
    internal void Attach(Func<CancellationToken, Task<WeatherForecast?>> fetch, Func<bool> canGoOnline)
    {
        _fetch = fetch;
        _canGoOnline = canGoOnline;
    }

    /// <summary>One quiet read in the background, so the first open shows the sky instead of loading it.</summary>
    internal void Prefetch()
    {
        if (_forecast == null && !_loading) LoadAsync().SafeFireAndForget("WEATHER");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _refreshTimer.Stop();
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loops.StopAll();
        _skyLoops.StopAll();
        IsVisibleChanged -= OnIsVisibleChanged;
        Loc.LanguageChanged -= OnLanguageChanged;
    }

    #region Lifecycle and data

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_disposed) return;
        if (IsVisible)
        {
            Render(entrance: true);
            _refreshTimer.Start();
            if (_forecast == null || DateTime.Now - _forecast.FetchedAt > StaleAfter) LoadAsync().SafeFireAndForget("WEATHER");
        }
        else
        {
            _refreshTimer.Stop();
            _loops.StopAll();
            _skyLoops.StopAll();
            _sky = null;
            _skyKey = "";
            _refreshSpin = null;
            _root.Children.Clear();
            // Next time the tab opens it starts at the next 24 hours again.
            _selectedDay = null;
        }
    }

    private void OnLanguageChanged() => Dispatcher.BeginInvoke(() =>
    {
        if (!_disposed && IsVisible) Render(entrance: false);
    });

    private async Task LoadAsync()
    {
        if (_disposed || _fetch == null || _loading) return;
        if (_canGoOnline?.Invoke() == false)
        {
            if (IsVisible) Render(entrance: false);
            return;
        }
        _loading = true;
        SetRefreshSpinning(true);
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        WeatherForecast? result = null;
        try
        {
            result = await _fetch(_loadCts.Token);
        }
        finally
        {
            _loading = false;
        }
        if (_disposed) return;
        SetRefreshSpinning(false);
        bool first = _forecast == null;
        if (result != null)
        {
            _forecast = result;
            _failed = false;
        }
        else
        {
            _failed = true;
        }
        if (!IsVisible) return;
        // New data redraws in place; the first data arrives with the full entrance.
        if (result != null || first) Render(entrance: first && result != null);
    }

    #endregion

    #region Layout

    private void Render(bool entrance)
    {
        _loops.StopAll();
        _root.Children.Clear();
        _root.RowDefinitions.Clear();
        _root.ColumnDefinitions.Clear();
        _refreshSpin = null;

        if (_canGoOnline?.Invoke() == false)
        {
            DropSky();
            BuildMessage(Loc.Get("weather.disabled"), Loc.Get("weather.enableInSettings"), retry: false);
            return;
        }
        if (_forecast == null)
        {
            DropSky();
            if (_failed && !_loading) BuildMessage(Loc.Get("weather.unavailable"), Loc.Get("weather.retryLater"), retry: true);
            else BuildSkeleton();
            return;
        }

        var forecast = _forecast;
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LeftWidth) });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ContentWidth - LeftWidth - RightWidth) });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(RightWidth) });

        var left = new Grid();
        left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(18) });
        left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });
        left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(108) });
        left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
        left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) });
        left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(136) });
        Grid.SetColumn(left, 0);
        _root.Children.Add(left);

        var header = BuildPlaceHeader(forecast);
        left.Children.Add(header);
        var hero = BuildHero(forecast, entrance);
        Grid.SetRow(hero, 2);
        left.Children.Add(hero);
        var nowcast = BuildNowcast(forecast, entrance);
        Grid.SetRow(nowcast, 4);
        left.Children.Add(nowcast);
        var tiles = BuildTiles(forecast, entrance);
        Grid.SetRow(tiles, 6);
        left.Children.Add(tiles);

        var right = new Grid();
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(18) });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(4) });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(ChartHeight) });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(DayRowHeight * 7) });
        Grid.SetColumn(right, 2);
        _root.Children.Add(right);

        _chartHeader = new Grid();
        right.Children.Add(_chartHeader);
        _chartHost = new Grid { ClipToBounds = false };
        Grid.SetRow(_chartHost, 2);
        right.Children.Add(_chartHost);
        ShowChart(forecast, entrance ? 260 : -1);
        var days = BuildDays(forecast, entrance);
        Grid.SetRow(days, 4);
        right.Children.Add(days);

        if (entrance)
        {
            WeatherMotion.Rise(header, 0, dy: 6);
            WeatherMotion.Rise(nowcast, 140, dy: 10);
            WeatherMotion.Rise(_chartHeader, 60, dy: 6);
        }
    }

    private static readonly Geometry PinShape = CreatePinShape();

    private static Geometry CreatePinShape()
    {
        var pin = Geometry.Parse("F0 M5,0 C2.24,0 0,2.24 0,5 C0,8.6 5,13 5,13 C5,13 10,8.6 10,5 C10,2.24 7.76,0 5,0 Z M5,3 A2,2 0 1 0 5,7 A2,2 0 1 0 5,3 Z");
        pin.Freeze();
        return pin;
    }

    private FrameworkElement BuildPlaceHeader(WeatherForecast forecast)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var line = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        line.Children.Add(new Path
        {
            Data = PinShape,
            Fill = Soft,
            Width = 8,
            Height = 10.5,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        });
        string place = string.IsNullOrWhiteSpace(forecast.Place) ? "—" : forecast.Place;
        line.Children.Add(Text(place, 11.5, Brushes.White, FontWeights.SemiBold));
        line.Children.Add(Text("  ·  " + Loc.Get("weather.updatedAt", forecast.FetchedAt.ToString("t", CultureInfo.CurrentCulture)), 10.5, Muted));
        row.Children.Add(line);

        // Tap to read again; spins while it does.
        _refreshSpin = new RotateTransform(0);
        var glyph = new TextBlock
        {
            Text = "",
            FontFamily = SymbolFont,
            FontSize = 10.5,
            Foreground = Soft,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = _refreshSpin
        };
        var button = new Border
        {
            Width = 20,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            Background = new SolidColorBrush(RowIdle),
            Cursor = Cursors.Hand,
            Child = glyph,
            ToolTip = forecast.Source
        };
        HoverTint(button);
        PressSquish(button);
        button.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            LoadAsync().SafeFireAndForget("WEATHER");
        };
        Grid.SetColumn(button, 1);
        row.Children.Add(button);
        if (_loading) SetRefreshSpinning(true);
        return row;
    }

    private void SetRefreshSpinning(bool on)
    {
        if (_refreshSpin == null) return;
        if (on && !AnimationConfig.ReduceMotion)
        {
            var spin = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.85)) { RepeatBehavior = RepeatBehavior.Forever };
            Timeline.SetDesiredFrameRate(spin, AnimationConfig.TargetFps);
            _refreshSpin.BeginAnimation(RotateTransform.AngleProperty, spin);
        }
        else
        {
            // Finish the turn instead of snapping back.
            double angle = _refreshSpin.Angle % 360;
            _refreshSpin.BeginAnimation(RotateTransform.AngleProperty, null);
            _refreshSpin.Angle = angle;
            if (!AnimationConfig.ReduceMotion)
                _refreshSpin.BeginAnimation(RotateTransform.AngleProperty, WeatherMotion.To(angle, 360, 420, WeatherMotion.OutBack));
        }
    }

    #endregion

    #region Hero: sky, temperature, condition

    private FrameworkElement BuildHero(WeatherForecast forecast, bool entrance)
    {
        var now = forecast.Now;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(108) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var kind = WeatherIcons.KindOf(now.Code);
        string key = $"{kind}|{now.IsDay}|{now.WindKmh >= WindyKmh}";
        var skyHost = new Grid { Width = 108, Height = 108, ClipToBounds = false };
        if (_sky != null && key == _skyKey && !_skyLoops.IsEmpty && !entrance)
        {
            // Same sky: keep it moving instead of restarting every drop.
            if (_sky.Parent is Panel oldHost) oldHost.Children.Remove(_sky);
            skyHost.Children.Add(_sky);
        }
        else
        {
            var oldSky = _sky;
            var oldLoops = _skyLoops;
            _skyLoops = new LoopSet();
            _sky = BuildSky(kind, now.IsDay, now.WindKmh, _skyLoops);
            _skyKey = key;
            skyHost.Children.Add(_sky);
            if (entrance || oldSky == null)
            {
                oldLoops.StopAll();
                WeatherMotion.Pop(_sky, entrance ? 40 : 0, fromScale: 0.55, ms: 620);
            }
            else
            {
                // The weather changed while watching: the old sky shrinks away under the new one.
                if (oldSky.Parent is Panel oldHost) oldHost.Children.Remove(oldSky);
                skyHost.Children.Insert(0, oldSky);
                oldSky.RenderTransformOrigin = new Point(0.5, 0.5);
                var shrink = new ScaleTransform(1, 1);
                oldSky.RenderTransform = shrink;
                var fade = WeatherMotion.To(oldSky.Opacity, 0, 380, WeatherMotion.Out);
                fade.Completed += (_, _) =>
                {
                    oldLoops.StopAll();
                    skyHost.Children.Remove(oldSky);
                };
                oldSky.BeginAnimation(OpacityProperty, fade);
                shrink.BeginAnimation(ScaleTransform.ScaleXProperty, WeatherMotion.To(1, 0.8, 380, WeatherMotion.Out));
                shrink.BeginAnimation(ScaleTransform.ScaleYProperty, WeatherMotion.To(1, 0.8, 380, WeatherMotion.Out));
                WeatherMotion.Pop(_sky, 120, fromScale: 1.25, ms: 620);
            }
        }
        grid.Children.Add(skyHost);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, 2);
        var temperature = new TextBlock
        {
            FontFamily = ThinFont,
            FontWeight = FontWeights.Light,
            FontSize = 50,
            Foreground = Brushes.White,
            Margin = new Thickness(-2, -10, 0, -6),
            Text = Degrees(now.Temperature)
        };
        text.Children.Add(temperature);
        text.Children.Add(Text(WeatherConditionFormatter.Format(now.Code), 13.5, Brushes.White, FontWeights.SemiBold, trim: true));
        text.Children.Add(Text(Loc.Get("weather.feelsLike", Degrees(now.FeelsLike)), 11, Soft, trim: true, margin: new Thickness(0, 3, 0, 0)));
        var today = forecast.Days.FirstOrDefault(d => d.Date == DateTime.Today) ?? forecast.Days[0];
        text.Children.Add(Text(Loc.Get("weather.highLow", Rounded(today.High), Rounded(today.Low)), 11, Muted, trim: true, margin: new Thickness(0, 1, 0, 0)));
        grid.Children.Add(text);

        // The number counts to the temperature; on a refresh from the old one.
        var tween = new WeatherTween { Step = v => temperature.Text = Degrees(v) };
        double target = now.Temperature;
        if (entrance) tween.Play(Math.Round(target) - Math.Clamp(Math.Abs(target), 4, 12), target, 900, WeatherMotion.OutQuint, 120);
        else if (!double.IsNaN(_shownTemperature) && Math.Round(_shownTemperature) != Math.Round(target)) tween.Play(_shownTemperature, target, 700, WeatherMotion.Out);
        _shownTemperature = target;

        if (entrance)
        {
            for (int i = 0; i < text.Children.Count; i++)
                WeatherMotion.Rise((UIElement)text.Children[i], 70 + i * 55, dy: 0, dx: 14);
        }
        return grid;
    }

    private void DropSky()
    {
        _skyLoops.StopAll();
        _sky = null;
        _skyKey = "";
    }

    #endregion

    #region Nowcast pill

    private FrameworkElement BuildNowcast(WeatherForecast forecast, bool entrance)
    {
        var (message, wet, snow) = NowcastText(forecast, DateTime.Now);
        var pill = new Border { CornerRadius = new CornerRadius(11), Background = CardBg, Padding = new Thickness(10, 0, 10, 0) };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        pill.Child = grid;

        grid.Children.Add(BuildNowcastIcon(wet, snow, forecast));
        var label = Text(message, 11.5, Soft, trim: true);
        label.VerticalAlignment = VerticalAlignment.Center;
        label.Margin = new Thickness(4, 0, 8, 0);
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);

        var bars = BuildNowcastBars(forecast, entrance);
        Grid.SetColumn(bars, 2);
        grid.Children.Add(bars);
        return pill;
    }

    private FrameworkElement BuildNowcastIcon(bool wet, bool snow, WeatherForecast forecast)
    {
        if (!wet)
        {
            var next = forecast.Hours.FirstOrDefault(h => h.Time >= DateTime.Now.AddHours(1)) ?? forecast.Hours[^1];
            var icon = WeatherIcons.Create(next.Code, next.IsDay, 16);
            icon.HorizontalAlignment = HorizontalAlignment.Left;
            icon.VerticalAlignment = VerticalAlignment.Center;
            return icon;
        }
        // Drops (or flakes) keep falling into the pill.
        var canvas = new Canvas { Width = 16, Height = 18, ClipToBounds = true, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
        for (int i = 0; i < 3; i++)
        {
            Shape drop = snow
                ? new Ellipse { Width = 3.6, Height = 3.6, Fill = Brushes.White }
                : new Rectangle { Width = 2, Height = 6, RadiusX = 1, RadiusY = 1, Fill = RainBrush };
            Canvas.SetLeft(drop, 2 + i * 5);
            Canvas.SetTop(drop, -6);
            var fall = new TranslateTransform();
            drop.RenderTransform = fall;
            drop.Opacity = 0;
            double seconds = snow ? 1.6 : 0.8;
            _loops.Run(fall, TranslateTransform.YProperty, new DoubleAnimation(0, 22, TimeSpan.FromSeconds(seconds))
            {
                RepeatBehavior = RepeatBehavior.Forever,
                BeginTime = TimeSpan.FromSeconds(seconds * i / 3)
            });
            _loops.Run(drop, OpacityProperty, FadeThrough(seconds, i * seconds / 3));
            canvas.Children.Add(drop);
        }
        return canvas;
    }

    private FrameworkElement BuildNowcastBars(WeatherForecast forecast, bool entrance)
    {
        var now = DateTime.Now;
        var values = new List<double>();
        var quarters = forecast.Quarters.Where(q => q.Start.AddMinutes(15) > now).Take(8).ToList();
        if (quarters.Count >= 4) values.AddRange(quarters.Select(q => q.Precipitation * 4)); // per hour
        else values.AddRange(forecast.Hours.Where(h => h.Time.AddHours(1) > now).Take(8).Select(h => h.Precipitation));

        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Height = 18 };
        for (int i = 0; i < values.Count; i++)
        {
            double v = values[i];
            bool any = v >= 0.05;
            double height = any ? 4 + Math.Min(1, Math.Sqrt(v / 4)) * 14 : 2;
            var bar = new Rectangle
            {
                Width = 3.5,
                Height = height,
                RadiusX = 1.6,
                RadiusY = 1.6,
                Fill = any ? RainBrush : TrackFill,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(i == 0 ? 0 : 2, 0, 0, 0)
            };
            panel.Children.Add(bar);
            if (entrance) WeatherMotion.Grow(bar, 260 + i * 40, horizontal: false, new Point(0.5, 1), 520);
        }
        return panel;
    }

    private static readonly Brush TrackFill = WeatherIcons.Brush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));

    /// <summary>
    /// What the next hours bring, in words: "Rain in 20 min", "Rain stops in about 30 min", "Rain from about 18:00",
    /// or dry. Quarter hours when the source has them, hours otherwise.
    /// </summary>
    internal static (string Text, bool Wet, bool Snow) NowcastText(WeatherForecast forecast, DateTime now)
    {
        const double WetQuarter = 0.1;  // mm in 15 minutes
        const double WetHour = 0.2;     // mm in an hour
        var current = forecast.Hours.LastOrDefault(h => h.Time <= now) ?? forecast.Hours[0];
        bool snowy = WeatherIcons.KindOf(forecast.Now.Code) == WeatherKind.Snow ||
                     (forecast.Now.Temperature <= 0.5 && WeatherIcons.KindOf(current.Code) is WeatherKind.Snow or WeatherKind.Sleet);
        string Key(string rain) => snowy ? rain.Replace("weather.rain", "weather.snow", StringComparison.Ordinal) : rain;

        var quarters = forecast.Quarters.Where(q => q.Start.AddMinutes(15) > now).Take(8).ToList();
        if (quarters.Count >= 4)
        {
            bool wetNow = quarters[0].Precipitation >= WetQuarter;
            int index = quarters.FindIndex(q => (q.Precipitation >= WetQuarter) != wetNow);
            if (index > 0)
            {
                int minutes = RoundTo5(Math.Max(5, (quarters[index].Start - now).TotalMinutes));
                return wetNow
                    ? (Loc.Get(Key("weather.rainStopsIn"), minutes), true, snowy)
                    : (Loc.Get(Key("weather.rainIn"), minutes), true, snowy);
            }
            if (wetNow) return (Loc.Get(Key("weather.rainContinues")), true, snowy);
        }

        var hours = forecast.Hours.Where(h => h.Time.AddHours(1) > now && h.Time <= now.AddHours(12)).ToList();
        if (hours.Count == 0) return (Loc.Get("weather.dryAhead"), false, false);
        bool Wet(WeatherHour h) => h.Precipitation >= WetHour || h.PrecipitationChance >= 70;
        snowy = snowy || hours.Where(Wet).Any(h => WeatherIcons.KindOf(h.Code) == WeatherKind.Snow);
        if (Wet(hours[0]))
        {
            var dry = hours.FirstOrDefault(h => !Wet(h));
            return dry == null
                ? (Loc.Get(Key("weather.rainContinues")), true, snowy)
                : (Loc.Get(Key("weather.rainUntil"), dry.Time.ToString("t", CultureInfo.CurrentCulture)), true, snowy);
        }
        var wet = hours.FirstOrDefault(Wet);
        if (wet != null)
            return (Loc.Get(Key("weather.rainFrom"), wet.Time.ToString("t", CultureInfo.CurrentCulture)), true, snowy);
        return (Loc.Get("weather.dryAhead"), false, false);
    }

    private static int RoundTo5(double minutes) => (int)(Math.Round(minutes / 5.0) * 5);

    #endregion

    #region Empty states

    private void BuildSkeleton()
    {
        // Grey shapes where the forecast will be, with a sheen running over them.
        var sheen = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5), MappingMode = BrushMappingMode.RelativeToBoundingBox };
        sheen.GradientStops.Add(new GradientStop(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF), 0));
        sheen.GradientStops.Add(new GradientStop(Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF), 0.5));
        sheen.GradientStops.Add(new GradientStop(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF), 1));
        var slide = new TranslateTransform(-1, 0);
        sheen.RelativeTransform = slide;
        _loops.Run(slide, TranslateTransform.XProperty, new DoubleAnimation(-1, 1, TimeSpan.FromSeconds(1.3)) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = WeatherMotion.Sine });

        var canvas = new Canvas();
        void Block(double x, double y, double w, double h, double r, int order)
        {
            var block = new Rectangle { Width = w, Height = h, RadiusX = r, RadiusY = r, Fill = sheen };
            Canvas.SetLeft(block, x);
            Canvas.SetTop(block, y);
            canvas.Children.Add(block);
            WeatherMotion.Rise(block, order * 35, dy: 6);
        }
        Block(0, 4, 120, 10, 5, 0);
        Block(4, 30, 96, 96, 48, 1);
        Block(118, 44, 92, 40, 10, 2);
        Block(118, 92, 120, 10, 5, 3);
        Block(0, 146, 256, 34, 11, 4);
        Block(0, 188, 124, 64, 12, 5);
        Block(132, 188, 124, 64, 12, 6);
        Block(0, 260, 124, 64, 12, 7);
        Block(132, 260, 124, 64, 12, 8);
        Block(276, 4, 140, 10, 5, 2);
        Block(276, 26, 404, 120, 14, 3);
        for (int i = 0; i < 7; i++) Block(276, 168 + i * 22, 404, 14, 7, 4 + i);
        _root.Children.Add(canvas);
    }

    private void BuildMessage(string title, string detail, bool retry)
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var icon = WeatherIcons.Create(3, true, 54);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        var bob = new TranslateTransform();
        icon.RenderTransform = bob;
        _loops.Swing(bob, TranslateTransform.YProperty, -3, 3, 2.2);
        stack.Children.Add(icon);
        var titleText = Text(title, 15, Brushes.White, FontWeights.SemiBold);
        titleText.HorizontalAlignment = HorizontalAlignment.Center;
        titleText.Margin = new Thickness(0, 10, 0, 2);
        stack.Children.Add(titleText);
        var detailText = Text(detail, 11.5, Muted);
        detailText.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(detailText);
        if (retry)
        {
            stack.Cursor = Cursors.Hand;
            stack.Background = Brushes.Transparent;
            stack.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                _failed = false;
                Render(entrance: false);
                LoadAsync().SafeFireAndForget("WEATHER");
            };
        }
        _root.Children.Add(stack);
        for (int i = 0; i < stack.Children.Count; i++) WeatherMotion.Rise((UIElement)stack.Children[i], i * 70, dy: 10);
    }

    #endregion

    #region Helpers

    private static TextBlock Text(string text, double size, Brush brush, FontWeight? weight = null, bool trim = false, Thickness? margin = null) => new()
    {
        Text = text,
        FontFamily = Font,
        FontSize = size,
        Foreground = brush,
        FontWeight = weight ?? FontWeights.Normal,
        TextTrimming = trim ? TextTrimming.CharacterEllipsis : TextTrimming.None,
        Margin = margin ?? new Thickness(0),
        VerticalAlignment = VerticalAlignment.Center
    };

    internal static string Degrees(double celsius) => Rounded(celsius) + "°";

    /// <summary>A whole degree, with a real minus sign.</summary>
    internal static string Rounded(double celsius)
    {
        int value = (int)Math.Round(celsius, MidpointRounding.AwayFromZero);
        return value < 0 ? "−" + (-value).ToString(CultureInfo.InvariantCulture) : value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Opacity in, hold, out over one cycle: for drops, flakes and twinkles.</summary>
    private static DoubleAnimationUsingKeyFrames FadeThrough(double seconds, double delaySeconds, double peak = 1, double inAt = 0.12, double outFrom = 0.78)
    {
        var frames = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds(seconds),
            RepeatBehavior = RepeatBehavior.Forever,
            BeginTime = TimeSpan.FromSeconds(delaySeconds)
        };
        frames.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        frames.KeyFrames.Add(new LinearDoubleKeyFrame(peak, KeyTime.FromPercent(inAt)));
        frames.KeyFrames.Add(new LinearDoubleKeyFrame(peak, KeyTime.FromPercent(outFrom)));
        frames.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
        return frames;
    }

    /// <summary>A soft background that fades in under the pointer.</summary>
    private static void HoverTint(Border border, Func<bool>? selected = null)
    {
        border.MouseEnter += (_, _) =>
        {
            if (border.Background is SolidColorBrush brush) WeatherMotion.Tint(brush, selected?.Invoke() == true ? RowSelected : RowHover);
        };
        border.MouseLeave += (_, _) =>
        {
            if (border.Background is SolidColorBrush brush) WeatherMotion.Tint(brush, selected?.Invoke() == true ? RowSelected : RowIdle, 240);
        };
    }

    #endregion
}
