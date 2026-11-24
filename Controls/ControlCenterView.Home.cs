using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using VNotch.Services;
using VNotch.Services.ControlCenter;

namespace VNotch.Controls;

public partial class ControlCenterView
{
    private const double EuroPerKwh = 0.25;
    private static readonly TimeSpan GuardWindow = TimeSpan.FromSeconds(3);
    private const int TargetSendDelayMs = 700;
    private const int HomeSettleMs = 1400;

    private static (Color Main, Color Warmer, Color Cooler) HomeNight => (RoomMist, RoomLilac, RoomMist);
    private static (Color Main, Color Warmer, Color Cooler) HomeLamp => (RoomSand, RoomSand, RoomMist);

    private PiHome? _home;
    private string _homeKey = "";
    private readonly StackPanel _homeLightSection = new();
    private readonly StackPanel _homeClimateSection = new();
    private readonly StackPanel _homePlugSection = new();
    private readonly StackPanel _homePiSection = new();
    private readonly List<Action<PiHome>> _homeDrawerUpdates = new();
    private readonly HashSet<string> _homeDrawerValues = new(StringComparer.Ordinal);
    private bool? _musicSync;
    private bool _homeRefreshQueued;
    private string? _guardArmed;
    private double? _targetDraft;
    private bool _targetSending;
    private DispatcherTimer? _targetSend;

    private static string Glyph(int code) => ((char)code).ToString();


