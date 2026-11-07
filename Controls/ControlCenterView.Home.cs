using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using VNotch.Services;
using VNotch.Services.ControlCenter;

namespace VNotch.Controls;

/// <summary>
/// The home tab (<see cref="ControlCenterPage.Pi"/>): the room from the desk, through the Pi dashboard's
/// /api/notch/zuhause. Big light tiles that bloom when a lamp comes on and fold their glow back in when it goes
/// off, the strip's colours, the heating with − / +, the air conditioner's remote, sockets with their draw (the PC
/// and the Pi ask before they switch off), the dashboard's scenes and the Pi in one line. The tab wears the
/// room's colour: the strip's when it is on, warm sand with a lamp on, night indigo when everything is off.
/// </summary>
public partial class ControlCenterView
{
    #region Home

    // A rough household price per kWh in Austria, for the "≈ €" next to the PC's day.
    private const double EuroPerKwh = 0.25;
    private static readonly TimeSpan GuardWindow = TimeSpan.FromSeconds(3);
    private const int TargetSendDelayMs = 700;
    private const int HomeSettleMs = 1400;
    private const int GlowFps = 24;

    private static readonly (Color Main, Color Warmer, Color Cooler) HomeNight =
        (Color.FromRgb(0x7C, 0x7F, 0xB8), Color.FromRgb(0xA1, 0x9B, 0xD2), Color.FromRgb(0x5F, 0x7F, 0xA8));
    private static readonly (Color Main, Color Warmer, Color Cooler) HomeLamp =
        (Color.FromRgb(0xC9, 0xA5, 0x77), Color.FromRgb(0xD2, 0x95, 0x7E), Color.FromRgb(0x8E, 0x7A, 0x62));
    private static readonly Color LampLight = Color.FromRgb(0xE8, 0xC4, 0x8E);
    private static readonly Color Grey = Color.FromRgb(0x8E, 0x8E, 0x9A);

    private PiHome? _home;
    private string _homeKey = "";
    private readonly LoopSet _homeLoops = new();
    private readonly StackPanel _homeReadingSection = new();
    private readonly StackPanel _homeLightSection = new();
    private readonly StackPanel _homeClimateSection = new();
    private readonly StackPanel _homePlugSection = new();
    private readonly StackPanel _homeSceneSection = new();
    private readonly StackPanel _homePiSection = new();
    private readonly Dictionary<string, LightTile> _lightTiles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TextBlock> _plugStatus = new(StringComparer.Ordinal);
    private HeatingParts? _heating;
    private TextBlock? _piLine;
    private bool? _musicSync;
    // After a tap the room is shown as asked until the dashboard has caught up, so the old state does not flash back.
    private DateTime _homeQuietUntilUtc;
    private string? _guardArmed;
    private double? _targetDraft;
    private DispatcherTimer? _targetSend;

    private static string Glyph(int code) => ((char)code).ToString();

    private StackPanel[] HomeSections => [_homeReadingSection, _homeLightSection, _homeClimateSection, _homePlugSection, _homeSceneSection, _homePiSection];

    private void AttachHome()
    {
        foreach (var section in HomeSections)
        {
            section.Tag = NestedSectionTag;
            ControlRoot.Children.Add(section);
        }
        SetMoodColors(HomeNight.Main, HomeNight.Warmer, HomeNight.Cooler);
    }

    private async Task RefreshMusicSyncAsync() => _musicSync = await GoveeSync.IsRunningAsync();

    private void BuildHome()
    {
        if (_disposed) return;
        if (_isLocalOnly() || _pi?.IsConfigured != true || _home is not { } home)
        {
            string text = _isLocalOnly() ? "pi.localOnly" : _pi?.IsConfigured != true ? "pi.notConfigured" : _piReachable ? "pi.loading" : "pi.unreachable";
            string emptyKey = "empty|" + text;
            if (emptyKey == _homeKey) return;
            _homeKey = emptyKey;
            ClearHome();
            _homeLightSection.Children.Add(NewRow(Glyph(0xE7BA), Loc.Get(text), muted: true));
            return;
        }

        var mood = HomeMood(home.Lights);
        SetMoodColors(mood.Main, mood.Warmer, mood.Cooler);
        string key = HomeKey(home);
        if (key == _homeKey)
        {
            UpdateHome(home);
            return;
        }
        _homeKey = key;
        ClearHome();
        _accent = _mood;
        BuildReadings(home);
        BuildLights(home);
        BuildClimate(home);
        BuildPlugs(home);
        BuildScenes(home);
        BuildPiLine(home);
    }

    private void ClearHome()
    {
        _homeLoops.StopAll();
        _lightTiles.Clear();
        _plugStatus.Clear();
        _heating = null;
        _piLine = null;
        foreach (var section in HomeSections)
            section.Children.Clear();
    }

