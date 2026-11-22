using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using VNotch.Services;
using VNotch.Services.Displays;

namespace VNotch.Controls;

public partial class ControlCenterView
{
    private readonly StackPanel _displaysSection = new();
    private IDisplayApi _displayApi = new Win32DisplayApi();
    private DisplayChanges? _displayChanges;
    private DispatcherTimer? _displayTimer;
    private IReadOnlyList<DisplayInfo> _displays = [];
    private string? _selectedDisplay;
    private int _displayRevision;
    private bool _showAllResolutions;
    private bool _expireDisplayChanges;
    private RollingNumber? _displaySeconds;
    private DdcBrightness? _displayBrightness;
    internal Func<DateTime> DisplayNow { get; set; } = () => DateTime.UtcNow;

    internal IDisplayApi DisplayApi
    {
        get => _displayApi;
        set
        {
            if (_displayApi is IDisposable disposable) disposable.Dispose();
            _displayApi = value;
            _displayChanges = null;
            _displayBrightness?.Dispose();
            _displayBrightness = null;
            _displaysSection.Children.Clear();
        }
    }

    private DisplayChanges Changes => _displayChanges ??= new DisplayChanges(DisplayApi, () => _expireDisplayChanges ? DateTime.MaxValue : DisplayNow());

    internal void RefreshDisplaysOnDisplayChange()
    {
        if (_compactSystemReady) RefreshCompactDisplays();
        if (!_disposed && Page == ControlCenterPage.System && IsVisible && IsExpanded("displays")) BuildDisplaysSection();
    }

    private void BuildDisplaysSection()
    {
        _accent = _mood;
        int revision = ++_displayRevision;
        try { _displays = DisplayApi.List(); }
        catch { _displays = []; }
        if (!_displays.Any(d => d.Id == _selectedDisplay))
            _selectedDisplay = _displays.FirstOrDefault(d => d.Primary)?.Id ?? _displays.FirstOrDefault()?.Id;
        _displaysSection.Children.Clear();
        _displaySeconds = null;
        var main = _displays.FirstOrDefault(d => d.Primary) ?? _displays.FirstOrDefault(d => d.Active);
        string summary = main == null ? _displays.Count.ToString() : $"{_displays.Count} · {main.Mode.Width}×{main.Mode.Height} · {main.Mode.RefreshHz} Hz";
        var header = BuildSectionHeader("displays", Loc.Get("control.displays.title"), "", null, 14);
        if (!IsExpanded("displays") && header is Grid grid)
        {
            var label = ColumnLabel(summary);
            label.Name = "DisplaysSummary";
            Grid.SetColumn(label, 2);
            grid.Children.Add(label);
        }
        _displaysSection.Children.Add(header);
        if (!IsExpanded("displays")) return;
        var selected = _displays.FirstOrDefault(d => d.Id == _selectedDisplay);
        if (selected == null) { ShowKeepBar(); return; }
        BuildDisplayLayout();
        var details = MakeLabel($"{selected.Name}  ·  {selected.Mode.Width}×{selected.Mode.Height}  ·  {selected.Mode.RefreshHz} Hz", 12, Muted);
        details.Margin = new Thickness(0, 4, 0, 8);
        _displaysSection.Children.Add(details);
        var brightnessHost = new StackPanel { Name = "DisplaysBrightness", Visibility = Visibility.Collapsed };
        _displaysSection.Children.Add(brightnessHost);
        _ = LoadDisplayBrightness(selected.Id, revision, brightnessHost);
        AddDisplayChoices(Loc.Get("control.displays.refresh"), ModeChoices.RefreshRates(selected.Modes, selected.Mode.Width, selected.Mode.Height).Select(hz => ($"{hz} Hz", hz == selected.Mode.RefreshHz, (Action)(() => ChangeDisplay(DisplayChangeKind.Mode, api => api.SetModes(new Dictionary<string, DisplayMode> { [selected.Id] = selected.Mode with { RefreshHz = hz } }))))));
        var sizes = _showAllResolutions ? selected.Modes.Select(m => (m.Width, m.Height)).Distinct().OrderByDescending(m => (long)m.Width * m.Height).ToArray() : ModeChoices.NearbySizes(selected.Modes, selected.Mode.Width, selected.Mode.Height).Select(m => (m.W, m.H)).ToArray();
        var sizeChoices = sizes.Select(size => ($"{size.Item1}×{size.Item2}", size.Item1 == selected.Mode.Width && size.Item2 == selected.Mode.Height, (Action)(() =>
        {
            var mode = selected.Modes.Where(m => m.Width == size.Item1 && m.Height == size.Item2).OrderBy(m => Math.Abs(m.RefreshHz - selected.Mode.RefreshHz)).FirstOrDefault();
            if (mode != null) ChangeDisplay(DisplayChangeKind.Mode, api => api.SetModes(new Dictionary<string, DisplayMode> { [selected.Id] = mode }));
        }))).ToList();
        if (!_showAllResolutions) sizeChoices.Add((Loc.Get("control.displays.more"), false, () => { _showAllResolutions = true; BuildDisplaysSection(); }));
        AddDisplayChoices(Loc.Get("control.displays.resolution"), sizeChoices, _showAllResolutions);
        AddDisplayChoices(Loc.Get("control.displays.zoom"), selected.ZoomSteps.Select(z => ($"{z} %", z == selected.ZoomPercent, (Action)(() => ChangeDisplay(DisplayChangeKind.Zoom, api => api.SetZoom(selected.Id, z))))));
        AddDisplayChoices("", new[] {
            (Loc.Get("control.displays.main"), selected.Primary, (Action)(() => { if (!selected.Primary) ChangeDisplay(DisplayChangeKind.Primary, api => api.SetPrimary(selected.Id)); })),
            (Loc.Get("control.displays.on"), selected.Active, (Action)(() => ChangeDisplay(DisplayChangeKind.Active, api => api.SetActive(selected.Id, !selected.Active))))
        }.Concat(selected.HdrSupported ? new[] { (Loc.Get("control.displays.hdr"), selected.HdrOn, (Action)(() => ChangeDisplay(DisplayChangeKind.Hdr, api => api.SetHdr(selected.Id, !selected.HdrOn)))) } : []));
        ShowKeepBar();
    }

