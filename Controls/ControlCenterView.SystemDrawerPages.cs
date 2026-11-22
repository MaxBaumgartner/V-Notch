using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using VNotch.Services;
using VNotch.Services.ControlCenter;
using VNotch.Services.Displays;

namespace VNotch.Controls;

/// <summary>
/// The drawer's two pages. Prozesse: the five apps that use the most CPU or RAM, re-sorted as they change, each
/// ended with two taps. Bildschirme: the screens as they are arranged, drawn to scale (tap picks one, a drag across
/// it dims it), the picked screen's refresh rate, resolution and scaling, and a countdown that undoes a risky change.
/// </summary>
public partial class ControlCenterView
{
    private const double ProcRowHeight = 32;
    private const double ArrangementWidth = 214, ArrangementHeight = 150;

    private sealed class ProcRow
    {
        public required Grid Row;
        public required TextBlock Name, Cpu, Ram;
        public required Border End;
        public required TranslateTransform Slot;
        public int Index = -1;
    }

    private sealed class ScreenTile
    {
        public required DisplayInfo Display;
        public required Grid Root;
        public required Border Fill, Outline;
        public required TextBlock Value, ValueDark;
        public required double Width;
    }

    private Canvas? _procList;
    private TextBlock? _procWaiting;
    private bool _procByMemory;
    private readonly Dictionary<string, ProcRow> _procRows = new(StringComparer.OrdinalIgnoreCase);
    private DispatcherTimer? _endDisarm;

    private Canvas? _arrangement;
    private StackPanel? _dispOptions, _dispOff;
    private Border? _dispConfirm;
    private TextBlock? _dispCountdown;
    private string? _dispSelected;
    private readonly List<ScreenTile> _screenTiles = new();

    #region Processes

    private FrameworkElement BuildProcessPage()
    {
        var page = new StackPanel { Visibility = Visibility.Collapsed, RenderTransform = new TranslateTransform() };
        page.Children.Add(DrawerPageHeader(Say("Prozesse", "Processes"), out var right));
        right.Children.Add(Segmented(["CPU", "RAM"], _procByMemory ? 1 : 0, i =>
        {
            _procByMemory = i == 1;
            _topMode = _procByMemory ? TopMode.Memory : TopMode.Cpu;
            _endArmed = null;
            RefreshSoon(0);
        }).Root);
        // The connection's speed lives here now, with the apps that use it, so the main page can breathe.
        _netLine = CompactText("", 11.5, CompactLabel);
        _netLine.Margin = new Thickness(2, -2, 0, 8);
        Typography.SetNumeralAlignment(_netLine, FontNumeralAlignment.Tabular);
        page.Children.Add(_netLine);
        var head = ProcGrid();
        head.Height = 20;
        foreach (var (text, column) in new[] { ("App", 0), ("CPU", 1), ("RAM", 2) })
        {
            var label = CompactText(text, 11, CompactLabel);
            label.VerticalAlignment = VerticalAlignment.Center;
            if (column > 0) label.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(label, column);
            head.Children.Add(label);
        }
        page.Children.Add(head);
        page.Children.Add(new Border { Height = 1, Background = DrawerHair, Margin = new Thickness(0, 2, 0, 2) });
        _procList = new Canvas { Height = 0 };
        _procList.SizeChanged += (_, e) => { foreach (var row in _procRows.Values) row.Row.Width = e.NewSize.Width; };
        page.Children.Add(_procList);
        _procWaiting = CompactText(Say("Liest die Prozesse …", "Reading processes …"), 12, CompactLabel);
        _procWaiting.Margin = new Thickness(0, 8, 0, 6);
        page.Children.Add(_procWaiting);
        return page;
    }

