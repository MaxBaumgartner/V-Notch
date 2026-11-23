using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using VNotch.Services;
using VNotch.Services.ControlCenter;

namespace VNotch.Controls;

public partial class ControlCenterView
{
    private static readonly Color RoomSand = Color.FromRgb(0xD2, 0xAE, 0x7C);
    private static readonly Color RoomMist = Color.FromRgb(0x7F, 0xA6, 0xCC);
    private static readonly Color RoomLilac = Color.FromRgb(0xA1, 0x9B, 0xD2);
    private static readonly Color RoomRose = Color.FromRgb(0xD2, 0x85, 0x7E);
    private static readonly Color RoomSage = Color.FromRgb(0x9D, 0xBD, 0xB1);
    private static readonly FontFamily HomeFont = new("Segoe UI Variable Text, Segoe UI");
    private static readonly Brush RoomLabel = Frozen("#94FFFFFF");
    private static readonly Brush RoomSub = Frozen("#BAFFFFFF");
    private static readonly Brush RoomLine = Frozen("#1AFFFFFF");
    private static readonly Brush RoomTrack = Frozen("#0FFFFFFF");
    private static readonly Brush RoomBorder = Frozen("#14FFFFFF");
    private const double HomeFaceHeight = 191;
    private const double HomeFaderHeight = 184;
    private const string HomeThermometer = "M10 14 V5 A2 2 0 0 1 14 5 V14 A4.5 4.5 0 1 1 10 14 Z M12 9 V17";
    private const string HomePlugIcon = "M8 3 V8 M16 3 V8 M6 8 H18 V11 A6 6 0 0 1 6 11 Z M12 17 V22";
    private const string HomeMicIcon = "M9 5 A3 3 0 0 1 15 5 V11 A3 3 0 0 1 9 11 Z M5 10 V11 A7 7 0 0 0 19 11 V10 M12 18 V22 M9 22 H15";
    private const string HomeLockIcon = "M6 10 H18 V21 H6 Z M8 10 V7 A4 4 0 0 1 16 7 V10";
    private const string HomePalette = "M13 3 A9 9 0 1 0 13 21 C16 21 13 18 15 16 S21 17 21 12 A9 9 0 0 0 13 3 Z M7 11 H7.2 M10 7 H10.2 M15 7.8 H15.2 M17 11.8 H17.2";
    private const string HomeNote = "M10 17 V6 L19 4 V15 M10 17 A3 2.5 0 1 1 7 14.5 H10 M19 15 A3 2.5 0 1 1 16 12.5 H19";

    private Grid? _homeFace;
    private string _homeFaceKey = "";
    private Canvas? _homeLightLayer;
    private readonly List<Action<PiHome>> _homeFaceUpdates = new();
    private readonly List<Action> _homeLightPlacements = new();
    private readonly HashSet<string> _homeFaceValues = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double?> _homeFaceLastSeen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _homeSwitchDrafts = new(StringComparer.Ordinal);
    private bool _placeHomeLightsQueued;
    private FrameworkElement? _homeDrawerThermostat;
    private StackPanel? _homeDetailsLabel;
    private Border? _homeDetailsButton;
    private double _homeHeatStrength;
    private Ellipse? _homeHeatingGlow, _homeDrawerHeatingGlow;
    private double _homeReveal;
    private Action? _retireHomeHeatingMotion;
    private Action? _paintHomePeak;
    private readonly List<Ellipse> _homeLampGlows = new();

    private bool _homeRendering;
    private TimeSpan? _homeFrame;
    private double _homeHeatPhase, _homeAudioLevel, _homeAudioTarget;
    private TimeSpan _homePeakAt, _homeAudioRefreshAt;
    private bool _homeOpeningPeaks;
    private MediaAudioSessions? _homeAudioSessions;
    private bool _homeStripHasMeter;

    private sealed record BrightnessDraft(string Id, int Level)
    {
        public bool CanConfirm { get; set; }
    }

    private BrightnessDraft? _homeBrightnessDraft;
    private Task _homeBrightnessSend = Task.CompletedTask;
    private bool _homeModeSending;

    private void UpdateHomeFace(PiHome home)
    {
        var plugs = home.Plugs.Where(p => !p.Guard).Take(3).ToArray();
        string key = string.Join("|", home.Lights.Select(l => $"{l.Id}:{l.Name}:{l.Rgb}")) + "/" +
            string.Join("|", plugs.Select(p => $"{p.Id}:{p.Name}")) + "/" +
            string.Join("|", home.Scenes.Select(s => $"{s.Id}:{s.Name}:{s.Icon}")) + "/" +
            string.Join("|", home.StripColors.Select(c => $"{c.Name}:{c.Color}")) + "/" +
            string.Join("|", home.Readings?.Select(r => r.Id) ?? []) +
            $"/{home.Climate != null}/{Loc.CurrentLanguage}";
        if (_homeFace == null || _homeFaceKey != key)
        {
            var previous = _homeFaceValues.ToArray();
            ClearHomeFace();
            BuildHomeFace(home, plugs);
            foreach (string removed in previous)
            {
                if (_homeFaceValues.Contains(removed)) continue;
                Seen.Forget(removed);
                _homeFaceLastSeen.Remove(removed);
            }
            _homeFaceKey = key;
        }
        foreach (var update in _homeFaceUpdates) update(home);
    }