    /// <summary>What changes the layout; lamps, temperatures and watts only change values in place.</summary>
    private string HomeKey(PiHome home)
    {
        var sb = new StringBuilder();
        foreach (string section in new[] { "home.lights", "home.climate", "home.plugs", "home.scenes", "home.pi" }) sb.Append(IsExpanded(section) ? '1' : '0');
        sb.Append('|').Append(_musicSync.HasValue).Append('|');
        foreach (var l in home.Lights) sb.Append(l.Id).Append(l.Available).Append(';');
        sb.Append('|').Append(home.Climate != null).Append(home.Climate?.WindowOpen).Append('|');
        foreach (var p in home.Plugs) sb.Append(p.Id).Append(p.Available).Append(p.On).Append(';');
        sb.Append('|').Append(_guardArmed);
        foreach (var a in home.AirConditioner) sb.Append(a.Id);
        foreach (var s in home.Scenes) sb.Append(s.Id);
        sb.Append('|');
        if (home.Pi != null) foreach (var i in home.Pi.Issues) sb.Append(i.Title).Append(i.FixId).Append(';');
        sb.Append('|');
        if (home.Readings != null) foreach (var r in home.Readings) sb.Append(r.Id).Append('=').Append(r.Label).Append(r.Display).Append(';');
        return sb.ToString();
    }

    /// <summary>A planner dashboard's start page (weather, when to leave, games) as small tiles: what it says, and the value.</summary>
    private void BuildReadings(PiHome home)
    {
        if (home.Readings is not { Count: > 0 } readings) return;
        int columns = Math.Min(3, readings.Count);
        var grid = TileGrid(columns, readings.Count);
        for (int i = 0; i < readings.Count; i++)
        {
            var label = MakeLabel(readings[i].Label, 11, Muted);
            label.TextTrimming = TextTrimming.CharacterEllipsis;
            var value = MakeLabel(readings[i].Display, 14, VNotch.Services.UiPalette.PrimaryBrush);
            value.FontWeight = FontWeights.SemiBold;
            value.TextTrimming = TextTrimming.CharacterEllipsis;
            value.Margin = new Thickness(0, 2, 0, 0);
            var card = Card();
            card.Padding = new Thickness(12, 9, 12, 9);
            card.Child = new StackPanel { Children = { label, value } };
            PlaceTile(grid, card, i, columns);
        }
        _homeReadingSection.Children.Add(grid);
    }

    /// <summary>The strip's colour while it is on, warm sand with a lamp on, night indigo when the room is dark.</summary>
    internal static (Color Main, Color Warmer, Color Cooler) HomeMood(IReadOnlyList<HomeLight> lights)
    {
        if (lights.FirstOrDefault(l => l.Rgb && l.On && l.Color != null) is { Color: { } strip })
        {
            var main = Mix(strip, Grey, 0.42);
            return (main, Mix(main, HomeLamp.Main, 0.5), Mix(main, HomeNight.Main, 0.5));
        }
        return lights.Any(l => l.On) ? HomeLamp : HomeNight;
    }

    private void UpdateHome(PiHome home)
    {
        bool quiet = DateTime.UtcNow < _homeQuietUntilUtc;
        if (!quiet)
        {
            foreach (var light in home.Lights)
                if (_lightTiles.TryGetValue(light.Id, out var tile)) tile.Show(light, animate: true);
        }
        if (_heating != null && home.Climate is { } climate) _heating.Show(climate, keepTarget: quiet || _targetDraft != null);
        foreach (var plug in home.Plugs)
            if (_plugStatus.TryGetValue(plug.Id, out var status)) status.Text = PlugText(plug);
        if (_piLine != null && home.Pi != null) _piLine.Text = PiText(home.Pi);
    }

    /// <summary>Sends a switch to the dashboard and shows the room again once Home Assistant has caught up.</summary>
    private async void SendHome(string action, string? id, JsonNode? value)
    {
        if (_pi == null) return;
        _homeQuietUntilUtc = DateTime.UtcNow.AddMilliseconds(HomeSettleMs + 600);
        bool ok = await _pi.SendHomeAsync(action, id, value);
        if (_disposed) return;
        if (ok) await Task.Delay(HomeSettleMs);
        _homeQuietUntilUtc = default;
        // Values change in place (the lamps keep their glow); only a changed layout rebuilds.
        RefreshPi();
    }

    #region Lights

