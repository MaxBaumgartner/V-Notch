using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using VNotch.Services;
using VNotch.Services.Displays;

namespace VNotch.Controls;

/// <summary>
/// The closed system tab, shaped like Control Center: connections as a plain column, the power mode as the one big
/// tile (its colour lights the tab from right behind it), and tall sliders for sound and brightness. Values stay
/// white; colour sits only in the light, the tile's border and glyph, and small status dots.
/// </summary>
public partial class ControlCenterView
{
    private const double FaceRowHeight = 150;
    private const double CapsuleWidth = 52;
    private const double CapsuleHeight = 122;
    private const double ModeSlotWidth = 50;

    // Leise blue, Ausgewogen silver, Leistung rosé, Extrem lilac: what the tile, its light and the thumb wear.
    private static readonly (Color Main, Color Warmer, Color Cooler)[] ModeMoods =
    [
        (Color.FromRgb(0x7F, 0xA6, 0xCC), Color.FromRgb(0xA1, 0x9B, 0xD2), Color.FromRgb(0x6F, 0x8F, 0xB5)),
        (Color.FromRgb(0xA9, 0xAF, 0xBC), Color.FromRgb(0xC4, 0xB6, 0xA4), Color.FromRgb(0x87, 0x98, 0xB0)),
        (Color.FromRgb(0xD2, 0x85, 0x7E), Color.FromRgb(0xD2, 0xAE, 0x7C), Color.FromRgb(0xC9, 0x8A, 0xB0)),
        (Color.FromRgb(0xA1, 0x9B, 0xD2), Color.FromRgb(0xC9, 0x8A, 0xB0), Color.FromRgb(0x7F, 0x8F, 0xCC))
    ];
    private static readonly Color ConnectionBlue = Color.FromRgb(0x7F, 0xA6, 0xCC);
    private static readonly Color MicSand = Color.FromRgb(0xD2, 0xAE, 0x7C);

    private sealed class FaceRow
    {
        public required Border Disc;
        public required TextBlock Glyph;
        public required TextBlock State;
        public required Ellipse Dot;
    }

    private sealed class FaceCapsule
    {
        public required Grid Root;
        public required Border Fill;
        public required TextBlock Number;
        public required TextBlock NumberDark;
        public Canvas? Dark;
        public double Height = CapsuleHeight;
        public double Level;
        public bool Dragging;
    }

    private FaceRow? _faceWifi, _faceBluetooth, _faceMic;
    private Border? _modeTile;
    private TextBlock? _modeGlyph, _modeThumbGlyph;
    private Grid? _modeName;
    private Grid? _modeTrack;
    private Border? _modeThumb;
    private readonly TranslateTransform _modeThumbShift = new();
    private readonly List<TextBlock> _modeSlotGlyphs = new();
    private int _modeShown = -2;
    private FaceCapsule? _volumeCapsule, _brightnessCapsule;
    private Grid? _brightnessColumn;
    private TextBlock? _outputLabel, _outputGlyph;
    private Border? _outputMenu;
    private string? _brightnessDisplay;
    private TextBlock? _brightnessName;

    private void BuildSystemFace()
    {
        var row = _faceGrid = new Grid { Height = FaceRowHeight };
        foreach (var width in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = width });

        var connections = new StackPanel { VerticalAlignment = VerticalAlignment.Center, MinWidth = 150, Margin = new Thickness(0, 0, 14, 0) };
        _faceWifi = FaceConnection(connections, 0xE701, Say("WLAN", "Wi-Fi"), () => { if (_wifiRadio != null) SetRadio(_wifiRadio, _snapshot?.WifiOn != true); }, () => OpenQuickPage(QuickPage.Wifi));
        _faceBluetooth = FaceConnection(connections, 0xE702, "Bluetooth", () => { if (_bluetoothRadio != null) SetRadio(_bluetoothRadio, _snapshot?.BluetoothOn != true); }, () => OpenQuickPage(QuickPage.Bluetooth));
        _faceMic = FaceConnection(connections, 0xE720, Say("Mikrofon", "Microphone"), ToggleWaveXlr);
        row.Children.Add(connections);

        if (_legionAttached)
        {
            _modeTile = BuildModeTile();
            Grid.SetColumn(_modeTile, 1);
            row.Children.Add(_modeTile);
        }