    private void ClearHomeFace()
    {
        _retireHomeHeatingMotion?.Invoke();
        _retireHomeHeatingMotion = null;
        foreach (var placeholder in _homeLightSection.Children.OfType<SkeletonPlaceholder>()) placeholder.Dispose();
        _homeLightSection.Children.Clear();
        _homeLightSection.Height = double.NaN;
        _homeLightSection.Visibility = Visibility.Visible;
        _homeFace = null;
        _homeFaceUpdates.Clear();
        _homeFaceValues.Clear();
        _homeLightPlacements.Clear();
        _homeLampGlows.Clear();
        _homeLightLayer?.Children.Clear();
        _homeHeatingGlow = _homeDrawerHeatingGlow = null;
        _homeHeatStrength = 0;
        _homeStripHasMeter = false;
        _paintHomePeak = null;
        UpdateHomeRendering();
    }

    private void BuildHomeFace(PiHome home, HomePlug[] plugs)
    {
        var face = _homeFace = new Grid { Name = "HomeFace", Height = HomeFaceHeight };
        var strip = home.Lights.FirstOrDefault(l => l.Rgb);
        var lamps = home.Lights.Where(l => !l.Rgb).ToArray();
        if (strip != null)
        {
            face.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
            face.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            face.ColumnDefinitions.Add(new ColumnDefinition());
            face.Children.Add(BuildHomeStrip(strip, home.StripColors));
        }

        var bank = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var devices = new Grid();
        for (int i = 0; i < 6; i++) devices.ColumnDefinitions.Add(new ColumnDefinition());
        if (lamps.Length > 0) devices.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        if (plugs.Length > 0) devices.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < lamps.Length; i++)
        {
            int row = i / 2;
            if (row > 0 && row == devices.RowDefinitions.Count)
                devices.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            PlaceDevice(BuildHomeDevice(lamps[i].Id, lamps[i].Name, true, i == 0), row, (i % 2) * 3, 3);
        }
        for (int i = 0; i < plugs.Length; i++)
            PlaceDevice(BuildHomeDevice(plugs[i].Id, plugs[i].Name, false), (lamps.Length + 1) / 2, i * 2, 2);
        bank.Children.Add(devices);
        if (BuildHomeReadings(home) is { } readings) bank.Children.Add(readings);

        var footer = new StackPanel { Margin = new Thickness(0, 7, 0, 0) };
        if (home.Climate != null)
        {
            var thermostat = BuildHomeThermostat();
            thermostat.HorizontalAlignment = HorizontalAlignment.Left;
            thermostat.Margin = new Thickness(0, 0, 0, 9);
            footer.Children.Add(thermostat);
        }
        var scenes = new WrapPanel();
        foreach (var scene in home.Scenes) scenes.Children.Add(BuildHomeScene(scene));
        footer.Children.Add(scenes);
        if (home.Climate != null || home.Scenes.Count > 0)
            bank.Children.Add(new Border
            {
                Margin = new Thickness(0, 10, 0, 0), BorderThickness = new Thickness(0, 1, 0, 0),
                BorderBrush = RoomLine, Child = footer
            });
        var separator = new Border
        {
            BorderBrush = RoomLine, BorderThickness = strip != null ? new Thickness(1, 0, 0, 0) : new Thickness(),
            Padding = strip != null ? new Thickness(17, 0, 0, 0) : new Thickness(), Child = bank
        };
        if (strip != null) Grid.SetColumn(separator, 2);
        face.Children.Add(separator);
        face.SizeChanged += (_, _) => QueueHomeLightPlacement();
        face.IsVisibleChanged += (_, _) =>
        {
            if (face.IsVisible)
            {
                if (_home is { } data)
                    foreach (var update in _homeFaceUpdates) update(data);
                QueueHomeLightPlacement();
            }
            UpdateHomeRendering();
        };
        _homeLightSection.Children.Add(face);
        RevealHomeDetails(_homeReveal);
        QueueHomeLightPlacement();