    /// <summary>
    /// A lamp as a big tile. Switching on blooms a glow out of the bulb, sends a burst of rays and makes the bulb hop;
    /// switching off folds the glow back into it. While on the glow breathes slowly (only while the tab is open).
    /// </summary>
    private sealed class LightTile
    {
        public required ControlCenterView View;
        public required Border Card;
        public required Grid Body;
        public required TextBlock Icon, State;
        public required SolidColorBrush Ink, Fill;
        public required GradientStop GlowCore, GlowMid;
        public required Ellipse Glow;
        public required ScaleTransform GlowScale;
        public required Canvas Rays;
        public required ScaleTransform RaysScale;
        public required SolidColorBrush RayInk;
        public HomeLight Light = null!;
        public bool? On;
        public Color Color;
        public readonly List<(Ellipse Dot, Color Color)> Dots = new();
        public Ellipse? MusicDot;

        public Color LitColor => Light.Rgb ? Mix(Color, Colors.White, 0.12) : LampLight;

        public void Show(HomeLight light, bool animate)
        {
            Light = light;
            var color = light.Rgb ? light.Color ?? Color : LampLight;
            bool changed = On != light.On || (light.On && color != Color);
            bool turnedOn = On == false && light.On;
            bool turnedOff = On == true && !light.On;
            On = light.On;
            Color = color;
            if (!changed && !animate) return;
            double ms = animate ? 380 : 0;
            var lit = LitColor;
            SetOrTint(Ink, light.On ? lit : ((SolidColorBrush)Muted).Color, ms);
            SetOrTint(Fill, light.On ? Alpha(lit, 0.16) : ((SolidColorBrush)CardBg).Color, ms);
            SetOrTint(RayInk, lit, 0);
            Fade(GlowCore, light.On ? Alpha(lit, 0.55) : Alpha(lit, 0), ms);
            Fade(GlowMid, light.On ? Alpha(lit, 0.18) : Alpha(lit, 0), ms);
            State.Text = StateText();
            foreach (var (dot, c) in Dots) dot.Stroke = light.On && Near(c, color) ? Brushes.White : RingOff;
            View.Breathe(this);
            if (!animate || AnimationConfig.ReduceMotion)
            {
                GlowScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                GlowScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                GlowScale.ScaleX = GlowScale.ScaleY = light.On ? 1 : 0.2;
                return;
            }
            if (turnedOn)
            {
                // The light blooms out of the bulb with a little overshoot, rays burst outwards and the bulb hops.
                GlowScale.BeginAnimation(ScaleTransform.ScaleXProperty, WeatherMotion.To(0.15, 1, 720, WeatherMotion.OutBack));
                GlowScale.BeginAnimation(ScaleTransform.ScaleYProperty, WeatherMotion.To(0.15, 1, 720, WeatherMotion.OutBack));
                var burst = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(620) };
                burst.KeyFrames.Add(new EasingDoubleKeyFrame(0.95, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(110)), PressEase));
                burst.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(620)), WeatherMotion.Out));
                Timeline.SetDesiredFrameRate(burst, AnimationConfig.TargetFps);
                Rays.BeginAnimation(OpacityProperty, burst);
                RaysScale.BeginAnimation(ScaleTransform.ScaleXProperty, WeatherMotion.To(0.6, 1.55, 620, WeatherMotion.OutQuint));
                RaysScale.BeginAnimation(ScaleTransform.ScaleYProperty, WeatherMotion.To(0.6, 1.55, 620, WeatherMotion.OutQuint));
                Bounce(Icon, 1.38);
            }
            else if (turnedOff)
            {
                // The glow folds back into the bulb, which sinks a little.
                GlowScale.BeginAnimation(ScaleTransform.ScaleXProperty, WeatherMotion.To(GlowScale.ScaleX, 0.2, 340, WeatherMotion.Out));
                GlowScale.BeginAnimation(ScaleTransform.ScaleYProperty, WeatherMotion.To(GlowScale.ScaleY, 0.2, 340, WeatherMotion.Out));
                Bounce(Icon, 0.84);
            }
        }

        public string StateText() =>
            !Light.Available ? Loc.Get("control.noAdapter")
            : !Light.On ? Loc.Get("control.off")
            : Light.Rgb && View._musicSync == true ? Loc.Get("home.musicSync")
            : Light.Rgb && Light.Brightness is { } b && b < 100 ? $"{Loc.Get("control.on")} · {b} %"
            : Loc.Get("control.on");
    }

    private static readonly Brush RingOff = Frozen("#26FFFFFF");

    private static bool Near(Color a, Color b) => Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B) < 40;

    /// <summary>A lit lamp's glow breathes slowly while the tab is on screen; an unlit one keeps still.</summary>
    private void Breathe(LightTile tile)
    {
        tile.Glow.BeginAnimation(OpacityProperty, null);
        if (tile.On != true || !IsVisible || AnimationConfig.ReduceMotion) return;
        _homeLoops.Run(tile.Glow, OpacityProperty, new DoubleAnimation(1, 0.62, TimeSpan.FromSeconds(2.6))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = WeatherMotion.Sine,
            BeginTime = TimeSpan.FromMilliseconds(800)
        }, GlowFps);
    }

    private void BuildLights(PiHome home)
    {
        _homeLightSection.Children.Add(BuildSectionHeader("home.lights", Loc.Get("home.lights"), "", null, topMargin: 0));
        if (!IsExpanded("home.lights") || home.Lights.Count == 0) return;
        var grid = TileGrid(home.Lights.Count, home.Lights.Count);
        for (int i = 0; i < home.Lights.Count; i++)
        {
            var tile = NewLightTile(home.Lights[i], home.StripColors);
            _lightTiles[home.Lights[i].Id] = tile;
            PlaceTile(grid, tile.Card, i, home.Lights.Count);
        }
        _homeLightSection.Children.Add(grid);
    }

    private LightTile NewLightTile(HomeLight light, IReadOnlyList<HomeColor> colors)
    {
        const double IconSize = 22, GlowSize = 170;
        var ink = new SolidColorBrush(((SolidColorBrush)Muted).Color);
        var icon = new TextBlock
        {
            Text = Glyph(light.Rgb ? 0xE790 : 0xE82F),
            FontFamily = SegoeSymbolFont,
            FontSize = IconSize,
            Foreground = ink,
            HorizontalAlignment = HorizontalAlignment.Left,
            RenderTransformOrigin = new Point(0.5, 0.5)
        };
        var name = MakeLabel(light.Name, 13, VNotch.Services.UiPalette.PrimaryBrush);
        name.Margin = new Thickness(0, 10, 0, 0);
        var state = MakeLabel("", 11, Muted);
        state.Margin = new Thickness(0, 1, 0, 0);
        var content = new StackPanel { Children = { icon, name, state } };

        // The glow: a soft disc centred on the bulb, coloured by the light, clipped to the tile.
        var core = new GradientStop(Colors.Transparent, 0);
        var mid = new GradientStop(Colors.Transparent, 0.45);
        var glowScale = new ScaleTransform(0.2, 0.2);
        var glow = new Ellipse
        {
            Width = GlowSize,
            Height = GlowSize,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(IconSize / 2 - GlowSize / 2, IconSize / 2 - GlowSize / 2, 0, 0),
            Fill = new RadialGradientBrush { GradientStops = { core, mid, new GradientStop(Colors.Transparent, 1) } },
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = glowScale,
            IsHitTestVisible = false
        };
        // Eight short rays around the bulb, for the burst when it comes on.
        var rayInk = new SolidColorBrush(LampLight);
        var raysScale = new ScaleTransform(1, 1);
        var rays = new Canvas
        {
            Width = 44,
            Height = 44,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(IconSize / 2 - 22, IconSize / 2 - 22, 0, 0),
            Opacity = 0,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = raysScale,
            IsHitTestVisible = false
        };
        for (int i = 0; i < 8; i++)
        {
            double a = i * Math.PI / 4;
            rays.Children.Add(new Line
            {
                X1 = 22 + Math.Cos(a) * 14, Y1 = 22 + Math.Sin(a) * 14,
                X2 = 22 + Math.Cos(a) * 19, Y2 = 22 + Math.Sin(a) * 19,
                Stroke = rayInk, StrokeThickness = 1.8, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
            });
        }

        var body = new Grid { Children = { glow, rays, content } };
        var fill = new SolidColorBrush(((SolidColorBrush)CardBg).Color);
        var card = Card();
        card.Padding = new Thickness(14, 12, 14, 12);
        card.Background = fill;
        card.Cursor = light.Available ? Cursors.Hand : Cursors.Arrow;
        card.Child = body;
        System.Windows.Automation.AutomationProperties.SetName(card, light.Name);
        // The glow stays inside the tile's rounded corners.
        var clip = new RectangleGeometry { RadiusX = 12, RadiusY = 12 };
        card.SizeChanged += (_, e) =>
        {
            clip.Rect = new Rect(-14, -12, e.NewSize.Width, e.NewSize.Height);
            body.Clip = clip;
        };

        var tile = new LightTile
        {
            View = this, Card = card, Body = body, Icon = icon, State = state, Ink = ink, Fill = fill,
            GlowCore = core, GlowMid = mid, Glow = glow, GlowScale = glowScale, Rays = rays, RaysScale = raysScale, RayInk = rayInk,
            Color = light.Color ?? LampLight
        };
        if (light.Rgb) content.Children.Add(StripDots(tile, colors));
        Pressable(card, () => tile.On == true ? new SolidColorBrush(Alpha(tile.LitColor, 0.16)) : CardBg, CardHover, grow: true);
        card.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            if (!tile.Light.Available) return;
            bool on = tile.On != true;
            _homeQuietUntilUtc = DateTime.UtcNow.AddSeconds(3);
            tile.Show(tile.Light with { On = on }, animate: true);
            SetHomeMoodFromTiles();
            SendHome("light", tile.Light.Id, JsonValue.Create(on));
        };
        tile.Show(light, animate: false);
        // Arriving lit: the glow blooms in with the tab instead of just being there.
        if (light.On && IsVisible && !AnimationConfig.ReduceMotion)
        {
            glowScale.BeginAnimation(ScaleTransform.ScaleXProperty, WeatherMotion.To(0.2, 1, 900, WeatherMotion.OutQuint, 200));
            glowScale.BeginAnimation(ScaleTransform.ScaleYProperty, WeatherMotion.To(0.2, 1, 900, WeatherMotion.OutQuint, 200));
        }
        return tile;
    }

    /// <summary>The strip's colours as dots under its name (and "music sync" first, when it is set up on this PC).</summary>
    private FrameworkElement StripDots(LightTile tile, IReadOnlyList<HomeColor> colors)
    {
        var dots = new WrapPanel { Margin = new Thickness(0, 9, 0, 0) };
        if (_musicSync != null)
        {
            var music = Dot(new LinearGradientBrush(new GradientStopCollection
            {
                new(Color.FromRgb(0xC4, 0xB6, 0xEA), 0), new(Color.FromRgb(0xE3, 0xC7, 0x9F), 0.5), new(Color.FromRgb(0xA9, 0xD2, 0xCC), 1)
            }, 45), Loc.Get("home.musicSync"));
            music.Stroke = _musicSync == true ? Brushes.White : RingOff;
            music.MouseLeftButtonDown += async (_, e) =>
            {
                e.Handled = true;
                bool run = _musicSync != true;
                Ripple(tile, music, Color.FromRgb(0xC4, 0xB6, 0xEA));
                music.Stroke = run ? Brushes.White : RingOff;
                if (await GoveeSync.SetRunningAsync(run)) _musicSync = run;
                tile.State.Text = tile.StateText();
            };
            tile.MusicDot = music;
            dots.Children.Add(music);
        }
        foreach (var color in colors)
        {
            var c = color.Color;
            var dot = Dot(new SolidColorBrush(Mix(c, Grey, 0.38)), color.Name);
            dot.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                if (!tile.Light.Available) return;
                Ripple(tile, dot, c);
                _homeQuietUntilUtc = DateTime.UtcNow.AddSeconds(3);
                tile.Show(tile.Light with { On = true, Color = c }, animate: true);
                SetHomeMoodFromTiles();
                SendHome("light", tile.Light.Id, new JsonObject { ["rgb"] = new JsonArray(c.R, c.G, c.B) });
            };
            tile.Dots.Add((dot, c));
            dots.Children.Add(dot);
        }
        return dots;

        Ellipse Dot(Brush fill, string tooltip)
        {
            var scale = new ScaleTransform(1, 1);
            var dot = new Ellipse
            {
                Width = 15,
                Height = 15,
                Fill = fill,
                Stroke = RingOff,
                StrokeThickness = 1.6,
                Margin = new Thickness(0, 0, 6, 4),
                Cursor = Cursors.Hand,
                ToolTip = tooltip,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = scale
            };
            dot.MouseEnter += (_, _) => Grow(scale, 1.3);
            dot.MouseLeave += (_, _) => Grow(scale, 1);
            return dot;
        }
    }

    private static void Grow(ScaleTransform scale, double to)
    {
        if (AnimationConfig.ReduceMotion) return;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, WeatherMotion.To(scale.ScaleX, to, 420, ReleaseSpring));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, WeatherMotion.To(scale.ScaleY, to, 420, ReleaseSpring));
    }

    /// <summary>A wave of the picked colour that spreads from the dot across the tile.</summary>
    private void Ripple(LightTile tile, FrameworkElement from, Color color)
    {
        if (AnimationConfig.ReduceMotion || !IsVisible) return;
        Point at;
        try
        {
            at = from.TransformToAncestor(tile.Body).Transform(new Point(from.ActualWidth / 2, from.ActualHeight / 2));
        }
        catch (InvalidOperationException)
        {
            return;
        }
        const double Size = 40;
        var scale = new ScaleTransform(0.2, 0.2);
        var wave = new Ellipse
        {
            Width = Size,
            Height = Size,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(at.X - Size / 2, at.Y - Size / 2, 0, 0),
            Fill = new SolidColorBrush(Alpha(color, 0.45)),
            IsHitTestVisible = false,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = scale
        };
        tile.Body.Children.Insert(1, wave);
        var fade = WeatherMotion.To(0.9, 0, 700, WeatherMotion.Out);
        fade.Completed += (_, _) => tile.Body.Children.Remove(wave);
        wave.BeginAnimation(OpacityProperty, fade);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, WeatherMotion.To(0.2, 9, 700, WeatherMotion.OutQuint));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, WeatherMotion.To(0.2, 9, 700, WeatherMotion.OutQuint));
    }

    /// <summary>Right after a tap the tab's colour follows what the tiles now show, before the dashboard confirms it.</summary>
    private void SetHomeMoodFromTiles()
    {
        var lights = _lightTiles.Values.Select(t => t.Light with { On = t.On == true, Color = t.Light.Rgb ? t.Color : t.Light.Color }).ToList();
        var mood = HomeMood(lights);
        SetMoodColors(mood.Main, mood.Warmer, mood.Cooler);
    }

    #endregion

    #region Climate

    private sealed class HeatingParts
    {
        public required TextBlock Status, Current, Target, Humidity;
        public required RollingNumber CurrentRoll;

        public void Show(HomeClimate climate, bool keepTarget)
        {
            Status.Text = climate.Mode == "off" ? Loc.Get("home.heatingOff") : climate.IsHeating ? Loc.Get("home.heatingNow") : "";
            if (climate.Current is { } current) CurrentRoll.To(current);
            if (!keepTarget && climate.Target is { } target) Target.Text = Degrees(target);
            Humidity.Text = climate.Humidity is { } h ? Loc.Get("home.humidity", Math.Round(h)) : "";
        }
    }

    private static string Degrees(double value) => value.ToString(value % 1 == 0 ? "0" : "0.0", CultureInfo.CurrentCulture) + "°";

    private void BuildClimate(PiHome home)
    {
        if (home.Climate is not { } climate && home.AirConditioner.Count == 0) return;
        string right = home.Climate?.WindowOpen == true ? Loc.Get("home.windowOpen") : "";
        _homeClimateSection.Children.Add(BuildSectionHeader("home.climate", Loc.Get("home.climate"), right, null, topMargin: 14));
        if (!IsExpanded("home.climate")) return;
        var grid = TileGrid(2, 2);
        int index = 0;
        if (home.Climate is { } heating) PlaceTile(grid, HeatingCard(heating), index++, 2);
        if (home.AirConditioner.Count > 0) PlaceTile(grid, AirConditionerCard(home.AirConditioner), index, 2);
        _homeClimateSection.Children.Add(grid);
    }

    private FrameworkElement HeatingCard(HomeClimate climate)
    {
        var title = MakeLabel(Loc.Get("home.heating"), 11, Muted);
        var status = MakeLabel("", 11, _moodSoft);
        status.Margin = new Thickness(6, 0, 0, 0);
        var head = new StackPanel { Orientation = Orientation.Horizontal, Children = { title, status } };

        var current = MakeLabel("–", 26, VNotch.Services.UiPalette.PrimaryBrush);
        var roll = new RollingNumber(v => current.Text = v.ToString("0.0", CultureInfo.CurrentCulture) + "°");
        var target = MakeLabel("", 15, _moodInk);
        target.MinWidth = 44;
        target.TextAlignment = TextAlignment.Center;
        target.RenderTransformOrigin = new Point(0.5, 0.5);
        var stepper = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { StepButton("−", -0.5), target, StepButton("+", 0.5) }
        };
        var middle = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        middle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        middle.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        middle.Children.Add(current);
        Grid.SetColumn(stepper, 1);
        middle.Children.Add(stepper);

        var humidity = MakeLabel("", 11, Muted);
        humidity.Margin = new Thickness(0, 2, 0, 0);
        var card = Card();
        card.Padding = new Thickness(14, 11, 14, 11);
        card.Child = new StackPanel { Children = { head, middle, humidity } };

        _heating = new HeatingParts { Status = status, Current = current, Target = target, Humidity = humidity, CurrentRoll = roll };
        _heating.Show(climate, keepTarget: false);
        return card;

        Border StepButton(string sign, double step)
        {
            var label = MakeLabel(sign, 15, VNotch.Services.UiPalette.PrimaryBrush);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            var button = new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(14),
                Background = ComboBg,
                BorderBrush = ComboBorder,
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Child = label
            };
            button.MouseEnter += (_, _) => button.Background = ComboHover;
            button.MouseLeave += (_, _) => button.Background = ComboBg;
            button.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                if (_home?.Climate is not { } now) return;
                double from = _targetDraft ?? now.Target ?? 20;
                double next = Math.Clamp(Math.Round((from + step) * 2) / 2, Math.Max(5, now.Min), Math.Min(30, now.Max));
                _targetDraft = next;
                target.Text = Degrees(next);
                Bounce(target, step > 0 ? 1.16 : 0.88);
                // Several taps in a row send one change.
                _targetSend ??= new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(TargetSendDelayMs) };
                _targetSend.Stop();
                _targetSend.Tick -= SendTarget;
                _targetSend.Tick += SendTarget;
                _targetSend.Start();
            };
            Squish(button);
            return button;
        }
    }

    private void SendTarget(object? sender, EventArgs e)
    {
        _targetSend?.Stop();
        if (_targetDraft is not { } value) return;
        _targetDraft = null;
        SendHome("temp", null, JsonValue.Create(value));
    }

    /// <summary>The air conditioner's IR remote: one chip per button; it has no state to show, so a press just answers.</summary>
    private FrameworkElement AirConditionerCard(IReadOnlyList<HomeAction> buttons)
    {
        var title = MakeLabel(Loc.Get("home.ac"), 11, Muted);
        var chips = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var button in buttons)
        {
            bool power = button.Icon == "power";
            var label = new StackPanel { Orientation = Orientation.Horizontal };
            if (power)
                label.Children.Add(new TextBlock { Text = Glyph(0xE7E8), FontFamily = SegoeSymbolFont, FontSize = 11, Foreground = _moodInk, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 6, 0) });
            label.Children.Add(MakeLabel(button.Name, 11.5, power ? _moodInk : VNotch.Services.UiPalette.PrimaryBrush));
            var chip = new Border
            {
                CornerRadius = new CornerRadius(11),
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 6, 6),
                Background = power ? new SolidColorBrush(Alpha(_mood.Color, 0.22)) : new SolidColorBrush(((SolidColorBrush)ComboBg).Color),
                Cursor = Cursors.Hand,
                Child = label
            };
            string id = button.Id;
            chip.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                Flash(chip);
                SendHome("ac", id, null);
            };
            Squish(chip);
            chips.Children.Add(chip);
        }
        var card = Card();
        card.Padding = new Thickness(14, 11, 14, 7);
        card.Child = new StackPanel { Children = { title, chips } };
        return card;

        void Flash(Border chip)
        {
            if (chip.Background is not SolidColorBrush { IsFrozen: false } fill) return;
            var rest = fill.Color;
            SetOrTint(fill, Alpha(_mood.Color, 0.55), 0);
            WeatherMotion.Tint(fill, rest, 600);
        }
    }

    #endregion

    #region Sockets, scenes, Pi

    private string PlugText(HomePlug plug)
    {
        if (!plug.Available) return Loc.Get("control.noAdapter");
        var text = new StringBuilder(Loc.Get(plug.On ? "control.on" : "control.off"));
        if (plug.On && plug.Watts is { } watts) text.Append(" · ").Append(Math.Round(watts)).Append(" W");
        if (plug.TodayKwh is { } kwh && kwh > 0)
            text.Append(" · ").Append(Loc.Get("home.today", kwh.ToString("0.00", CultureInfo.CurrentCulture), (kwh * EuroPerKwh).ToString("0.00", CultureInfo.CurrentCulture)));
        return text.ToString();
    }

    private void BuildPlugs(PiHome home)
    {
        if (home.Plugs.Count == 0) return;
        _homePlugSection.Children.Add(BuildSectionHeader("home.plugs", Loc.Get("home.plugs"), Loc.Get("home.power"), null, topMargin: 14));
        if (!IsExpanded("home.plugs")) return;
        foreach (var plug in home.Plugs)
        {
            var row = NewRow(Glyph(plug.Name == "PC" ? 0xE7F4 : plug.Name == "Pi" ? 0xE950 : 0xE945), plug.Name);
            string id = plug.Id;
            if (!plug.Available) AddStatus(row, PlugText(plug), Muted);
            else if (!plug.Guard || !plug.On) AddStatus(row, PlugText(plug), plug.On ? _moodSoft : Muted, Toggle(plug.On, on => SendHome("plug", id, JsonValue.Create(on))));
            else
            {
                // The PC and the Pi switch themselves off with their socket: a first press only asks.
                AddStatus(row, PlugText(plug), _moodSoft);
                bool armed = _guardArmed == id;
                var button = TextButton(Loc.Get(armed ? "home.sure" : "home.turnOff"), accent: false, () =>
                {
                    if (_guardArmed != id)
                    {
                        _guardArmed = id;
                        _homeKey = "";
                        BuildHome();
                        DisarmLater(id);
                        return;
                    }
                    _guardArmed = null;
                    SendHome("plug", id, JsonValue.Create(false));
                });
                if (armed)
                {
                    button.Background = EndConfirmBg;
                    ((TextBlock)button.Child).Foreground = HotTemp;
                    Bounce(button, 1.08);
                }
                AddAction(row, button);
            }
            if (row.Children[^1] is Grid { Children.Count: > 0 } panel && panel.Children[^1] is TextBlock status) _plugStatus[id] = status;
            else if (row.Children.Count > 1 && row.Children[1] is Grid statusPanel && statusPanel.Children[^1] is TextBlock label) _plugStatus[id] = label;
            _homePlugSection.Children.Add(row);
        }
    }

    private void DisarmLater(string id)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = GuardWindow };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_guardArmed != id) return;
            _guardArmed = null;
            _homeKey = "";
            BuildHome();
        };
        timer.Start();
    }

    private static int SceneGlyph(string icon) => icon switch
    {
        "moon" => 0xE708,
        "game" => 0xE7FC,
        "book" => 0xE736,
        "wind" => 0xE753,
        _ => 0xE781
    };

    private void BuildScenes(PiHome home)
    {
        if (home.Scenes.Count == 0) return;
        _homeSceneSection.Children.Add(BuildSectionHeader("home.scenes", Loc.Get("home.scenes"), "", null, topMargin: 14));
        if (!IsExpanded("home.scenes")) return;
        var grid = TileGrid(home.Scenes.Count, home.Scenes.Count);
        for (int i = 0; i < home.Scenes.Count; i++)
        {
            var scene = home.Scenes[i];
            int glyph = SceneGlyph(scene.Icon);
            var icon = new TextBlock { Text = Glyph(glyph), FontFamily = SegoeSymbolFont, FontSize = 16, Foreground = _moodInk, VerticalAlignment = VerticalAlignment.Center, RenderTransformOrigin = new Point(0.5, 0.5) };
            var name = MakeLabel(scene.Name, 12.5, VNotch.Services.UiPalette.PrimaryBrush);
            name.Margin = new Thickness(10, 0, 0, 0);
            var card = Card();
            card.Padding = new Thickness(12, 9, 12, 9);
            card.Cursor = Cursors.Hand;
            card.Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, name } };
            Pressable(card, () => CardBg, CardHover, grow: true);
            string id = scene.Id;
            card.MouseLeftButtonDown += async (_, e) =>
            {
                e.Handled = true;
                // A tick answers the tap, then the scene's own icon comes back.
                icon.Text = Glyph(0xE73E);
                Bounce(icon, 1.4);
                SendHome("scene", id, null);
                await Task.Delay(1400);
                icon.Text = Glyph(glyph);
            };
            PlaceTile(grid, card, i, home.Scenes.Count);
        }
        _homeSceneSection.Children.Add(grid);
    }

    private static string PiText(HomePi pi)
    {
        var parts = new List<string>();
        if (pi.Temp is { } temp) parts.Add(Math.Round(temp) + "°");
        if (pi.UptimeSeconds is { } up) parts.Add(Loc.Get("home.piUp", FormatDuration(TimeSpan.FromSeconds(up))));
        if (pi.Issues.Count == 0) parts.Add(Loc.Get("home.piOk"));
        return string.Join(" · ", parts);
    }

    /// <summary>The Pi in one quiet line; checks that are not green get a row each, with their fix when there is one.</summary>
    private void BuildPiLine(PiHome home)
    {
        if (home.Pi is not { } pi) return;
        _homePiSection.Children.Add(BuildSectionHeader("home.pi", "Pi", "", null, topMargin: 14));
        if (!IsExpanded("home.pi")) return;
        var row = NewRow(Glyph(0xE950), "Raspberry Pi");
        AddStatus(row, PiText(pi), pi.Issues.Count == 0 ? HandedIn : Muted);
        if (row.Children[^1] is Grid { Children.Count: > 0 } panel && panel.Children[^1] is TextBlock line) _piLine = line;
        _homePiSection.Children.Add(row);
        foreach (var issue in pi.Issues)
        {
            var issueRow = NewRow(Glyph(issue.Status == "error" ? 0xE783 : 0xE7BA), issue.Title);
            AddStatus(issueRow, issue.Detail, issue.Status == "error" ? HotTemp : DueSoon);
            if (issue.FixId is { } fix)
            {
                string label = issue.FixLabel ?? Loc.Get("home.fix");
                AddAction(issueRow, TextButton(Loc.Get("home.fix"), accent: false, () => SendHome("fix", fix, null)));
                issueRow.ToolTip = label;
            }
            _homePiSection.Children.Add(issueRow);
        }
    }

    #endregion

    #endregion
}
