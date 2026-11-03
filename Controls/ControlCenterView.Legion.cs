using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;
using System.Windows.Threading;
using VNotch.Services;

namespace VNotch.Controls;

/// <summary>
/// The <see cref="ControlCenterPage.Legion"/> page: what Lenovo Vantage does on a Legion that matters at
/// the desk — the power mode (Fn+Q), CPU and GPU temperature, the Windows key and touchpad locks.
/// The whole page wears the colour the power button glows in for the current mode: switching modes slides
/// a glowing pill to the new tile and every gauge and lit tile fades to the new colour with it.
/// </summary>
public partial class ControlCenterView
{
    #region Legion

    private static readonly TimeSpan LegionRefreshInterval = TimeSpan.FromSeconds(2);
    private const double LegionTileGap = 8;

    private static readonly (LegionMode Mode, string Glyph, string Key, Color Glow)[] LegionModes =
    [
        (LegionMode.Quiet, "", "legion.quiet", Color.FromRgb(0x0A, 0x84, 0xFF)),
        (LegionMode.Balanced, "", "legion.balanced", Color.FromRgb(0xF2, 0xF2, 0xF2)),
        (LegionMode.Performance, "", "legion.performance", Color.FromRgb(0xFF, 0x45, 0x3A)),
        (LegionMode.Extreme, "", "legion.extreme", Color.FromRgb(0xBF, 0x5A, 0xF2)),
    ];
    private static readonly Color LegionCustomGlow = Color.FromRgb(0xFF, 0x9F, 0x0A);

    private DispatcherTimer? _legionTimer;
    private LegionState? _legion;
    private bool _legionReachable = true;
    private bool _legionBusy;
    private string _legionKey = "";
    private LegionParts? _legionParts;
    private readonly LoopSet _legionLoops = new();
    private readonly StackPanel _legionSection = new();
    private readonly StackPanel _legionModeSection = new() { Tag = NestedSectionTag };
    private readonly StackPanel _legionSystemSection = new() { Tag = NestedSectionTag };
    private bool _legionWasReady;

    // One colour for the whole page, faded on every mode change.
    private readonly SolidColorBrush _legionAccent = new(LegionModes[1].Glow);
    private readonly SolidColorBrush _legionWash = new(Color.FromArgb(0x38, 0xF2, 0xF2, 0xF2));

    private sealed class LegionParts
    {
        public required Grid ModeGrid;
        public required Border Pill;
        public required TranslateTransform PillShift;
        public required ScaleTransform PillScale;
        public required DropShadowEffect PillGlow;
        public required ModeTile[] Modes;
        public required FrameworkElement CustomNote;
        public required Grid SystemGrid;
        public required Gauge Cpu, Gpu;
        public required KeyTile WinKey, Touchpad;
        public int Selected = -2; // -2: nothing shown yet, -1: a mode without a tile (custom)
    }

    private sealed class ModeTile
    {
        public required Border Card;
        public required TextBlock Glyph;
        public required SolidColorBrush Ink;
        public required TextBlock State;
    }

    private sealed class Gauge
    {
        public required Path Value;
        public required TextBlock Number;
        public required WeatherTween Tween;
        public double Shown = double.NaN;
    }

    private sealed class KeyTile
    {
        public required Border Card;
        public required TextBlock Glyph;
        public required SolidColorBrush Ink;
        public required TextBlock State;
        public bool? On;
    }