        void PlaceDevice(FrameworkElement item, int row, int column, int span)
        {
            Grid.SetRow(item, row);
            Grid.SetColumn(item, column);
            Grid.SetColumnSpan(item, span);
            devices.Children.Add(item);
        }
    }

    private void RevealHomeDetails(double progress)
    {
        if (_homeFace == null) return;
        // Most of the face stays in view as details emerge below it. It clears the drawer at full exposure.
        double height = HomeFaceHeight * (1 - progress * progress * progress);
        _homeLightSection.ClipToBounds = true;
        _homeLightSection.Height = height;
        _homeLightSection.Visibility = height > .05 ? Visibility.Visible : Visibility.Collapsed;
        _homeLightSection.IsHitTestVisible = progress < .05;
        bool changed = (_homeReveal >= .5) != (progress >= .5);
        _homeReveal = progress;
        foreach (var glow in _homeLampGlows) glow.Opacity = (double)glow.Tag * (1 - progress);
        PaintHomeHeat();
        if (changed || progress is > 0 and < 1) QueueHomeLightPlacement();
    }

    private FrameworkElement BuildHomeDevice(string id, string name, bool lamp, bool overhead = false)
    {
        string outline = lamp ? overhead
            ? "M8.5 15 C8.5 12.5 5.5 11.9 5.5 8 A6.5 6.5 0 0 1 18.5 8 C18.5 11.9 15.5 12.5 15.5 15 Z M9 18 H15 M10.5 21 H13.5"
            : "M7 4 H17 L20 14 H4 Z M12 14 V20 M8 21 H16"
            : IsHomeMic(id, name) ? HomeMicIcon : HomePlugIcon;
        var icon = HomeOutline(outline, 18);
        var disc = HomeDisc(lamp ? 39 : 37, icon);
        disc.Background = Frozen("#161617");
        var discFrame = new Grid { HorizontalAlignment = HorizontalAlignment.Center, Children = { disc } };
        var dot = new Ellipse
        {
            Width = 6, Height = 6, Fill = Frozen("#101012"), Stroke = Frozen("#97979B"), StrokeThickness = 1,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 1)
        };
        if (!lamp) discFrame.Children.Add(dot);
        var label = HomeText(name, 12, RoomLabel);
        var content = new StackPanel
        {
            Orientation = lamp ? Orientation.Horizontal : Orientation.Vertical,
            HorizontalAlignment = lamp ? HorizontalAlignment.Left : HorizontalAlignment.Center,
            Children = { discFrame, label }
        };
        label.Margin = lamp ? new Thickness(9, 1, 0, 0) : new Thickness(0, 5, 0, 0);
        if (lamp) label.VerticalAlignment = VerticalAlignment.Top;
        var button = new Border
        {
            Background = Brushes.Transparent, Cursor = Cursors.Hand, CornerRadius = new CornerRadius(12),
            Padding = new Thickness(4, 2, 4, 2), Margin = lamp ? new Thickness() : new Thickness(0, 9, 0, 0),
            Child = content, ToolTip = name
        };
        if (lamp && !overhead) button.Margin = new Thickness(4, 0, 0, 0);
        AutomationProperties.SetName(button, name);
        Pressable(button, () => Brushes.Transparent, CardHover);

        Ellipse? light = null;
        if (lamp)
        {
            light = AddHomeLight(RoomSand, .19);
            light.Tag = 0.0;
            _homeLampGlows.Add(light);
            PlaceHomeLight(light, () => HomeCenter(disc), 94, 94);
        }
        var fill = new SolidColorBrush(Color.FromRgb(22, 22, 23));
        disc.Background = fill;
        var show = TrackHomeValue(button, (lamp ? "light:" : "plug:") + id, value =>
        {
            double level = Math.Clamp(value ?? 0, 0, 1);
            fill.Color = Mix(Color.FromRgb(22, 22, 23), Color.FromRgb(36, 36, 38), level);
            if (light != null)
            {
                light.Tag = level;
                light.Opacity = level * (1 - _homeReveal);
                disc.BorderBrush = new SolidColorBrush(Mix(Color.FromArgb(0x14, 255, 255, 255), Alpha(RoomSand, .63), level));
            }
        });
        bool? Reported() => lamp ? _home?.Lights.FirstOrDefault(l => l.Id == id)?.On : _home?.Plugs.FirstOrDefault(p => p.Id == id)?.On;
        _homeFaceUpdates.Add(home =>
        {
            var lightState = lamp ? home.Lights.First(l => l.Id == id) : null;
            var plugState = lamp ? null : home.Plugs.First(p => p.Id == id);
            bool available = lightState?.Available ?? plugState!.Available;
            bool on = available && _homeSwitchDrafts.GetValueOrDefault(id, lightState?.On ?? plugState!.On);
            button.IsEnabled = available;
            button.Opacity = available ? 1 : .4;
            HomeIconPath(icon).Stroke = label.Foreground = on ? CompactInk : RoomLabel;
            dot.Fill = on ? CompactInk : Frozen("#101012");
            dot.Stroke = on ? CompactInk : Frozen("#97979B");
            show(on ? 1 : 0);
        });
        button.MouseLeftButtonUp += async (_, e) =>
        {
            e.Handled = true;
            if (!button.IsEnabled || Reported() is not { } current || !_homeSwitchDrafts.TryAdd(id, !current)) return;
            if (_home is { } data) UpdateHomeFace(data);
            try { await PostHome(lamp ? "light" : "plug", id, JsonValue.Create(!current)); }
            finally
            {
                _homeSwitchDrafts.Remove(id);
                RefreshHomeAfterCommand();
            }
        };
        return button;
    }

    private FrameworkElement BuildHomeStrip(HomeLight light, IReadOnlyList<HomeColor> colors)
    {
        const double width = 43;
        var ink = new LinearGradientBrush(Mix(Color.FromRgb(20, 20, 22), RoomMist, .35), RoomMist,
            new Point(.5, 1), new Point(.5, 0));
        var fill = Capsule(new Border { Background = ink, Height = 0, VerticalAlignment = VerticalAlignment.Bottom }, 19);
        var track = new Grid
        {
            Width = 35, Height = 176, Clip = new RectangleGeometry(new Rect(0, 0, 35, 176), 17.5, 17.5),
            IsHitTestVisible = false, Children = { fill }
        };
        var ticks = new Canvas { Width = 8, Height = 164, Opacity = .42, IsHitTestVisible = false };
        for (int y = 8; y < 164; y += 11)
        {
            var tick = new Rectangle { Width = 8, Height = 1, Fill = Frozen("#59FFFFFF") };
            Canvas.SetTop(tick, y);
            ticks.Children.Add(tick);
        }
        var thumb = new Border
        {
            Width = 19, Height = 5, Background = CompactInk, CornerRadius = new CornerRadius(2.5),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false
        };
        var thumbMove = new TranslateTransform();
        thumb.RenderTransform = thumbMove;
        var fader = new Border
        {
            Name = "HomeGoveeCapsule", Width = width, Height = HomeFaderHeight, CornerRadius = new CornerRadius(width / 2),
            Background = Frozen("#19191B"), BorderBrush = Frozen("#24FFFFFF"), BorderThickness = new Thickness(1),
            Cursor = Cursors.SizeNS, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(1, 5, 0, 0), Child = new Grid { Children = { track, ticks, thumb } }
        };
        fader.ToolTip = Say("Govee-Helligkeit", "Govee brightness");
        AutomationProperties.SetName(fader, (string)fader.ToolTip);

        var group = new Grid { Children = { fader } };
        var number = HomeText("–", 41, CompactInk, 500);
        number.LineHeight = 41;
        number.RenderTransform = new TranslateTransform(0, 4);
        var percentage = HomeText("%", 20.5, RoomSub, 500);
        percentage.RenderTransform = new TranslateTransform(0, 4);
        percentage.Margin = new Thickness(2, 0, 0, 1);
        percentage.VerticalAlignment = number.VerticalAlignment = VerticalAlignment.Bottom;
        var reading = new StackPanel
        {
            Height = 41, Margin = new Thickness(0, 9, 0, 9), Orientation = Orientation.Horizontal,
            Children = { number, percentage }
        };
        var mode = new Grid { Width = 72, Height = 51 };
        mode.RowDefinitions.Add(new RowDefinition());
        mode.RowDefinitions.Add(new RowDefinition());
        var selectionShift = new TranslateTransform();
        var selection = new Border
        {
            Height = 25, CornerRadius = new CornerRadius(11), Background = Frozen("#303033"),
            VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false, RenderTransform = selectionShift
        };
        Grid.SetRowSpan(selection, 2);
        mode.Children.Add(selection);
        var colour = HomeModeOption(Say("Farbe", "Colour"), HomePalette);
        var music = HomeModeOption(Say("Musik", "Music"), HomeNote);
        mode.Children.Add(colour);
        mode.Children.Add(music);
        Grid.SetRow(music, 1);
        var segment = new Border
        {
            CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), BorderBrush = RoomBorder,
            Background = RoomTrack, Padding = new Thickness(2), Child = mode, Margin = new Thickness(0, 0, 8, 0)
        };
        var dotBrush = new SolidColorBrush(RoomMist);
        var hue = HomeDisc(30, new Ellipse { Width = 14, Height = 14, Fill = dotBrush });
        hue.Cursor = Cursors.Hand;
        hue.ToolTip = Say("Farbe wählen", "Choose strip colour");
        var controls = new WrapPanel { Children = { segment, hue } };
        hue.Margin = new Thickness(0, 8, 0, 0);
        var readout = new StackPanel
        {
            Margin = new Thickness(57, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            Children = { HomeText("Govee", 21, CompactInk, 560), reading, controls }
        };
        group.Children.Add(readout);

        var swatches = new StackPanel { Orientation = Orientation.Horizontal };
        var swatchPopup = new Border
        {
            Background = Frozen("#18181A"), BorderBrush = Frozen("#2BFFFFFF"), BorderThickness = new Thickness(1),
            Padding = new Thickness(3), CornerRadius = new CornerRadius(20), Child = swatches, Visibility = Visibility.Collapsed
        };
        var popupLayer = new Canvas { Children = { swatchPopup }, IsHitTestVisible = true };
        Canvas.SetLeft(swatchPopup, 49);
        Canvas.SetBottom(swatchPopup, 0);
        Panel.SetZIndex(popupLayer, 10);
        group.Children.Add(popupLayer);
        Pressable(hue, () => RoomTrack, CardHover);
        hue.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            swatchPopup.Visibility = swatchPopup.IsVisible ? Visibility.Collapsed : Visibility.Visible;
        };
        foreach (var color in colors)
        {
            var swatch = HomeDisc(30, new Ellipse { Width = 14, Height = 14, Fill = new SolidColorBrush(HomeStripColor(color.Color)) });
            swatch.Background = Brushes.Transparent;
            swatch.ToolTip = color.Name;
            swatch.Cursor = Cursors.Hand;
            Pressable(swatch, () => Brushes.Transparent, CardHover);
            swatch.MouseLeftButtonUp += async (_, e) =>
            {
                e.Handled = true;
                swatchPopup.Visibility = Visibility.Collapsed;
                if (_musicSync == true && !await SetHomeMusic(false)) return;
                SendHome("light", light.Id, new JsonObject { ["rgb"] = new JsonArray(color.Color.R, color.Color.G, color.Color.B) });
            };
            swatches.Children.Add(swatch);
        }

        var glowBrush = HomeLightBrush(RoomMist, .68);
        var glow = AddHomeLight(glowBrush);
        double edgeX = 43.5;
        PlaceHomeLight(glow, () =>
        {
            if (fader.IsVisible) edgeX = HomeCenter(fader).X;
            return new Point(edgeX, _homeLightLayer!.ActualHeight - 1);
        }, 336, 36);
        bool dragging = false, meter = false, acceptingGesture = false;
        double dragValue = 0, solidLevel = 0, musicMix = 0;
        double? paintedLevel = null;
        var rubber = new WeatherTween();
        var returnToMeter = new WeatherTween { Value = 1 };
        void Paint()
        {
            double mix = musicMix * Math.Clamp(returnToMeter.Value, 0, 1);
            double level = Math.Clamp(dragging ? dragValue : solidLevel * (1 - mix) + _homeAudioLevel * mix, 0, 1);
            fill.Height = track.Height * level;
            glow.Opacity = level / 1.2;
            double clamped = Math.Clamp(HomeFaderHeight * (1 - level), 12, HomeFaderHeight - 12);
            thumbMove.Y = clamped - 3.5 + rubber.Value;
        }
        rubber.Step = _ => Paint();
        returnToMeter.Step = _ => Paint();
        _paintHomePeak = Paint;
        var showLevel = TrackHomeValue(fader, "light:" + light.Id + ":level", value =>
        {
            solidLevel = value ?? 0;
            if (!dragging) PaintNumber(value.HasValue && paintedLevel.HasValue ? value : paintedLevel);
            Paint();
        }, smooth: true, canAnimate: () => !acceptingGesture);
        var showMode = TrackHomeValue(fader, "light:" + light.Id + ":music", value =>
        {
            selectionShift.Y = 26 * (value ?? 0);
        }, smooth: true);
        var showMeter = TrackHomeValue(fader, "light:" + light.Id + ":meter", value =>
        {
            musicMix = Math.Clamp(value ?? 0, 0, 1);
            Paint();
            _homeStripHasMeter = meter || musicMix > .001;
            UpdateHomeRendering();
        }, smooth: true);

        void ReadGesture(MouseEventArgs e)
        {
            double raw = 1 - e.GetPosition(fader).Y / HomeFaderHeight;
            dragValue = Math.Clamp(raw, 0, 1);
            double overflow = (dragValue - raw) * HomeFaderHeight;
            Motion.Stop(rubber, WeatherTween.ValueProperty, Math.Sign(overflow) * (1 - 1 / (1 + Math.Abs(overflow) * .55 / 18)) * 18);
            number.Text = Math.Round(dragValue * 100).ToString(CultureInfo.CurrentCulture);
            percentage.Visibility = Visibility.Visible;
            Paint();
        }
        fader.MouseLeftButtonDown += (_, e) =>
        {
            if (!fader.CaptureMouse()) return;
            swatchPopup.Visibility = Visibility.Collapsed;
            dragging = true;
            ReadGesture(e);
            e.Handled = true;
        };
        fader.MouseMove += (_, e) => { if (dragging) { ReadGesture(e); e.Handled = true; } };
        fader.MouseLeftButtonUp += (_, e) =>
        {
            if (!dragging) return;
            ReadGesture(e);
            dragging = false;
            fader.ReleaseMouseCapture();
            int percent = (int)Math.Round(dragValue * 100);
            var draft = _homeBrightnessDraft = new BrightnessDraft(light.Id, percent);
            paintedLevel = percent / 100.0;
            acceptingGesture = true;
            if (musicMix > .001)
                Motion.To(returnToMeter, WeatherTween.ValueProperty, 1, Motion.Shift, from: 0);
            showLevel(percent / 100.0);
            acceptingGesture = false;
            _homeBrightnessSend = SendHomeBrightnessAsync(_homeBrightnessSend, draft);
            Motion.To(rubber, WeatherTween.ValueProperty, 0, Motion.Shift);
            e.Handled = true;
        };
        fader.LostMouseCapture += (_, _) =>
        {
            if (!dragging) return;
            dragging = false;
            Motion.Stop(rubber, WeatherTween.ValueProperty, 0);
            PaintNumber(paintedLevel);
            Paint();
        };
        fader.IsVisibleChanged += (_, _) =>
        {
            if (!fader.IsVisible)
            {
                if (fader.IsMouseCaptured) fader.ReleaseMouseCapture();
                swatchPopup.Visibility = Visibility.Collapsed;
                Motion.Stop(rubber, WeatherTween.ValueProperty, 0);
                Motion.Stop(returnToMeter, WeatherTween.ValueProperty, 1);
            }
            Paint();
            UpdateHomeRendering();
        };
        colour.MouseLeftButtonUp += async (_, e) => { e.Handled = true; await SetHomeMusic(false); };
        music.MouseLeftButtonUp += async (_, e) => { e.Handled = true; await SetHomeMusic(true); };
        Color last = RoomMist, from = RoomMist, paintedColor = RoomMist;
        string colourKey = "home.face.light:" + light.Id + ":colour";
        _homeFaceValues.Add(colourKey);
        var tint = new WeatherTween { Step = value => PaintColour(Mix(from, last, Math.Clamp(value, 0, 1))) };
        fader.IsVisibleChanged += (_, _) => { if (!fader.IsVisible) Motion.Stop(tint, WeatherTween.ValueProperty, 1); };
        _homeFaceUpdates.Add(data =>
        {
            var state = data.Lights.First(l => l.Id == light.Id);
            int? level = state switch
            {
                { Available: false } => null,
                { On: false } => 0,
                { Brightness: { } percent } => Math.Clamp(percent, 0, 100),
                _ => null
            };
            if (_homeBrightnessDraft is { } draft && draft.Id == state.Id)
            {
                if (draft.CanConfirm && level == draft.Level) _homeBrightnessDraft = null;
                else level = draft.Level;
            }
            fader.IsEnabled = state.Available;
            fader.Opacity = state.Available ? 1 : .4;
            bool musicOn = _musicSync == true;
            music.IsEnabled = state.Available && _musicSync.HasValue;
            hue.IsEnabled = state.Available && colors.Count > 0;
            colour.IsEnabled = state.Available;
            ToolTipService.SetShowOnDisabled(music, true);
            music.ToolTip = _musicSync.HasValue ? null : Say("Musik-Sync ist auf diesem PC nicht eingerichtet", "Music sync is unavailable on this PC");

            var shown = AsShown(state);
            if (shown.Color is { } rgb)
            {
                Color muted = HomeStripColor(rgb);
                bool changed = HomeValuesVisible(drawer: false) && Seen.Changed(colourKey, muted.ToString());
                if (last != muted)
                {
                    from = paintedColor;
                    last = muted;
                    if (changed && !AnimationConfig.ReduceMotion) Motion.To(tint, WeatherTween.ValueProperty, 1, Motion.Shift, from: 0);
                    else { Motion.Stop(tint, WeatherTween.ValueProperty, 1); PaintColour(last); }
                }
            }
            meter = musicOn && state.Available && level > 0;
            showMode(musicOn ? 1 : 0);
            showMeter(meter ? 1 : 0);
            ((TextBlock)((StackPanel)colour.Child).Children[1]).Foreground = musicOn ? RoomLabel : CompactInk;
            ((TextBlock)((StackPanel)music.Child).Children[1]).Foreground = musicOn ? CompactInk : RoomLabel;
            paintedLevel = level / 100.0;
            if (!dragging)
            {
                showLevel(paintedLevel);
            }
        });
        return group;

        void PaintColour(Color color)
        {
            paintedColor = color;
            dotBrush.Color = color;
            ink.GradientStops[0].Color = Mix(Color.FromRgb(20, 20, 22), color, .35);
            ink.GradientStops[1].Color = color;
            PaintHomeLight(glowBrush, color, .68);
        }

        void PaintNumber(double? level)
        {
            number.Text = level is { } shown ? Math.Round(shown * 100).ToString(CultureInfo.CurrentCulture) : "–";
            percentage.Visibility = level.HasValue ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private Border HomeModeOption(string text, string outline)
    {
        var icon = HomeOutline(outline, 14);
        HomeIconPath(icon).Stroke = RoomLabel;
        icon.Margin = new Thickness(0, 0, 5, 0);
        var option = new Border
        {
            Background = Brushes.Transparent, CornerRadius = new CornerRadius(12), Padding = new Thickness(9, 0, 5, 0),
            Cursor = Cursors.Hand, Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, HomeText(text, 12, RoomLabel) } }
        };
        Pressable(option, () => Brushes.Transparent, CardHover);
        return option;
    }

    private async Task<bool> SetHomeMusic(bool music)
    {
        if (_musicSync == music || _musicSync == null) return !music || _musicSync == true;
        if (_homeModeSending) return false;
        _homeModeSending = true;
        try
        {
            if (!await GoveeSync.SetRunningAsync(music) || _disposed) return false;
            _musicSync = music;
            if (_home is { } home) UpdateHomeFace(home);
            return true;
        }
        finally { _homeModeSending = false; }
    }

    private async Task SendHomeBrightnessAsync(Task previous, BrightnessDraft draft)
    {
        await previous;
        if (_pi == null || _disposed) return;
        var result = await _pi.SendHomeAsync("light", draft.Id, new JsonObject { ["brightness_pct"] = draft.Level });
        if (_disposed || !ReferenceEquals(_homeBrightnessDraft, draft)) return;
        if (result == HomeCommandResult.Rejected)
        {
            _homeBrightnessDraft = null;
            if (_home is { } data) UpdateHomeFace(data);
        }
        else
        {
            draft.CanConfirm = true;
        }
        RefreshHomeAfterCommand();
    }

    private FrameworkElement BuildHomeThermostat()
    {
        var icon = HomeOutline(HomeThermometer, 18);
        HomeIconPath(icon).Stroke = RoomLabel;
        var number = HomeText("–", 18, CompactInk, 540);
        number.Margin = new Thickness(5, 0, 0, 0);
        var thermostat = new StackPanel
        {
            Name = "HomeThermostat", Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
            Children = { icon, number }, ToolTip = Say("Raumtemperatur", "Room temperature")
        };
        var glow = _homeHeatingGlow = AddHomeLight(RoomRose, .53);
        var drawerGlow = _homeDrawerHeatingGlow = AddHomeLight(RoomRose, .53);
        PlaceHomeLight(glow, () => HomeCenter(thermostat), 190, 124);
        PlaceHomeLight(drawerGlow, () => _homeDrawerThermostat is { } source ? HomeCenter(source) : new Point(), 190, 124);
        var showHeat = TrackHomeValue(this, "heating", value =>
        {
            _homeHeatStrength = Math.Clamp(value ?? 0, 0, 1);
            PaintHomeHeat();
            UpdateHomeRendering();
        }, smooth: true);
        var showCurrent = TrackHomeValue(thermostat, "temperature:room", value => number.Text = HomeTemperature(value), smooth: true);
        _homeFaceUpdates.Add(data =>
        {
            var climate = data.Climate!;
            showCurrent(climate.Current);
            showHeat(climate.IsHeating && climate.Target is { } target && climate.Current is { } current
                ? Math.Clamp(target - current, 0, 3) / 3 : 0);
        });
        return thermostat;
    }

    private void PaintHomeHeat()
    {
        if (_homeHeatingGlow == null) return;
        double wave = AnimationConfig.ReduceMotion ? 1 : .93 + .07 * Math.Cos(_homeHeatPhase);
        _homeHeatingGlow.Opacity = _homeHeatStrength * wave * (1 - _homeReveal);
        if (_homeDrawerHeatingGlow != null)
            _homeDrawerHeatingGlow.Opacity = _homeHeatStrength * wave * _homeReveal;
    }

    private void UpdateHomeRendering()
    {
        bool audio = !_disposed && _homeFace?.IsVisible == true && _homeStripHasMeter && !AnimationConfig.ReduceMotion;
        bool want = audio || !_disposed && IsVisible && _homeHeatStrength > .001 && !AnimationConfig.ReduceMotion;
        if (!audio)
        {
            _homeAudioSessions?.Dispose();
            _homeAudioSessions = null;
            _homeAudioLevel = _homeAudioTarget = 0;
            _homeAudioRefreshAt = _homePeakAt = TimeSpan.Zero;
        }
        if (want == _homeRendering) return;
        _homeRendering = want;
        if (want) CompositionTarget.Rendering += RenderHome;
        else
        {
            CompositionTarget.Rendering -= RenderHome;
            _homeFrame = null;
        }
    }

    private void RenderHome(object? sender, EventArgs e)
    {
        if (e is not RenderingEventArgs frame || frame.RenderingTime == _homeFrame) return;
        double seconds = _homeFrame is { } previous ? Math.Min(.05, (frame.RenderingTime - previous).TotalSeconds) : 0;
        _homeFrame = frame.RenderingTime;
        if (AnimationConfig.ReduceMotion) { UpdateHomeRendering(); PaintHomeHeat(); _paintHomePeak?.Invoke(); return; }
        if (_homeHeatStrength > 0)
        {
            _homeHeatPhase = (_homeHeatPhase + seconds * 2 * Math.PI / Motion.HomeHeatCycle.TotalSeconds) % (2 * Math.PI);
            PaintHomeHeat();
        }
        if (_homeFace?.IsVisible != true || !_homeStripHasMeter) return;
        if (frame.RenderingTime >= _homeAudioRefreshAt)
        {
            _homeAudioRefreshAt = frame.RenderingTime + Motion.HomePeakRefresh;
            OpenHomePeakSessions();
        }
        if (frame.RenderingTime >= _homePeakAt)
        {
            _homePeakAt = frame.RenderingTime + Motion.HomePeakSample;
            float peak = 0;
            if (_homeAudioSessions is { } sessions)
                foreach (var session in sessions.Sessions) peak = Math.Max(peak, session.ReadPeak());
            _homeAudioTarget = Math.Sqrt(Math.Clamp(peak, 0, 1));
        }
        // The mixer has a fast attack and a slower release. Apply the same envelope at any refresh rate.
        double retention = _homeAudioTarget > _homeAudioLevel ? .40 : .82;
        _homeAudioLevel += (_homeAudioTarget - _homeAudioLevel) * (1 - Math.Pow(retention, seconds / Motion.HomePeakSample.TotalSeconds));
        _paintHomePeak?.Invoke();
    }

    private async void OpenHomePeakSessions()
    {
        if (_homeOpeningPeaks) return;
        _homeOpeningPeaks = true;
        try
        {
            var sessions = await Task.Run(WindowsMediaAudioSessions.Read);
            if (_disposed || _homeFace?.IsVisible != true || !_homeStripHasMeter || AnimationConfig.ReduceMotion)
            {
                sessions.Dispose();
                return;
            }
            _homeAudioSessions?.Dispose();
            _homeAudioSessions = sessions;
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("GOVEE", $"Peak meters not read: {ex.GetType().Name}: {ex.Message}");
        }
        finally { _homeOpeningPeaks = false; }
    }

    private Border BuildHomeScene(HomeAction scene)
    {
        string outline = scene.Icon switch
        {
            "moon" => "M19 16 A8 8 0 0 1 8 5 A8.5 8.5 0 1 0 19 16 Z",
            "game" => "M7 7 H17 C20 7 22 15 21 17 S17 17 15 15 H9 C7 17 4 19 3 17 S4 7 7 7 Z M6 11 H10 M8 9 V13 M15 10 H15.1 M17 12 H17.1",
            "book" => "M5 4 H19 V20 H6 A2 2 0 0 1 4 18 V6 A2 2 0 0 1 5 4 Z M5 16 H19",
            "wind" => "M2 8 H15 A3 3 0 1 0 12 5 M3 12 H19 A2 2 0 1 1 17 14 M4 16 H11 A3 3 0 1 1 8 19",
            _ => "M12 3 V7 M12 17 V21 M3 12 H7 M17 12 H21"
        };
        var icon = HomeOutline(outline, 14);
        icon.Margin = new Thickness(0, 0, 5, 0);
        var pill = HomeChip(new StackPanel
        {
            Orientation = Orientation.Horizontal, Children = { icon, HomeText(scene.Name, 11, CompactInk) }
        }, new Thickness(7, 4, 7, 4));
        pill.Margin = new Thickness(0, 0, 5, 0);
        AutomationProperties.SetName(pill, scene.Name);
        bool pending = false;
        pill.MouseLeftButtonUp += async (_, e) =>
        {
            e.Handled = true;
            if (pending) return;
            pending = true;
            try { await PostHome("scene", scene.Id, null); }
            finally { pending = false; RefreshHomeAfterCommand(); }
        };
        return pill;
    }

    private static bool IsHomeMic(string id, string name) => id == "switch.mikrofon" ||
        name.Contains("Mikrofon", StringComparison.OrdinalIgnoreCase) || name.Contains("Microphone", StringComparison.OrdinalIgnoreCase);

    private static CultureInfo HomeCulture => CultureInfo.GetCultureInfo(Loc.CurrentLanguage);

    private static string HomeTemperature(double? value) =>
        value is { } degrees ? degrees.ToString("0.0", HomeCulture) + "°" : "–";

    private static Color HomeStripColor(Color color)
    {
        if (color == RoomMist || color == RoomLilac || color == RoomSand || color == RoomRose || color == RoomSage)
            return color;
        if (color.R == color.G && color.G == color.B) return RoomLilac;
        if (color.G >= color.R && color.G >= color.B) return RoomSage;
        if (color.B > color.R) return color.R > color.G ? RoomLilac : RoomMist;
        if (color.B > color.G && color.R - color.B < color.B - color.G) return RoomLilac;
        return color.G - color.B > (color.R - color.B) / 4 ? RoomSand : RoomRose;
    }

    private TextBlock HomeText(string text, double size, Brush foreground, int weight = 400)
    {
        var label = CompactText(text, size, foreground);
        label.FontFamily = HomeFont;
        label.FontWeight = FontWeight.FromOpenTypeWeight(weight);
        label.LineHeight = size * 1.2;
        label.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        if (size >= 18)
            label.LayoutTransform = new ScaleTransform(size >= 27 ? .87 : .94, 1);
        Typography.SetNumeralAlignment(label, FontNumeralAlignment.Tabular);
        return label;
    }

    private static Border HomeDisc(double size, UIElement child) => new()
    {
        Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Background = RoomTrack, Child = child,
        BorderThickness = new Thickness(1), BorderBrush = RoomBorder,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
    };

    /// <summary>
    /// WPF draws a radius above half a side as an ellipse quarter, so a 24 px chip with radius 24 came out oval. CSS,
    /// where these designs come from, shrinks it to a capsule instead: a border sized by its content keeps at most
    /// half its shorter side.
    /// </summary>
    private static Border Capsule(Border border, double radius)
    {
        border.CornerRadius = new CornerRadius(radius);
        border.SizeChanged += (_, _) =>
            border.CornerRadius = new CornerRadius(Math.Min(radius, Math.Min(border.ActualWidth, border.ActualHeight) / 2));
        return border;
    }

    private Border HomeChip(UIElement content, Thickness padding)
    {
        var chip = Capsule(new Border
        {
            Padding = padding, BorderThickness = new Thickness(1), BorderBrush = RoomBorder,
            Background = RoomTrack, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Child = content
        }, 24);
        Pressable(chip, () => RoomTrack, CardHover);
        return chip;
    }

    private static Viewbox HomeOutline(string geometry, double size) => new Viewbox
    {
        Width = size, Height = size, Stretch = Stretch.Uniform,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        Child = new Canvas { Width = 24, Height = 24, Children =
        {
            new Path
            {
                Data = Geometry.Parse(geometry), Stroke = CompactInk, StrokeThickness = 1.6,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round
            }
        } }
    };

    private static Path HomeIconPath(Viewbox icon) => (Path)((Canvas)icon.Child).Children[0];

    private bool HomeValuesVisible(bool drawer) =>
        IsVisible && (drawer ? _drawerBody.Visibility : _homeLightSection.Visibility) == Visibility.Visible;

    private Action<double?> TrackHomeValue(FrameworkElement owner, string key, Action<double?> paint, bool smooth = false,
        bool drawer = false, Func<bool>? canAnimate = null)
    {
        key = (drawer ? "home.details." : "home.face.") + key;
        (drawer ? _homeDrawerValues : _homeFaceValues).Add(key);
        double? target = null;
        bool initialized = false;
        var tween = new WeatherTween { Step = value => paint(value) };
        DependencyPropertyChangedEventHandler stopWhenHidden = (_, _) =>
        {
            if (!owner.IsVisible && target is { } rest) Motion.Stop(tween, WeatherTween.ValueProperty, rest);
        };
        owner.IsVisibleChanged += stopWhenHidden;
        if (ReferenceEquals(owner, this) && key == "home.face.heating")
            _retireHomeHeatingMotion = () =>
            {
                owner.IsVisibleChanged -= stopWhenHidden;
                Motion.Stop(tween, WeatherTween.ValueProperty, 0);
            };
        return value =>
        {
            // The tab's IsVisibleChanged refresh can precede WPF's propagation of IsVisible to its children.
            bool showing = ReferenceEquals(owner, this) ? IsVisible : HomeValuesVisible(drawer);
            bool changed = showing && (value is { } number ? Seen.Changed(key, number, "0.###") : Seen.Changed(key, ""));
            if (changed && !AnimationConfig.ReduceMotion && canAnimate?.Invoke() != false &&
                _homeFaceLastSeen.GetValueOrDefault(key) is { } before && value is { } next)
            {
                double? from = !initialized || target == value ? before : null;
                Motion.To(tween, WeatherTween.ValueProperty, next, smooth || next > before ? Motion.Shift : Motion.Exit, from: from);
            }
            else if (!initialized || target != value || !showing || AnimationConfig.ReduceMotion)
            {
                Motion.Stop(tween, WeatherTween.ValueProperty, value ?? 0);
                paint(value);
            }
            initialized = true;
            target = value;
            if (showing) _homeFaceLastSeen[key] = value;
        };
    }

    private Point HomeCenter(FrameworkElement source) =>
        source.TranslatePoint(new Point(source.ActualWidth / 2, source.ActualHeight / 2), _homeLightLayer);

    private Ellipse AddHomeLight(Color color, double strength) => AddHomeLight(HomeLightBrush(color, strength));

    private static Ellipse AddHomeLight(Brush brush) => new() { Fill = brush, Opacity = 0 };

    private static RadialGradientBrush HomeLightBrush(Color color, double strength)
    {
        var brush = new RadialGradientBrush { GradientStops = { new GradientStop(), new GradientStop(Colors.Transparent, .48), new GradientStop(Colors.Transparent, 1) } };
        PaintHomeLight(brush, color, strength);
        return brush;
    }

    private static void PaintHomeLight(RadialGradientBrush brush, Color color, double strength)
    {
        brush.GradientStops[0].Color = Alpha(color, strength);
        brush.GradientStops[1].Color = Alpha(color, strength * .39);
        brush.GradientStops[2].Color = Alpha(color, 0);
    }

    private void PlaceHomeLight(Ellipse light, Func<Point> locate, double width, double height)
    {
        light.Width = width;
        light.Height = height;
        _homeLightPlacements.Add(() =>
        {
            var center = locate();
            Canvas.SetLeft(light, center.X - width / 2);
            Canvas.SetTop(light, center.Y - height / 2);
            if (light.Parent == null) _homeLightLayer!.Children.Add(light);
        });
    }

    private void QueueHomeLightPlacement()
    {
        if (_placeHomeLightsQueued) return;
        _placeHomeLightsQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _placeHomeLightsQueued = false;
            if (_disposed || !IsVisible || _backdrop is not { IsVisible: true } backdrop) return;
            if (_homeLightLayer == null)
            {
                _homeLightLayer = backdrop.UseRoomLights();
                _homeLightLayer.SizeChanged += (_, _) => QueueHomeLightPlacement();
            }
            foreach (var place in _homeLightPlacements) place();
        });
    }
}
