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
/// /api/notch/zuhause. Round switches that light up when a lamp comes on and fold their glow back in when it goes
/// off, the strip's colours, the heating with − / +, the air conditioner's remote, sockets with their draw (the PC
/// and the Pi ask before they switch off), and the Pi's status and fixes. The tab wears the
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

    private static readonly (Color Main, Color Warmer, Color Cooler) HomeNight =
        (Color.FromRgb(0x7C, 0x7F, 0xB8), Color.FromRgb(0xA1, 0x9B, 0xD2), Color.FromRgb(0x5F, 0x7F, 0xA8));
    private static readonly (Color Main, Color Warmer, Color Cooler) HomeLamp =
        (Color.FromRgb(0xC9, 0xA5, 0x77), Color.FromRgb(0xD2, 0x95, 0x7E), Color.FromRgb(0x8E, 0x7A, 0x62));
    private static readonly Color LampLight = Color.FromRgb(0xE8, 0xC4, 0x8E);
    private static readonly Color Grey = Color.FromRgb(0x8E, 0x8E, 0x9A);

    private PiHome? _home;
    private string _homeKey = "";
    private readonly LoopSet _homeLoops = new();
    private readonly StackPanel _homeLightSection = new();
    private readonly StackPanel _homeClimateSection = new();
    private readonly StackPanel _homePlugSection = new();
    private readonly StackPanel _homePiSection = new();
    private readonly Dictionary<string, LightTile> _lightTiles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TextBlock> _plugStatus = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PlugReadout> _plugReadouts = new(StringComparer.Ordinal);
    private HeatingParts? _heating;
    private TextBlock? _piLine;
    private PiReadout? _piReadout;
    private bool? _musicSync;
    // After a tap the room is shown as asked until the dashboard has caught up, so the old state does not flash back.
    private DateTime _homeQuietUntilUtc;
    private string? _guardArmed;
    private double? _targetDraft;
    private DispatcherTimer? _targetSend;

    private static string Glyph(int code) => ((char)code).ToString();


    private void AttachHome()
    {
        MountCompactPage();
        _compactPage.Children.Add(_homeLightSection);
        SetMoodColors(HomeNight.Main, HomeNight.Warmer, HomeNight.Cooler);
        CoverColorsFile.Changed += OnCoverColorsChanged;
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
            RemoveCompactDrawer();
            SetCompactStatus("");
            ClearHome();
            _homeLightSection.Children.Add(text is "pi.loading" or "pi.unreachable"
                ? new SkeletonPlaceholder(!_piReachable, Loc.Get(text), 82, 56, 48, 34)
                : NewRow(Glyph(0xE7BA), Loc.Get(text), muted: true));
            return;
        }

        var mood = HomeMood(home.Lights.Select(AsShown).ToList());
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
        BuildPiLine(home);
        var right = new StackPanel { Children = { _homePlugSection, _homePiSection } };
        AddDrawer(CompactColumns(_homeClimateSection, right));
        UpdateHomeStatus(home);
    }

    private void ClearHome()
    {
        foreach (var section in new[] { _homeClimateSection, _homePlugSection, _homePiSection })
            if (section.Parent is Panel parent) parent.Children.Remove(section);
        _homeLoops.StopAll();
        _lightTiles.Clear();
        _plugStatus.Clear();
        _plugReadouts.Clear();
        _heating = null;
        _piLine = null;
        _piReadout = null;
        foreach (var section in new[] { _homeLightSection, _homeClimateSection, _homePlugSection, _homePiSection })
            section.Children.Clear();
    }

    /// <summary>What changes the layout; lamps, temperatures and watts only change values in place.</summary>
    private string HomeKey(PiHome home)
    {
        var sb = new StringBuilder();
        foreach (string section in new[] { "home.lights", "home.climate", "home.plugs", "home.pi" }) sb.Append(IsExpanded(section) ? '1' : '0');
        sb.Append('|').Append(_musicSync.HasValue).Append('|');
        foreach (var l in home.Lights) sb.Append(l.Id).Append(l.Name).Append(l.Rgb).Append(l.Available).Append(';');
        sb.Append('|').Append(home.Climate != null).Append(home.Climate?.WindowOpen).Append('|');
        foreach (var p in home.Plugs) sb.Append(p.Id).Append(p.Name).Append(p.Guard).Append(p.Available).Append(p.On).Append(';');
        sb.Append('|').Append(_guardArmed);
        foreach (var a in home.AirConditioner) sb.Append(a.Id);
        sb.Append('|');
        if (home.Pi != null) foreach (var i in home.Pi.Issues) sb.Append(i.Title).Append(i.FixId).Append(';');
        sb.Append('|');
        if (home.Readings != null) foreach (var r in home.Readings) sb.Append(r.Id).Append('=').Append(r.Label).Append(r.Display).Append(';');
        return sb.ToString();
    }

    /// <summary>A planner dashboard's start page (weather, when to leave, games) as small tiles: what it says, and the value.
    /// A planner has no lamps, so they take the lights' place in the main area.</summary>
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
        _homeLightSection.Children.Add(grid);
    }

    /// <summary>
    /// The strip's colour while it is on (muted to the tab's calm tones), warm sand with a lamp on, night indigo
    /// when the room is dark. Pass the lights through <see cref="AsShown"/> first, so music sync counts with the
    /// cover's colour.
    /// </summary>
    internal static (Color Main, Color Warmer, Color Cooler) HomeMood(IReadOnlyList<HomeLight> lights)
    {
        if (lights.FirstOrDefault(l => l.Rgb && l.On && l.Color != null) is { Color: { } strip })
        {
            var main = Calm(strip, maxSaturation: 0.3, lightness: 0.6);
            return (main, Mix(main, HomeLamp.Main, 0.5), Mix(main, HomeNight.Main, 0.5));
        }
        return lights.Any(l => l.On) ? HomeLamp : HomeNight;
    }

    /// <summary>
    /// The strip as the room sees it: with music sync and a song playing it glows in the cover's main colour (Home
    /// Assistant only knows its resting colour then); otherwise in the colour picked.
    /// </summary>
    internal static HomeLight AsShown(HomeLight light, bool musicSync, IReadOnlyList<Color> cover) =>
        light.Rgb && musicSync && cover.Count > 0 ? light with { Color = cover[0] } : light;

    private HomeLight AsShown(HomeLight light) => AsShown(light, _musicSync == true, CoverColorsFile.Current);

    private void OnCoverColorsChanged()
    {
        if (_disposed || Page != ControlCenterPage.Pi || _home is not { } home || _musicSync != true) return;
        UpdateHome(home, force: true);
        SetHomeMoodFromTiles();
    }

    /// <summary>A light's colour as the notch shows it: its hue, but with little saturation and a set lightness.</summary>
    internal static Color Calm(Color c, double maxSaturation, double lightness)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = 0;
        if (d > 0)
        {
            h = max == r ? (g - b) / d % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
            h = (h * 60 + 360) % 360;
        }
        double l = (max + min) / 2;
        double s = d == 0 ? 0 : d / (1 - Math.Abs(2 * l - 1));
        s = Math.Min(s, maxSaturation);
        l = lightness;
        double chroma = (1 - Math.Abs(2 * l - 1)) * s, x = chroma * (1 - Math.Abs(h / 60 % 2 - 1)), m = l - chroma / 2;
        (double r1, double g1, double b1) = h switch
        {
            < 60 => (chroma, x, 0.0),
            < 120 => (x, chroma, 0.0),
            < 180 => (0.0, chroma, x),
            < 240 => (0.0, x, chroma),
            < 300 => (x, 0.0, chroma),
            _ => (chroma, 0.0, x)
        };
        return Color.FromRgb((byte)Math.Round((r1 + m) * 255), (byte)Math.Round((g1 + m) * 255), (byte)Math.Round((b1 + m) * 255));
    }

    private void UpdateHomeStatus(PiHome home) => SetCompactStatus(home.Climate is { } c
        ? $"{(c.IsHeating ? "♨ " : "")}{(c.Current is { } current ? Degrees(current) : "–")}  → {(c.Target is { } target ? Degrees(target) : "–")}" : "");

    private void UpdateHome(PiHome home, bool force = false)
    {
        UpdateHomeStatus(home);
        bool quiet = !force && DateTime.UtcNow < _homeQuietUntilUtc;
        if (!quiet)
        {
            foreach (var light in home.Lights)
                if (_lightTiles.TryGetValue(light.Id, out var tile)) tile.Show(AsShown(light), animate: true);
        }
        if (_heating != null && home.Climate is { } climate) _heating.Show(climate, keepTarget: quiet || _targetDraft != null);
        foreach (var plug in home.Plugs)
            if (_plugReadouts.TryGetValue(plug.Id, out var readout)) readout.Show(plug);
        if (home.Pi != null) _piReadout?.Show(home.Pi);
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
    /// A round light switch. Its glow and glyph answer state changes and remain still at rest.
    /// </summary>
    private sealed class LightTile
    {
        public required ControlCenterView View;
        public required Border Card;
        public required TextBlock Icon, State;
        public required SolidColorBrush Ink, Fill;
        public required GradientStop GlowCore, GlowMid;
        public required ScaleTransform GlowScale;
        public HomeLight Light = null!;
        public bool? On;
        public Color Color;
        public readonly List<(Ellipse Dot, Color Color)> Dots = new();
        public FrameworkElement? Controls;
        public Action? RefreshStrip;
        public TextBlock? BrightnessLabel;
        public ScaleTransform? BrightnessFill;

        public Color LitColor => Light.Rgb ? Calm(Color, maxSaturation: 0.45, lightness: 0.66) : LampLight;

        public void Show(HomeLight light, bool animate)
        {
            Light = light;
            var color = light.Rgb ? light.Color ?? Color : LampLight;
            bool changed = On != light.On || (light.On && color != Color);
            bool turnedOn = On == false && light.On;
            bool turnedOff = On == true && !light.On;
            On = light.On;
            Color = color;
            if (Controls != null) { Controls.Opacity = light.On ? 1 : .4; Controls.IsEnabled = light.On && light.Available; }
            RefreshStrip?.Invoke();
            if (!changed && !animate) return;
            bool moving = animate && Card.IsVisible && !AnimationConfig.ReduceMotion;
            double ms = moving ? 180 : 0;
            var lit = LitColor;
            SetOrTint(Ink, light.On ? lit : ((SolidColorBrush)Muted).Color, ms);
            SetOrTint(Fill, light.On ? Alpha(lit, 0.16) : ((SolidColorBrush)CardBg).Color, ms);
            Fade(GlowCore, light.On ? Alpha(lit, 0.55) : Alpha(lit, 0), ms);
            Fade(GlowMid, light.On ? Alpha(lit, 0.18) : Alpha(lit, 0), ms);
            SetDetailText(State, StateText());
            if (BrightnessLabel != null) BrightnessLabel.Text = $"{light.Brightness ?? 100} %";
            if (BrightnessFill != null) Motion.Stop(BrightnessFill, ScaleTransform.ScaleXProperty, (light.Brightness ?? 100) / 100d);
            foreach (var (dot, c) in Dots) dot.Stroke = light.On && Near(c, color) ? Brushes.White : RingOff;
            if (!moving)
            {
                Settle();
                return;
            }
            if (turnedOn)
            {
                Motion.To(GlowScale, ScaleTransform.ScaleXProperty, 1, Motion.Shift);
                Motion.To(GlowScale, ScaleTransform.ScaleYProperty, 1, Motion.Shift);
                DetailReaction(Icon);
            }
            else if (turnedOff)
            {
                // The glow folds back into the switch.
                Motion.To(GlowScale, ScaleTransform.ScaleXProperty, 0.2, Motion.Exit);
                Motion.To(GlowScale, ScaleTransform.ScaleYProperty, 0.2, Motion.Exit);
                DetailReaction(Icon);
            }
        }

        public void Settle()
        {
            Motion.Stop(GlowScale, ScaleTransform.ScaleXProperty, On == true ? 1 : 0.2);
            Motion.Stop(GlowScale, ScaleTransform.ScaleYProperty, On == true ? 1 : 0.2);
            var lit = LitColor;
            SetOrTint(Ink, On == true ? lit : ((SolidColorBrush)Muted).Color, 0);
            SetOrTint(Fill, On == true ? Alpha(lit, 0.16) : ((SolidColorBrush)CardBg).Color, 0);
            Fade(GlowCore, On == true ? Alpha(lit, 0.55) : Alpha(lit, 0), 0);
            Fade(GlowMid, On == true ? Alpha(lit, 0.18) : Alpha(lit, 0), 0);
        }

        public string StateText() =>
            !Light.Available ? "Offline"
            : !Light.On ? Loc.Get("control.off")
            : Loc.Get("control.on");
    }

    private static readonly Brush RingOff = Frozen("#26FFFFFF");

    private static bool Near(Color a, Color b) => Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B) < 40;

    private void BuildLights(PiHome home)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var light in home.Lights)
        {
            var tile = NewLightTile(AsShown(light), home.StripColors);
            _lightTiles[light.Id] = tile;
            row.Children.Add(tile.Card);
            if (light.Rgb) row.Children.Add(StripControls(tile, home.StripColors));
        }
        foreach (var plug in home.Plugs.Where(p => !p.Guard).Take(3))
        {
            var socket = NewLightTile(new HomeLight(plug.Id, plug.Name, false, plug.Available, plug.On, null, null), Array.Empty<HomeColor>());
            socket.Icon.Text = Glyph(plug.Name.Contains("Mikrofon", StringComparison.OrdinalIgnoreCase) ? 0xE720 : 0xE945);
            socket.Card.IsHitTestVisible = false;
            var hit = new Border { Child = socket.Card, Cursor = Cursors.Hand, Background = Brushes.Transparent };
            bool on = plug.On;
            Squish(hit);
            hit.MouseLeftButtonUp += (_, e) => { e.Handled = true; if (!plug.Available) return; on = !on;
                socket.Show(socket.Light with { On = on }, true); SendHome("plug", plug.Id, JsonValue.Create(on)); };
            row.Children.Add(hit);
        }
        _homeLightSection.Children.Add(row);
    }

    private LightTile NewLightTile(HomeLight light, IReadOnlyList<HomeColor> colors)
    {
        var ink = new SolidColorBrush(((SolidColorBrush)CompactLabel).Color);
        var fill = new SolidColorBrush(((SolidColorBrush)CompactTrack).Color);
        var icon = CompactGlyph(light.Rgb ? 0xE790 : 0xE82F, ink);
        icon.FontSize = 20; icon.Width = 24;
        var core = new GradientStop(Colors.Transparent, 0);
        var mid = new GradientStop(Colors.Transparent, .5);
        var glowScale = new ScaleTransform(.2, .2);
        var glow = new Ellipse { Width = 52, Height = 52, IsHitTestVisible = false,
            Fill = new RadialGradientBrush { GradientStops = { core, mid, new GradientStop(Colors.Transparent, 1) } },
            RenderTransformOrigin = new Point(.5, .5), RenderTransform = glowScale };
        var ring = new Border { Width = 52, Height = 52, CornerRadius = new CornerRadius(26), Background = fill, Child = icon };
        var body = new Grid { Height = 54, Children = { glow, ring } };
        var name = CompactText(light.Name, 11, CompactLabel); name.TextAlignment = TextAlignment.Center;
        name.Margin = new Thickness(0, 5, 0, 0);
        var state = CompactText("", 10.5, CompactLabel); state.TextAlignment = TextAlignment.Center;
        var card = new Border { Width = 62, Background = Brushes.Transparent, Cursor = Cursors.Hand,
            Opacity = light.Available ? 1 : .4, Child = new StackPanel { Children = { body, name, state } } };
        System.Windows.Automation.AutomationProperties.SetName(card, light.Name);
        var tile = new LightTile { View = this, Card = card, Icon = icon, State = state,
            Ink = ink, Fill = fill, GlowCore = core, GlowMid = mid, GlowScale = glowScale, Color = light.Color ?? LampLight };
        Squish(card);
        card.MouseEnter += (_, _) => ring.BorderThickness = new Thickness(1);
        card.MouseLeave += (_, _) => ring.BorderThickness = new Thickness(0);
        ring.BorderBrush = Frozen("#26FFFFFF");
        card.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (!tile.Light.Available) return;
            bool on = tile.On != true;
            tile.Show(tile.Light with { On = on }, animate: true);
            SetHomeMoodFromTiles();
            SendHome("light", tile.Light.Id, JsonValue.Create(on));
        };
        card.IsVisibleChanged += (_, _) => { if (!card.IsVisible) tile.Settle(); };
        tile.Show(light, animate: false);
        return tile;
    }

    private FrameworkElement StripControls(LightTile tile, IReadOnlyList<HomeColor> colors)
    {
        var panel = new StackPanel { Width = 138, Margin = new Thickness(4, 0, 4, 0),
            IsEnabled = tile.Light.Available && tile.On == true, Opacity = tile.On == true ? 1 : .4 };
        var modes = new Grid { Height = 23 };
        modes.ColumnDefinitions.Add(new ColumnDefinition()); modes.ColumnDefinitions.Add(new ColumnDefinition());
        var dots = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 2) };
        foreach (var color in colors.Take(8))
        {
            var dot = new Ellipse { Width = 12, Height = 12, Fill = new SolidColorBrush(Mix(color.Color, Grey, .3)),
                Margin = new Thickness(2, 2, 3, 2), StrokeThickness = 1, ToolTip = color.Name, Cursor = Cursors.Hand };
            tile.Dots.Add((dot, color.Color)); Squish(dot);
            dot.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                tile.Show(tile.Light with { Color = color.Color }, true);
                SetHomeMoodFromTiles();
                SendHome("light", tile.Light.Id, new JsonObject { ["rgb"] = new JsonArray(color.Color.R, color.Color.G, color.Color.B) });
            };
            dots.Children.Add(dot);
        }
        var fold = new Border { ClipToBounds = true, Height = _musicSync == true ? 0 : 23, Child = dots };
        var height = new WeatherTween { Step = h => fold.Height = Math.Max(0, h), Value = fold.Height };
        Border? colorButton = null, musicButton = null;
        bool? shownMusic = null;
        void PaintMode()
        {
            bool music = _musicSync == true;
            if (shownMusic == music) return;
            shownMusic = music;
            colorButton!.Background = music ? Brushes.Transparent : Frozen("#26FFFFFF");
            musicButton!.Background = music ? Frozen("#26FFFFFF") : Brushes.Transparent;
            musicButton.ToolTip = _musicSync == null ? "Musik-Sync ist auf diesem PC nicht eingerichtet" : "Musik";
            musicButton.IsEnabled = _musicSync != null;
            if (panel.IsVisible) Motion.To(height, WeatherTween.ValueProperty, music ? 0 : 23, Motion.Shift);
            else Motion.Stop(height, WeatherTween.ValueProperty, music ? 0 : 23);
            Motion.Stop(dots, OpacityProperty, music ? 0 : 1);
        }
        async void Select(bool music)
        {
            if ((_musicSync == true) == music || _musicSync == null && music) return;
            if (_musicSync != null && !await GoveeSync.SetRunningAsync(music)) return;
            _musicSync = music; PaintMode(); SetDetailText(tile.State, tile.StateText());
            if (_home != null) UpdateHome(_home, force: true);
        }
        colorButton = CompactPill("Farbe", () => Select(false));
        musicButton = CompactPill("Musik", () => Select(true));
        colorButton.HorizontalAlignment = musicButton.HorizontalAlignment = HorizontalAlignment.Stretch;
        Grid.SetColumn(musicButton, 1);
        modes.Children.Add(colorButton); modes.Children.Add(musicButton);
        panel.Children.Add(new Border { CornerRadius = new CornerRadius(9), Background = CompactTrack, Child = modes });
        var brightness = CompactText($"{tile.Light.Brightness ?? 100} %", 10.5, CompactLabel);
        brightness.HorizontalAlignment = HorizontalAlignment.Right;
        brightness.Margin = new Thickness(0, 5, 0, 0);
        panel.Children.Add(brightness);
        tile.BrightnessLabel = brightness;
        var bar = ThinBar((tile.Light.Brightness ?? 100) / 100d, tile.Ink, _ => { });
        // The current Pi contract exposes brightness as telemetry, but accepts only RGB and on/off for lights.
        bar.IsEnabled = false;
        tile.BrightnessFill = (ScaleTransform)bar.Tag;
        ToolTipService.SetShowOnDisabled(bar, true);
        bar.ToolTip = "Helligkeit: Der Pi-Steuerweg unterstützt bisher nur Farbe und An/Aus";
        panel.Children.Add(bar); panel.Children.Add(fold);
        tile.Controls = panel;
        tile.RefreshStrip = PaintMode;
        panel.IsVisibleChanged += (_, _) => { if (!panel.IsVisible) Motion.Stop(height, WeatherTween.ValueProperty, _musicSync == true ? 0 : 23); };
        PaintMode();
        return panel;
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
        public required RollingNumber CurrentRoll, TargetRoll, HumidityRoll;
        public bool HasHumidity;

        public void Show(HomeClimate climate, bool keepTarget)
        {
            SetDetailText(Status, climate.Mode == "off" ? Loc.Get("home.heatingOff") : climate.IsHeating ? Loc.Get("home.heatingNow") : "");
            if (climate.Current is { } current) CurrentRoll.To(current);
            if (!keepTarget && climate.Target is { } target) TargetRoll.To(target);
            HasHumidity = climate.Humidity != null;
            if (!HasHumidity) SetDetailText(Humidity, "");
            HumidityRoll.To(climate.Humidity ?? 0);
        }
    }

    private static string Degrees(double value) => value.ToString(value % 1 == 0 ? "0" : "0.0", CultureInfo.CurrentCulture) + "°";

    private void BuildClimate(PiHome home)
    {
        if (home.Climate is { } climate) _homeClimateSection.Children.Add(HeatingCard(climate));
        if (home.AirConditioner.Count > 0) _homeClimateSection.Children.Add(AirConditionerCard(home.AirConditioner));
    }

    private FrameworkElement HeatingCard(HomeClimate climate)
    {
        var title = CompactText(Loc.Get("home.heating"), 11, Muted);
        var status = CompactText("", 11, _moodSoft);
        status.Margin = new Thickness(6, 0, 0, 0);
        var head = new StackPanel { Orientation = Orientation.Horizontal, Children = { title, status } };

        var current = CompactText("–", 11, CompactLabel);
        var roll = new RollingNumber(v => current.Text = v.ToString("0.0", CultureInfo.CurrentCulture) + "°", current);
        var target = CompactText("", 26, CompactInk);
        target.MinWidth = 62;
        target.TextAlignment = TextAlignment.Center;
        target.RenderTransformOrigin = new Point(0.5, 0.5);
        var targetRoll = new RollingNumber(value => target.Text = Degrees(value), target);
        var stepper = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { StepButton("−", -0.5), target, StepButton("+", 0.5) }
        };
        stepper.Margin = new Thickness(0, 6, 0, 5);
        var humidity = CompactText("", 11, CompactLabel);
        var room = new WrapPanel { Children =
        {
            CompactText("Im Raum ", 11, CompactLabel), current,
            CompactText(", ", 11, CompactLabel), humidity,
            CompactText(climate.WindowOpen ? ", Fenster offen" : ", Fenster zu", 11, CompactLabel)
        } };
        var card = new Border { Background = Brushes.Transparent, Tag = "drawer-row", Padding = new Thickness(0, 0, 0, 10),
            Child = new StackPanel { Children = { head, stepper, room } } };

        HeatingParts? parts = null;
        var humidityRoll = new RollingNumber(value =>
        {
            if (parts?.HasHumidity == true) SetRollingDetailText(humidity, Loc.Get("home.humidity", Math.Round(value)));
        }, humidity);
        parts = new HeatingParts { Status = status, Current = current, Target = target, Humidity = humidity, CurrentRoll = roll, TargetRoll = targetRoll, HumidityRoll = humidityRoll };
        _heating = parts;
        _heating.Show(climate, keepTarget: false);
        return card;

        Border StepButton(string sign, double step)
        {
            var label = CompactText(sign, 15, VNotch.Services.UiPalette.PrimaryBrush);
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
                double next = Math.Clamp(Math.Round((from + step) * 2) / 2, now.Min, now.Max);
                _targetDraft = next;
                targetRoll.To(next);
                DetailReaction(target);
                // Several taps in a row send one change.
                _targetSend ??= new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(TargetSendDelayMs) };
                _targetSend.Stop();
                _targetSend.Tick -= SendTarget;
                _targetSend.Tick += SendTarget;
                _targetSend.Start();
            };
            Squish(button);
            DetailHover(button, label, y: step > 0 ? -2 : 2);
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
        var title = CompactText(Loc.Get("home.ac"), 11, Muted);
        var chips = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var button in buttons)
        {
            bool power = button.Icon == "power";
            var label = new StackPanel { Orientation = Orientation.Horizontal };
            if (power)
                label.Children.Add(new TextBlock { Text = Glyph(0xE7E8), FontFamily = SegoeSymbolFont, FontSize = 11, Foreground = _moodInk, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 6, 0) });
            label.Children.Add(CompactText(button.Name, 11.5, power ? _moodInk : VNotch.Services.UiPalette.PrimaryBrush));
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
        var card = new Border { Background = Brushes.Transparent, Tag = "drawer-row" };
        card.Padding = new Thickness(0, 0, 0, 3);
        card.Child = new StackPanel { Children = { title, chips } };
        return card;

        void Flash(Border chip)
        {
            if (chip.Background is not SolidColorBrush { IsFrozen: false } fill) return;
            var rest = fill.Color;
            SetOrTint(fill, Alpha(_mood.Color, 0.55), 0);
            WeatherMotion.Tint(fill, rest, 220);
        }
    }

    #endregion

    #region Sockets and Pi

    private static string PlugText(HomePlug plug)
    {
        if (!plug.Available) return Loc.Get("control.noAdapter");
        var text = new StringBuilder(Loc.Get(plug.On ? "control.on" : "control.off"));
        if (plug.On && plug.Watts is { } watts) text.Append(" · ").Append(Math.Round(watts)).Append(" W");
        if (plug.TodayKwh is { } kwh && kwh > 0)
            text.Append(" · ").Append(Loc.Get("home.today", kwh.ToString("0.00", CultureInfo.CurrentCulture), (kwh * EuroPerKwh).ToString("0.00", CultureInfo.CurrentCulture)));
        return text.ToString();
    }

    private sealed class PlugReadout
    {
        private readonly TextBlock _text;
        private readonly RollingNumber _wattsRoll, _energyRoll;
        private HomePlug _plug;
        private double _watts, _energy;

        public PlugReadout(TextBlock text, HomePlug plug)
        {
            _text = text;
            _plug = plug;
            _watts = plug.Watts ?? 0;
            _energy = plug.TodayKwh ?? 0;
            _wattsRoll = new RollingNumber(value => { _watts = value; Paint(); }, text);
            _energyRoll = new RollingNumber(value => { _energy = value; Paint(); }, text);
            _wattsRoll.To(_watts, 0);
            _energyRoll.To(_energy, 0);
            text.IsVisibleChanged += (_, _) =>
            {
                if (!text.IsVisible) return;
                _wattsRoll.Reveal();
                _energyRoll.Reveal();
            };
        }

        public void Show(HomePlug plug)
        {
            _plug = plug;
            _wattsRoll.To(plug.Watts ?? 0);
            _energyRoll.To(plug.TodayKwh ?? 0);
        }

        private void Paint()
        {
            _text.Text = PlugText(_plug with
        {
            Watts = _plug.Watts == null ? null : Math.Max(0, _watts),
            TodayKwh = _plug.TodayKwh == null ? null : Math.Max(0, _energy)
            });
            _text.ToolTip = _text.Text;
        }
    }

    private void BuildPlugs(PiHome home)
    {
        foreach (var plug in home.Plugs)
        {
            string id = plug.Id;
            var watts = CompactText(PlugText(plug), 10.5, CompactLabel);
            watts.MaxWidth = 95; watts.ToolTip = PlugText(plug);
            _plugStatus[id] = watts; _plugReadouts[id] = new PlugReadout(watts, plug);
            var action = new StackPanel { Orientation = Orientation.Horizontal };
            if (_guardArmed == id)
            {
                action.Children.Add(CompactText("Wirklich aus?", 10.5, CompactInk));
                action.Children.Add(CompactPill("Aus", () => { _guardArmed = null; SendHome("plug", id, JsonValue.Create(false)); RedrawHome(); }));
                action.Children.Add(CompactPill("Nein", () => { _guardArmed = null; RedrawHome(); }));
            }
            else
            {
                action.Children.Add(watts);
                if (plug.Guard) action.Children.Add(CompactGlyph(0xE72E));
                var toggle = Toggle(plug.On, on =>
                {
                    if (plug.Guard && !on) { _guardArmed = id; RedrawHome(); DisarmLater(id); }
                    else SendHome("plug", id, JsonValue.Create(on));
                });
                toggle.IsEnabled = plug.Available;
                toggle.Margin = new Thickness(7, 0, 0, 0); action.Children.Add(toggle);
            }
            var row = CompactLine(plug.Name == "PC" ? 0xE7F4 : plug.Name == "Pi" ? 0xE950 : 0xE945, plug.Name, action);
            if (!plug.Available) row.Opacity = .4;
            _homePlugSection.Children.Add(row);
        }
    }

    private void RedrawHome() { _homeKey = ""; BuildHome(); ContentHeightChanged?.Invoke(); }

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

    private static string PiText(HomePi pi)
    {
        var parts = new List<string>();
        if (pi.Temp is { } temp) parts.Add(Math.Round(temp) + "°");
        if (pi.UptimeSeconds is { } up) parts.Add(Loc.Get("home.piUp", FormatDuration(TimeSpan.FromSeconds(up))));
        if (pi.Issues.Count == 0) parts.Add(Loc.Get("home.piOk"));
        return string.Join(" · ", parts);
    }

    private sealed class PiReadout
    {
        private readonly TextBlock _text;
        private readonly RollingNumber _temperatureRoll, _uptimeRoll;
        private HomePi _pi;
        private double _temperature, _uptime;

        public PiReadout(TextBlock text, HomePi pi)
        {
            _text = text;
            _pi = pi;
            _temperature = pi.Temp ?? 0;
            _uptime = pi.UptimeSeconds ?? 0;
            _temperatureRoll = new RollingNumber(value => { _temperature = value; Paint(); }, text);
            _uptimeRoll = new RollingNumber(value => { _uptime = value; Paint(); }, text);
            _temperatureRoll.To(_temperature, 0);
            _uptimeRoll.To(_uptime, 0);
            text.IsVisibleChanged += (_, _) =>
            {
                if (text.IsVisible) _temperatureRoll.Reveal();
            };
        }

        public void Show(HomePi pi)
        {
            _pi = pi;
            _temperatureRoll.To(pi.Temp ?? 0);
            _uptimeRoll.To(pi.UptimeSeconds ?? 0);
        }

        private void Paint() => _text.Text = "Pi " + PiText(_pi with
        {
            Temp = _pi.Temp == null ? null : _temperature,
            UptimeSeconds = _pi.UptimeSeconds == null ? null : (long)Math.Max(0, _uptime)
        });
    }

    /// <summary>The Pi in one quiet line; checks that are not green get a row each, with their fix when there is one.</summary>
    private void BuildPiLine(PiHome home)
    {
        if (home.Pi is not { } pi) return;
        _piLine = CompactText("Pi " + PiText(pi), 11, CompactLabel);
        _piLine.TextWrapping = TextWrapping.Wrap;
        _piLine.Tag = "drawer-row";
        _piReadout = new PiReadout(_piLine, pi);
        _homePiSection.Children.Add(_piLine);
        foreach (var issue in pi.Issues)
        {
            FrameworkElement? fix = issue.FixId is { } id ? CompactPill(issue.FixLabel ?? Loc.Get("home.fix"), () => SendHome("fix", id, null)) : null;
            var row = CompactLine(0xE7BA, issue.Title, fix); row.ToolTip = issue.Detail;
            _homePiSection.Children.Add(row);
            var detail = CompactText(issue.Detail, 10.5, CompactLabel); detail.TextWrapping = TextWrapping.Wrap;
            _homePiSection.Children.Add(detail);
        }
    }

    #endregion

    #endregion
}
