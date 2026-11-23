using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using VNotch.Services;
using VNotch.Services.ControlCenter;

namespace VNotch.Controls;

/// <summary>
/// The OMEN laptop's performance mode as the system tab's cover tile, where a Lenovo Legion shows its own: the same
/// track with a thumb, but it only ever switches on a second tap. The first tap rings the slot and asks; a tap on the
/// current mode or three seconds without a second tap call it off.
/// </summary>
public partial class ControlCenterView
{
    #region Laptop tile

    private Border? _laptopTile, _laptopTileThumb, _laptopTileRing;
    private TextBlock? _laptopTileGlyph, _laptopTileThumbGlyph, _laptopTileReadout;
    private Grid? _laptopTileName;
    private readonly TranslateTransform _laptopTileThumbShift = new(), _laptopTileRingShift = new();
    private readonly List<TextBlock> _laptopTileSlotGlyphs = new();
    private int _laptopTileShown = -1;
    private int? _gpuTemp;

    /// <summary>CPU °C: Lenovo's helper on a Legion, the OMEN BIOS on an HP.</summary>
    private int? CpuTempNow => _legionAttached ? _legion?.CpuTemp is > 0 ? _legion.CpuTemp : null : _laptopStatus?.Temperature is > 0 ? _laptopStatus.Temperature : null;

    /// <summary>GPU °C: Lenovo's helper on a Legion, otherwise what the graphics driver tells Windows.</summary>
    private int? GpuTempNow => _legionAttached && _legion?.GpuTemp is > 0 ? _legion.GpuTemp : _gpuTemp;

