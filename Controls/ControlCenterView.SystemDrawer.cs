using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using VNotch.Services;
using VNotch.Services.ControlCenter;
using VNotch.Services.Displays;
using VNotch.Services.Phone;

namespace VNotch.Controls;

/// <summary>
/// The system tab's drawer, laid out on the face's own columns: an app mixer (a capsule per app, a mute button
/// under it, a line beside it that moves with the app's sound) and the second screen's brightness on top; batteries
/// under the connections and the load numbers under the power mode; keep-awake and the laptop locks in a footer.
/// "Prozesse ›" and "Bildschirme ›" slide in their own pages, like WLAN and Bluetooth do on the face.
/// </summary>
public partial class ControlCenterView
{
    private enum DrawerPage { Main, Processes, Displays }

    private const double MixCapsuleWidth = 44, MixCapsuleHeight = 66, MeterHeight = 58;
    private const double ScreenWidth = 104, ScreenHeight = 44;
    private const int MixMaxChannels = 5, BatteryRows = 3;
    private static readonly Color SandColor = Color.FromRgb(0xD2, 0xAE, 0x7C);
    private static readonly Color RoseColor = Color.FromRgb(0xD2, 0x85, 0x7E);
    private static readonly Brush DrawerHair = Frozen("#14FFFFFF");
    private static readonly Brush LinkInk = Frozen("#B8FFFFFF");
    private static readonly Brush SoftInk = Frozen("#A8FFFFFF");
    private static readonly Brush CapsuleWhite = Frozen("#F2F2F4");
    private static readonly Brush CapsuleMuted = Frozen("#40FFFFFF");

    /// <summary>One app in the mixer: every audio session it has (Chrome, Discord and games open several).</summary>
    private sealed class MixChannel
    {
        public required string Key;
        public required uint[] ProcessIds;
        public required bool SystemSounds;
        public required FaceCapsule Capsule;
        public required Border MeterFill;
        public required TextBlock MuteGlyph;
        public required TextBlock Name;
        public Ellipse? Glow;
        public Image? Icon;
        public bool Muted;
        public double Meter;
    }

    private sealed class ScreenSlider
    {
        public required string Id;
        public required FrameworkElement Root;
        public required Grid Glass;
        public required Border Fill;
        public required TextBlock Value, ValueDark;
        public double Level;
        public bool Dragging;
    }

    private sealed class SegmentedParts
    {
        public required Grid Root;
        public required Action<int, bool> Select;
    }

    private Grid? _drawerRoot;
    private StackPanel? _drawerMain;
    private FrameworkElement? _drawerProcs, _drawerDisplays;
    private readonly List<Grid> _drawerGrids = new();
    private Grid? _drawerRowA;
    private StackPanel? _mixColumn, _screenColumn, _batteryHost;
    private UniformGrid? _mixHost;
    private TextBlock? _mixEmpty, _screenName;
    private Border? _screenHolder;
    private TextBlock? _cpuValue, _gpuValue, _ramValue, _cpuSub, _gpuSub, _ramSub, _netLine;
    private Border? _cpuBar, _gpuBar, _ramBar;
    private Ellipse? _cpuHeat, _gpuHeat;
    private DockPanel? _footer;
    private SegmentedParts? _awakeSegment;
    private TextBlock? _awakeGlyph, _awakeText;
    private DrawerPage _drawerPage;
    private readonly Dictionary<string, MixChannel> _mixChannels = new(StringComparer.OrdinalIgnoreCase);
    private string _mixKey = "", _batteryKey = "", _footerKey = "";
    private ScreenSlider? _screenSlider;
    private Dictionary<string, int?> _monitorLevels = new();
    private IReadOnlyList<DisplayInfo> _drawerDisplayList = [];
    private DispatcherTimer? _peakTimer;
    private MediaAudioSessions? _peakSessions;
    private bool _peakOpening;