    private async Task LoadDisplayBrightness(string id, int revision, StackPanel host)
    {
        int? value;
        try { value = DisplayApi is Win32DisplayApi native ? await native.GetBrightnessAsync(id) : await DisplayBrightness.GetAsync(id); }
        catch { return; }
        if (_disposed || revision != _displayRevision || !IsExpanded("displays") || value == null) return;
        var label = MakeLabel(Loc.Get("control.displays.brightness"), 11, _moodInk);
        // The same thin mood bar as the app volumes: click or drag sets it, the wheel nudges it by 5.
        var fill = new ScaleTransform(value.Value / 100.0, 1);
        var number = MakeLabel($"{value.Value} %", 11, Muted);
        number.Width = 38;
        number.TextAlignment = TextAlignment.Right;
        number.VerticalAlignment = VerticalAlignment.Center;
        var track = new Grid
        {
            Height = 4,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new Border { Background = Track, CornerRadius = new CornerRadius(2) },
                new Border { Background = MoodBar, CornerRadius = new CornerRadius(2), RenderTransform = fill, RenderTransformOrigin = new Point(0, 0.5) }
            }
        };
        var hit = new Grid { Name = "DisplaysBrightnessSlider", Height = 18, Background = Brushes.Transparent, Cursor = Cursors.Hand, Children = { track } };
        var bar = new DockPanel { Margin = new Thickness(0, 2, 10, 8) };
        DockPanel.SetDock(number, Dock.Right);
        bar.Children.Add(number);
        bar.Children.Add(hit);
        var shownLevel = new RollingNumber(n => number.Text = $"{Math.Round(n)} %", number);
        int requestedLevel = value.Value;
        var trackScale = new ScaleTransform(1, 1);
        track.RenderTransform = trackScale;
        track.RenderTransformOrigin = new Point(0.5, 0.5);
        DetailHover(hit, number, x: 2, y: 0);
        void SetLevel(int level)
        {
            level = Math.Clamp(level, 0, 100);
            requestedLevel = level;
            Motion.To(fill, ScaleTransform.ScaleXProperty, level / 100.0, Motion.Glide);
            shownLevel.To(level);
            DisplayBrightness.Set(id, level);
        }
        void SetFrom(MouseEventArgs e)
        {
            double w = track.ActualWidth;
            if (w > 0) SetLevel((int)Math.Round(Math.Clamp(e.GetPosition(track).X / w, 0, 1) * 100));
        }
        hit.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            hit.CaptureMouse();
            Motion.To(trackScale, ScaleTransform.ScaleYProperty, 1.3, Motion.PressDown);
            SetFrom(e);
        };
        hit.MouseMove += (_, e) => { if (hit.IsMouseCaptured) SetFrom(e); };
        hit.MouseLeftButtonUp += (_, _) => hit.ReleaseMouseCapture();
        hit.LostMouseCapture += (_, _) => Motion.To(trackScale, ScaleTransform.ScaleYProperty, 1, Motion.Press);
        hit.PreviewMouseWheel += (_, e) =>
        {
            e.Handled = true;
            SetLevel((int)Math.Round(fill.ScaleX * 20) * 5 + Math.Sign(e.Delta) * 5);
        };
        host.Children.Add(label);
        host.Children.Add(bar);
        host.Visibility = Visibility.Visible;
        PopIn(host, 0);
        DetailArrive(label, 0);
        DetailArrive(bar, 1);
        shownLevel.To(0, 0);
        shownLevel.To(value.Value);
        if (host.IsVisible)
            Motion.To(fill, ScaleTransform.ScaleXProperty, value.Value / 100.0, Motion.Glide,
                from: AnimationConfig.ReduceMotion ? null : 0, delay: Motion.Stagger(2));
        host.IsVisibleChanged += (_, _) =>
        {
            if (host.IsVisible) return;
            Motion.Stop(fill, ScaleTransform.ScaleXProperty, requestedLevel / 100.0);
            Motion.Stop(trackScale, ScaleTransform.ScaleYProperty, 1);
        };
    }

    private DdcBrightness DisplayBrightness => _displayBrightness ??= new DdcBrightness(new DisplayBrightnessPort(this), () => DateTime.UtcNow);

    private sealed class DisplayBrightnessPort(ControlCenterView owner) : IDdcPort
    {
        public int? Read(string id) => owner.DisplayApi.GetBrightness(id);
        public bool Write(string id, int value) => new DisplayChanges(owner.DisplayApi, () => DateTime.UtcNow).Apply(DisplayChangeKind.Brightness, api => api.SetBrightness(id, value));
    }

    private void AddDisplayChoices(string title, IEnumerable<(string Text, bool Selected, Action Click)> choices, bool vertical = false)
    {
        if (title.Length > 0) { var label = MakeLabel(title, 11, _moodInk); label.Margin = new Thickness(0, 3, 0, 3); _displaysSection.Children.Add(label); }
        var row = new WrapPanel { Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 5) };
        foreach (var (text, selected, click) in choices)
        {
            var label = MakeLabel(text, 11, selected ? _moodInk : Muted);
            var chip = new Border { Background = selected ? Wash(_mood, .25) : CardBg, CornerRadius = new CornerRadius(6), Padding = new Thickness(9, 5, 9, 5), Margin = new Thickness(0, 0, 5, 4), Cursor = Cursors.Hand, Child = label, Tag = text };
            Squish(chip);
            DetailHover(chip, label, x: 2, y: 0);
            chip.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
            row.Children.Add(chip);
        }
        if (vertical) _displaysSection.Children.Add(new ScrollViewer { Content = row, MaxHeight = 160, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        else _displaysSection.Children.Add(row);
    }

    private void ChangeDisplay(DisplayChangeKind kind, Action<IDisplayApi> change)
    {
        Changes.Apply(kind, change);
        BuildDisplaysSection();
        if (Changes.NeedsConfirm && _displayTimer == null)
        {
            _displayTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _displayTimer.Tick += (_, _) => TickDisplayChanges();
            _displayTimer.Start();
        }
    }

    internal void TickDisplayChanges()
    {
        Changes.Tick();
        if (!Changes.NeedsConfirm)
        {
            _displayTimer?.Stop(); _displayTimer = null;
            if (IsVisible && IsExpanded("displays")) BuildDisplaysSection();
            if (_drawerPage == DrawerPage.Displays) FillDisplayPage();
        }
        else
        {
            _displaySeconds?.To(Changes.SecondsLeft, 250);
            UpdateDisplayCountdown();
        }
    }

    private void ShowKeepBar()
    {
        if (!Changes.NeedsConfirm) return;
        var text = MakeLabel(Loc.Get("control.displays.keepPrompt"), 12, UiPalette.PrimaryBrush);
        var seconds = MakeLabel("", 11, Muted);
        _displaySeconds = new RollingNumber(n => seconds.Text = string.Format(Loc.Get("control.displays.backIn"), (int)Math.Round(n)), seconds);
        _displaySeconds.To(Changes.SecondsLeft);
        var button = new Border { Background = Wash(_mood, .25), CornerRadius = new CornerRadius(6), Padding = new Thickness(9, 5, 9, 5), Cursor = Cursors.Hand, Child = MakeLabel(Loc.Get("control.displays.keep"), 11, _moodInk) };
        Squish(button);
        button.MouseLeftButtonUp += (_, e) => { e.Handled = true; Changes.Confirm(); _displayTimer?.Stop(); _displayTimer = null; BuildDisplaysSection(); };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        row.Children.Add(button); seconds.Margin = new Thickness(10, 5, 0, 0); row.Children.Add(seconds);
        var bar = new StackPanel { Name = "DisplaysKeepBar", Margin = new Thickness(0, 5, 0, 0), Children = { text, row } };
        _displaysSection.Children.Add(bar);
        if (!AnimationConfig.ReduceMotion)
        {
            var shift = new TranslateTransform(); bar.RenderTransform = shift;
            bar.BeginAnimation(OpacityProperty, WeatherMotion.To(0, 1, 180, WeatherMotion.Out));
            Motion.To(shift, TranslateTransform.YProperty, 0, Motion.Tile, from: 8);
        }
    }

    private void BuildDisplayLayout()
    {
        int revision = _displayRevision;
        var positioned = _displays.Where(d => d.Bounds.Width > 0 && d.Bounds.Height > 0).ToArray();
        if (positioned.Length == 0) return;
        int minX = positioned.Min(d => d.Bounds.X), minY = positioned.Min(d => d.Bounds.Y);
        int maxX = positioned.Max(d => d.Bounds.X + d.Bounds.Width), maxY = positioned.Max(d => d.Bounds.Y + d.Bounds.Height);
        double scale = Math.Min(220d / Math.Max(1, maxX - minX), 120d / Math.Max(1, maxY - minY));
        var canvas = new Canvas { Name = "DisplaysLayout", Width = 220, Height = Math.Max(30, (maxY - minY) * scale), HorizontalAlignment = HorizontalAlignment.Left, ClipToBounds = false };
        foreach (var d in positioned)
        {
            var rect = new Rectangle { Width = Math.Max(4, d.Bounds.Width * scale), Height = Math.Max(4, d.Bounds.Height * scale), RadiusX = 4, RadiusY = 4, Stroke = _moodInk, StrokeThickness = 1.5, Fill = d.Id == _selectedDisplay ? Wash(_mood, .25) : CardBg, Opacity = d.Active ? 1 : .45, StrokeDashArray = d.Active ? null : new DoubleCollection { 3, 2 }, Cursor = Cursors.Hand, Tag = d.Id };
            Canvas.SetLeft(rect, (d.Bounds.X - minX) * scale); Canvas.SetTop(rect, (d.Bounds.Y - minY) * scale);
            Point start = default, sample = default; double left = 0, top = 0; bool dragged = false;
            long sampledAt = 0; Vector speed = default;
            var feedback = new ScaleTransform(1, 1);
            rect.RenderTransform = feedback;
            rect.RenderTransformOrigin = new Point(0.5, 0.5);
            void SetScale(double value, SpringMotion spring)
            {
                if (!rect.IsVisible)
                {
                    Motion.Stop(feedback, ScaleTransform.ScaleXProperty, 1);
                    Motion.Stop(feedback, ScaleTransform.ScaleYProperty, 1);
                    return;
                }
                Motion.To(feedback, ScaleTransform.ScaleXProperty, value, spring);
                Motion.To(feedback, ScaleTransform.ScaleYProperty, value, spring);
            }
            rect.MouseEnter += (_, _) => SetScale(HoverScale(rect), Motion.Press);
            rect.MouseLeave += (_, _) => { if (!rect.IsMouseCaptured) SetScale(1, Motion.Press); };
            rect.LostMouseCapture += (_, _) => SetScale(rect.IsMouseOver ? HoverScale(rect) : 1, Motion.Press);
            rect.IsVisibleChanged += (_, _) =>
            {
                if (rect.IsVisible) return;
                Motion.Stop(feedback, ScaleTransform.ScaleXProperty, 1);
                Motion.Stop(feedback, ScaleTransform.ScaleYProperty, 1);
                Motion.Stop(rect, Canvas.LeftProperty);
                Motion.Stop(rect, Canvas.TopProperty);
            };
            rect.MouseLeftButtonDown += (_, e) =>
            {
                Motion.Stop(rect, Canvas.LeftProperty); Motion.Stop(rect, Canvas.TopProperty);
                start = sample = e.GetPosition(canvas); sampledAt = System.Diagnostics.Stopwatch.GetTimestamp(); speed = default;
                left = Canvas.GetLeft(rect); top = Canvas.GetTop(rect); dragged = false;
                SetScale(0.96, Motion.PressDown); rect.CaptureMouse(); e.Handled = true;
            };
            rect.MouseMove += (_, e) =>
            {
                if (!rect.IsMouseCaptured) return;
                var p = e.GetPosition(canvas);
                double elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(sampledAt).TotalSeconds;
                if (elapsed > 0) speed = (p - sample) / elapsed;
                sample = p; sampledAt = System.Diagnostics.Stopwatch.GetTimestamp();
                if ((p - start).Length > 3) dragged = true;
                if (dragged) { Canvas.SetLeft(rect, left + p.X - start.X); Canvas.SetTop(rect, top + p.Y - start.Y); }
            };
            rect.MouseLeftButtonUp += (_, e) =>
            {
                rect.ReleaseMouseCapture(); e.Handled = true;
                if (!dragged) { _selectedDisplay = d.Id; _showAllResolutions = false; BuildDisplaysSection(); return; }
                var moved = new Int32Rect((int)Math.Round((Canvas.GetLeft(rect) / scale) + minX), (int)Math.Round((Canvas.GetTop(rect) / scale) + minY), d.Bounds.Width, d.Bounds.Height);
                var snapped = SnapToNeighbours(moved, positioned.Where(x => x.Id != d.Id && x.Active).Select(x => x.Bounds).ToArray());
                double targetX = (snapped.X - minX) * scale, targetY = (snapped.Y - minY) * scale;
                var positions = positioned.Where(x => x.Active).ToDictionary(x => x.Id, x => x.Id == d.Id ? snapped : new Int32Point(x.Bounds.X, x.Bounds.Y));
                if (!positions.ContainsKey(d.Id)) { BuildDisplaysSection(); return; }
                void ApplyPosition()
                {
                    if (!_disposed && revision == _displayRevision)
                        ChangeDisplay(DisplayChangeKind.Position, api => api.SetPositions(positions));
                }
                if (AnimationConfig.ReduceMotion) ApplyPosition();
                else
                {
                    bool recent = System.Diagnostics.Stopwatch.GetElapsedTime(sampledAt).TotalMilliseconds < 100;
                    Motion.To(rect, Canvas.LeftProperty, targetX, Motion.Glide, velocity: recent ? speed.X : 0, done: ApplyPosition);
                    Motion.To(rect, Canvas.TopProperty, targetY, Motion.Glide, velocity: recent ? speed.Y : 0);
                }
            };
            canvas.Children.Add(rect);
        }
        _displaysSection.Children.Add(canvas);
    }

    internal static Int32Point SnapToNeighbours(Int32Rect moving, IReadOnlyList<Int32Rect> others)
    {
        if (others.Count == 0) return new Int32Point(moving.X, moving.Y);
        var candidates = new List<Int32Point>();
        var xs = others.SelectMany(r => new[] { r.X - moving.Width, r.X + r.Width, r.X, r.X + r.Width - moving.Width }).Append(moving.X).ToArray();
        var ys = others.SelectMany(r => new[] { r.Y - moving.Height, r.Y + r.Height, r.Y, r.Y + r.Height - moving.Height }).Append(moving.Y).ToArray();
        foreach (var r in others)
        {
            foreach (int xEdge in new[] { r.X - moving.Width, r.X + r.Width }) foreach (int yEdge in ys) candidates.Add(new(xEdge, yEdge));
            foreach (int yEdge in new[] { r.Y - moving.Height, r.Y + r.Height }) foreach (int xEdge in xs) candidates.Add(new(xEdge, yEdge));
            int y = Math.Clamp(moving.Y, r.Y - moving.Height + 1, r.Y + r.Height - 1);
            int x = Math.Clamp(moving.X, r.X - moving.Width + 1, r.X + r.Width - 1);
            candidates.Add(new(r.X - moving.Width, y)); candidates.Add(new(r.X + r.Width, y));
            candidates.Add(new(x, r.Y - moving.Height)); candidates.Add(new(x, r.Y + r.Height));
        }
        bool Clear(Int32Point p) => others.All(r => p.X + moving.Width <= r.X || p.X >= r.X + r.Width || p.Y + moving.Height <= r.Y || p.Y >= r.Y + r.Height) && others.Any(r => ((p.X + moving.Width == r.X || p.X == r.X + r.Width) && p.Y < r.Y + r.Height && p.Y + moving.Height > r.Y) || ((p.Y + moving.Height == r.Y || p.Y == r.Y + r.Height) && p.X < r.X + r.Width && p.X + moving.Width > r.X));
        return candidates.Where(Clear).OrderBy(p => Math.Pow((double)p.X - moving.X, 2) + Math.Pow((double)p.Y - moving.Y, 2)).FirstOrDefault(new Int32Point(moving.X, moving.Y));
    }
}