    /// <summary>Built once into the face's middle column, which stays empty without a Legion.</summary>
    private void EnsureLaptopTile()
    {
        if (_laptopTile != null || _legionAttached || _faceGrid == null) return;
        var symbols = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        _laptopTileGlyph = new TextBlock { FontFamily = symbols, FontSize = 30, Foreground = CompactInk, HorizontalAlignment = HorizontalAlignment.Left };
        _laptopTileReadout = CompactText(Loc.Get("control.laptop.mode"), 11, CompactLabel);
        _laptopTileReadout.HorizontalAlignment = HorizontalAlignment.Right;
        _laptopTileReadout.VerticalAlignment = VerticalAlignment.Top;
        _laptopTileReadout.Margin = new Thickness(36, 0, 0, 0);
        _laptopTileReadout.TextTrimming = TextTrimming.CharacterEllipsis;
        Typography.SetNumeralAlignment(_laptopTileReadout, FontNumeralAlignment.Tabular);
        var top = new Grid { Children = { _laptopTileGlyph, _laptopTileReadout } };
        _laptopTileName = new Grid { Height = 34, ClipToBounds = true, Margin = new Thickness(0, 4, 0, 0) };

        var track = new Grid
        {
            Height = 36, Width = 2 + ModeSlotWidth * LaptopModes.Length + 2, HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom, Cursor = Cursors.Hand, Background = Brushes.Transparent
        };
        track.Children.Add(new Border { CornerRadius = new CornerRadius(18), Background = Frozen("#14FFFFFF") });
        var slots = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2) };
        for (int i = 0; i < LaptopModes.Length; i++)
        {
            var glyph = new TextBlock { Text = LegionModes[i].Glyph, FontFamily = symbols, FontSize = 14, Foreground = CompactLabel,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            _laptopTileSlotGlyphs.Add(glyph);
            slots.Children.Add(new Border { Width = ModeSlotWidth, Height = 32, Background = Brushes.Transparent, Child = glyph,
                ToolTip = Loc.Get("control.laptop." + LaptopModes[i]) });
        }
        track.Children.Add(slots);
        _laptopTileRing = new Border
        {
            Width = ModeSlotWidth, Height = 32, CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1.5),
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(2, 0, 0, 0), RenderTransform = _laptopTileRingShift,
            IsHitTestVisible = false, Visibility = Visibility.Collapsed
        };
        track.Children.Add(_laptopTileRing);
        _laptopTileThumbGlyph = new TextBlock { FontFamily = symbols, FontSize = 15, Foreground = Frozen("#151515"),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _laptopTileThumb = new Border
        {
            Width = ModeSlotWidth, Height = 32, CornerRadius = new CornerRadius(16), Background = Frozen("#F0F5F5F7"),
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(2, 0, 0, 0), RenderTransform = _laptopTileThumbShift,
            IsHitTestVisible = false, Child = _laptopTileThumbGlyph
        };
        track.Children.Add(_laptopTileThumb);
        // A press and a release on the track make one tap; nothing switches on the way down, so a tile sliding under
        // the pointer while the tab opens cannot count.
        track.MouseLeftButtonDown += (_, e) => { e.Handled = true; track.CaptureMouse(); };
        track.MouseLeftButtonUp += (_, e) =>
        {
            if (!track.IsMouseCaptured) return;
            e.Handled = true;
            track.ReleaseMouseCapture();
            TapLaptopMode(LaptopSlotAt(e.GetPosition(track).X));
        };

        var stack = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(top, Dock.Top);
        stack.Children.Add(top);
        DockPanel.SetDock(_laptopTileName, Dock.Top);
        stack.Children.Add(_laptopTileName);
        DockPanel.SetDock(track, Dock.Bottom);
        stack.Children.Add(track);
        _laptopTile = new Border
        {
            MinWidth = 2 * 16 + 2 + ModeSlotWidth * LaptopModes.Length + 2, CornerRadius = new CornerRadius(24),
            Background = Frozen("#0FFFFFFF"), BorderThickness = new Thickness(1), BorderBrush = Frozen("#1AFFFFFF"),
            Padding = new Thickness(16, 14, 16, 14), Child = stack, Visibility = Visibility.Collapsed
        };
        Grid.SetColumn(_laptopTile, 1);
        _faceGrid.Children.Add(_laptopTile);
        // The light behind the face gathers at the cover tile; this one is it on an HP.
        _modeTile ??= _laptopTile;
        _laptopTile.SizeChanged += (_, _) => GatherSystemLightSoon();
        _laptopTile.IsVisibleChanged += (_, _) => GatherSystemLightSoon();
    }

    internal static int LaptopSlotAt(double x) => Math.Clamp((int)((x - 2) / ModeSlotWidth), 0, LaptopModes.Length - 1);

    private void TapLaptopMode(int index)
    {
        if (_laptopStatus?.Mode is not { } current) return;
        string chosen = LaptopModes[index];
        if (chosen == current)
        {
            if (_laptopModeArmed == null) return;
            _laptopModeArmed = null;
            LaptopArmChanged();
            return;
        }
        if (_laptopModeArmed != chosen)
        {
            _laptopModeArmed = chosen;
            LaptopArmChanged();
            DisarmLaptopModeLater(chosen);
            return;
        }
        _laptopModeArmed = null;
        RuntimeLog.Log("LAPTOP", $"Performance mode {current} -> {chosen}, confirmed on the system tile");
        LaptopArmChanged();
        LaptopAction(() => LaptopControl.SetMode(chosen));
    }

    /// <summary>The tile follows the laptop: the thumb sits on the mode it is in, a ringed slot waits for its second tap.</summary>
    private void PaintLaptopTile(LaptopStatus? laptop)
    {
        if (_laptopTile == null || _laptopTileName == null || _laptopTileThumb == null || _laptopTileRing == null) return;
        int index = laptop?.Mode is { } mode ? Array.IndexOf(LaptopModes, mode) : -1;
        _laptopTile.Visibility = index >= 0 ? Visibility.Visible : Visibility.Collapsed;
        if (index < 0 || laptop == null)
        {
            _laptopTileShown = -1;
            return;
        }
        int armed = _laptopModeArmed is { } wanted ? Array.IndexOf(LaptopModes, wanted) : -1;
        var mood = ModeMoods[index];
        _laptopTileGlyph!.Text = LegionModes[index].Glyph;
        _laptopTileGlyph.Foreground = new SolidColorBrush(Lift(mood.Main, .35));
        _laptopTile.BorderBrush = new SolidColorBrush(Alpha(mood.Main, .5));
        _laptopTileReadout!.Text = armed >= 0 ? Loc.Get("control.laptop.tapAgain") : TileReadout(laptop) is { Length: > 0 } readout ? readout : Loc.Get("control.laptop.mode");
        _laptopTileReadout.Foreground = armed >= 0 ? new SolidColorBrush(Lift(ModeMoods[armed].Main, .2)) : CompactLabel;
        string name = armed >= 0 ? Loc.Get("control.laptop." + LaptopModes[armed]) + "?" : Loc.Get("control.laptop." + LaptopModes[index]);
        SwapCompactLabel(_laptopTileName, name, horizontal: false, direction: (armed >= 0 ? armed : index) >= Math.Max(_laptopTileShown, 0) ? 1 : -1, size: 26);
        _laptopTileThumbGlyph!.Text = LegionModes[index].Glyph;
        for (int i = 0; i < _laptopTileSlotGlyphs.Count; i++) _laptopTileSlotGlyphs[i].Opacity = i == index ? 0 : 1;
        bool animate = _laptopTile.IsVisible && _laptopTileShown >= 0 && !AnimationConfig.ReduceMotion;
        if (animate) Motion.To(_laptopTileThumbShift, TranslateTransform.XProperty, index * ModeSlotWidth, Motion.Shift);
        else Motion.Stop(_laptopTileThumbShift, TranslateTransform.XProperty, index * ModeSlotWidth);
        _laptopTileShown = index;
        _laptopTileRing.Visibility = armed >= 0 ? Visibility.Visible : Visibility.Collapsed;
        if (armed >= 0)
        {
            _laptopTileRing.BorderBrush = new SolidColorBrush(ModeMoods[armed].Main);
            Motion.Stop(_laptopTileRingShift, TranslateTransform.XProperty, armed * ModeSlotWidth);
            if (_laptopTileRing.IsVisible && !AnimationConfig.ReduceMotion) Bounce(_laptopTileRing, 1.08);
        }
    }

    /// <summary>"52 °C · 3,100 rpm": the temperature and the faster fan; what the BIOS did not say is left out.</summary>
    internal static string TileReadout(LaptopStatus laptop)
    {
        string temp = laptop.Temperature is > 0 and var t ? $"{t} °C" : "";
        int fan = Math.Max(laptop.CpuFanRpm ?? 0, laptop.GpuFanRpm ?? 0);
        string fans = fan > 0 ? Loc.Get("control.laptop.fan", fan.ToString("N0", Loc.GetCulture())) : "";
        return temp.Length > 0 && fans.Length > 0 ? $"{temp} · {fans}" : temp + fans;
    }

    /// <summary>The armed mode changed: the tile and the drawer's section both show it.</summary>
    private void LaptopArmChanged()
    {
        BuildLaptopSectionIfChanged(_laptopStatus);
        PaintLaptopTile(_laptopStatus);
    }

    #endregion
}