    private static Grid ProcGrid()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
        return grid;
    }

    /// <summary>Rows stay across refreshes: numbers change in place and a new order glides.</summary>
    private void FillProcessPage(List<ProcessLoad>? top)
    {
        if (_procWaiting != null) _procWaiting.Visibility = top is { Count: > 0 } ? Visibility.Collapsed : Visibility.Visible;
        if (_procList == null || top == null) return;
        _lastTop = top;
        bool motion = _procList.IsVisible && !AnimationConfig.ReduceMotion;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < top.Count; i++)
        {
            var load = top[i];
            seen.Add(load.Name);
            if (!_procRows.TryGetValue(load.Name, out var row))
            {
                row = ProcLine(load, i);
                _procRows[load.Name] = row;
                _procList.Children.Add(row.Row);
                if (motion) PopIn(row.Row, Motion.Stagger(i).TotalMilliseconds);
            }
            row.Name.Inlines.Clear();
            row.Name.Inlines.Add(new Run(load.Title));
            if (load.Instances > 1) row.Name.Inlines.Add(new Run($"  × {load.Instances}") { FontSize = 11, Foreground = CompactLabel });
            row.Cpu.Text = $"{load.CpuPercent:0.0} %";
            row.Ram.Text = FormatBytes(load.MemoryBytes);
            row.Cpu.Foreground = _procByMemory ? CompactLabel : CompactInk;
            row.Ram.Foreground = _procByMemory ? CompactInk : CompactLabel;
            PaintEnd(row, load);
            if (row.Index == i) continue;
            row.Index = i;
            if (motion) Motion.To(row.Slot, TranslateTransform.YProperty, i * ProcRowHeight, Motion.Glide);
            else Motion.Stop(row.Slot, TranslateTransform.YProperty, i * ProcRowHeight);
        }
        foreach (var name in _procRows.Keys.ToList())
        {
            if (seen.Contains(name)) continue;
            var gone = _procRows[name];
            _procRows.Remove(name);
            if (!motion) { _procList.Children.Remove(gone.Row); continue; }
            var list = _procList;
            Motion.To(gone.Row, OpacityProperty, 0, Motion.Exit, done: () => list.Children.Remove(gone.Row));
        }
        double height = top.Count * ProcRowHeight;
        if (motion) Motion.To(_procList, HeightProperty, height, Motion.Glide);
        else Motion.Stop(_procList, HeightProperty, height);
        if (_drawerPage == DrawerPage.Processes) RefitDrawer();
    }

    private ProcRow ProcLine(ProcessLoad load, int index)
    {
        var grid = ProcGrid();
        grid.Height = ProcRowHeight;
        if (_procList!.ActualWidth > 0) grid.Width = _procList.ActualWidth;
        var name = CompactText("", 12.5, CompactInk);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.VerticalAlignment = VerticalAlignment.Center;
        var cpu = CompactText("", 12, CompactInk);
        var ram = CompactText("", 12, CompactLabel);
        foreach (var (block, column) in new[] { (cpu, 1), (ram, 2) })
        {
            block.HorizontalAlignment = HorizontalAlignment.Right;
            block.VerticalAlignment = VerticalAlignment.Center;
            Typography.SetNumeralAlignment(block, FontNumeralAlignment.Tabular);
            Grid.SetColumn(block, column);
        }
        var endText = CompactText(Loc.Get("control.top.end"), 11.5, LinkInk);
        endText.HorizontalAlignment = HorizontalAlignment.Center;
        endText.VerticalAlignment = VerticalAlignment.Center;
        var end = new Border
        {
            Height = 26, MinWidth = 76, CornerRadius = new CornerRadius(13), Padding = new Thickness(11, 0, 11, 0), Background = Frozen("#0FFFFFFF"),
            BorderThickness = new Thickness(1), BorderBrush = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand, Child = endText
        };
        Pressable(end, () => Frozen("#0FFFFFFF"), CardHover);
        string processName = load.Name;
        end.MouseLeftButtonUp += (_, e) => { e.Handled = true; EndFromPage(processName); };
        Grid.SetColumn(end, 3);
        grid.Children.Add(name);
        grid.Children.Add(cpu);
        grid.Children.Add(ram);
        grid.Children.Add(end);
        var slot = new TranslateTransform(0, index * ProcRowHeight);
        grid.RenderTransform = slot;
        return new ProcRow { Row = grid, Name = name, Cpu = cpu, Ram = ram, End = end, Slot = slot, Index = index };
    }

    private void PaintEnd(ProcRow row, ProcessLoad load)
    {
        row.End.Visibility = load.CanEnd ? Visibility.Visible : Visibility.Hidden;
        bool armed = _endArmed == load.Name && DateTime.UtcNow < _endArmedUntil;
        ((TextBlock)row.End.Child).Text = Loc.Get(armed ? "control.top.confirm" : "control.top.end");
        ((TextBlock)row.End.Child).Foreground = armed ? CompactInk : LinkInk;
        row.End.BorderBrush = armed ? new SolidColorBrush(RoseColor) : Brushes.Transparent;
    }

    /// <summary>The first tap asks ("Sicher?") for a few seconds, the second ends the app.</summary>
    private async void EndFromPage(string name)
    {
        if (_endArmed != name || DateTime.UtcNow >= _endArmedUntil)
        {
            _endArmed = name;
            _endArmedUntil = DateTime.UtcNow + EndConfirmWindow;
            FillProcessPage(_lastTop);
            _endDisarm?.Stop();
            _endDisarm = new DispatcherTimer(DispatcherPriority.Background) { Interval = EndConfirmWindow };
            _endDisarm.Tick += (_, _) => { _endDisarm?.Stop(); _endArmed = null; FillProcessPage(_lastTop); };
            _endDisarm.Start();
            return;
        }
        _endArmed = null;
        _endDisarm?.Stop();
        await Task.Run(() => ProcessLoadSampler.End(name));
        if (!_disposed) RefreshSoon(200);
    }

    #endregion

    #region Displays

    private FrameworkElement BuildDisplayPage()
    {
        var page = new StackPanel { Visibility = Visibility.Collapsed, RenderTransform = new TranslateTransform() };
        page.Children.Add(DrawerPageHeader(Say("Bildschirme", "Displays"), out var right));
        right.Children.Add(DrawerLink(Say("Windows-Einstellungen", "Windows settings"), heading: false, () =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:display") { UseShellExecute = true }); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { RuntimeLog.Warn("CONTROL-CENTER", $"Display settings not opened: {ex.Message}"); }
        }));
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ArrangementWidth) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _arrangement = new Canvas { Width = ArrangementWidth, Height = 80, Margin = new Thickness(0, 4, 0, 0) };
        _dispOff = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        var hint = CompactText(Say("Tippen wählt aus · Ziehen dimmt", "Tap to pick · drag to dim"), 10.5, Frozen("#66FFFFFF"));
        hint.HorizontalAlignment = HorizontalAlignment.Center;
        hint.Margin = new Thickness(0, 8, 0, 0);
        body.Children.Add(new StackPanel { Children = { _arrangement, _dispOff, hint } });
        _dispOptions = new StackPanel();
        Grid.SetColumn(_dispOptions, 2);
        body.Children.Add(_dispOptions);
        page.Children.Add(body);

        _dispCountdown = CompactText("", 12, CompactLabel);
        _dispCountdown.VerticalAlignment = VerticalAlignment.Center;
        _dispCountdown.Margin = new Thickness(10, 0, 0, 0);
        Typography.SetNumeralAlignment(_dispCountdown, FontNumeralAlignment.Tabular);
        var ask = CompactText(Say("Bleibt das so?", "Keep this?"), 12, CompactInk);
        ask.VerticalAlignment = VerticalAlignment.Center;
        var undo = ConfirmPill(Say("Zurücksetzen", "Revert"), main: false, () => { Changes.Revert(); StopDisplayTimer(); FillDisplayPage(); });
        var keep = ConfirmPill(Loc.Get("control.displays.keep"), main: true, () => { Changes.Confirm(); StopDisplayTimer(); FillDisplayPage(); });
        var bar = new DockPanel { LastChildFill = false, Height = 40 };
        DockPanel.SetDock(keep, Dock.Right);
        DockPanel.SetDock(undo, Dock.Right);
        bar.Children.Add(keep);
        bar.Children.Add(undo);
        bar.Children.Add(ask);
        bar.Children.Add(_dispCountdown);
        _dispConfirm = new Border { BorderBrush = DrawerHair, BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(0, 10, 0, 0),
            Child = bar, Visibility = Visibility.Collapsed };
        page.Children.Add(_dispConfirm);
        return page;
    }

    private Border ConfirmPill(string text, bool main, Action click)
    {
        var label = CompactText(text, 12, main ? Frozen("#151515") : CompactInk);
        label.VerticalAlignment = VerticalAlignment.Center;
        var rest = main ? Frozen("#F0F5F5F7") : Frozen("#14FFFFFF");
        var pill = new Border { Height = 28, CornerRadius = new CornerRadius(14), Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(8, 0, 0, 0),
            Background = rest, Cursor = Cursors.Hand, Child = label, VerticalAlignment = VerticalAlignment.Center };
        Pressable(pill, () => rest, main ? Frozen("#FFFFFFFF") : CardHover);
        pill.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
        return pill;
    }

    private void StopDisplayTimer()
    {
        _displayTimer?.Stop();
        _displayTimer = null;
    }

    /// <summary>Leaving the page (or picking another screen) undoes a change that was not kept yet.</summary>
    private void RevertPendingDisplayChange()
    {
        if (!Changes.NeedsConfirm) return;
        Changes.Revert();
        StopDisplayTimer();
        if (_drawerPage == DrawerPage.Displays) FillDisplayPage();
    }

    private void ChangeFromPage(DisplayChangeKind kind, Action<IDisplayApi> change)
    {
        Changes.Apply(kind, change);
        if (Changes.NeedsConfirm && _displayTimer == null)
        {
            _displayTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _displayTimer.Tick += (_, _) => TickDisplayChanges();
            _displayTimer.Start();
        }
        FillDisplayPage();
        RefreshCompactDisplays();
    }

    private void FillDisplayPage()
    {
        if (_arrangement == null || _dispOptions == null || _dispOff == null || _dispConfirm == null) return;
        IReadOnlyList<DisplayInfo> displays;
        try { displays = DisplayApi.List(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { displays = []; }
        if (!displays.Any(d => d.Id == _dispSelected))
            _dispSelected = (displays.FirstOrDefault(d => d.Active && !d.Primary) ?? displays.FirstOrDefault(d => d.Primary) ?? displays.FirstOrDefault())?.Id;
        BuildArrangement(displays);
        BuildDisplayOptions(displays.FirstOrDefault(d => d.Id == _dispSelected));
        _dispConfirm.Visibility = Changes.NeedsConfirm ? Visibility.Visible : Visibility.Collapsed;
        UpdateDisplayCountdown();
        if (_drawerPage == DrawerPage.Displays) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, RefitDrawer);
    }

    private void UpdateDisplayCountdown()
    {
        if (_dispCountdown != null) _dispCountdown.Text = string.Format(Loc.Get("control.displays.backIn"), Changes.SecondsLeft);
    }

    /// <summary>The screens where Windows has them, scaled into the left column and centred.</summary>
    private void BuildArrangement(IReadOnlyList<DisplayInfo> displays)
    {
        _arrangement!.Children.Clear();
        _screenTiles.Clear();
        _dispOff!.Children.Clear();
        var placed = displays.Where(d => d.Active && d.Bounds.Width > 0 && d.Bounds.Height > 0).ToArray();
        if (placed.Length > 0)
        {
            int minX = placed.Min(d => d.Bounds.X), minY = placed.Min(d => d.Bounds.Y);
            int maxX = placed.Max(d => d.Bounds.X + d.Bounds.Width), maxY = placed.Max(d => d.Bounds.Y + d.Bounds.Height);
            double scale = Math.Min(ArrangementWidth / Math.Max(1, maxX - minX), ArrangementHeight / Math.Max(1, maxY - minY));
            double offsetX = (ArrangementWidth - (maxX - minX) * scale) / 2;
            _arrangement.Height = Math.Max(30, (maxY - minY) * scale);
            const double gap = 3;
            foreach (var display in placed)
            {
                double width = Math.Max(30, display.Bounds.Width * scale - gap), height = Math.Max(20, display.Bounds.Height * scale - gap);
                var tile = BuildScreenTile(display, width, height);
                Canvas.SetLeft(tile.Root, offsetX + (display.Bounds.X - minX) * scale + gap / 2);
                Canvas.SetTop(tile.Root, (display.Bounds.Y - minY) * scale + gap / 2);
                _arrangement.Children.Add(tile.Root);
                _screenTiles.Add(tile);
            }
        }
        else _arrangement.Height = 0;
        foreach (var off in displays.Where(d => !d.Active))
        {
            var line = DrawerLink(Say($"{DisplayLabel(off)} · aus", $"{DisplayLabel(off)} · off"), heading: false, () => { RevertPendingDisplayChange(); _dispSelected = off.Id; FillDisplayPage(); });
            line.Margin = new Thickness(0);
            _dispOff.Children.Add(line);
        }
        PaintScreenTiles();
    }

    private ScreenTile BuildScreenTile(DisplayInfo display, double width, double height)
    {
        Grid Readout(Brush ink, out TextBlock value)
        {
            var name = CompactText(DisplayLabel(display), 11.5, ink);
            name.FontWeight = FontWeights.SemiBold;
            name.VerticalAlignment = VerticalAlignment.Top;
            name.Margin = new Thickness(8, 6, 8, 0);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            var hz = CompactText($"{display.Mode.RefreshHz} Hz", 10.5, ink);
            hz.VerticalAlignment = VerticalAlignment.Bottom;
            hz.Margin = new Thickness(8, 0, 0, 5);
            value = CompactText("", 11, ink);
            value.FontWeight = FontWeights.SemiBold;
            value.HorizontalAlignment = HorizontalAlignment.Right;
            value.VerticalAlignment = VerticalAlignment.Bottom;
            value.Margin = new Thickness(0, 0, 8, 5);
            foreach (var block in new[] { hz, value }) Typography.SetNumeralAlignment(block, FontNumeralAlignment.Tabular);
            // Small tiles keep just the name.
            if (height < 44) hz.Visibility = value.Visibility = Visibility.Collapsed;
            return new Grid { Width = width, Height = height, HorizontalAlignment = HorizontalAlignment.Left, Children = { name, hz, value } };
        }
        var light = Readout(Frozen("#D9FFFFFF"), out var lightValue);
        var dark = Readout(Frozen("#161616"), out var darkValue);
        var fill = new Border { Background = CapsuleWhite, HorizontalAlignment = HorizontalAlignment.Left, Width = 0, ClipToBounds = true, Child = dark };
        var outline = new Border { BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(5), IsHitTestVisible = false };
        var root = new Grid
        {
            Width = width, Height = height, Background = Frozen("#10FFFFFF"), Cursor = Cursors.Hand, ToolTip = display.Name,
            Clip = new RectangleGeometry(new Rect(0, 0, width, height), 5, 5), Children = { light, fill, outline }
        };
        var tile = new ScreenTile { Display = display, Root = root, Fill = fill, Outline = outline, Value = lightValue, ValueDark = darkValue, Width = width };
        double downX = 0;
        bool down = false, moved = false;
        void SetFrom(MouseEventArgs e) => SetTileLevel(tile, Math.Clamp(e.GetPosition(root).X / width, 0, 1));
        root.MouseLeftButtonDown += (_, e) => { e.Handled = true; down = true; moved = false; downX = e.GetPosition(root).X; root.CaptureMouse(); };
        root.MouseMove += (_, e) =>
        {
            if (!down || _monitorLevels.GetValueOrDefault(display.Id) == null) return;
            if (!moved && Math.Abs(e.GetPosition(root).X - downX) < 4) return;
            moved = true;
            root.Cursor = Cursors.SizeWE;
            SetFrom(e);
        };
        root.MouseLeftButtonUp += (_, e) =>
        {
            if (!down) return;
            e.Handled = true;
            down = false;
            root.ReleaseMouseCapture();
            root.Cursor = Cursors.Hand;
            if (moved || _dispSelected == display.Id) return;
            RevertPendingDisplayChange();
            _dispSelected = display.Id;
            FillDisplayPage();
        };
        root.LostMouseCapture += (_, _) => { down = false; root.Cursor = Cursors.Hand; };
        root.PreviewMouseWheel += (_, e) =>
        {
            if (_monitorLevels.GetValueOrDefault(display.Id) is not { } level) return;
            e.Handled = true;
            SetTileLevel(tile, Math.Clamp((Math.Round(level / 5.0) * 5 + Math.Sign(e.Delta) * 5) / 100.0, 0, 1));
        };
        return tile;
    }

    /// <summary>Brightness and selection on every tile; a screen without DDC shows no level.</summary>
    private void PaintScreenTiles()
    {
        foreach (var tile in _screenTiles)
        {
            bool selected = tile.Display.Id == _dispSelected;
            tile.Outline.BorderBrush = selected ? Frozen("#D9FFFFFF") : Frozen("#40FFFFFF");
            var level = _monitorLevels.GetValueOrDefault(tile.Display.Id);
            tile.Value.Text = tile.ValueDark.Text = level is { } percent ? $"{percent} %" : "";
            double width = level is { } p ? p / 100.0 * tile.Width : 0;
            if (tile.Fill.IsVisible && !AnimationConfig.ReduceMotion && !tile.Root.IsMouseCaptured) Motion.To(tile.Fill, WidthProperty, width, Motion.Shift);
            else Motion.Stop(tile.Fill, WidthProperty, width);
        }
    }

    private void SetTileLevel(ScreenTile tile, double level)
    {
        int percent = (int)Math.Round(level * 100);
        _monitorLevels[tile.Display.Id] = percent;
        tile.Value.Text = tile.ValueDark.Text = $"{percent} %";
        Motion.Stop(tile.Fill, WidthProperty, level * tile.Width);
        DisplayBrightness.Set(tile.Display.Id, percent);
        // The face's capsule and the drawer's screen show the same screen: they follow.
        if (tile.Display.Id == _brightnessDisplay) ShowCapsuleLevel(_brightnessCapsule, level);
        if (_screenSlider?.Id == tile.Display.Id) SetScreenLevel(_screenSlider, level, user: false);
    }

    private void BuildDisplayOptions(DisplayInfo? display)
    {
        var host = _dispOptions!;
        host.Children.Clear();
        if (display == null)
        {
            host.Children.Add(CompactText(Say("Keine Bildschirme gefunden", "No displays found"), 12, CompactLabel));
            return;
        }
        var title = CompactText(display.Internal ? Say("Laptop-Bildschirm", "Laptop screen") : display.Name, 13, CompactInk);
        title.FontWeight = FontWeights.SemiBold;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        string role = display.Primary ? Say("Hauptanzeige", "Main display") : display.Active ? Say("Zweitanzeige", "Second display") : Say("Aus", "Off");
        var meta = CompactText(display.Active ? $"{role} · {display.Mode.Width}×{display.Mode.Height}" : role, 11.5, CompactLabel);
        meta.Margin = new Thickness(0, 2, 0, 4);
        host.Children.Add(title);
        host.Children.Add(meta);
        bool pending = Changes.NeedsConfirm;
        if (display.Active)
        {
            var rates = ModeChoices.RefreshRates(display.Modes, display.Mode.Width, display.Mode.Height).TakeLast(6).ToList();
            AddOption(Loc.Get("control.displays.refresh"), rates.Select(hz => $"{hz} Hz").ToList(), rates.IndexOf(display.Mode.RefreshHz), pending, i =>
                ChangeFromPage(DisplayChangeKind.Mode, api => api.SetModes(new Dictionary<string, DisplayMode> { [display.Id] = display.Mode with { RefreshHz = rates[i] } })));
            var sizes = ModeChoices.NearbySizes(display.Modes, display.Mode.Width, display.Mode.Height).Select(m => (m.W, m.H)).ToList();
            if (!sizes.Contains((display.Mode.Width, display.Mode.Height))) sizes.Add((display.Mode.Width, display.Mode.Height));
            sizes = sizes.OrderBy(s => (long)s.W * s.H).ToList();
            int current = sizes.IndexOf((display.Mode.Width, display.Mode.Height));
            sizes = sizes.Skip(Math.Max(0, Math.Min(current - 1, sizes.Count - 3))).Take(3).ToList();
            AddOption(Loc.Get("control.displays.resolution"), sizes.Select(s => $"{s.W}×{s.H}").ToList(), sizes.IndexOf((display.Mode.Width, display.Mode.Height)), pending, i =>
            {
                var size = sizes[i];
                var mode = display.Modes.Where(m => m.Width == size.W && m.Height == size.H).OrderBy(m => Math.Abs(m.RefreshHz - display.Mode.RefreshHz)).FirstOrDefault();
                if (mode != null) ChangeFromPage(DisplayChangeKind.Mode, api => api.SetModes(new Dictionary<string, DisplayMode> { [display.Id] = mode }));
            });
            var zooms = display.ZoomSteps.Take(5).ToList();
            if (zooms.Count > 1)
                AddOption(Loc.Get("control.displays.zoom"), zooms.Select(z => $"{z} %").ToList(), zooms.IndexOf(display.ZoomPercent), pending, i =>
                    ChangeFromPage(DisplayChangeKind.Zoom, api => api.SetZoom(display.Id, zooms[i])));
        }
        var toggles = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        if (display.Active && display.HdrSupported)
            toggles.Children.Add(DisplaySwitch(Loc.Get("control.displays.hdr"), display.HdrOn, !pending,
                on => ChangeFromPage(DisplayChangeKind.Hdr, api => api.SetHdr(display.Id, on))));
        toggles.Children.Add(DisplaySwitch(Say("Hauptanzeige", "Main display"), display.Primary, !pending && !display.Primary && display.Active,
            _ => ChangeFromPage(DisplayChangeKind.Primary, api => api.SetPrimary(display.Id))));
        toggles.Children.Add(DisplaySwitch(Loc.Get("control.displays.on"), display.Active, !pending && !display.Primary,
            on => ChangeFromPage(DisplayChangeKind.Active, api => api.SetActive(display.Id, on))));
        host.Children.Add(toggles);
    }

    private void AddOption(string label, IReadOnlyList<string> items, int index, bool locked, Action<int> pick)
    {
        if (items.Count == 0) return;
        var name = CompactText(label, 11, CompactLabel);
        name.Margin = new Thickness(0, 6, 0, 3);
        // One choice is no choice: just the value.
        FrameworkElement segment = items.Count == 1 ? CompactText(items[0], 12, CompactInk) : Segmented(items, index, pick).Root;
        if (locked) { segment.IsEnabled = false; segment.Opacity = .45; }
        _dispOptions!.Children.Add(name);
        _dispOptions.Children.Add(segment);
    }

    /// <summary>A screen's short name; the laptop's own panel (or a nameless one) gets a plain word instead of "\\.\DISPLAY1".</summary>
    private static string DisplayLabel(DisplayInfo display)
    {
        if (display.Internal) return "Laptop";
        if (display.Name.Length == 0 || display.Name.StartsWith(@"\\", StringComparison.Ordinal)) return Say("Bildschirm", "Display");
        return ShortMonitorName(display.Name);
    }

    private FrameworkElement DisplaySwitch(string text, bool on, bool enabled, Action<bool> changed)
    {
        var label = CompactText(text, 12, LinkInk);
        label.VerticalAlignment = VerticalAlignment.Center;
        label.Margin = new Thickness(0, 0, 7, 0);
        var toggle = TintedToggle(on, ConnectionBlue, changed);
        var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16, 4), Children = { label, toggle } };
        if (!enabled) { item.IsEnabled = false; item.Opacity = .45; }
        return item;
    }

    #endregion
}
