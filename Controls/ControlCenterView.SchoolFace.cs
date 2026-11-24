using System.Globalization;
using System.Windows;
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
    private static readonly Brush SchoolLabel = Frozen("#A4A4AA");
    private static readonly Brush SchoolTile = Frozen("#0FFFFFFF");
    private static readonly Brush SchoolRim = Frozen("#1BFFFFFF");
    private static readonly Brush SchoolTicks = Frozen("#343436");
    private static readonly FontFamily SchoolFont = new("Segoe UI Variable Text, Segoe UI");
    private const double SchoolDialSize = 140;
    private readonly WeatherTween _schoolLight = new();
    private readonly HashSet<FrameworkElement> _schoolMotion = [];
    private readonly ScaleTransform _schoolDialScale = new(1, 1);
    private readonly RotateTransform _schoolRingTurn = new();
    private readonly TranslateTransform _schoolHeroShift = new();
    private Canvas _schoolRing = null!;
    private readonly Line[] _schoolTicks = new Line[46];
    private Func<DateTimeOffset> _schoolNow = () => DateTimeOffset.Now;
    private DispatcherTimer? _schoolCountdownTimer;
    private Border _schoolFaceSlot = null!;
    private Grid _schoolFace = null!, _schoolHero = null!, _schoolDial = null!;
    private Border _schoolTestDrop = null!;
    private StackPanel _schoolActions = null!;
    private TextBlock _schoolTitle = null!, _schoolCountdown = null!, _schoolUnavailable = null!, _schoolSummary = null!;
    private Run _schoolDigits = null!, _schoolUnit = null!;
    private Ellipse _schoolDialDot = null!;
    private ContentControl _schoolSource = null!;
    private TextBlock _schoolCount = null!;
    private Button _schoolPrev = null!, _schoolNext = null!;
    private string? _schoolActionRevision, _schoolFaceKey;
    private SchoolItem[] _schoolItems = [];
    private int _schoolFaceIndex, _schoolLit = -1;
    private Color? _schoolLightColor;
    private double _schoolLightTarget;
    private bool _schoolBreathing, _schoolPaged;
    private long _schoolBloom;
    private DateTime _schoolBuiltDay;

    internal StackPanel SchoolStatus { get; } = new() { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed };
    internal event Action? SchoolCollapseRequested;

    private sealed record SchoolItem(string Key, string Subject, string Title, string Source, DateTimeOffset? Due,
        PiDeadline? Deadline = null, PiExam? Exam = null);

    private SchoolItem? SchoolCover => _schoolItems.ElementAtOrDefault(_schoolFaceIndex);
    private bool SchoolDataAvailable => !_isLocalOnly() && _pi?.IsConfigured == true && _piSchool != null;
    private bool AnimateSchool => IsVisible && !AnimationConfig.ReduceMotion;

    private void MountSchoolFace()
    {
        _compactPage.Margin = new Thickness(0);
        ControlScrollViewer.OpacityMask = null;
        ControlScrollViewer.PanningMode = PanningMode.None;
        ControlScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;

        _schoolSource = new ContentControl { VerticalAlignment = VerticalAlignment.Center };
        _schoolCount = SchoolText("", 13);
        Typography.SetNumeralAlignment(_schoolCount, FontNumeralAlignment.Tabular);
        _schoolPrev = SchoolIconButton("left", Say("Vorheriger Eintrag", "Previous item"), () => PageSchoolFace(-1), 31, 29, 16);
        _schoolNext = SchoolIconButton("right", Say("Nächster Eintrag", "Next item"), () => PageSchoolFace(1), 31, 29, 16);
        _schoolCount.TextTrimming = TextTrimming.None;
        var count = new Border { Padding = new Thickness(4, 0, 4, 0), Child = _schoolCount };
        var pager = new StackPanel { Orientation = Orientation.Horizontal, Children = { _schoolPrev, count, _schoolNext } };
        var bar = SchoolColumns(_schoolSource, pager);
        bar.Height = 29;

        _schoolDial = BuildSchoolDial();
        _schoolTitle = SchoolHeading("", 44, 1.08, .90, 575);
        _schoolTitle.TextWrapping = TextWrapping.Wrap;
        _schoolTitle.RenderTransform = new TranslateTransform(0, 2);
        _schoolTitle.MaxHeight = 96;
        _schoolActions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 17, 0, 0) };
        var copy = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { _schoolTitle, _schoolActions } };
        _schoolHero = new Grid { MinHeight = 140, Margin = new Thickness(0, 12, 0, 2), Background = Brushes.Transparent,
            RenderTransform = _schoolHeroShift };
        _schoolHero.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(165) });
        _schoolHero.ColumnDefinitions.Add(new ColumnDefinition());
        _schoolHero.Children.Add(_schoolDial);
        Grid.SetColumn(copy, 1);
        _schoolHero.Children.Add(copy);


        _schoolFace = new Grid { MinHeight = 202, VerticalAlignment = VerticalAlignment.Top };
        _schoolFace.Children.Add(new StackPanel { Margin = new Thickness(25, 19, 25, 0), Children = { bar, _schoolHero } });
        _schoolTestDrop = new Border { Background = Brushes.Transparent, Child = _schoolFace };
        HookSchoolSwipe();
        _schoolFaceSlot = new Border { ClipToBounds = true, Child = new Grid { Children = { _schoolTestDrop } } };
        _schoolTestDrop.VerticalAlignment = VerticalAlignment.Top;
        _schoolFace.IsVisibleChanged += (_, _) =>
        {
            if (!_schoolFace.IsVisible || !SchoolDataAvailable) return;
            UpdateSchoolClock(_schoolNow());
            QueueSchoolLight();
        };
        _schoolDial.SizeChanged += (_, _) => QueueSchoolLight();
        _schoolUnavailable = SchoolText("", 16);
        _schoolUnavailable.Margin = new Thickness(25, 19, 25, 14);
        _piSchoolSection.Children.Add(_schoolFaceSlot);
        _piSchoolSection.Children.Add(_schoolUnavailable);
        _schoolSummary = SchoolText("", 12);
        SchoolStatus.Children.Add(_schoolSummary);
        _schoolLight.Step = value => { if (_backdrop != null) _backdrop.Intensity = value; };
    }

    private Grid BuildSchoolDial()
    {
        const double center = SchoolDialSize / 2;
        var ticks = _schoolRing = new Canvas { RenderTransformOrigin = new Point(.5, .5), RenderTransform = _schoolRingTurn };
        for (int n = 0; n < _schoolTicks.Length; n++)
        {
            double angle = (-226 + n * 272d / (_schoolTicks.Length - 1)) * Math.PI / 180;
            double x = Math.Cos(angle), y = Math.Sin(angle), inner = n % 5 == 0 ? 52 : 55;
            _schoolTicks[n] = new Line { X1 = center + x * inner, Y1 = center + y * inner, X2 = center + x * 63, Y2 = center + y * 63 };
            ticks.Children.Add(_schoolTicks[n]);
        }
        _schoolDialDot = new Ellipse { Width = 6, Height = 6 };
        Canvas.SetLeft(_schoolDialDot, 67);
        Canvas.SetTop(_schoolDialDot, 112);
        ticks.Children.Add(_schoolDialDot);

        _schoolCountdown = SchoolText("", 42, CompactInk, FontWeight.FromOpenTypeWeight(520));
        _schoolCountdown.HorizontalAlignment = HorizontalAlignment.Center;
        _schoolCountdown.LayoutTransform = new ScaleTransform(.98, 1);
        _schoolCountdown.RenderTransform = new TranslateTransform(1, 0);
        Typography.SetNumeralAlignment(_schoolCountdown, FontNumeralAlignment.Tabular);
        _schoolDigits = new Run();
        _schoolUnit = new Run { FontSize = 23, FontWeight = FontWeight.FromOpenTypeWeight(420) };
        _schoolCountdown.Inlines.Add(_schoolDigits);
        _schoolCountdown.Inlines.Add(_schoolUnit);

        return new Grid { Width = SchoolDialSize, Height = SchoolDialSize, HorizontalAlignment = HorizontalAlignment.Left,
            RenderTransformOrigin = new Point(.5, .5), RenderTransform = _schoolDialScale, Children = { ticks, _schoolCountdown } };
    }

    private TextBlock SchoolText(string text, double size, Brush? brush = null, FontWeight? weight = null)
    {
        var block = CompactText(text, size, brush ?? SchoolLabel);
        block.FontFamily = SchoolFont;
        block.FontWeight = weight ?? FontWeights.Normal;
        block.HorizontalAlignment = HorizontalAlignment.Left;
        TextOptions.SetTextFormattingMode(block, TextFormattingMode.Ideal);
        return block;
    }

    private TextBlock SchoolHeading(string text, double size, double lineHeight, double widthScale, int weight)
    {
        var line = SchoolText(text, size, CompactInk, FontWeight.FromOpenTypeWeight(weight));
        line.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        line.LineHeight = size * lineHeight;
        line.LayoutTransform = new ScaleTransform(widthScale, 1);
        return line;
    }

    private static Grid SchoolColumns(params FrameworkElement[] children)
    {
        var grid = new Grid();
        for (int i = 0; i < children.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = i == 0 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            Grid.SetColumn(children[i], i);
            grid.Children.Add(children[i]);
        }
        return grid;
    }

    private void RefreshSchoolFace(PiDeadline[] deadlines, PiExam[] exams, DateTimeOffset now)
    {
        bool available = SchoolDataAvailable;
        _schoolFaceSlot.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        _schoolUnavailable.Visibility = available ? Visibility.Collapsed : Visibility.Visible;
        SchoolStatus.Visibility = available && IsVisible ? Visibility.Visible : Visibility.Collapsed;
        _schoolUnavailable.Text = _isLocalOnly() ? Say("Schule ist im lokalen Modus ausgeblendet.", "School is unavailable in local-only mode.")
            : _pi?.IsConfigured != true ? Say("Pi-Dashboard verbinden", "Connect the Pi dashboard")
            : _piReachable ? Say("Schuldaten werden geladen…", "Loading school data…") : Say("Pi-Dashboard nicht erreichbar", "Pi dashboard unreachable");
        if (!available)
        {
            _schoolItems = [];
            _schoolFaceKey = null;
            _schoolPaged = false;
            StopSchoolFaceMotion();
            Seen.ForgetAll("school:");
            return;
        }

        var items = deadlines.Select(d => new SchoolItem("hand:" + AssignmentKey(d), DeadlineSubject(d), d.Name, TaskSource(d), d.Due, Deadline: d))
            .Concat(exams.Select(e => new SchoolItem("test:" + ExamKey(e), ExamSubject(e), ExamTitle(e), Say("Test", "Test"), ExamDue(e), Exam: e)))
            .ToArray();
        foreach (var item in _schoolItems)
            if (!items.Any(next => item.Key == next.Key)) Seen.Forget("school:last-day:" + item.Key);
        _schoolItems = items;
        int selected = _schoolPaged ? Array.FindIndex(items, item => item.Key == _schoolFaceKey) : 0;
        _schoolFaceIndex = selected < 0 ? 0 : selected;
        if (selected < 0) _schoolPaged = false;

        _schoolSummary.Text = Say($"{deadlines.Length} Abgaben  ·  {exams.Length} Tests", $"{deadlines.Length} hand-ins  ·  {exams.Length} tests");
        Seen.Changed("school:counts", _schoolSummary.Text);
        PaintSchoolCover(now);
    }

    private void PaintSchoolCover(DateTimeOffset now)
    {
        var item = SchoolCover;
        if (_schoolFaceKey != item?.Key)
        {
            _schoolFaceKey = item?.Key;
            StopSchoolDial();
        }
        _schoolPrev.IsEnabled = _schoolFaceIndex > 0;
        _schoolNext.IsEnabled = _schoolFaceIndex + 1 < _schoolItems.Length;
        _schoolCount.Text = $"{_schoolFaceIndex + 1} / {_schoolItems.Length}";
        _schoolCount.Visibility = _schoolPrev.Visibility = _schoolNext.Visibility = item != null ? Visibility.Visible : Visibility.Hidden;
        _schoolDial.Visibility = item != null ? Visibility.Visible : Visibility.Hidden;
        _schoolTitle.Text = item?.Title ?? Say("Alles abgegeben", "All handed in");
        _schoolTitle.ToolTip = item?.Title;
        _schoolTitle.FontSize = item != null ? 44 : 27;
        _schoolTitle.TextTrimming = TextTrimming.CharacterEllipsis;
        string revision = $"{Loc.CurrentLanguage}|{item?.Key}|{item?.Source}|{(item == null ? null : SchoolItemColor(item))}|"
            + (item?.Deadline is { } deadline ? DeadlineRevision(deadline) : "");
        Seen.Changed("school:hero", $"{revision}|{item?.Title}");
        if (_schoolActionRevision != revision)
        {
            _schoolActionRevision = revision;
            _removeSchoolHeroDrop?.Invoke();
            _removeSchoolHeroDrop = null;
            _schoolTestDrop.AllowDrop = false;
            _schoolTestDrop.Background = Brushes.Transparent;
            _schoolActions.Children.Clear();
            _schoolActions.ToolTip = null;
            _schoolSource.Content = item != null ? SchoolSourceRow(item.Subject, item.Source, SchoolItemColor(item)) : null;
            if (item?.Deadline is { } handIn) AddSchoolTaskActions(handIn, _schoolActions, _schoolHero);
            if (item?.Exam is { } exam)
            {
                _schoolActions.Children.Add(SchoolPrimary("book", Say("Lernen", "Study"), () => SelectStudy(exam)));
                _schoolActions.Children.Add(SchoolTool("folder", Say("Lernordner öffnen", "Open study folder"), () => OpenStudyFolder(exam)));
                _schoolActions.Children.Add(SchoolTool("chat", Say("Claude-Sitzung fortsetzen", "Resume Claude session"), () => ResumeStudy(exam)));
                _schoolTestDrop.AllowDrop = true;
            }
        }
        UpdateSchoolClock(now);
        UpdateCountdownVisibility();
    }

    private void PageSchoolFace(int step, double from = 0)
    {
        int index = Math.Clamp(_schoolFaceIndex + step, 0, Math.Max(0, _schoolItems.Length - 1));
        if (index != _schoolFaceIndex)
        {
            _schoolPaged = index != 0;
            _schoolFaceIndex = index;
            PaintSchoolCover(_schoolNow());
            ContentHeightChanged?.Invoke();
            if (SchoolCover?.Deadline is { } handIn) LoadPiAssignment(handIn.Cmid);
            if (from == 0) from = Math.Sign(step) * 30;
        }
        if (AnimateSchool) Motion.To(_schoolHeroShift, TranslateTransform.XProperty, 0, Motion.Glide, from: from);
        else Motion.Stop(_schoolHeroShift, TranslateTransform.XProperty, 0);
    }

    private void HookSchoolSwipe()
    {
        Point? start = null;
        bool dragging = false;
        _schoolHero.Cursor = Cursors.SizeWE;
        _schoolHero.PreviewMouseLeftButtonDown += (_, e) =>
        {
            for (var source = e.OriginalSource as DependencyObject; source != null && source != _schoolHero;
                 source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source))
                if (source is Button) return;
            if (_schoolItems.Length == 0) return;
            start = e.GetPosition(this);
            dragging = false;
            _schoolHero.CaptureMouse();
            e.Handled = true;
        };
        _schoolHero.PreviewMouseMove += (_, e) =>
        {
            if (start is not { } origin || e.LeftButton != MouseButtonState.Pressed) return;
            Vector move = e.GetPosition(this) - origin;
            if (!dragging && (Math.Abs(move.X) < SystemParameters.MinimumHorizontalDragDistance || Math.Abs(move.X) < Math.Abs(move.Y))) return;
            dragging = true;
            if (!AnimateSchool) return;
            bool end = move.X > 0 ? _schoolFaceIndex == 0 : _schoolFaceIndex == _schoolItems.Length - 1;
            double offset = Math.CopySign(MainWindow.ShellRubber(Math.Abs(move.X), end ? 22 : 130), move.X);
            Motion.Stop(_schoolHeroShift, TranslateTransform.XProperty, offset);
        };
        _schoolHero.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (start is not { } origin) return;
            double dx = e.GetPosition(this).X - origin.X;
            start = null;
            _schoolHero.ReleaseMouseCapture();
            int step = dragging && Math.Abs(dx) >= 44 ? -Math.Sign(dx) : 0;
            double from = _schoolHeroShift.X;
            if (step < 0 && _schoolFaceIndex > 0 || step > 0 && _schoolFaceIndex + 1 < _schoolItems.Length)
                from += Math.Sign(step) * 85;
            PageSchoolFace(step, from);
            e.Handled = true;
        };
        _schoolHero.LostMouseCapture += (_, _) =>
        {
            if (start == null) return;
            start = null;
            PageSchoolFace(0, _schoolHeroShift.X);
        };
        _schoolTestDrop.DragOver += (_, e) => { if (SchoolCover?.Exam is { }) _schoolTestDrop.Background = PreviewStudyDrop(e) ? SchoolHover : Brushes.Transparent; };
        _schoolTestDrop.DragLeave += (_, _) => _schoolTestDrop.Background = Brushes.Transparent;
        _schoolTestDrop.Drop += async (_, e) => { if (SchoolCover?.Exam is { } exam) { _schoolTestDrop.Background = Brushes.Transparent; await DropStudyFiles(exam, e); } };
    }

    internal static (string Digits, string Unit) SchoolCountdown(DateTimeOffset due, DateTimeOffset now)
    {
        double hours = (due - now).TotalHours;
        if (hours <= 0) return ("0", "m");
        if (hours >= 48) return (Math.Ceiling(hours / 24).ToString(CultureInfo.CurrentCulture), "d");
        if (hours >= 1) return (Math.Ceiling(hours).ToString(CultureInfo.CurrentCulture), "h");
        return (Math.Ceiling(hours * 60).ToString(CultureInfo.CurrentCulture), "m");
    }

    private void UpdateSchoolClock(DateTimeOffset now)
    {
        if (SchoolCover is { } cover)
        {
            var (digits, unit) = cover.Due is { } due ? SchoolCountdown(due, now) : ("–", "");
            double hours = (cover.Due - now)?.TotalHours ?? 120;
            int lit = (int)Math.Round((1 - Math.Clamp(hours, 0, 120) / 120) * _schoolTicks.Length, MidpointRounding.AwayFromZero);
            Color color = SchoolItemColor(cover);
            if (_schoolLit != lit || _schoolLightColor != color)
            {
                var ink = new SolidColorBrush(color);
                ink.Freeze();
                for (int i = 0; i < _schoolTicks.Length; i++)
                {
                    _schoolTicks[i].Stroke = i < lit ? ink : SchoolTicks;
                    _schoolTicks[i].StrokeThickness = i < lit ? 2.9 : 1.7;
                }
                _schoolDialDot.Fill = ink;
                _schoolLit = lit;
            }
            Seen.Changed("school:clock", digits + unit);
            Seen.Changed("school:ticks", lit);
            if (_schoolDigits.Text != digits) _schoolDigits.Text = digits;
            if (_schoolUnit.Text != unit) _schoolUnit.Text = unit;
            _schoolDial.ToolTip = cover.Due?.ToLocalTime().ToString("f", SchoolDateCulture) ?? cover.Exam?.Date.ToString("D", SchoolDateCulture);
        }

        bool tighten = false;
        if (SchoolCover is { Due: { } deadline } shown && DrawerHeight < 1 && _schoolFace.IsVisible)
        {
            double hours = (deadline - now).TotalHours;
            bool inLastDay = hours is > 0 and <= 24;
            tighten = Seen.Changed("school:last-day:" + shown.Key, inLastDay) && inLastDay;
        }
        if (tighten && AnimateSchool) TightenSchoolDial();
        UpdateSchoolDayClock(now);
        UpdateSchoolLight(now, tighten);
    }

    private void TightenSchoolDial()
    {
        Motion.To(_schoolDialScale, ScaleTransform.ScaleXProperty, 1.055, Motion.SchoolDialGather, from: .94, done: () =>
        {
            if (!AnimateSchool) return;
            Motion.To(_schoolDialScale, ScaleTransform.ScaleXProperty, 1, Motion.SchoolDialSettle);
            Motion.To(_schoolDialScale, ScaleTransform.ScaleYProperty, 1, Motion.SchoolDialSettle);
        });
        Motion.To(_schoolDialScale, ScaleTransform.ScaleYProperty, 1.055, Motion.SchoolDialGather, from: .94);
        Motion.To(_schoolRingTurn, RotateTransform.AngleProperty, 0, Motion.Shift, from: -13);
        Motion.To(_schoolRing, OpacityProperty, 1, Motion.Shift, from: .62);
    }

    private void StopSchoolDial()
    {
        Motion.Stop(_schoolDialScale, ScaleTransform.ScaleXProperty, 1);
        Motion.Stop(_schoolDialScale, ScaleTransform.ScaleYProperty, 1);
        Motion.Stop(_schoolRingTurn, RotateTransform.AngleProperty, 0);
        Motion.Stop(_schoolRing, OpacityProperty, 1);
    }

    private void UpdateCountdownVisibility()
    {
        if (_disposed || !IsVisible || !SchoolDataAvailable) { _schoolCountdownTimer?.Stop(); return; }
        if (_schoolCountdownTimer == null)
        {
            _schoolCountdownTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
            _schoolCountdownTimer.Tick += (_, _) =>
            {
                var now = _schoolNow();
                if (_schoolArmedPush != null && DateTime.UtcNow >= _schoolPushUntil)
                {
                    _schoolArmedPush = null;
                    RedrawPi();
                }
                else if (now.Date != _schoolBuiltDay) BuildPiPage();
                else UpdateSchoolClock(now);
            };
        }
        _schoolCountdownTimer.Start();
    }

    private void UpdateSchoolLight(DateTimeOffset now, bool tighten)
    {
        var cover = SchoolCover;
        Color? color = cover == null ? null : SchoolItemColor(cover);
        if (_schoolLightColor != color)
        {
            _schoolLightColor = color;
            QueueSchoolLight();
        }

        double hours = (cover?.Due - now)?.TotalHours ?? 120;
        double strength = cover == null ? 0 : hours switch
        {
            <= 0 => .14,
            <= 1 => .60,
            <= 24 => .40 + .20 * (1 - hours / 24),
            _ => .13 + .28 * (1 - Math.Min(hours, 120) / 120)
        };
        Seen.Changed("school:light", strength);
        bool bloom = DrawerHeight < 1 && Seen.Changed("school:bloom", _schoolBloom.ToString(CultureInfo.InvariantCulture));
        _schoolLightTarget = strength;
        if (!AnimateSchool || DrawerHeight >= 1)
        {
            _schoolBreathing = false;
            _schoolLight.Stop(strength);
        }
        else if (bloom)
        {
            _schoolBreathing = false;
            Motion.To(_schoolLight, WeatherTween.ValueProperty, Math.Max(.54, strength + .14), Motion.Shift,
                done: () => Motion.To(_schoolLight, WeatherTween.ValueProperty, _schoolLightTarget, Motion.Glide));
        }
        else if (tighten)
        {
            _schoolBreathing = false;
            Motion.To(_schoolLight, WeatherTween.ValueProperty, strength, Motion.Glide);
        }
        else if (!Motion.IsRunning(_schoolLight, WeatherTween.ValueProperty))
        {
            if (hours is > 0 and <= 1)
            {
                if (!_schoolBreathing)
                {
                    _schoolBreathing = true;
                    BreatheSchoolLight(true);
                }
            }
            else
            {
                _schoolBreathing = false;
                _schoolLight.Value = strength;
            }
        }
    }

    private void BreatheSchoolLight(bool up)
    {
        if (!_schoolBreathing || !AnimateSchool || DrawerHeight >= 1) return;
        Motion.To(_schoolLight, WeatherTween.ValueProperty, _schoolLightTarget + (up ? .065 : -.035), Motion.Breath,
            done: () => BreatheSchoolLight(!up));
    }

    private void QueueSchoolLight() => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
    {
        if (_disposed || !IsVisible || _backdrop is not { IsVisible: true } backdrop || !_schoolDial.IsVisible) return;
        if (_schoolLightColor is not { } color) { backdrop.Intensity = 0; return; }
        backdrop.SetMood(color, Colors.Transparent, Colors.Transparent, 0);
        backdrop.Gather(_schoolDial.TranslatePoint(new Point(SchoolDialSize / 2, SchoolDialSize / 2), backdrop), .72, .60);
        backdrop.Intensity = _schoolLight.Value;
    });

    private void SchoolPulse(FrameworkElement row)
    {
        if (!AnimateSchool || !row.IsVisible) return;
        if (_schoolMotion.Add(row)) row.IsVisibleChanged += StopSchoolPulseOnHide;
        Motion.To(row, OpacityProperty, 1, Motion.Shift, from: .78, done: () => FinishSchoolPulse(row));
    }

    private void StopSchoolPulseOnHide(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is FrameworkElement { IsVisible: false } row) FinishSchoolPulse(row);
    }

    private void FinishSchoolPulse(FrameworkElement row)
    {
        row.IsVisibleChanged -= StopSchoolPulseOnHide;
        _schoolMotion.Remove(row);
        Motion.Stop(row, OpacityProperty, 1);
    }

    private void StopSchoolFaceMotion()
    {

        _schoolCountdownTimer?.Stop();
        SchoolStatus.Visibility = Visibility.Collapsed;
        _schoolBreathing = false;
        foreach (var row in _schoolMotion.ToArray()) FinishSchoolPulse(row);
        _schoolLight.Stop(0);
        if (_schoolHero.IsMouseCaptured) _schoolHero.ReleaseMouseCapture();
        Motion.Stop(_schoolHeroShift, TranslateTransform.XProperty, 0);
        StopSchoolDial();
        _schoolArmedPush = null;
    }
}