    private void AttachHome()
    {
        MountCompactPage();
        _compactPage.Margin = new Thickness(5, 6, 5, 0);
        _compactPage.Children.Add(_homeLightSection);
        _homeDetailsLabel = new StackPanel { Orientation = Orientation.Horizontal };
        _homeDetailsButton = new Border
        {
            Height = 26, Background = Brushes.Transparent, CornerRadius = new CornerRadius(13),
            Child = _homeDetailsLabel, Cursor = System.Windows.Input.Cursors.Hand, Padding = new Thickness(8, 0, 8, 0),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -26, 24, 0), Visibility = Visibility.Collapsed
        };
        Pressable(_homeDetailsButton, () => Brushes.Transparent, CardHover);
        Panel.SetZIndex(_homeDetailsButton, 1);
        ((Grid)Content).Children.Add(_homeDetailsButton);
        _homeDetailsButton.MouseLeftButtonUp += (_, e) =>
        {
            if (_drawerHeight == null) return;
            e.Handled = true;
            SetDrawerOpen(!(_drawerWheelActive ? _drawerWheelTarget > _drawerExtent / 2 : _drawerOpen));
        };
        UpdateHomeDetailsButton();
        CoverColorsFile.Changed += OnCoverColorsChanged;
    }

    private void UpdateHomeDetailsButton()
    {
        if (_homeDetailsLabel == null || _homeDetailsButton == null) return;
        string text = _drawerOpen ? Say("Übersicht", "Face") : Say("Details", "Details");
        if (AutomationProperties.GetName(_homeDetailsButton) == text) return;
        AutomationProperties.SetName(_homeDetailsButton, text);
        var icon = HomeOutline(_drawerOpen ? "M15 7 L10 12 L15 17" : "M8 10 L12 14 L16 10", 15);
        HomeIconPath(icon).Stroke = RoomSub;
        icon.Margin = _drawerOpen ? new Thickness(0, 0, 5, 0) : new Thickness(5, 0, 0, 0);
        _homeDetailsLabel.Children.Clear();
        if (_drawerOpen) _homeDetailsLabel.Children.Add(icon);
        _homeDetailsLabel.Children.Add(HomeText(text, 12, RoomSub));
        if (!_drawerOpen) _homeDetailsLabel.Children.Add(icon);
    }

    private async Task RefreshMusicSyncAsync()
    {
        if (_homeModeSending) return;
        var before = _musicSync;
        var music = await GoveeSync.IsRunningAsync();
        if (!_homeModeSending && _musicSync == before) _musicSync = music;
    }

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
            if (_homeDetailsButton != null) _homeDetailsButton.Visibility = Visibility.Collapsed;
            SetCompactStatus("");
            ClearHome();
            ClearHomeFace();
            Seen.ForgetAll("home.face.");
            Seen.ForgetAll("home.details.");
            _homeFaceLastSeen.Clear();
            _homeLightSection.Children.Add(text is "pi.loading" or "pi.unreachable"
                ? new SkeletonPlaceholder(!_piReachable, Loc.Get(text), 82, 56, 48, 34)
                : NewRow(Glyph(0xE7BA), Loc.Get(text), muted: true));
            return;
        }

        UpdateHomeFace(home);
        var acButtons = ShowHomeAirConditioner(DateTime.Now.Month, home.AirConditionerOn)
            ? home.AirConditioner.Where(a => a.Icon is "power" or "fan" or "snow").ToArray() : [];
        string key = HomeKey(home, acButtons);
        if (key != _homeKey)
        {
            _homeKey = key;
            var previous = _homeDrawerValues.ToArray();
            ClearHome();
            BuildPlugs(home);
            BuildClimate(home, acButtons);
            BuildPiLine(home);
            foreach (string removed in previous)
            {
                if (_homeDrawerValues.Contains(removed)) continue;
                Seen.Forget(removed);
                _homeFaceLastSeen.Remove(removed);
            }

            var drawer = new Grid { Name = "HomeDetails", MinHeight = acButtons.Length > 0 ? 191 : 158 };
            drawer.ColumnDefinitions.Add(new ColumnDefinition());
            drawer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(25) });
            drawer.ColumnDefinitions.Add(new ColumnDefinition());
            drawer.RowDefinitions.Add(new RowDefinition());
            drawer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var rule = new Border { Width = 1, Background = RoomLine, HorizontalAlignment = HorizontalAlignment.Left };
            Grid.SetColumn(rule, 2);
            Grid.SetColumn(_homeClimateSection, 2);
            _homeClimateSection.Margin = new Thickness(23, 0, 0, 0);
            drawer.Children.Add(_homePlugSection);
            drawer.Children.Add(rule);
            drawer.Children.Add(_homeClimateSection);
            if (_homePiSection.Children.Count > 0)
            {
                Grid.SetRow(_homePiSection, 1);
                Grid.SetColumnSpan(_homePiSection, 3);
                drawer.Children.Add(_homePiSection);
            }
            AddDrawer(drawer, replaceHomeFace: true);
            if (_homeDetailsButton != null) _homeDetailsButton.Visibility = Visibility.Visible;
            QueueHomeLightPlacement();
        }
        UpdateHomeDetailsButton();
        UpdateHome(home);
    }

    private void ClearHome()
    {
        foreach (var section in new[] { _homeClimateSection, _homePlugSection, _homePiSection })
        {
            if (section.Parent is Panel parent) parent.Children.Remove(section);
            section.Children.Clear();
        }
        _homeDrawerUpdates.Clear();
        _homeDrawerValues.Clear();
        _homeDrawerThermostat = null;
    }

    private static bool ShowHomeAirConditioner(int month, bool? reportedOn) => month is >= 6 and <= 9 || reportedOn == true;

    private string HomeKey(PiHome home, IReadOnlyList<HomeAction> acButtons) => string.Join("|",
        Loc.CurrentLanguage, home.Climate != null, _guardArmed,
        string.Join(";", acButtons.Select(a => a.Id)),
        string.Join(";", home.Plugs.Where(p => !IsHomeMic(p.Id, p.Name)).Select(p => $"{p.Id}:{p.Name}:{p.Guard}:{p.TodayKwh.HasValue}")),
        home.Pi != null, string.Join(";", home.Pi?.Issues.Select(i => $"{i.Title}:{i.Detail}:{i.FixId}:{i.FixLabel}") ?? []));

    /// <summary>
    /// A planner dashboard's start page (the weather, when to leave, games) as tiles on the face: a planner has no
    /// lamps or sockets to show there. The tiles are built once per set of readings; their words change in place.
    /// </summary>
    private FrameworkElement? BuildHomeReadings(PiHome home)
    {
        if (home.Readings is not { Count: > 0 } readings) return null;
        const double gap = 8;
        int count = Math.Min(readings.Count, 6), columns = count == 4 ? 2 : Math.Min(3, count);
        var grid = new Grid { Margin = new Thickness(0, 0, -gap, -gap) };
        for (int i = 0; i < columns; i++) grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < (count + columns - 1) / columns; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < count; i++)
        {
            string id = readings[i].Id;
            var label = HomeText(readings[i].Label, 12, RoomLabel);
            label.TextTrimming = TextTrimming.CharacterEllipsis;
            var value = HomeText(readings[i].Display, 20, CompactInk, 600);
            value.TextTrimming = TextTrimming.CharacterEllipsis;
            value.Margin = new Thickness(0, 3, 0, 0);
            var tile = new Border
            {
                CornerRadius = new CornerRadius(14), Background = RoomTrack, BorderBrush = RoomBorder, BorderThickness = new Thickness(1),
                Padding = new Thickness(13, 9, 13, 10), Margin = new Thickness(0, 0, gap, gap),
                Child = new StackPanel { Children = { label, value } }
            };
            Grid.SetRow(tile, i / columns);
            Grid.SetColumn(tile, i % columns);
            grid.Children.Add(tile);
            _homeFaceUpdates.Add(now =>
            {
                if (now.Readings?.FirstOrDefault(r => r.Id == id) is not { } reading) return;
                label.Text = reading.Label;
                value.Text = reading.Display;
                tile.ToolTip = $"{reading.Label}: {reading.Display}";
            });
        }
        return grid;
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
        UpdateHomeFace(home);
        UpdateHome(home);
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

    private void UpdateHome(PiHome home)
    {
        foreach (var update in _homeDrawerUpdates) update(home);
    }

    private async void SendHome(string action, string? id, JsonNode? value)
    {
        await PostHome(action, id, value);
        RefreshHomeAfterCommand();
    }

    private async Task PostHome(string action, string? id, JsonNode? value)
    {
        if (_pi == null || _disposed) return;
        if (await _pi.SendHomeAsync(action, id, value) == HomeCommandResult.Accepted && !_disposed)
            await Task.Delay(HomeSettleMs);
    }

    private void RefreshHomeAfterCommand()
    {
        if (_disposed) return;
        if (_piRefreshing) _homeRefreshQueued = true;
        else RefreshPi();
    }

    private void StepHomeTarget(double step)
    {
        if (_home is not { Climate: { } climate } home || (_targetDraft ?? climate.Target) is not { } from) return;
        double low = Math.Max(5, climate.Min), high = Math.Min(30, climate.Max);
        if (high <= low) return;
        double next = Math.Clamp(Math.Round((from + step) * 2) / 2, low, high);
        if (from == next) return;
        _targetDraft = next;
        UpdateHomeFace(home);
        UpdateHome(home);
        if (_targetSend == null)
        {
            _targetSend = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(TargetSendDelayMs) };
            _targetSend.Tick += SendTarget;
        }
        _targetSend.Stop();
        _targetSend.Start();
    }

    private async void SendTarget(object? sender, EventArgs e)
    {
        _targetSend?.Stop();
        if (_targetSending || _targetDraft is not { } value) return;
        _targetSending = true;
        try { await PostHome("temp", null, JsonValue.Create(value)); }
        finally
        {
            _targetSending = false;
            if (!_disposed && _targetSend?.IsEnabled != true)
            {
                if (_targetDraft == value) _targetDraft = null;
                else SendTarget(null, EventArgs.Empty);
            }
            RefreshHomeAfterCommand();
        }
    }


    private void BuildClimate(PiHome home, IReadOnlyList<HomeAction> ac)
    {
        if (home.Climate != null)
        {
            _homeClimateSection.Children.Add(HomeText(Say("Raumklima", "Room climate"), 20, CompactInk, 550));
            var current = HomeText("–", 34, CompactInk, 500);
            var state = HomeText("", 13, RoomLabel);
            state.Margin = new Thickness(9, 0, 0, 4);
            state.VerticalAlignment = VerticalAlignment.Bottom;
            var room = new StackPanel
            {
                Height = 41, Margin = new Thickness(0, 8, 0, 5), Orientation = Orientation.Horizontal, Children = { current, state }
            };
            _homeDrawerThermostat = current;
            _homeClimateSection.Children.Add(room);

            var target = HomeText("–", 13, CompactInk);
            target.MinWidth = 31;
            target.TextAlignment = TextAlignment.Center;
            target.Margin = new Thickness(9, 0, 9, 0);
            var minus = TargetButton("−", -.5);
            var plus = TargetButton("+", .5);
            var caption = HomeText(Say("Ziel", "Target"), 13, RoomLabel);
            caption.Margin = new Thickness(0, 0, 9, 0);
            _homeClimateSection.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, Children = { caption, minus, target, plus }
            });
            var humidity = HomeText("", 13, CompactInk);
            humidity.Margin = new Thickness(8, 0, 0, 0);
            var window = HomeText("", 13, RoomSub);
            window.Margin = new Thickness(8, 0, 0, 0);
            var drop = HomeOutline("M12 3 C12 3 6 10 6 14 A6 6 0 0 0 18 14 C18 10 12 3 12 3 Z", 15);
            HomeIconPath(drop).Stroke = RoomLabel;
            var readings = new DockPanel { Height = 27, LastChildFill = true, Children = { drop, window, humidity } };
            DockPanel.SetDock(window, Dock.Right);
            _homeClimateSection.Children.Add(HomeDataLine(readings, 28));

            var showCurrent = TrackHomeValue(current, "temperature:room", value => current.Text = HomeTemperature(value), smooth: true, drawer: true);
            var showTarget = TrackHomeValue(target, "temperature:target", value => target.Text = HomeTemperature(value), smooth: true, drawer: true);
            var showHumidity = TrackHomeValue(humidity, "humidity", value =>
                humidity.Text = value is { } rh ? Say($"{Math.Round(rh)} % Luftfeuchte", $"{Math.Round(rh)}% humidity") : "", drawer: true);
            _homeDrawerUpdates.Add(data =>
            {
                var climate = data.Climate!;
                double? setpoint = _targetDraft ?? climate.Target;
                showCurrent(climate.Current);
                showTarget(setpoint);
                showHumidity(climate.Humidity);
                drop.Visibility = climate.Humidity.HasValue ? Visibility.Visible : Visibility.Collapsed;
                ShowHomeText(state, "heating", climate.Action == null ? "" :
                    climate.IsHeating ? Say("heizt", "Heating") : Say("Inaktiv", "Idle"));
                ShowHomeText(window, "window", climate.WindowOpen ? Say("Fenster offen", "Window open") : Say("Fenster zu", "Window closed"));
                minus.IsEnabled = setpoint > Math.Max(5, climate.Min);
                plus.IsEnabled = setpoint < Math.Min(30, climate.Max);
                minus.Opacity = minus.IsEnabled ? 1 : .4;
                plus.Opacity = plus.IsEnabled ? 1 : .4;
            });
        }
        if (ac.Count == 0) return;
        var buttons = new WrapPanel { Margin = new Thickness(0, 9, 0, 0) };
        foreach (var action in ac)
        {
            var label = HomeText(action.Icon switch
            {
                "power" => Say("Klima An/Aus", "A/C power"),
                "fan" => Say("Stufe", "Fan"),
                _ => Say("Kühl", "Cool")
            }, 12, RoomLabel);
            var button = HomeChip(label, new Thickness(8, 0, 8, 0));
            button.MinHeight = 24;
            button.Margin = new Thickness(0, 0, 5, 0);
            button.MouseLeftButtonUp += (_, e) => { e.Handled = true; SendHome("ac", action.Id, null); };
            buttons.Children.Add(button);
        }
        _homeClimateSection.Children.Add(buttons);

        Border TargetButton(string sign, double step)
        {
            var button = HomeDisc(25, HomeText(sign, 13, CompactInk));
            ((TextBlock)button.Child).TextAlignment = TextAlignment.Center;
            button.Cursor = System.Windows.Input.Cursors.Hand;
            button.ToolTip = Say($"Ziel {sign}0,5°", $"Target {sign}0.5°");
            AutomationProperties.SetName(button, (string)button.ToolTip);
            Pressable(button, () => RoomTrack, CardHover);
            button.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                if (button.IsEnabled) StepHomeTarget(step);
            };
            return button;
        }
    }

    private void BuildPlugs(PiHome home)
    {
        var plugs = home.Plugs.Where(p => !IsHomeMic(p.Id, p.Name)).ToArray();
        if (plugs.Length == 0) return;
        _homePlugSection.Children.Add(HomeText(Say("Strom", "Power"), 20, CompactInk, 550));
        if (plugs.FirstOrDefault(p => p.TodayKwh.HasValue) is { } metered)
        {
            var kwh = HomeText("", 27, CompactInk, 500);
            var cost = HomeText("", 12, RoomLabel);
            cost.Margin = new Thickness(4, 0, 0, 4);
            cost.VerticalAlignment = VerticalAlignment.Bottom;
            _homePlugSection.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3),
                Children = { kwh, cost }
            });
            var showEnergy = TrackHomeValue(kwh, "energy:" + metered.Id, number =>
            {
                if (number is not { } energy) return;
                energy = Math.Max(0, energy);
                kwh.Text = energy.ToString("0.00", HomeCulture) + " kWh";
                decimal estimate = Math.Round((decimal)energy * (decimal)EuroPerKwh, 2);
                cost.Text = $"{metered.Name} {Say("heute", "today")} · €" + estimate.ToString("0.00", HomeCulture);
            }, drawer: true);
            _homeDrawerUpdates.Add(data => showEnergy(data.Plugs.First(p => p.Id == metered.Id).TodayKwh));
        }
        foreach (var plug in plugs)
        {
            string id = plug.Id;
            var name = HomeText(plug.Name, 13, CompactInk);
            name.Margin = new Thickness(8, 0, 0, 0);
            var status = HomeText("", 13, RoomSub);
            status.IsVisibleChanged += (_, _) => { if (!status.IsVisible) Motion.Stop(status, OpacityProperty, 1); };
            var icon = HomeOutline(plug.Guard ? HomeLockIcon : HomePlugIcon, 15);
            HomeIconPath(icon).Stroke = RoomLabel;
            var line = new DockPanel { Background = Brushes.Transparent };
            line.Children.Add(icon);
            var row = HomeDataLine(line, 24);
            if (_guardArmed == id)
            {
                var confirm = new StackPanel
                {
                    Orientation = Orientation.Horizontal, Children =
                    {
                        HomeText(Say("Wirklich aus?", "Turn off?"), 11, RoomLabel),
                        CompactPill(Say("Aus", "Off"), () => { _guardArmed = null; SendHome("plug", id, JsonValue.Create(false)); RedrawHome(); }),
                        CompactPill(Say("Nein", "Cancel"), () => { _guardArmed = null; RedrawHome(); })
                    }
                };
                DockPanel.SetDock(confirm, Dock.Right);
                line.Children.Add(confirm);
            }
            else
            {
                DockPanel.SetDock(status, Dock.Right);
                line.Children.Add(status);
                row.Cursor = System.Windows.Input.Cursors.Hand;
                row.MouseLeftButtonUp += (_, e) =>
                {
                    if (_home?.Plugs.FirstOrDefault(p => p.Id == id) is not { Available: true } state) return;
                    e.Handled = true;
                    if (state.Guard && state.On) { _guardArmed = id; RedrawHome(); DisarmLater(id); }
                    else SendHome("plug", id, JsonValue.Create(!state.On));
                };
            }
            line.Children.Add(name);
            _homePlugSection.Children.Add(row);

            HomePlug shown = plug;
            void Paint(double? watts)
            {
                status.Text = !shown.Available ? Say("Offline", "Offline") :
                    shown.On && watts.HasValue ? $"{Math.Max(0, watts.Value):0} W" : Loc.Get(shown.On ? "control.on" : "control.off");
            }
            var showWatts = TrackHomeValue(status, "plug:" + id + ":watts", Paint, drawer: true);
            _homeDrawerUpdates.Add(data =>
            {
                shown = data.Plugs.First(p => p.Id == id);
                row.IsEnabled = shown.Available;
                row.Opacity = shown.Available ? 1 : .4;
                showWatts(shown.Available && shown.On ? shown.Watts : null);
                if (!HomeDetailChanged("plug:" + id + ":state", $"{shown.Available}:{shown.On}"))
                {
                    if (!shown.Available || !shown.On || shown.Watts == null) Paint(null);
                }
                else
                {
                    Paint(shown.Watts);
                    Motion.To(status, OpacityProperty, 1, Motion.Shift, from: .65);
                }
            });
        }
    }

    private Border HomeDataLine(FrameworkElement line, double height) => new()
    {
        MinHeight = height, BorderThickness = new Thickness(0, 0, 0, 1), BorderBrush = RoomLine,
        Background = Brushes.Transparent, Tag = "drawer-row", Child = line
    };

    private void ShowHomeText(TextBlock label, string key, string value)
    {
        if (label.Tag == null)
        {
            label.Tag = key;
            label.IsVisibleChanged += (_, _) => { if (!label.IsVisible) Motion.Stop(label, OpacityProperty, 1); };
        }
        if (HomeDetailChanged(key, value)) Motion.To(label, OpacityProperty, 1, Motion.Shift, from: .65);
        label.Text = value;
    }

    private bool HomeDetailChanged(string key, string value)
    {
        key = "home.details." + key;
        _homeDrawerValues.Add(key);
        return HomeValuesVisible(drawer: true) && Seen.Changed(key, value) && !AnimationConfig.ReduceMotion;
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
            RedrawHome();
        };
        timer.Start();
    }

    private void BuildPiLine(PiHome home)
    {
        if (home.Pi is not { } pi) return;
        var line = HomeText("", 11, RoomLabel);
        line.TextWrapping = TextWrapping.Wrap;
        _homePiSection.Margin = new Thickness(0, 8, 0, 0);
        _homePiSection.Children.Add(line);
        HomePi reported = pi;
        double? temperature = pi.Temp;
        void Paint()
        {
            var parts = new List<string>();
            if (temperature is { } temp) parts.Add(temp.ToString("0", HomeCulture) + "°");
            if (reported.UptimeSeconds is { } up) parts.Add(Loc.Get("home.piUp", FormatDuration(TimeSpan.FromSeconds(up))));
            if (reported.Issues.Count == 0) parts.Add(Loc.Get("home.piOk"));
            line.Text = "Pi " + string.Join(" · ", parts);
        }
        var showTemperature = TrackHomeValue(line, "pi.temperature", value => { temperature = value; Paint(); }, drawer: true);
        _homeDrawerUpdates.Add(data =>
        {
            reported = data.Pi!;
            const string uptimeKey = "home.details.pi.uptime";
            if (reported.UptimeSeconds is { } uptime)
            {
                _homeDrawerValues.Add(uptimeKey);
                if (HomeValuesVisible(drawer: true)) Seen.Changed(uptimeKey, uptime);
            }
            else Seen.Forget(uptimeKey);
            showTemperature(reported.Temp);
            Paint();
        });
        foreach (var issue in pi.Issues)
        {
            FrameworkElement? fix = issue.FixId is { } id ? CompactPill(issue.FixLabel ?? Loc.Get("home.fix"), () => SendHome("fix", id, null)) : null;
            var row = CompactLine(0xE7BA, issue.Title, fix);
            row.ToolTip = issue.Detail;
            _homePiSection.Children.Add(row);
            var detail = HomeText(issue.Detail, 11, RoomLabel);
            detail.TextWrapping = TextWrapping.Wrap;
            _homePiSection.Children.Add(detail);
        }
    }
}