        var volume = BuildCapsule(0xE767, v => _volume?.SetVolume((float)v), Say("Lautstärke", "Volume"));
        _volumeCapsule = volume;
        // The face's columns must leave the mode tile its full width (236 px of 568): the output chip is just its name
        // (capped) and a small arrow, so the volume column stays at most ~100 px; the network and screen names are
        // capped the same way instead of widening their column into the tile.
        _outputLabel = CompactText("–", 11.5, CompactInk);
        _outputLabel.MaxWidth = 58;
        _outputLabel.TextTrimming = TextTrimming.CharacterEllipsis;
        _outputGlyph = CompactGlyph(0xE7F5);
        var chevron = CompactGlyph(0xE70D);
        chevron.FontSize = 8;
        chevron.Width = 10;
        chevron.Margin = new Thickness(3, 1, 0, 0);
        var outputButton = new Border
        {
            CornerRadius = new CornerRadius(9), Padding = new Thickness(7, 3, 7, 3), Background = Frozen("#14FFFFFF"), Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { _outputLabel, chevron } }
        };
        Pressable(outputButton, () => Frozen("#14FFFFFF"), CardHover);
        outputButton.MouseLeftButtonUp += (_, e) => { e.Handled = true; ToggleOutputMenu(); };
        _outputMenu = new Border
        {
            CornerRadius = new CornerRadius(12), Padding = new Thickness(4), MinWidth = 150,
            Background = Frozen("#F21D1D21"), BorderBrush = Frozen("#1AFFFFFF"), BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(-120, 0, 0, 26), Visibility = Visibility.Collapsed,
            RenderTransformOrigin = new Point(1, 1), RenderTransform = new ScaleTransform(1, 1)
        };
        var volumeColumn = FaceColumn(volume.Root, outputButton, 14);
        volumeColumn.Children.Add(_outputMenu);
        volumeColumn.ClipToBounds = false;
        Grid.SetColumn(volumeColumn, 2);
        row.Children.Add(volumeColumn);

        var brightness = BuildCapsule(0xE706, SetFaceBrightness, Say("Helligkeit", "Brightness"));
        _brightnessCapsule = brightness;
        _brightnessName = CompactText("Monitor", 11, CompactLabel);
        _brightnessName.MaxWidth = 52;
        _brightnessName.TextTrimming = TextTrimming.CharacterEllipsis;
        _brightnessColumn = FaceColumn(brightness.Root, _brightnessName, 10);
        _brightnessColumn.Visibility = Visibility.Collapsed; // until the monitor answers over DDC
        Grid.SetColumn(_brightnessColumn, 3);
        row.Children.Add(_brightnessColumn);

        // The light sits right behind the mode tile; it follows the tile wherever layout puts it.
        if (_modeTile != null)
        {
            _modeTile.SizeChanged += (_, _) => GatherSystemLightSoon();
            _modeTile.IsVisibleChanged += (_, _) => { GatherSystemLightSoon(); if (!_modeTile.IsVisible) CloseOutputMenu(); };
        }
        _compactPage.Children.Add(row);
        IsVisibleChanged += (_, _) => { if (IsVisible) _ = LoadFaceBrightness(); };
    }

    private static Grid FaceColumn(UIElement capsule, UIElement label, double leftGap)
    {
        var column = new Grid { Margin = new Thickness(leftGap, 0, 0, 0) };
        column.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        column.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        column.Children.Add(capsule);
        if (label is FrameworkElement element) { element.HorizontalAlignment = HorizontalAlignment.Center; element.Margin = new Thickness(0, 6, 0, 0); }
        Grid.SetRow(label, 1);
        column.Children.Add(label);
        return column;
    }

    /// <summary>The round button switches it; with <paramref name="open"/>, the name opens its page (a small › says so).</summary>
    private FaceRow FaceConnection(Panel host, int glyph, string name, Action toggle, Action? open = null)
    {
        var icon = CompactGlyph(glyph);
        icon.FontSize = 14;
        var disc = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Background = Frozen("#1AFFFFFF"), Child = icon };
        var dot = new Ellipse { Width = 6, Height = 6, Fill = Frozen("#40FFFFFF"), Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
        var state = CompactText("–", 11.5, CompactLabel);
        state.MaxWidth = 80;
        state.TextTrimming = TextTrimming.CharacterEllipsis;
        var title = CompactText(name, 13, CompactInk);
        title.FontWeight = FontWeights.SemiBold;
        var text = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            Children = { title, new StackPanel { Orientation = Orientation.Horizontal, Children = { dot, state } } } };
        var content = new StackPanel { Orientation = Orientation.Horizontal, Children = { disc, text } };
        if (open != null)
        {
            var chevron = CompactGlyph(0xE76C);
            chevron.FontSize = 9;
            chevron.Margin = new Thickness(6, 0, 0, 0);
            chevron.VerticalAlignment = VerticalAlignment.Center;
            content.Children.Add(chevron);
        }
        var row = new Border
        {
            Background = Brushes.Transparent, Cursor = Cursors.Hand, Margin = new Thickness(0, 5, 0, 5), ToolTip = name,
            Child = content
        };
        Squish(disc);
        disc.Background = Frozen("#1AFFFFFF");
        disc.MouseLeftButtonUp += (_, e) => { e.Handled = true; toggle(); };
        row.MouseLeftButtonUp += (_, e) => { e.Handled = true; if (open != null) open(); else toggle(); };
        host.Children.Add(row);
        return new FaceRow { Disc = disc, Glyph = icon, State = state, Dot = dot };
    }

    /// <summary>On: white glyph and a coloured dot. Off: grey. Unknown (no adapter, no Wave Link): dimmed.</summary>
    private static void PaintFaceRow(FaceRow? row, bool? on, string onText, string offText, Color color)
    {
        if (row == null) return;
        bool changed = row.Disc.Tag is bool previous && previous != on;
        row.Disc.Tag = on;
        ((FrameworkElement)row.Disc.Parent).IsEnabled = on != null;
        ((FrameworkElement)row.Disc.Parent).Opacity = on == null ? .45 : 1;
        row.Glyph.Foreground = on == true ? CompactInk : Frozen("#73FFFFFF");
        row.Dot.Fill = on == true ? new SolidColorBrush(color) : Frozen("#40FFFFFF");
        row.State.Text = on == true ? onText : on == false ? offText : Say("Nicht da", "Not found");
        if (changed && row.Glyph.IsVisible && !AnimationConfig.ReduceMotion)
        {
            var scale = FeedbackScale(row.Glyph);
            row.Glyph.RenderTransformOrigin = new Point(.5, .5);
            Motion.To(scale, ScaleTransform.ScaleXProperty, 1, Motion.Pop, from: .6);
            Motion.To(scale, ScaleTransform.ScaleYProperty, 1, Motion.Pop, from: .6);
        }
    }

    private Border BuildModeTile()
    {
        _modeGlyph = new TextBlock { FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 30, Foreground = CompactInk, HorizontalAlignment = HorizontalAlignment.Left };
        var label = CompactText(Loc.Get("legion.mode"), 11, CompactLabel);
        label.HorizontalAlignment = HorizontalAlignment.Right;
        label.VerticalAlignment = VerticalAlignment.Top;
        var top = new Grid { Children = { _modeGlyph, label } };
        _modeName = new Grid { Height = 34, ClipToBounds = true, Margin = new Thickness(0, 4, 0, 0) };

        _modeTrack = new Grid { Height = 36, Width = 2 + ModeSlotWidth * LegionModes.Length + 2, HorizontalAlignment = HorizontalAlignment.Left, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Bottom };
        var trackShape = new Border { CornerRadius = new CornerRadius(18), Background = Frozen("#14FFFFFF") };
        _modeTrack.Background = Brushes.Transparent;
        _modeTrack.Children.Add(trackShape);
        var slots = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2) };
        for (int i = 0; i < LegionModes.Length; i++)
        {
            var glyph = new TextBlock { Text = LegionModes[i].Glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 14,
                Foreground = CompactLabel, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            _modeSlotGlyphs.Add(glyph);
            slots.Children.Add(new Border { Width = ModeSlotWidth, Height = 32, Background = Brushes.Transparent, Child = glyph, ToolTip = Loc.Get(LegionModes[i].Key) });
        }
        _modeTrack.Children.Add(slots);
        _modeThumbGlyph = new TextBlock { FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 15, Foreground = Frozen("#151515"),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _modeThumb = new Border
        {
            Width = ModeSlotWidth, Height = 32, CornerRadius = new CornerRadius(16), Background = Frozen("#F0F5F5F7"),
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(2, 0, 0, 0), RenderTransform = _modeThumbShift,
            IsHitTestVisible = false, Child = _modeThumbGlyph
        };
        _modeTrack.Children.Add(_modeThumb);
        HookModeTrack(_modeTrack);

        var stack = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(top, Dock.Top); stack.Children.Add(top);
        DockPanel.SetDock(_modeName, Dock.Top); stack.Children.Add(_modeName);
        DockPanel.SetDock(_modeTrack, Dock.Bottom); stack.Children.Add(_modeTrack);
        return new Border
        {
            MinWidth = 2 * 16 + 2 + ModeSlotWidth * LegionModes.Length + 2, CornerRadius = new CornerRadius(24),
            Background = Frozen("#0FFFFFFF"), BorderThickness = new Thickness(1), BorderBrush = Frozen("#1AFFFFFF"),
            Padding = new Thickness(16, 14, 16, 14), Child = stack
        };
    }

    /// <summary>Tap a slot, or drag the thumb along the track; it snaps to the nearest mode on release.</summary>
    private void HookModeTrack(Grid track)
    {
        double downX = 0, startShift = 0;
        bool down = false, dragged = false;
        track.MouseLeftButtonDown += (_, e) =>
        {
            if (_legionBusy) return;
            down = true; dragged = false;
            downX = e.GetPosition(track).X; startShift = _modeThumbShift.X;
            track.CaptureMouse();
            e.Handled = true;
        };
        track.MouseMove += (_, e) =>
        {
            if (!down) return;
            double dx = e.GetPosition(track).X - downX;
            if (!dragged && Math.Abs(dx) < 4) return;
            dragged = true;
            Motion.Stop(_modeThumbShift, TranslateTransform.XProperty, Math.Clamp(startShift + dx, 0, ModeSlotWidth * (LegionModes.Length - 1)));
        };
        track.MouseLeftButtonUp += (_, e) =>
        {
            if (!down) return;
            down = false;
            track.ReleaseMouseCapture();
            e.Handled = true;
            double x = dragged ? _modeThumbShift.X + ModeSlotWidth / 2 : e.GetPosition(track).X - 2;
            int index = Math.Clamp((int)(x / ModeSlotWidth), 0, LegionModes.Length - 1);
            Motion.To(_modeThumbShift, TranslateTransform.XProperty, index * ModeSlotWidth, Motion.Press);
            _ = SetFaceMode(index);
        };
        track.LostMouseCapture += (_, _) => { if (down) { down = false; ShowFaceMode(force: true); } };
    }

    private async Task SetFaceMode(int index)
    {
        if (_legionBusy || _legion != null && (int)LegionModes[index].Mode == _legion.Mode) { ShowFaceMode(force: true); return; }
        _legionBusy = true;
        try
        {
            if (await LegionService.SetModeAsync(LegionModes[index].Mode) is { } state) ApplyLegion(state);
            else ShowFaceMode(force: true);
        }
        finally { _legionBusy = false; }
    }

    /// <summary>
    /// The tile shows the current mode: glyph and border in its colour, the name rolls over, the thumb springs to its
    /// slot, and the light behind the tile changes colour a beat later (also when Fn+Q changed it).
    /// </summary>
    private void ShowFaceMode(bool force = false)
    {
        if (_modeTile == null || _modeName == null || _modeThumb == null) return;
        int index = _legion == null ? -1 : Array.FindIndex(LegionModes, m => (int)m.Mode == _legion.Mode);
        if (index == _modeShown && !force) return;
        bool animate = _modeTile.IsVisible && _modeShown >= -1 && !AnimationConfig.ReduceMotion;
        int previous = _modeShown;
        _modeShown = index;
        int visual = index < 0 ? 1 : index;
        var look = _legion == null ? (Glyph: LegionModes[1].Glyph, Name: "–") : (LegionModeLook(_legion.Mode).Glyph, LegionModeLook(_legion.Mode).Name);
        var mood = ModeMoods[visual];
        _modeGlyph!.Text = look.Glyph;
        _modeGlyph.Foreground = new SolidColorBrush(Lift(mood.Main, .35));
        _modeThumbGlyph!.Text = index < 0 ? "" : LegionModes[index].Glyph;
        _modeThumb.Opacity = index < 0 ? .25 : 1;
        // The thumb carries the selected glyph; the slot under it stays empty so the two never sit on each other.
        for (int i = 0; i < _modeSlotGlyphs.Count; i++) _modeSlotGlyphs[i].Opacity = i == index ? 0 : 1;
        _modeTile.BorderBrush = new SolidColorBrush(Alpha(mood.Main, .5));
        SwapCompactLabel(_modeName, look.Name, horizontal: false, direction: index >= previous ? 1 : -1, size: 26);
        if (animate) Motion.To(_modeThumbShift, TranslateTransform.XProperty, visual * ModeSlotWidth, Motion.Shift);
        else Motion.Stop(_modeThumbShift, TranslateTransform.XProperty, visual * ModeSlotWidth);
        SetMoodColors(mood.Main, mood.Warmer, mood.Cooler);
        GatherSystemLightSoon();
    }

    private void GatherSystemLightSoon() => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, GatherSystemLight);

    private void GatherSystemLight()
    {
        if (_disposed || _backdrop == null || _modeTile == null || !_modeTile.IsVisible || !_backdrop.IsVisible) return;
        var center = _modeTile.TranslatePoint(new Point(_modeTile.ActualWidth / 2, _modeTile.ActualHeight / 2), _backdrop);
        _backdrop.Gather(center);
    }

    /// <summary>
    /// A tall slider like Control Center's. With <paramref name="icon"/> (an app's own icon) it sits on top at the
    /// bottom; with <paramref name="tap"/>, a press that does not drag calls it (the app mixer mutes that way).
    /// </summary>
    private FaceCapsule BuildCapsule(int glyph, Action<double> set, string name, double width = CapsuleWidth, double height = CapsuleHeight,
        ImageSource? icon = null, Action? tap = null)
    {
        bool small = height < CapsuleHeight;
        TextBlock Glyph(Brush brush)
        {
            var g = CompactGlyph(glyph);
            g.FontSize = small ? 16 : 18;
            g.Foreground = brush;
            return g;
        }
        TextBlock Number(Brush brush)
        {
            var n = CompactText("", small ? 10.5 : 12, brush);
            n.FontWeight = FontWeights.SemiBold;
            n.Opacity = 0;
            return n;
        }
        // A light copy over the empty track and a dark copy over the white fill, cut exactly at the fill's top edge,
        // so the glyph and the number read at every level. Both copies sit in identical canvases (the dark one is
        // clipped, not moved), so they line up to the pixel. An app icon has its own colours and simply sits on top.
        var lightGlyph = Glyph(Frozen("#8CFFFFFF"));
        var darkGlyph = Glyph(Frozen("#161616"));
        var number = Number(Frozen("#D9FFFFFF"));
        var numberDark = Number(Frozen("#161616"));
        if (icon != null) lightGlyph.Visibility = darkGlyph.Visibility = Visibility.Collapsed;
        var fill = new Border { Background = Frozen("#F2F2F4"), VerticalAlignment = VerticalAlignment.Bottom, Height = 0 };
        var over = new Canvas();
        Place(over, lightGlyph, number);
        var cut = new RectangleGeometry(new Rect(0, height, width, 0));
        var dark = new Canvas { IsHitTestVisible = false, Clip = cut };
        Place(dark, darkGlyph, numberDark);
        fill.SizeChanged += (_, e) => cut.Rect = new Rect(0, height - e.NewSize.Height, width, e.NewSize.Height);
        double radius = small ? 17 : 22;
        var root = new Grid
        {
            Width = width, Height = height, Cursor = Cursors.SizeNS, ToolTip = name,
            Clip = new RectangleGeometry(new Rect(0, 0, width, height), radius, radius),
            Children = { new Border { Background = Frozen("#1AFFFFFF") }, over, fill, dark }
        };
        if (icon != null)
            root.Children.Add(new Image { Source = icon, Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Bottom,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 9), IsHitTestVisible = false });
        var capsule = new FaceCapsule { Root = root, Fill = fill, Number = number, NumberDark = numberDark, Dark = dark, Height = height };
        HookCapsule(capsule, set, tap);
        return capsule;

        void Place(Canvas canvas, TextBlock glyphBlock, TextBlock numberBlock)
        {
            glyphBlock.Width = width; glyphBlock.TextAlignment = TextAlignment.Center;
            numberBlock.Width = width; numberBlock.TextAlignment = TextAlignment.Center;
            Canvas.SetBottom(glyphBlock, small ? 10 : 12);
            Canvas.SetBottom(numberBlock, height - (small ? 21 : 26));
            canvas.Children.Add(glyphBlock);
            canvas.Children.Add(numberBlock);
        }
    }

    private void HookCapsule(FaceCapsule capsule, Action<double> set, Action? tap = null)
    {
        double downY = 0, startLevel = 0;
        bool down = false, moved = false;
        void Apply(double level, bool user)
        {
            level = Math.Clamp(level, 0, 1);
            capsule.Level = level;
            Motion.Stop(capsule.Fill, HeightProperty, level * capsule.Height);
            capsule.Number.Text = capsule.NumberDark.Text = $"{level * 100:0}";
            if (user) set(level);
        }
        capsule.Root.MouseLeftButtonDown += (_, e) =>
        {
            down = true; moved = false; capsule.Dragging = true;
            downY = e.GetPosition(capsule.Root).Y; startLevel = capsule.Level;
            capsule.Root.CaptureMouse();
            e.Handled = true;
            ShowCapsuleNumber(capsule, true);
        };
        capsule.Root.MouseMove += (_, e) =>
        {
            if (!down) return;
            double dy = downY - e.GetPosition(capsule.Root).Y;
            // A few pixels of give, so a tap (mute) never nudges the level.
            if (!moved && Math.Abs(dy) < 3) return;
            moved = true;
            Apply(startLevel + dy / capsule.Height, true);
        };
        capsule.Root.MouseLeftButtonUp += (_, e) =>
        {
            if (!down) return;
            down = false; capsule.Dragging = false;
            capsule.Root.ReleaseMouseCapture();
            e.Handled = true;
            ShowCapsuleNumber(capsule, false);
            if (!moved) tap?.Invoke();
        };
        capsule.Root.LostMouseCapture += (_, _) => { down = false; capsule.Dragging = false; ShowCapsuleNumber(capsule, false); };
        capsule.Root.PreviewMouseWheel += (_, e) =>
        {
            e.Handled = true;
            Apply(capsule.Level + Math.Sign(e.Delta) * WheelVolumeStep, true);
        };
    }

    private static void ShowCapsuleNumber(FaceCapsule capsule, bool on)
    {
        foreach (var number in new[] { capsule.Number, capsule.NumberDark })
        {
            if (AnimationConfig.ReduceMotion || !number.IsVisible) Motion.Stop(number, OpacityProperty, on ? 1 : 0);
            else Motion.To(number, OpacityProperty, on ? 1 : 0, on ? Motion.Press : Motion.Exit);
        }
    }

    /// <summary>Moves a capsule to a level that changed elsewhere (keys, another app); a drag in progress wins.</summary>
    private static void ShowCapsuleLevel(FaceCapsule? capsule, double level)
    {
        if (capsule == null || capsule.Dragging) return;
        level = Math.Clamp(level, 0, 1);
        capsule.Level = level;
        capsule.Number.Text = capsule.NumberDark.Text = $"{level * 100:0}";
        if (capsule.Fill.IsVisible && !AnimationConfig.ReduceMotion) Motion.To(capsule.Fill, HeightProperty, level * capsule.Height, Motion.Shift);
        else Motion.Stop(capsule.Fill, HeightProperty, level * capsule.Height);
    }

    private async Task LoadFaceBrightness()
    {
        if (_brightnessColumn == null || _disposed) return;
        string? id;
        IReadOnlyList<DisplayInfo> displays;
        try { displays = DisplayApi.List(); id = displays.FirstOrDefault(d => d.Primary)?.Id; }
        catch { displays = []; id = null; }
        if (id == null) return;
        // With a second screen, "Monitor" would be ambiguous next to the drawer's screens: it names the main one.
        if (_brightnessName != null)
            _brightnessName.Text = displays.Count(d => d.Active) > 1 ? ShortMonitorName(displays.First(d => d.Id == id).Name) : "Monitor";
        int? value;
        try { value = DisplayApi is Win32DisplayApi native ? await native.GetBrightnessAsync(id) : await DisplayBrightness.GetAsync(id); }
        catch { value = null; }
        if (_disposed) return;
        _brightnessDisplay = value == null ? null : id;
        _brightnessColumn.Visibility = value == null ? Visibility.Collapsed : Visibility.Visible;
        if (value is { } percent) ShowCapsuleLevel(_brightnessCapsule, percent / 100.0);
    }

    private void SetFaceBrightness(double level)
    {
        if (_brightnessDisplay is { } id) DisplayBrightness.Set(id, (int)Math.Round(level * 100));
    }

    private void ShowFaceOutput()
    {
        if (_outputLabel == null) return;
        var output = _outputs.FirstOrDefault(o => o.IsDefault);
        _outputLabel.Text = output == null ? "–" : ShortOutputName(output);
        if (_outputGlyph != null) _outputGlyph.Text = Glyph(OutputGlyph(output));
        ((FrameworkElement)((FrameworkElement)_outputLabel.Parent).Parent).ToolTip = output?.FriendlyName ?? Loc.Get("control.noAdapter");
        ((FrameworkElement)((FrameworkElement)_outputLabel.Parent).Parent).Opacity = _outputs.Count > 1 ? 1 : .6;
        if (_outputMenu?.Visibility == Visibility.Visible) FillOutputMenu();
    }

    /// <summary>A small menu over the slider with every output, the current one ticked.</summary>
    private void ToggleOutputMenu()
    {
        if (_outputMenu == null || _outputs.Count < 2) return;
        if (_outputMenu.Visibility == Visibility.Visible) { CloseOutputMenu(); return; }
        FillOutputMenu();
        _outputMenu.Visibility = Visibility.Visible;
        var scale = (ScaleTransform)_outputMenu.RenderTransform;
        if (AnimationConfig.ReduceMotion) { _outputMenu.Opacity = 1; scale.ScaleX = scale.ScaleY = 1; return; }
        Motion.To(scale, ScaleTransform.ScaleXProperty, 1, Motion.Pop, from: .9);
        Motion.To(scale, ScaleTransform.ScaleYProperty, 1, Motion.Pop, from: .9);
        Motion.To(_outputMenu, OpacityProperty, 1, Motion.Press, from: 0);
    }

    private void FillOutputMenu()
    {
        if (_outputMenu == null) return;
        var list = new StackPanel();
        foreach (var output in _outputs)
        {
            var check = CompactGlyph(0xE73E);
            check.FontSize = 11;
            check.Opacity = output.IsDefault ? 1 : 0;
            var name = CompactText(output.Name, 12, CompactInk);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.MaxWidth = 170;
            var line = new DockPanel { Height = 28, Margin = new Thickness(10, 0, 10, 0), LastChildFill = true };
            DockPanel.SetDock(check, Dock.Right);
            check.Margin = new Thickness(10, 0, 0, 0);
            check.VerticalAlignment = name.VerticalAlignment = VerticalAlignment.Center;
            line.Children.Add(check);
            line.Children.Add(name);
            var item = new Border { CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = line };
            Pressable(item, () => Brushes.Transparent, CardHover);
            var chosen = output;
            item.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                SelectCompactOutput(chosen);
                Dispatcher.BeginInvoke(DispatcherPriority.Background, CloseOutputMenu);
            };
            list.Children.Add(item);
        }
        _outputMenu.Child = list;
    }

    private void CloseOutputMenu()
    {
        if (_outputMenu == null || _outputMenu.Visibility != Visibility.Visible) return;
        if (AnimationConfig.ReduceMotion || !_outputMenu.IsVisible) { _outputMenu.Visibility = Visibility.Collapsed; return; }
        var menu = _outputMenu;
        Motion.To(menu, OpacityProperty, 0, Motion.Exit, done: () => { if (menu.Opacity < .05) menu.Visibility = Visibility.Collapsed; });
    }
}