    /// <summary>For the <see cref="ControlCenterPage.Legion"/> page.</summary>
    internal void AttachLegion()
    {
        _legionSection.Children.Add(_legionModeSection);
        _legionSection.Children.Add(_legionSystemSection);
        ControlRoot.Children.Add(_legionSection);
        _legionTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = LegionRefreshInterval };
        _legionTimer.Tick += (_, _) => RefreshLegion();
    }

    private void OnLegionVisibleChanged(bool visible)
    {
        if (visible)
        {
            BuildLegionPage();
            // Every opening plays the arrival again: the pill pops onto its mode, the rings sweep up.
            if (_legionParts is { } parts && _legion != null)
            {
                parts.Selected = -2;
                parts.Cpu.Shown = parts.Gpu.Shown = double.NaN;
                UpdateLegion(_legion);
            }
            RefreshLegion();
            _legionTimer?.Start();
        }
        else
        {
            _legionTimer?.Stop();
            _legionLoops.StopAll();
        }
    }

    private async void RefreshLegion()
    {
        if (_legionBusy || _disposed) return;
        _legionBusy = true;
        try
        {
            ApplyLegion(await LegionService.GetStateAsync());
        }
        finally
        {
            _legionBusy = false;
        }
    }

    private void ApplyLegion(LegionState? state)
    {
        if (_disposed) return;
        _legionReachable = state != null;
        if (state != null) _legion = state;
        BuildLegionPage();
        if (_legionParts != null && _legion != null) UpdateLegion(_legion);
    }

    /// <summary>Builds the tiles once per layout (reachable, sections open); values only animate after that.</summary>
    private void BuildLegionPage()
    {
        if (_disposed) return;
        bool ready = _legionReachable && _legion != null;
        // Opening or closing a section only shows or hides its rows (ToggleLegionSection): a rebuild
        // would make the pill and the rings arrive all over again.
        string key = $"{_legionReachable}|{ready}";
        if (key == _legionKey) return;
        _legionKey = key;
        _legionParts = null;
        _legionLoops.StopAll();
        _legionModeSection.Children.Clear();
        _legionSystemSection.Children.Clear();

        _legionModeSection.Children.Add(BuildSectionHeader("legion.mode", Loc.Get("legion.mode"), "", null, topMargin: 0));
        if (!ready)
        {
            _legionModeSection.Children.Add(NewRow("", Loc.Get(_legionReachable ? "pi.loading" : "legion.setupHint"), muted: true));
            if (!_legionReachable) _legionModeSection.Children.Add(LegionSetupTile());
            return;
        }

        var modeGrid = LegionModeGrid(out var pill, out var shift, out var pillScale, out var glow, out var modes);
        var customNote = NewRow("", Loc.Get("legion.custom"), muted: true);
        customNote.Visibility = Visibility.Collapsed;
        modeGrid.Visibility = IsExpanded("legion.mode") ? Visibility.Visible : Visibility.Collapsed;
        _legionModeSection.Children.Add(modeGrid);
        _legionModeSection.Children.Add(customNote);

        _legionSystemSection.Children.Add(BuildSectionHeader("control.system", Loc.Get("control.system"), "", null, topMargin: 6));
        var systemGrid = TileGrid(4, 4);
        var cpu = GaugeTile("CPU", out var cpuCard);
        var gpu = GaugeTile("GPU", out var gpuCard);
        var winKey = LegionKeyTile("", Loc.Get("legion.winKey"), on => LegionService.SetWinKeyLockedAsync(!on));
        var touchpad = LegionKeyTile("", Loc.Get("legion.touchpad"), on => LegionService.SetTouchpadLockedAsync(!on));
        PlaceTile(systemGrid, cpuCard, 0, 4);
        PlaceTile(systemGrid, gpuCard, 1, 4);
        PlaceTile(systemGrid, winKey.Card, 2, 4);
        PlaceTile(systemGrid, touchpad.Card, 3, 4);
        systemGrid.Visibility = IsExpanded("control.system") ? Visibility.Visible : Visibility.Collapsed;
        _legionSystemSection.Children.Add(systemGrid);

        _legionParts = new LegionParts
        {
            ModeGrid = modeGrid, Pill = pill, PillShift = shift, PillScale = pillScale, PillGlow = glow, Modes = modes,
            CustomNote = customNote, SystemGrid = systemGrid, Cpu = cpu, Gpu = gpu, WinKey = winKey, Touchpad = touchpad
        };
        if (_legion != null) UpdateLegion(_legion);
        // Arriving from "loading" or the setup: the page falls into place.
        if (ready && !_legionWasReady && IsVisible) RevealChildren(_legionSection, TimeSpan.FromMilliseconds(20), 0);
        _legionWasReady = ready;
    }

    /// <summary>
    /// A section header was tapped: shows or hides that section's rows in place (the header is replaced, as it
    /// remembers its own state) so the pill and the rings stay where they are.
    /// </summary>
    private void ToggleLegionSection(string section)
    {
        if (_legionParts is not { } parts)
        {
            _legionKey = "";
            BuildLegionPage();
            return;
        }
        bool mode = section == "legion.mode";
        var panel = mode ? _legionModeSection : _legionSystemSection;
        bool open = IsExpanded(section);
        // A UIElementCollection slot cannot be overwritten while it holds a visual: take the old header out first.
        panel.Children.RemoveAt(0);
        panel.Children.Insert(0, mode
            ? BuildSectionHeader("legion.mode", Loc.Get("legion.mode"), "", null, topMargin: 0)
            : BuildSectionHeader("control.system", Loc.Get("control.system"), "", null, topMargin: 6));
        for (int i = 1; i < panel.Children.Count; i++)
        {
            // Undo the fold-away the header played on the way out.
            var row = (FrameworkElement)panel.Children[i];
            row.BeginAnimation(OpacityProperty, null);
            row.Opacity = 1;
            row.RenderTransform = null;
        }
        if (mode)
        {
            parts.ModeGrid.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            parts.CustomNote.Visibility = open && _legion?.Mode == (int)LegionMode.Custom ? Visibility.Visible : Visibility.Collapsed;
        }
        else parts.SystemGrid.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
    }

    #region Modes

    /// <summary>Four tiles over one glowing pill that slides to whichever mode is on.</summary>
    private Grid LegionModeGrid(out Border pill, out TranslateTransform shift, out ScaleTransform pillScale,
        out DropShadowEffect glow, out ModeTile[] modes)
    {
        var grid = TileGrid(LegionModes.Length, LegionModes.Length);
        glow = new DropShadowEffect { Color = _legionAccent.Color, BlurRadius = 18, ShadowDepth = 0, Opacity = 0.55 };
        shift = new TranslateTransform();
        pillScale = new ScaleTransform(1, 1);
        pill = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = _legionWash,
            BorderBrush = _legionAccent,
            BorderThickness = new Thickness(1.2),
            Margin = new Thickness(0, 0, 0, 8),
            Opacity = 0,
            Effect = glow,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new TransformGroup { Children = { pillScale, shift } },
            IsHitTestVisible = false
        };
        Grid.SetColumn(pill, 0);
        grid.Children.Add(pill);

        modes = new ModeTile[LegionModes.Length];
        for (int i = 0; i < LegionModes.Length; i++)
        {
            var (mode, glyphText, name, _) = LegionModes[i];
            var ink = new SolidColorBrush(((SolidColorBrush)Muted).Color);
            var glyph = new TextBlock { Text = glyphText, FontFamily = SegoeSymbolFont, FontSize = 17, Foreground = ink, HorizontalAlignment = HorizontalAlignment.Left };
            var label = MakeLabel(Loc.Get(name), 12.5, VNotch.Services.UiPalette.PrimaryBrush);
            label.Margin = new Thickness(0, 7, 0, 0);
            var state = MakeLabel("", 11, Muted);
            state.Margin = new Thickness(0, 1, 0, 0);
            var card = Card();
            card.Padding = new Thickness(12, 10, 12, 10);
            card.Cursor = Cursors.Hand;
            card.Child = new StackPanel { Children = { glyph, label, state } };
            System.Windows.Automation.AutomationProperties.SetName(card, Loc.Get(name));
            int index = i;
            Pressable(card, () => _legionParts?.Selected == index ? Brushes.Transparent : CardBg, CardHover, gentle: true);
            card.MouseLeftButtonDown += async (_, e) =>
            {
                e.Handled = true;
                if (_legionParts?.Selected == index) { Bounce(glyph, 1.22); return; }
                _legionBusy = true; // no poll may paint the old mode over the tap
                try
                {
                    SelectLegionMode(index, (int)mode);
                    ApplyLegion(await LegionService.SetModeAsync(mode));
                }
                finally
                {
                    _legionBusy = false;
                }
            };
            Grid.SetColumn(card, i * 2);
            card.Margin = new Thickness(0, 0, 0, 8);
            grid.Children.Add(card);
            modes[i] = new ModeTile { Card = card, Glyph = glyph, Ink = ink, State = state };
        }

        // Columns are shared out at layout time: keep the pill on its tile when the width settles.
        grid.SizeChanged += (_, _) =>
        {
            if (_legionParts is { Selected: >= 0 } parts && ReferenceEquals(parts.ModeGrid, grid))
                parts.PillShift.X = PillX(parts, parts.Selected);
        };
        return grid;
    }

    private static double PillX(LegionParts parts, int index) => index * (parts.Modes[0].Card.ActualWidth + LegionTileGap);

    /// <summary>Moves the page to a mode: pill, colours, glyphs and labels, all animated.</summary>
    private void SelectLegionMode(int index, int rawMode)
    {
        if (_legionParts is not { } parts || parts.Selected == index && index >= 0) return;
        int previous = parts.Selected;
        parts.Selected = index;

        var glow = index >= 0 ? LegionModes[index].Glow : rawMode == (int)LegionMode.Custom ? LegionCustomGlow : ((SolidColorBrush)Muted).Color;
        bool first = previous == -2;
        double fade = first ? 0 : 520;
        var wash = Color.FromArgb(0x38, glow.R, glow.G, glow.B);
        SetOrTint(_legionAccent, glow, fade);
        SetOrTint(_legionWash, wash, fade);
        // Lit key tiles follow the page into the new colour.
        foreach (var key in new[] { parts.WinKey, parts.Touchpad })
            if (key.On == true)
            {
                SetOrTint(key.Ink, glow, fade);
                SetOrTint((SolidColorBrush)key.Card.Background, wash, fade);
            }
        if (!AnimationConfig.ReduceMotion)
            parts.PillGlow.BeginAnimation(DropShadowEffect.ColorProperty, new ColorAnimation(glow, TimeSpan.FromMilliseconds(Math.Max(fade, 1))) { EasingFunction = WeatherMotion.Out });
        else parts.PillGlow.Color = glow;

        for (int i = 0; i < parts.Modes.Length; i++)
        {
            var tile = parts.Modes[i];
            bool on = i == index;
            SetOrTint((SolidColorBrush)tile.Card.Background, on ? Colors.Transparent : ((SolidColorBrush)CardBg).Color, fade == 0 ? 0 : 260);
            SetOrTint(tile.Ink, on ? glow : ((SolidColorBrush)Muted).Color, fade == 0 ? 0 : 320);
            tile.State.Text = on ? Loc.Get("legion.active") : "";
            tile.State.Foreground = on ? _legionAccent : Muted;
        }
        parts.CustomNote.Visibility = rawMode == (int)LegionMode.Custom && IsExpanded("legion.mode") ? Visibility.Visible : Visibility.Collapsed;

        _legionLoops.StopAll();
        if (index < 0)
        {
            parts.Pill.BeginAnimation(OpacityProperty, WeatherMotion.To(parts.Pill.Opacity, 0, 200, WeatherMotion.Out));
            return;
        }

        var activeGlyph = parts.Modes[index].Glyph;
        if (first || previous < 0 || AnimationConfig.ReduceMotion)
        {
            parts.PillShift.BeginAnimation(TranslateTransform.XProperty, null);
            parts.PillShift.X = PillX(parts, index);
            parts.Pill.BeginAnimation(OpacityProperty, WeatherMotion.To(parts.Pill.Opacity, 1, 360, WeatherMotion.Out, first ? 180 : 0));
            if (!AnimationConfig.ReduceMotion)
            {
                parts.PillScale.BeginAnimation(ScaleTransform.ScaleXProperty, WeatherMotion.To(0.78, 1, 640, SoftSpring, first ? 180 : 0));
                parts.PillScale.BeginAnimation(ScaleTransform.ScaleYProperty, WeatherMotion.To(0.78, 1, 640, SoftSpring, first ? 180 : 0));
            }
        }
        else
        {
            // The pill flies over: it stretches along the way (more the further it goes) and lands with a wobble.
            double from = parts.PillShift.X, to = PillX(parts, index);
            int distance = Math.Abs(index - previous);
            // The same small overshoot (about a tenth of a tile) however far it flies, so it never leaves the row.
            var fly = WeatherMotion.To(from, to, 520 + distance * 60, FreezeEase(new BackEase { Amplitude = 0.4 / distance, EasingMode = EasingMode.EaseOut }));
            parts.PillShift.BeginAnimation(TranslateTransform.XProperty, fly);
            var stretch = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(620) };
            stretch.KeyFrames.Add(new EasingDoubleKeyFrame(1 + 0.09 * distance, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(140)), PressEase));
            stretch.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(620)), SoftSpring));
            var squash = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(620) };
            squash.KeyFrames.Add(new EasingDoubleKeyFrame(1 - 0.045 * distance, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(140)), PressEase));
            squash.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(620)), SoftSpring));
            Timeline.SetDesiredFrameRate(stretch, AnimationConfig.TargetFps);
            Timeline.SetDesiredFrameRate(squash, AnimationConfig.TargetFps);
            parts.PillScale.BeginAnimation(ScaleTransform.ScaleXProperty, stretch);
            parts.PillScale.BeginAnimation(ScaleTransform.ScaleYProperty, squash);
            parts.Pill.BeginAnimation(OpacityProperty, WeatherMotion.To(parts.Pill.Opacity, 1, 200, WeatherMotion.Out));
            Bounce(activeGlyph, 1.4);
            if (previous >= 0) Bounce(parts.Modes[previous].Glyph, 0.85);
        }

        // The lit mode breathes softly while the page is open.
        if (IsVisible)
        {
            _legionLoops.Swing(activeGlyph, OpacityProperty, 1, 0.55, 1.4, delaySeconds: 0.8);
            _legionLoops.Swing(parts.PillGlow, DropShadowEffect.BlurRadiusProperty, 18, 28, 1.4, delaySeconds: 0.8);
        }
    }

    #endregion

    #region Gauges and keys

    /// <summary>A three-quarter ring that fills with the temperature in the page's colour; the number rolls.</summary>
    private Gauge GaugeTile(string label, out Border card)
    {
        const double Size = 62, Stroke = 5;
        double r = (Size - Stroke) / 2, c = Size / 2;
        var track = new Path
        {
            Data = Arc(c, c, r, 135, 270),
            Stroke = Track,
            StrokeThickness = Stroke,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };
        var value = new Path
        {
            Stroke = _legionAccent,
            StrokeThickness = Stroke,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 6, ShadowDepth = 0, Opacity = 0.4 }
        };
        var number = new TextBlock
        {
            Text = "–",
            FontFamily = ControlFont,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = VNotch.Services.UiPalette.PrimaryBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 0, 2)
        };
        var caption = MakeLabel(label, 10, Muted);
        caption.HorizontalAlignment = HorizontalAlignment.Center;
        caption.VerticalAlignment = VerticalAlignment.Bottom;
        caption.Margin = new Thickness(0, 0, 0, -2);
        var dial = new Grid { Width = Size, Height = Size, Children = { track, value, number, caption } };

        card = Card();
        card.Padding = new Thickness(8, 8, 8, 6);
        card.Child = dial;
        dial.HorizontalAlignment = HorizontalAlignment.Center;

        var gauge = new Gauge { Value = value, Number = number, Tween = new WeatherTween() };
        gauge.Tween.Step = t =>
        {
            value.Data = Arc(c, c, r, 135, 270 * Math.Clamp((t - 30) / 70, 0, 1));
            number.Text = $"{Math.Round(t)}°";
        };
        return gauge;
    }

    private static void SetGauge(Gauge gauge, int celsius)
    {
        if (celsius <= 0) return;
        double from = double.IsNaN(gauge.Shown) ? 30 : gauge.Tween.Value;
        bool first = double.IsNaN(gauge.Shown);
        if (gauge.Shown == celsius) return;
        gauge.Shown = celsius;
        // The first reading sweeps the ring up from cold; later ones glide.
        gauge.Tween.Play(from, celsius, first ? 1100 : 700, first ? WeatherMotion.OutQuint : WeatherMotion.Out, first ? 250 : 0);
        gauge.Number.Foreground = celsius >= 90 ? Red : VNotch.Services.UiPalette.PrimaryBrush;
    }

    private static Geometry Arc(double cx, double cy, double r, double startDegrees, double sweepDegrees)
    {
        if (sweepDegrees < 0.5) return Geometry.Empty;
        double a0 = startDegrees * Math.PI / 180, a1 = (startDegrees + sweepDegrees) * Math.PI / 180;
        var figure = new PathFigure { StartPoint = new Point(cx + r * Math.Cos(a0), cy + r * Math.Sin(a0)), IsFilled = false };
        figure.Segments.Add(new ArcSegment(new Point(cx + r * Math.Cos(a1), cy + r * Math.Sin(a1)), new Size(r, r), 0,
            sweepDegrees > 180, SweepDirection.Clockwise, true));
        var geometry = new PathGeometry { Figures = { figure } };
        geometry.Freeze();
        return geometry;
    }

    /// <summary>A key lock as a tile, lit in the page's colour while the key works.</summary>
    private KeyTile LegionKeyTile(string glyphText, string label, Func<bool, Task<LegionState?>> apply)
    {
        var ink = new SolidColorBrush(((SolidColorBrush)Muted).Color);
        var glyph = new TextBlock { Text = glyphText, FontFamily = SegoeSymbolFont, FontSize = 17, Foreground = ink, HorizontalAlignment = HorizontalAlignment.Left };
        var name = MakeLabel(label, 12.5, VNotch.Services.UiPalette.PrimaryBrush);
        name.Margin = new Thickness(0, 7, 0, 0);
        var state = MakeLabel("", 11, Muted);
        state.Margin = new Thickness(0, 1, 0, 0);
        var card = Card();
        card.Padding = new Thickness(12, 10, 12, 10);
        card.Cursor = Cursors.Hand;
        card.Child = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { glyph, name, state } };
        System.Windows.Automation.AutomationProperties.SetName(card, label);
        var tile = new KeyTile { Card = card, Glyph = glyph, Ink = ink, State = state };
        Pressable(card, () => tile.On == true ? _legionWash : CardBg, CardHover, gentle: true);
        card.MouseLeftButtonDown += async (_, e) =>
        {
            e.Handled = true;
            bool next = tile.On != true;
            _legionBusy = true;
            try
            {
                PaintKey(tile, next, animate: true);
                ApplyLegion(await apply(next));
            }
            finally
            {
                _legionBusy = false;
            }
        };
        return tile;
    }

    private void PaintKey(KeyTile tile, bool on, bool animate)
    {
        if (tile.On == on) return;
        bool changed = tile.On != null;
        tile.On = on;
        double ms = animate || changed ? 280 : 0;
        SetOrTint((SolidColorBrush)tile.Card.Background, on ? _legionWash.Color : ((SolidColorBrush)CardBg).Color, ms);
        SetOrTint(tile.Ink, on ? _legionAccent.Color : ((SolidColorBrush)Muted).Color, ms);
        tile.State.Text = Loc.Get(on ? "control.on" : "legion.locked");
        if (changed) Bounce(tile.Glyph, on ? 1.32 : 0.84);
    }

    private void UpdateLegion(LegionState state)
    {
        if (_legionParts is not { } parts) return;
        int index = Array.FindIndex(LegionModes, m => (int)m.Mode == state.Mode);
        if (index != parts.Selected || parts.Selected == -2) SelectLegionMode(index, state.Mode);
        SetGauge(parts.Cpu, state.CpuTemp);
        SetGauge(parts.Gpu, state.GpuTemp);
        PaintKey(parts.WinKey, !state.WinKeyLocked, animate: false);
        PaintKey(parts.Touchpad, !state.TouchpadLocked, animate: false);
    }

    #endregion

    private FrameworkElement LegionSetupTile()
    {
        var label = MakeLabel(Loc.Get("legion.setup"), 12.5, VNotch.Services.UiPalette.PrimaryBrush);
        var card = Card();
        card.Cursor = Cursors.Hand;
        card.HorizontalAlignment = HorizontalAlignment.Left;
        card.Margin = new Thickness(0, 4, 0, 0);
        card.Child = label;
        Pressable(card, () => CardBg, CardHover);
        card.MouseLeftButtonDown += async (_, e) =>
        {
            e.Handled = true;
            label.Text = Loc.Get("legion.settingUp");
            await LegionService.RunSetupAsync();
            _legionKey = "";
            RefreshLegion();
        };
        return card;
    }

    #endregion
}