    /// <summary>Builds the drawer once; refreshes only fill its parts, so a moving slider is never rebuilt under the pointer.</summary>
    private void EnsureSystemDrawer()
    {
        if (_drawerRoot != null) return;
        _drawerMain = new StackPanel { RenderTransform = new TranslateTransform() };

        // Four modules on a quiet surface, like Control Center's, on the face's columns with 10 px between them; the
        // footer stays on the bare backdrop. Top row: the mixer up to the power tile's right edge, the second screen
        // under the face's sliders.
        _drawerRowA = DrawerColumns();
        _mixColumn = new StackPanel();
        _mixColumn.Children.Add(DrawerHeading("Apps"));
        _mixHost = new UniformGrid { Rows = 1, Columns = 5, Margin = new Thickness(0, 2, 0, 0) };
        _mixEmpty = CompactText(Say("Gerade spielt keine App Ton", "No app is playing sound"), 11.5, CompactLabel);
        _mixEmpty.Margin = new Thickness(0, 6, 0, 4);
        _mixColumn.Children.Add(_mixHost);
        _mixColumn.Children.Add(_mixEmpty);
        var apps = DrawerModule(_mixColumn);
        apps.Margin = new Thickness(0, 0, 10, 0);
        Grid.SetColumnSpan(apps, 2);
        _drawerRowA.Children.Add(apps);
        _screenColumn = new StackPanel();
        _screenColumn.Children.Add(DrawerLink(Say("Bildschirme", "Displays"), heading: true, () => ShowDrawerPage(DrawerPage.Displays)));
        _screenHolder = new Border { Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        _screenName = CompactText("", 11.5, SoftInk);
        _screenName.HorizontalAlignment = HorizontalAlignment.Center;
        _screenName.Margin = new Thickness(0, 6, 0, 0);
        _screenColumn.Children.Add(_screenHolder);
        _screenColumn.Children.Add(_screenName);
        var screen = DrawerModule(_screenColumn);
        Grid.SetColumn(screen, 2);
        _drawerRowA.Children.Add(screen);
        _drawerMain.Children.Add(_drawerRowA);

        // Second row: batteries under the connections, the load under the power mode.
        var rowB = DrawerColumns();
        rowB.Margin = new Thickness(0, 10, 0, 0);
        var batteries = new StackPanel();
        batteries.Children.Add(DrawerHeading(Say("Akkus", "Batteries")));
        _batteryHost = new StackPanel();
        batteries.Children.Add(_batteryHost);
        var batteryModule = DrawerModule(batteries);
        batteryModule.Margin = new Thickness(0, 0, 10, 0);
        rowB.Children.Add(batteryModule);
        var load = DrawerModule(BuildLoadColumn());
        Grid.SetColumn(load, 1);
        Grid.SetColumnSpan(load, 2);
        rowB.Children.Add(load);
        _drawerMain.Children.Add(rowB);

        _footer = new DockPanel { Height = 36, LastChildFill = false, Margin = new Thickness(2, 10, 0, 0) };
        _drawerMain.Children.Add(_footer);

        _drawerRoot = new Grid { ClipToBounds = false, Children = { _drawerMain } };
        AddDrawer(_drawerRoot);
        // The hairline that opens the drawer keeps some air from the face's labels above it (inside the clip, so a
        // closed drawer adds nothing).
        if (_drawerFrame != null) _drawerFrame.Margin = new Thickness(DrawerBleed, 12, DrawerBleed, 0);
        if (_faceGrid != null) _faceGrid.SizeChanged += (_, _) => SyncDrawerColumns();
        SyncDrawerColumns();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) return;
            // Closing the notch is not a "no": a display change keeps counting down, as in Windows.
            ShowDrawerPage(DrawerPage.Main, animate: false, keepPendingDisplay: true);
            StopPeaks();
        };
    }

    /// <summary>Called on every refresh: patches what changed, then fits the drawer if it is open.</summary>
    private void FillSystemDrawer(Snapshot s)
    {
        EnsureSystemDrawer();
        bool changed = FillMixer(s.Apps);
        changed |= FillBatteries(s.Devices);
        changed |= FillFooter();
        UpdateDrawerLoad(s);
        if (_drawerPage == DrawerPage.Processes) FillProcessPage(s.Top);
        RefitDrawer();
    }

    /// <summary>
    /// After layout, an open drawer springs to what it now holds: rows that arrive later (a screen's brightness,
    /// the first numbers, a new app) grow it instead of being cut off.
    /// </summary>
    private void RefitDrawer()
    {
        if (!_drawerOpen || _drawerDragging || _drawerHeight == null) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (_disposed || !_drawerOpen || _drawerDragging || _drawerHeight == null) return;
            double target = MeasureDrawer();
            if (Math.Abs(target - DrawerHeight) < .5) return;
            if (!IsVisible || AnimationConfig.ReduceMotion)
            {
                Motion.Stop(_drawerHeight, WeatherTween.ValueProperty, target);
                return;
            }
            IsLayoutFollowing = true;
            Motion.To(_drawerHeight, WeatherTween.ValueProperty, target, Motion.Shift,
                done: () => { IsLayoutFollowing = false; ContentHeightChanged?.Invoke(); });
        }));
    }

    #region Layout helpers

    /// <summary>Three columns that follow the face: connections, the power tile, the two sliders.</summary>
    private Grid DrawerColumns()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(164) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(128) });
        _drawerGrids.Add(grid);
        return grid;
    }

    private void SyncDrawerColumns()
    {
        if (_faceGrid == null) return;
        var columns = _faceGrid.ColumnDefinitions;
        double left = columns[0].ActualWidth > 0 ? columns[0].ActualWidth : 164;
        // The right column is a fixed 128 (it starts about where the face's volume capsule does): the face's own
        // right columns change width with the output name and the brightness capsule without the face resizing, so
        // following them left the screen module too narrow for its screen.
        const double right = 128;
        foreach (var grid in _drawerGrids)
        {
            grid.ColumnDefinitions[0].Width = new GridLength(left);
            grid.ColumnDefinitions[2].Width = new GridLength(right);
        }
    }

    /// <summary>A quiet label over a module; the module's values carry the weight.</summary>
    private TextBlock DrawerHeading(string text)
    {
        var heading = CompactText(text, 12, CompactLabel);
        heading.FontWeight = FontWeights.SemiBold;
        heading.Height = 20;
        heading.Padding = new Thickness(0, 2, 0, 0);
        return heading;
    }

    /// <summary>One group of the drawer: a faint surface with a hairline, rounded like the face's tiles.</summary>
    private static Border DrawerModule(UIElement content) => new()
    {
        CornerRadius = new CornerRadius(14), Background = Frozen("#0AFFFFFF"), BorderBrush = Frozen("#14FFFFFF"),
        BorderThickness = new Thickness(1), Padding = new Thickness(10, 7, 10, 10), Child = content
    };

    /// <summary>A text link with a small chevron; as a heading it reads like the other headings.</summary>
    private Border DrawerLink(string text, bool heading, Action open)
    {
        var label = CompactText(text, 12, heading ? CompactLabel : LinkInk);
        if (heading) label.FontWeight = FontWeights.SemiBold;
        var chevron = CompactGlyph(0xE76C);
        chevron.FontSize = 9;
        chevron.Width = 12;
        chevron.Margin = new Thickness(2, 1, 0, 0);
        var link = new Border
        {
            Height = heading ? 20 : 24, CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 0, 4, 0), Background = Brushes.Transparent,
            Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-6, 0, 0, 0),
            Child = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { label, chevron } }
        };
        Pressable(link, () => Brushes.Transparent, CardHover, gentle: true);
        link.MouseLeftButtonUp += (_, e) => { e.Handled = true; open(); };
        return link;
    }

    /// <summary>A segmented control whose grey thumb slides to the pick (the white thumb stays the face's).</summary>
    private SegmentedParts Segmented(IReadOnlyList<string> items, int index, Action<int> pick)
    {
        var shift = new TranslateTransform();
        var thumb = new Border { CornerRadius = new CornerRadius(11), Background = Frozen("#2EFFFFFF"), HorizontalAlignment = HorizontalAlignment.Left,
            Width = 0, RenderTransform = shift };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var labels = new List<TextBlock>();
        var cells = new List<Border>();
        int current = index;
        void Paint()
        {
            for (int i = 0; i < labels.Count; i++) labels[i].Foreground = i == current ? CompactInk : Frozen("#8CFFFFFF");
        }
        void Place(bool animate)
        {
            if (current < 0 || current >= cells.Count || cells[current].ActualWidth <= 0) return;
            double x = cells[current].TranslatePoint(new Point(0, 0), row).X, width = cells[current].ActualWidth;
            if (animate && row.IsVisible && !AnimationConfig.ReduceMotion)
            {
                Motion.To(shift, TranslateTransform.XProperty, x, Motion.Shift);
                Motion.To(thumb, WidthProperty, width, Motion.Shift);
            }
            else
            {
                Motion.Stop(shift, TranslateTransform.XProperty, x);
                Motion.Stop(thumb, WidthProperty, width);
            }
        }
        for (int i = 0; i < items.Count; i++)
        {
            var label = CompactText(items[i], 11.5, CompactLabel);
            label.VerticalAlignment = VerticalAlignment.Center;
            Typography.SetNumeralAlignment(label, FontNumeralAlignment.Tabular);
            labels.Add(label);
            var cell = new Border { Padding = new Thickness(9, 0, 9, 0), Height = 24, Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = label };
            int at = i;
            cell.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                if (at == current) return;
                current = at;
                Paint();
                Place(true);
                pick(at);
            };
            cells.Add(cell);
            row.Children.Add(cell);
        }
        var inner = new Grid { Margin = new Thickness(2), Children = { thumb, row } };
        var root = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
            Children = { new Border { CornerRadius = new CornerRadius(13), Background = Frozen("#12FFFFFF") }, inner }
        };
        row.SizeChanged += (_, _) => Place(false);
        root.IsVisibleChanged += (_, _) =>
        {
            if (root.IsVisible) { Place(false); return; }
            Motion.Stop(shift, TranslateTransform.XProperty);
            Motion.Stop(thumb, WidthProperty);
        };
        Paint();
        return new SegmentedParts
        {
            Root = root,
            Select = (i, animate) =>
            {
                if (i == current) return;
                current = i;
                Paint();
                Place(animate);
            }
        };
    }

    /// <summary>A switch in a given colour (the pill switch takes the section's accent when it is made).</summary>
    private FrameworkElement TintedToggle(bool on, Color tint, Action<bool> changed)
    {
        var accent = _accent;
        var brush = new SolidColorBrush(tint);
        brush.Freeze();
        _accent = brush;
        try { return Toggle(on, changed); }
        finally { _accent = accent; }
    }

    /// <summary>The system tab's own words: German when the app is German, English otherwise.</summary>
    private static string Say(string de, string en) => Loc.CurrentLanguage == "de" ? de : en;

    /// <summary>An app's colour from its icon, softened to the tab's muted pastels (or none for a plain glyph).</summary>
    private static Color? AppTint(ImageSource? icon)
    {
        if (icon is not BitmapSource bitmap) return null;
        Color c;
        try { c = DynamicIslandColorExtractor.GetDominantColor(bitmap); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException) { return null; }
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), delta = max - min;
        if (delta < .08) return null;  // a grey icon has no colour worth showing
        double hue = max == r ? (g - b) / delta % 6 : max == g ? (b - r) / delta + 2 : (r - g) / delta + 4;
        hue = (hue * 60 + 360) % 360;
        // Muted like mist, lilac, sand: low saturation, light.
        const double s = .42, l = .68;
        double chroma = (1 - Math.Abs(2 * l - 1)) * s, x = chroma * (1 - Math.Abs(hue / 60 % 2 - 1)), m = l - chroma / 2;
        (double r1, double g1, double b1) = hue switch
        {
            < 60 => (chroma, x, 0d), < 120 => (x, chroma, 0d), < 180 => (0d, chroma, x),
            < 240 => (0d, x, chroma), < 300 => (x, 0d, chroma), _ => (chroma, 0d, x)
        };
        return Color.FromRgb((byte)((r1 + m) * 255), (byte)((g1 + m) * 255), (byte)((b1 + m) * 255));
    }

    internal static string ShortMonitorName(string name)
    {
        string trimmed = name.Trim();
        int space = trimmed.IndexOf(' ');
        return space > 2 ? trimmed[..space] : trimmed;
    }

    #endregion

    #region Mixer

    private bool FillMixer(List<AudioSessionInfo>? apps)
    {
        // No sound read this time (the tab was hidden): keep what the mixer shows.
        if (_mixHost == null || _mixEmpty == null || apps == null) return false;
        // One capsule per app, not per session: its volume and mute reach every session it has.
        var groups = apps.GroupBy(MixKey, StringComparer.OrdinalIgnoreCase).Take(MixMaxChannels).ToList();
        string key = string.Join(";", groups.Select(g => $"{g.Key}:{string.Join(",", g.Select(a => a.ProcessId).Order())}:{g.Any(a => a.Icon != null)}"));
        bool rebuilt = key != _mixKey;
        if (rebuilt)
        {
            _mixKey = key;
            _mixHost.Children.Clear();
            _mixChannels.Clear();
            _mixHost.Columns = Math.Max(4, groups.Count);
            foreach (var group in groups) _mixHost.Children.Add(MixCell(group.Key, group.ToList()));
            _mixHost.Visibility = groups.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            _mixEmpty.Visibility = groups.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
            // New sessions: the level lines read them from a fresh list.
            _peakSessions?.Dispose();
            _peakSessions = null;
            UpdatePeakTimer();
        }
        foreach (var group in groups)
        {
            if (!_mixChannels.TryGetValue(group.Key, out var channel)) continue;
            ShowCapsuleLevel(channel.Capsule, group.Max(a => a.Volume));
            bool muted = group.All(a => a.IsMuted);
            if (channel.Muted != muted) PaintMute(channel, muted, animate: !rebuilt);
        }
        return rebuilt;
    }

    private static string MixKey(AudioSessionInfo app) => app.IsSystemSounds ? "\u0001system" : app.DisplayName.Trim();

    private FrameworkElement MixCell(string key, List<AudioSessionInfo> sessions)
    {
        var first = sessions[0];
        string name = first.IsSystemSounds ? "System" : first.DisplayName;
        // The capsule only sets the volume; muting is the button under it (a tap on the capsule muted too, which
        // read as a glitch while dragging).
        var capsule = BuildCapsule(0xE767, v => SetAppVolume(key, v), Say($"{name}: ziehen ändert die Lautstärke", $"{name}: drag to set the volume"),
            MixCapsuleWidth, MixCapsuleHeight, sessions.FirstOrDefault(s => s.Icon != null)?.Icon);
        capsule.Root.HorizontalAlignment = HorizontalAlignment.Left;
        // The app's own colour (from its icon, muted): its level line, and a soft light behind it that rises with its sound.
        var icon = sessions.FirstOrDefault(s => s.Icon != null)?.Icon;
        var tint = AppTint(icon);
        var lineBrush = new SolidColorBrush(tint ?? Color.FromArgb(0xA3, 0xFF, 0xFF, 0xFF));
        lineBrush.Freeze();
        var meterFill = new Border { Background = lineBrush, CornerRadius = new CornerRadius(1.5), VerticalAlignment = VerticalAlignment.Bottom, Height = 0 };
        Ellipse? glow = null;
        if (tint is { } color)
        {
            var light = new RadialGradientBrush
            {
                GradientStops = { new GradientStop(Color.FromArgb(0x8C, color.R, color.G, color.B), 0), new GradientStop(Color.FromArgb(0x30, color.R, color.G, color.B), .5),
                    new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1) }
            };
            light.Freeze();
            glow = new Ellipse { Width = 104, Height = 118, Fill = light, Opacity = 0, IsHitTestVisible = false,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                // Negative margins on both sides: centred on the capsule, but no taller than it for layout.
                Margin = new Thickness(MixCapsuleWidth / 2 - 52, MixCapsuleHeight / 2 - 59, 0, MixCapsuleHeight / 2 - 59) };
        }
        var meter = new Border
        {
            Width = 3, Height = MeterHeight, CornerRadius = new CornerRadius(1.5), Background = Frozen("#0FFFFFFF"), Child = meterFill,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(MixCapsuleWidth + 4, 0, 0, 4),
            IsHitTestVisible = false
        };
        // The line sits right of the capsule; the same room on the left keeps the capsule centred over its name.
        var wrap = new Grid { Width = MixCapsuleWidth + 14, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(7, 0, 0, 0), ClipToBounds = false };
        if (glow != null) wrap.Children.Add(glow);
        wrap.Children.Add(capsule.Root);
        wrap.Children.Add(meter);

        var glyph = CompactGlyph(0xE767);
        glyph.FontSize = 11;
        glyph.Width = 14;
        glyph.RenderTransformOrigin = new Point(.5, .5);
        var label = CompactText(name, 11.5, SoftInk);
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        label.MaxWidth = 50;
        label.Margin = new Thickness(3, 0, 0, 0);
        var button = new Border
        {
            Height = 20, CornerRadius = new CornerRadius(8), Padding = new Thickness(5, 0, 6, 0), Margin = new Thickness(0, 4, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center, Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = Say($"{name} stummschalten", $"Mute {name}"),
            Child = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { glyph, label } }
        };
        Pressable(button, () => Brushes.Transparent, CardHover, gentle: true);
        button.MouseLeftButtonUp += (_, e) => { e.Handled = true; ToggleAppMute(key); };

        var channel = new MixChannel
        {
            Key = key, ProcessIds = sessions.Select(s => s.ProcessId).Distinct().ToArray(), SystemSounds = first.IsSystemSounds,
            Capsule = capsule, MeterFill = meterFill, MuteGlyph = glyph, Name = label, Glow = glow, Icon = capsule.Root.Children.OfType<Image>().FirstOrDefault()
        };
        _mixChannels[key] = channel;
        ShowCapsuleLevel(capsule, sessions.Max(s => s.Volume));
        PaintMute(channel, sessions.All(s => s.IsMuted), animate: false);
        return new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Children = { wrap, button } };
    }

    private void SetAppVolume(string key, double level)
    {
        if (!_mixChannels.TryGetValue(key, out var channel) || _mixer?.Invoke() is not { } mixer) return;
        var pids = channel.ProcessIds;
        Task.Run(() => { foreach (uint pid in pids) mixer.SetSessionVolume(pid, (float)level); });
    }

    /// <summary>
    /// Mutes (or unmutes) every session of the app at once on screen, then follows what Windows reports. Windows only
    /// toggles, so a session that already had the wanted state is toggled back.
    /// </summary>
    private async void ToggleAppMute(string key)
    {
        if (!_mixChannels.TryGetValue(key, out var channel) || _mixer?.Invoke() is not { } mixer) return;
        bool target = !channel.Muted;
        PaintMute(channel, target, animate: true);
        var pids = channel.ProcessIds;
        bool reached = await Task.Run(() =>
        {
            bool all = true;
            foreach (uint pid in pids)
            {
                bool muted = mixer.ToggleSessionMute(pid);
                if (muted != target) muted = mixer.ToggleSessionMute(pid);
                all &= muted == target;
            }
            return all;
        });
        if (_disposed || !_mixChannels.TryGetValue(key, out var current) || current != channel) return;
        if (!reached) RefreshSoon(150);
    }

    private static void PaintMute(MixChannel channel, bool muted, bool animate)
    {
        channel.Muted = muted;
        channel.Capsule.Fill.Background = muted ? CapsuleMuted : CapsuleWhite;
        channel.MuteGlyph.Text = Glyph(muted ? 0xE74F : 0xE767);
        channel.MuteGlyph.Foreground = muted ? new SolidColorBrush(RoseColor) : CompactLabel;
        channel.Name.Foreground = muted ? Frozen("#6BFFFFFF") : SoftInk;
        if (channel.Icon != null) channel.Icon.Opacity = muted ? .45 : 1;
        // The grey muted fill lets the light glyph show through; the dark copy would double it.
        if (channel.Capsule.Dark != null) channel.Capsule.Dark.Opacity = muted ? 0 : 1;
        if (!animate || !channel.MuteGlyph.IsVisible || AnimationConfig.ReduceMotion) return;
        var scale = FeedbackScale(channel.MuteGlyph);
        Motion.To(scale, ScaleTransform.ScaleXProperty, 1, Motion.Pop, from: .6);
        Motion.To(scale, ScaleTransform.ScaleYProperty, 1, Motion.Pop, from: .6);
    }

    /// <summary>The level lines run only while the drawer shows the mixer; they read each session's peak meter.</summary>
    private void UpdatePeakTimer()
    {
        bool want = !_disposed && IsVisible && _drawerOpen && _drawerPage == DrawerPage.Main && _mixChannels.Count > 0 && !AnimationConfig.ReduceMotion;
        if (!want) { StopPeaks(); return; }
        if (_peakTimer == null)
        {
            _peakTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(50) };
            _peakTimer.Tick += (_, _) => TickPeaks();
        }
        if (!_peakTimer.IsEnabled) _peakTimer.Start();
        if (_peakSessions == null) OpenPeakSessions();
    }

    private async void OpenPeakSessions()
    {
        if (_peakOpening) return;
        _peakOpening = true;
        try
        {
            var sessions = await Task.Run(WindowsMediaAudioSessions.Read);
            if (_disposed || _peakTimer?.IsEnabled != true || _peakSessions != null) sessions.Dispose();
            else _peakSessions = sessions;
        }
        catch (Exception ex)
        {
            // Any HRESULT can come back as its own exception type; the lines just stay still.
            RuntimeLog.Warn("CONTROL-CENTER", $"Peak meters not read: {ex.GetType().Name}: {ex.Message}");
        }
        finally { _peakOpening = false; }
    }

    private void StopPeaks()
    {
        _peakTimer?.Stop();
        _peakSessions?.Dispose();
        _peakSessions = null;
        foreach (var channel in _mixChannels.Values)
        {
            channel.Meter = 0;
            channel.MeterFill.Height = 0;
            if (channel.Glow != null) channel.Glow.Opacity = 0;
        }
    }

    private void TickPeaks()
    {
        var sessions = _peakSessions?.Sessions;
        foreach (var channel in _mixChannels.Values)
        {
            double peak = 0;
            if (sessions != null && !channel.Muted)
                foreach (var session in sessions)
                    if (channel.SystemSounds ? session.IsSystemSoundsSession : !session.IsSystemSoundsSession && channel.ProcessIds.Contains(session.GetProcessID))
                        peak = Math.Max(peak, session.ReadPeak());
            // A quick rise and a slow fall, like a real meter; the square root makes quiet sound visible.
            double target = Math.Sqrt(Math.Clamp(peak, 0, 1));
            channel.Meter += (target - channel.Meter) * (target > channel.Meter ? .6 : .18);
            if (channel.Meter < .01) channel.Meter = 0;
            channel.MeterFill.Height = channel.Meter * MeterHeight;
            if (channel.Glow != null) channel.Glow.Opacity = channel.Meter * .9;
        }
    }

    #endregion

    #region Second screen

    /// <summary>The first other screen that answers over DDC gets the little screen slider; otherwise a plain screen opens the page.</summary>
    private void FillScreenSlider()
    {
        if (_screenHolder == null || _screenName == null) return;
        var other = _drawerDisplayList.FirstOrDefault(d => d.Active && !d.Primary && _monitorLevels.GetValueOrDefault(d.Id) != null);
        if (other == null)
        {
            _screenSlider = null;
            var glyph = CompactGlyph(0xE7F4);
            glyph.FontSize = 26;
            glyph.Width = 40;
            var plain = new Border { Width = 56, Height = 46, CornerRadius = new CornerRadius(12), Background = Brushes.Transparent, Cursor = Cursors.Hand,
                Child = glyph, ToolTip = Say("Anzeige-Einstellungen", "Display settings") };
            Pressable(plain, () => Brushes.Transparent, CardHover, gentle: true);
            plain.MouseLeftButtonUp += (_, e) => { e.Handled = true; ShowDrawerPage(DrawerPage.Displays); };
            _screenHolder.Child = plain;
            int count = _drawerDisplayList.Count(d => d.Active);
            _screenName.Text = count > 1 ? Say($"{count} Bildschirme", $"{count} displays") : Say("Anzeige", "Display");
            return;
        }
        if (_screenSlider?.Id != other.Id)
        {
            _screenSlider = BuildScreenSlider(other.Id);
            _screenHolder.Child = _screenSlider.Root;
        }
        _screenName.Text = ShortMonitorName(other.Name);
        if (!_screenSlider.Dragging) SetScreenLevel(_screenSlider, _monitorLevels[other.Id]!.Value / 100.0, user: false);
    }

    private ScreenSlider BuildScreenSlider(string id)
    {
        Grid Readout(Brush ink, out TextBlock value, Brush? sunInk = null)
        {
            var sun = CompactGlyph(0xE706, sunInk ?? ink);
            sun.FontSize = 14;
            sun.HorizontalAlignment = HorizontalAlignment.Left;
            sun.Margin = new Thickness(9, 0, 0, 0);
            value = CompactText("", 13.5, ink);
            value.FontWeight = FontWeights.SemiBold;
            value.HorizontalAlignment = HorizontalAlignment.Right;
            value.VerticalAlignment = VerticalAlignment.Center;
            value.Margin = new Thickness(0, 0, 10, 0);
            Typography.SetNumeralAlignment(value, FontNumeralAlignment.Tabular);
            return new Grid { Width = ScreenWidth, Height = ScreenHeight, HorizontalAlignment = HorizontalAlignment.Left, Children = { sun, value } };
        }
        // As on the capsules: a light readout on the glass, a dark copy inside the white fill, cut at its edge.
        var light = Readout(Frozen("#D9FFFFFF"), out var lightValue, new SolidColorBrush(SandColor));
        var dark = Readout(Frozen("#161616"), out var darkValue, new SolidColorBrush(Color.FromRgb(0x8A, 0x6A, 0x3E)));
        var fill = new Border { Background = CapsuleWhite, HorizontalAlignment = HorizontalAlignment.Left, Width = 0, ClipToBounds = true, Child = dark };
        var glass = new Grid
        {
            Width = ScreenWidth, Height = ScreenHeight, Cursor = Cursors.SizeWE, Background = Frozen("#12FFFFFF"),
            Clip = new RectangleGeometry(new Rect(0, 0, ScreenWidth, ScreenHeight), 6, 6),
            Children = { light, fill, new Border { BorderBrush = Frozen("#52FFFFFF"), BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(6), IsHitTestVisible = false } }
        };
        var neck = new Border { Width = 3, Height = 7, Background = Frozen("#40FFFFFF") };
        var foot = new Border { Width = 30, Height = 2, CornerRadius = new CornerRadius(1), Background = Frozen("#40FFFFFF") };
        var root = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Children = { glass, neck, foot } };
        var slider = new ScreenSlider { Id = id, Root = root, Glass = glass, Fill = fill, Value = lightValue, ValueDark = darkValue };
        void SetFrom(MouseEventArgs e) => SetScreenLevel(slider, Math.Clamp(e.GetPosition(glass).X / ScreenWidth, 0, 1), user: true);
        glass.MouseLeftButtonDown += (_, e) => { e.Handled = true; slider.Dragging = true; glass.CaptureMouse(); SetFrom(e); };
        glass.MouseMove += (_, e) => { if (slider.Dragging) SetFrom(e); };
        glass.MouseLeftButtonUp += (_, e) => { if (!slider.Dragging) return; e.Handled = true; slider.Dragging = false; glass.ReleaseMouseCapture(); };
        glass.LostMouseCapture += (_, _) => slider.Dragging = false;
        glass.PreviewMouseWheel += (_, e) =>
        {
            e.Handled = true;
            SetScreenLevel(slider, Math.Round(slider.Level * 20 + Math.Sign(e.Delta)) / 20, user: true);
        };
        glass.ToolTip = Say("Ziehen oder scrollen ändert die Helligkeit", "Drag or scroll to set the brightness");
        return slider;
    }

    private void SetScreenLevel(ScreenSlider slider, double level, bool user)
    {
        level = Math.Clamp(level, 0, 1);
        slider.Level = level;
        int percent = (int)Math.Round(level * 100);
        slider.Value.Text = slider.ValueDark.Text = $"{percent} %";
        if (user || !slider.Fill.IsVisible || AnimationConfig.ReduceMotion) Motion.Stop(slider.Fill, WidthProperty, level * ScreenWidth);
        else Motion.To(slider.Fill, WidthProperty, level * ScreenWidth, Motion.Shift);
        if (!user) return;
        _monitorLevels[slider.Id] = percent;
        DisplayBrightness.Set(slider.Id, percent);
        if (_drawerPage == DrawerPage.Displays) PaintScreenTiles();
    }

    #endregion

    #region Batteries

    private bool FillBatteries(List<BluetoothDevice> devices)
    {
        if (_batteryHost == null) return false;
        var phone = _phone?.Invoke();
        var connected = devices.Where(d => d.IsConnected && d.Battery >= 0).ToList();
        string key = string.Join(";", connected.Select(d => $"{d.Address}:{d.Name}:{d.Battery}")) + "|" +
            (phone is { State: not PhoneState.Offline } ? $"{phone.Name}:{phone.State}:{phone.BatteryPercent}:{phone.Charging}" : "");
        if (key == _batteryKey) return false;
        _batteryKey = key;
        _batteryHost.Children.Clear();
        int rows = 0;
        foreach (var device in connected.OrderBy(d => d.Battery))
        {
            if (rows++ >= (phone is { State: not PhoneState.Offline } ? BatteryRows - 1 : BatteryRows)) break;
            _batteryHost.Children.Add(BatteryLine(device.Glyph.Length > 0 ? device.Glyph[0] : 0xE7F6, device.Name, device.Battery, false, null));
        }
        if (phone is { State: not PhoneState.Offline })
        {
            bool ready = phone.State == PhoneState.Device;
            var line = BatteryLine(0xE8EA, phone.Name ?? Loc.Get("phone.name"), phone.BatteryPercent ?? -1, phone.Charging,
                ready ? () => _openPhone?.Invoke() : null, ready ? null : Say("Erlauben", "Allow"));
            line.ToolTip = ready ? Say("Bildschirm vom Handy zeigen", "Show the phone's screen") : Loc.Get("phone.allow");
            if (ready) line.MouseEnter += (_, _) => _prewarmPhone?.Invoke();
            _batteryHost.Children.Add(line);
        }
        if (_batteryHost.Children.Count == 0)
        {
            var none = CompactText(Say("Nichts verbunden", "Nothing connected"), 11.5, CompactLabel);
            none.Margin = new Thickness(0, 5, 0, 0);
            _batteryHost.Children.Add(none);
        }
        return true;
    }

    /// <summary>One device: a small ring with its glyph, the name, the battery (and a cast glyph for the phone).</summary>
    private Border BatteryLine(int glyph, string name, int percent, bool charging, Action? open, string? note = null)
    {
        var dock = new DockPanel { LastChildFill = true };
        var ring = BatteryRing(glyph, percent);
        ring.Margin = new Thickness(0, 0, 6, 0);
        dock.Children.Add(ring);
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        var value = CompactText(note ?? (percent >= 0 ? $"{percent} %" : ""), 12, note == null ? CompactInk : CompactLabel);
        Typography.SetNumeralAlignment(value, FontNumeralAlignment.Tabular);
        right.Children.Add(value);
        if (charging)
        {
            var bolt = CompactGlyph(0xE945, new SolidColorBrush(Color.FromRgb(0x9D, 0xBD, 0xB1)));
            bolt.FontSize = 10;
            bolt.Width = 12;
            right.Children.Add(bolt);
        }
        DockPanel.SetDock(right, Dock.Right);
        dock.Children.Add(right);
        var label = CompactText(name, 12, Frozen("#D9FFFFFF"));
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        label.VerticalAlignment = VerticalAlignment.Center;
        var middle = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        middle.Children.Add(label);
        if (open != null)
        {
            var cast = CompactGlyph(0xEBC6);
            cast.FontSize = 11;
            cast.Width = 16;
            cast.Margin = new Thickness(4, 1, 0, 0);
            middle.Children.Add(cast);
        }
        dock.Children.Add(middle);
        var line = new Border { Height = 28, CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(-6, 0, -6, 0),
            Background = Brushes.Transparent, Child = dock };
        if (open != null)
        {
            line.Cursor = Cursors.Hand;
            Pressable(line, () => Brushes.Transparent, CardHover, gentle: true);
            line.MouseLeftButtonUp += (_, e) => { e.Handled = true; open(); };
        }
        return line;
    }

    private static Grid BatteryRing(int glyph, int percent)
    {
        const double size = 18, stroke = 1.7;
        var grid = new Grid { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(new Ellipse { Stroke = Frozen("#24FFFFFF"), StrokeThickness = stroke });
        if (percent > 0)
        {
            double circumference = Math.PI * (size - stroke);
            // The ring says how full it is: sage when fine, sand from half down, rose when it needs the charger.
            var level = percent <= 20 ? RoseColor : percent <= 50 ? SandColor : Color.FromRgb(0x9D, 0xBD, 0xB1);
            var arc = new Ellipse
            {
                Stroke = new SolidColorBrush(level), StrokeThickness = stroke,
                StrokeDashArray = new DoubleCollection { Math.Min(percent, 100) / 100.0 * circumference / stroke, 1000 },
                StrokeDashCap = PenLineCap.Round, RenderTransformOrigin = new Point(.5, .5), RenderTransform = new RotateTransform(-90)
            };
            grid.Children.Add(arc);
        }
        grid.Children.Add(new TextBlock
        {
            Text = Glyph(glyph), FontFamily = FluentFont, FontSize = 8.5, Foreground = Frozen("#D9FFFFFF"),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        });
        return grid;
    }

    #endregion

    #region Load

    private FrameworkElement BuildLoadColumn()
    {
        var column = new StackPanel();
        var header = new DockPanel { LastChildFill = false };
        header.Children.Add(DrawerHeading(Say("Leistung", "Performance")));
        var link = DrawerLink(Say("Prozesse", "Processes"), heading: false, () => ShowDrawerPage(DrawerPage.Processes));
        link.Margin = new Thickness(0, 0, -4, 0);
        DockPanel.SetDock(link, Dock.Right);
        header.Children.Add(link);
        column.Children.Add(header);

        var metrics = new Grid { Margin = new Thickness(0, 2, 0, 0), Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = Say("Prozesse zeigen", "Show processes") };
        for (int i = 0; i < 3; i++) metrics.ColumnDefinitions.Add(new ColumnDefinition());
        // Each part keeps its colour on a thin usage line: CPU mist, GPU lilac, RAM sand.
        metrics.Children.Add(Metric(0, "CPU", Color.FromRgb(0x7F, 0xA6, 0xCC), out _cpuValue, out _cpuSub, out _cpuHeat, out _cpuBar));
        metrics.Children.Add(Metric(1, "GPU", Color.FromRgb(0xA1, 0x9B, 0xD2), out _gpuValue, out _gpuSub, out _gpuHeat, out _gpuBar));
        metrics.Children.Add(Metric(2, "RAM", SandColor, out _ramValue, out _ramSub, out _, out _ramBar));
        metrics.MouseLeftButtonUp += (_, e) => { e.Handled = true; ShowDrawerPage(DrawerPage.Processes); };
        // Until the first reading: dashes, so the row keeps its height.
        SetMetric(_cpuValue, "–", "%");
        SetMetric(_gpuValue, "–", "%");
        SetMetric(_ramValue, "–", "GB");
        column.Children.Add(metrics);
        return column;
    }

    private const double UsageBarWidth = 64;

    private StackPanel Metric(int column, string name, Color color, out TextBlock value, out TextBlock sub, out Ellipse heat, out Border bar)
    {
        value = new TextBlock { FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = CompactInk, FontFamily = CompactText("", 12, CompactInk).FontFamily,
            Margin = new Thickness(0, 1, 0, 0) };
        Typography.SetNumeralAlignment(value, FontNumeralAlignment.Tabular);
        var fill = new SolidColorBrush(color);
        fill.Freeze();
        bar = new Border { Width = 0, Height = 2, CornerRadius = new CornerRadius(1), Background = fill, HorizontalAlignment = HorizontalAlignment.Left };
        var track = new Border { Width = UsageBarWidth, Height = 2, CornerRadius = new CornerRadius(1), Background = Frozen("#1AFFFFFF"),
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 3, 0, 4), Child = bar };
        heat = new Ellipse { Width = 6, Height = 6, Margin = new Thickness(0, 1, 5, 0), Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
        sub = CompactText("", 11.5, CompactLabel);
        Typography.SetNumeralAlignment(sub, FontNumeralAlignment.Tabular);
        var cell = new StackPanel
        {
            Children =
            {
                CompactText(name, 11, CompactLabel), value, track,
                new StackPanel { Orientation = Orientation.Horizontal, Children = { heat, sub } }
            }
        };
        Grid.SetColumn(cell, column);
        return cell;
    }

    /// <summary>A usage line glides to its new share; the numbers above it stay put.</summary>
    private static void SetUsage(Border? bar, double? share)
    {
        if (bar == null) return;
        double width = Math.Clamp(share ?? 0, 0, 1) * UsageBarWidth;
        if (Math.Abs(bar.Width - width) < .5) return;
        if (bar.IsVisible && !AnimationConfig.ReduceMotion) Motion.To(bar, WidthProperty, width, Motion.Shift);
        else Motion.Stop(bar, WidthProperty, width);
    }

    /// <summary>Digits change in place; only a new value rewrites a number.</summary>
    private static void SetMetric(TextBlock? block, string value, string unit)
    {
        if (block == null) return;
        string text = value + unit;
        if (block.Tag as string == text) return;
        block.Tag = text;
        block.Inlines.Clear();
        block.Inlines.Add(new Run(value));
        block.Inlines.Add(new Run(" " + unit) { FontSize = 12, FontWeight = FontWeights.Medium, Foreground = Frozen("#9EFFFFFF") });
    }

    private static void SetHeat(Ellipse? dot, int? temp)
    {
        if (dot == null) return;
        dot.Visibility = temp is >= 80 ? Visibility.Visible : Visibility.Collapsed;
        dot.Fill = new SolidColorBrush(temp is >= 90 ? RoseColor : SandColor);
    }

    private void UpdateDrawerLoad(Snapshot s)
    {
        if (s.Stats is not { } stats) return;
        SetMetric(_cpuValue, stats.CpuPercent is { } cpu ? $"{cpu:0}" : "–", "%");
        SetMetric(_gpuValue, stats.GpuPercent is { } gpu ? $"{gpu:0}" : "–", "%");
        SetMetric(_ramValue, $"{stats.RamUsedGb:0.0}", "GB");
        int? cpuTemp = _legion?.CpuTemp is > 0 ? _legion.CpuTemp : null, gpuTemp = _legion?.GpuTemp is > 0 ? _legion.GpuTemp : null;
        if (_cpuSub != null) _cpuSub.Text = cpuTemp is { } c ? $"{c} °C" : "";
        if (_gpuSub != null) _gpuSub.Text = gpuTemp is { } g ? $"{g} °C" : "";
        if (_ramSub != null) _ramSub.Text = Say($"von {stats.RamTotalGb:0} GB", $"of {stats.RamTotalGb:0} GB");
        SetHeat(_cpuHeat, cpuTemp);
        SetHeat(_gpuHeat, gpuTemp);
        SetUsage(_cpuBar, stats.CpuPercent / 100);
        SetUsage(_gpuBar, stats.GpuPercent / 100);
        SetUsage(_ramBar, stats.RamTotalGb > 0 ? stats.RamUsedGb / stats.RamTotalGb : null);
        if (_netLine != null)
        {
            bool online = s.Wired.Any(w => w.IsUp) || s.Networks.Any(n => n.IsConnected);
            string link = s.Wired.Any(w => w.IsUp) ? "Ethernet" : s.Networks.Any(n => n.IsConnected) ? Say("WLAN", "Wi-Fi") : "Offline";
            // A sage dot while the connection is up, like the face's status dots.
            _netLine.Inlines.Clear();
            _netLine.Inlines.Add(new Run("● ") { FontSize = 8, Foreground = online ? new SolidColorBrush(Color.FromRgb(0x9D, 0xBD, 0xB1)) : Frozen("#40FFFFFF"),
                BaselineAlignment = BaselineAlignment.Center });
            _netLine.Inlines.Add(new Run($"{link}     ↓ {FormatRate(stats.DownBytesPerSecond)}     ↑ {FormatRate(stats.UpBytesPerSecond)}"));
        }
    }

    #endregion

    #region Footer

    /// <summary>Keep-awake on the left, the laptop locks on the right; a mode change only moves the thumb.</summary>
    private bool FillFooter()
    {
        if (_footer == null) return false;
        string key = $"{_keepAwakeMode != null && _setKeepAwakeMode != null}|{_legionAttached}|{_legion != null}|{_legion?.WinKeyLocked}|{_legion?.TouchpadLocked}";
        bool rebuilt = key != _footerKey;
        if (rebuilt)
        {
            _footerKey = key;
            _footer.Children.Clear();
            _awakeSegment = null;
            if (_keepAwakeMode != null && _setKeepAwakeMode != null)
            {
                _awakeGlyph = CompactGlyph(0xEC32);
                _awakeGlyph.FontSize = 14;
                _awakeText = CompactText(Say("Wach bleiben", "Stay awake"), 12, Frozen("#BFFFFFFF"));
                _awakeText.MinWidth = 82;
                _awakeText.Margin = new Thickness(6, 0, 6, 0);
                _awakeText.VerticalAlignment = VerticalAlignment.Center;
                var set = _setKeepAwakeMode;
                _awakeSegment = Segmented([Say("Aus", "Off"), "Auto", Say("Immer", "Always")], AwakeIndex(), i =>
                    set(i switch { 0 => KeepAwakeMode.Off, 2 => KeepAwakeMode.Always, _ => KeepAwakeMode.Automatic }));
                var awake = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent,
                    Children = { _awakeGlyph, _awakeText, _awakeSegment.Root } };
                DockPanel.SetDock(awake, Dock.Left);
                _footer.Children.Add(awake);
            }
            if (_legion is { } state)
            {
                var locks = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                locks.Children.Add(LockSwitch(Say("Win-Taste sperren", "Lock Win key"), state.WinKeyLocked, async on => ApplyLegion(await LegionService.SetWinKeyLockedAsync(on))));
                locks.Children.Add(LockSwitch(Say("Touchpad sperren", "Lock touchpad"), state.TouchpadLocked, async on => ApplyLegion(await LegionService.SetTouchpadLockedAsync(on))));
                DockPanel.SetDock(locks, Dock.Right);
                _footer.Children.Add(locks);
            }
            else if (_legionAttached)
            {
                var setup = CompactPill(Say("Lenovo-Helfer einrichten", "Set up Lenovo helper"), async () => { await LegionService.RunSetupAsync(); RefreshLegion(); });
                setup.ToolTip = Loc.Get("legion.setupHint");
                DockPanel.SetDock(setup, Dock.Right);
                _footer.Children.Add(setup);
            }
            _footer.Visibility = _footer.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        PaintAwake();
        return rebuilt;
    }

    private int AwakeIndex() => _keepAwakeMode?.Invoke() switch { KeepAwakeMode.Off => 0, KeepAwakeMode.Always => 2, _ => 1 };

    /// <summary>While it really holds the PC awake, the cup turns sand and the words say so; the tooltip says why.</summary>
    private void PaintAwake()
    {
        if (_awakeSegment == null || _awakeGlyph == null || _awakeText == null) return;
        _awakeSegment.Select(AwakeIndex(), true);
        string label = _keepAwakeLabel?.Invoke() ?? "";
        bool holding = _keepAwakeMode?.Invoke() == KeepAwakeMode.Always ||
            label == Loc.Get("control.keepAwake.claude") || label == Loc.Get("control.keepAwake.download") || label == Loc.Get("control.keepAwake.timer");
        _awakeGlyph.Foreground = holding ? new SolidColorBrush(SandColor) : CompactLabel;
        _awakeText.Text = holding ? Say("Hält wach", "Keeping awake") : Say("Wach bleiben", "Stay awake");
        ((FrameworkElement)_awakeText.Parent).ToolTip = label.Length > 0 ? label : null;
    }

    private FrameworkElement LockSwitch(string text, bool locked, Action<bool> changed)
    {
        var label = CompactText(text, 11.5, LinkInk);
        label.VerticalAlignment = VerticalAlignment.Center;
        label.Margin = new Thickness(0, 0, 7, 0);
        var toggle = TintedToggle(locked, SandColor, changed);
        return new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 0, 0, 0), Children = { label, toggle } };
    }

    #endregion

    #region Pages

    private FrameworkElement? DrawerPageElement(DrawerPage page) => page switch
    {
        DrawerPage.Processes => _drawerProcs,
        DrawerPage.Displays => _drawerDisplays,
        _ => _drawerMain
    };

    /// <summary>The current page slides out, the next slides in from the side it belongs to, and the drawer fits it.</summary>
    private void ShowDrawerPage(DrawerPage page, bool animate = true, bool keepPendingDisplay = false)
    {
        if (_drawerRoot == null || page == _drawerPage) return;
        if (page == DrawerPage.Processes && _drawerProcs == null) { _drawerProcs = BuildProcessPage(); _drawerRoot.Children.Add(_drawerProcs); }
        if (page == DrawerPage.Displays && _drawerDisplays == null) { _drawerDisplays = BuildDisplayPage(); _drawerRoot.Children.Add(_drawerDisplays); }
        var leaving = DrawerPageElement(_drawerPage)!;
        var arriving = DrawerPageElement(page)!;
        var left = _drawerPage;
        _drawerPage = page;
        if (left == DrawerPage.Processes) { _topMode = TopMode.Off; _processes.Reset(); }
        if (left == DrawerPage.Displays && !keepPendingDisplay) RevertPendingDisplayChange();
        if (page == DrawerPage.Processes) { _topMode = _procByMemory ? TopMode.Memory : TopMode.Cpu; FillProcessPage(_lastTop); RefreshSoon(0); }
        if (page == DrawerPage.Displays) { FillDisplayPage(); RefreshCompactDisplays(); }

        leaving.Visibility = Visibility.Collapsed;
        arriving.Visibility = Visibility.Visible;
        var shift = (TranslateTransform)arriving.RenderTransform;
        bool motion = animate && IsVisible && !AnimationConfig.ReduceMotion;
        if (motion)
        {
            Motion.To(shift, TranslateTransform.XProperty, 0, Motion.Glide, from: page == DrawerPage.Main ? -18 : 18);
            Motion.To(arriving, OpacityProperty, 1, Motion.Tile, from: 0);
        }
        else
        {
            Motion.Stop(shift, TranslateTransform.XProperty, 0);
            Motion.Stop(arriving, OpacityProperty, 1);
        }
        UpdatePeakTimer();
        RefitDrawer();
    }

    /// <summary>Header of a drawer page: back, title, and room on the right for its own control.</summary>
    private DockPanel DrawerPageHeader(string title, out StackPanel right)
    {
        var back = CompactGlyph(0xE76B);
        back.FontSize = 12;
        var backButton = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Background = Frozen("#1AFFFFFF"),
            Cursor = Cursors.Hand, Child = back, ToolTip = Say("Zurück", "Back") };
        Squish(backButton);
        backButton.MouseLeftButtonUp += (_, e) => { e.Handled = true; ShowDrawerPage(DrawerPage.Main); };
        var name = CompactText(title, 15, CompactInk);
        name.FontWeight = FontWeights.SemiBold;
        name.Margin = new Thickness(12, 0, 0, 0);
        name.VerticalAlignment = VerticalAlignment.Center;
        right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var header = new DockPanel { Margin = new Thickness(0, 2, 0, 8), LastChildFill = false };
        DockPanel.SetDock(right, Dock.Right);
        header.Children.Add(right);
        header.Children.Add(backButton);
        header.Children.Add(name);
        return header;
    }

    #endregion
}
