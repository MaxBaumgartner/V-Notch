using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using VNotch.Services;
using VNotch.Services.ControlCenter;

namespace VNotch.Controls;

public partial class ControlCenterView
{
    private const int SchoolPageRows = 5;
    private const int SchoolDayPageSize = 6;
    private const double LessonHeight = 52, LessonStep = 56.8;
    private static readonly CultureInfo SchoolCulture = CultureInfo.GetCultureInfo("en-GB");
    private static readonly CultureInfo GermanSchoolCulture = CultureInfo.GetCultureInfo("de-AT");
    private static CultureInfo SchoolDateCulture => Loc.CurrentLanguage == "de" ? GermanSchoolCulture : SchoolCulture;
    private static readonly Brush SchoolHover = Frozen("#222225");
    private static readonly Brush SchoolIconInk = Frozen("#BBBCC1");
    private static readonly Brush SchoolHandle = Frozen("#88888C");
    private static readonly IReadOnlyDictionary<string, Color> SchoolPalette = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase)
    {
        ["DBI"] = Color.FromRgb(157, 189, 177), ["WMC"] = Color.FromRgb(147, 191, 192),
        ["POSE"] = Color.FromRgb(205, 154, 153), ["AM"] = Color.FromRgb(210, 133, 126),
        ["SYP"] = Color.FromRgb(199, 183, 138), ["NSCS"] = Color.FromRgb(127, 166, 204),
        ["D"] = Color.FromRgb(205, 175, 148), ["E"] = Color.FromRgb(190, 156, 118),
        ["RK"] = Color.FromRgb(177, 195, 160)
    };

    private enum SchoolDrawerTab { Work, Tests, Day }
    private SchoolDrawerTab _schoolTab;
    private TextBlock? _schoolDayClock;
    private Grid[] _schoolDayColumns = [];
    private readonly Grid?[] _schoolNowMarkers = new Grid?[2];
    private PiLesson[] _schoolDayLessons = [];
    private string? _schoolDayRevision, _schoolMarkerRevision;
    private int? _schoolLessonOffset;
    private bool _schoolDrawerCoversFace;
    private int _schoolHandInPage, _schoolExamPage;
    private string? _schoolSelectedExam, _schoolSelectedTask, _schoolConfirmTask;
    private readonly HashSet<string> _schoolRowKeys = [];

    internal static Color SubjectColor(string subject) => PiDashboardClient.UntisColor(SchoolSubject(subject));

    private Color SchoolColor(string subject, Color? eventColor = null)
    {
        if (SchoolPalette.TryGetValue(subject, out var softened)) return softened;
        Color original = eventColor ?? (_piSchool?.SubjectColors.TryGetValue(subject, out var exact) == true ? exact : SubjectColor(subject));
        return Color.FromRgb(Soften(original.R, 154), Soften(original.G, 154), Soften(original.B, 162));

        static byte Soften(byte value, byte neutral) => (byte)Math.Round(value * .45 + neutral * .55);
    }

    private Color SchoolItemColor(SchoolItem item) => SchoolColor(item.Subject, item.Exam?.Color);
    private static string SchoolSubject(string value) => PiDashboardClient.UntisSubject(value);
    private static string DeadlineSubject(PiDeadline deadline) => SchoolSubject(deadline.Course);
    private static string AssignmentKey(PiDeadline deadline) => deadline.Cmid > 0 ? $"cmid:{deadline.Cmid}" : $"{deadline.Course}|{deadline.Name}";
    private static string ExamKey(PiExam exam) => $"{exam.Date:yyyy-MM-dd}|{exam.Start}|{exam.Subject}|{exam.Kind}|{exam.Title}";
    private static string ExamTitle(PiExam exam) => exam.Title.Length > 0 ? exam.Title : exam.Kind;
    private static DateTimeOffset? ExamDue(PiExam exam)
    {
        var date = exam.Date.Date;
        if (!TimeOnly.TryParseExact(exam.Start, ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) return null;
        date += time.ToTimeSpan();
        return new DateTimeOffset(date, TimeZoneInfo.Local.GetUtcOffset(date));
    }

    private bool IsClassroomTask(PiDeadline deadline) =>
        deadline.Kind == "classroom" || _piAssignments.GetValueOrDefault(deadline.Cmid)?.Mode is "classroom" or "pose";

    private string TaskSource(PiDeadline deadline) =>
        IsClassroomTask(deadline) ? "Classroom 50" : deadline.Kind == "nw" ? "NW" : "Moodle";

    private string DeadlineRevision(PiDeadline deadline)
    {
        _piAssignments.TryGetValue(deadline.Cmid, out var assignment);
        _piTasks.TryGetValue(deadline.Cmid, out var task);
        _schoolPush.TryGetValue(AssignmentKey(deadline), out var push);
        return $"{AssignmentKey(deadline)}|{deadline}|{SchoolColor(DeadlineSubject(deadline))}|{assignment}|{task}|{push}";
    }

    private FrameworkElement SchoolSourceRow(string subject, string source, Color color) =>
        new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children =
        {
            SchoolDot(color, 9), SchoolText(subject, 15, CompactInk, FontWeights.SemiBold),
            new Border { Padding = new Thickness(9, 0, 0, 0), Child = SchoolText(source, 15) }
        } };

    private static Ellipse SchoolDot(Color color, double gap) => new()
    {
        Width = 7, Height = 7, Fill = new SolidColorBrush(color),
        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, gap, 0)
    };

    private static Button SchoolButton(FrameworkElement content, string label, Action action)
    {
        var button = new Button
        {
            Content = content, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch, Cursor = Cursors.Hand, ToolTip = label,
            Template = new ControlTemplate(typeof(Button))
            {
                VisualTree = new FrameworkElementFactory(typeof(ContentPresenter))
            }
        };
        System.Windows.Automation.AutomationProperties.SetName(button, label);
        button.Click += (_, _) => action();
        return button;
    }

    private static Button SchoolPaintButton(Border tile, string label, Action action, Brush hover)
    {
        var rest = tile.Background;
        var button = SchoolButton(tile, label, action);
        void Paint() => tile.Background = button.IsEnabled && (button.IsMouseOver || button.IsKeyboardFocusWithin) ? hover : rest;
        button.MouseEnter += (_, _) => Paint();
        button.MouseLeave += (_, _) => Paint();
        button.IsKeyboardFocusWithinChanged += (_, _) => Paint();
        button.IsEnabledChanged += (_, _) => Paint();
        return button;
    }

    private Button SchoolPrimary(string? glyph, string label, Action action)
    {
        Brush ink = Frozen("#111111");
        var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (glyph != null)
        {
            var icon = SchoolIcon(glyph, 20, ink);
            icon.Margin = new Thickness(0, 0, 8, 0);
            content.Children.Add(icon);
        }
        content.Children.Add(SchoolText(label, 15, ink, FontWeight.FromOpenTypeWeight(620)));
        return SchoolPaintButton(new Border { Height = 38, CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 0, 16, 0), Background = CompactInk, Child = content }, label, action, Frozen("#DDDDDD"));
    }

    private Button SchoolTool(string glyph, string label, Action action) =>
        SchoolIconButton(glyph, label, action, 40, 38, 20, tile: true);

    private Button SchoolIconButton(string glyph, string label, Action action, double width = 36, double height = 34,
        double iconSize = 20, bool tile = false)
    {
        var button = SchoolPaintButton(new Border
        {
            Width = width, Height = height, CornerRadius = new CornerRadius(9),
            Margin = tile ? new Thickness(6, 0, 0, 0) : new Thickness(0),
            BorderThickness = tile ? new Thickness(1) : new Thickness(0),
            BorderBrush = SchoolRim, Background = tile ? SchoolTile : Brushes.Transparent,
            Child = SchoolIcon(glyph, iconSize)
        }, label, action, tile ? Frozen("#262628") : SchoolHover);
        button.IsEnabledChanged += (_, _) => button.Opacity = button.IsEnabled ? 1 : .52;
        return button;
    }

    private static FrameworkElement SchoolIcon(string glyph, double size = 20, Brush? ink = null)
    {
        string vector = glyph switch
        {
            "up" => "M12,16V3m-5,5 5,-5 5,5M4,15v5h16v-5",
            "down" => "M12,3v13m-5,-5 5,5 5,-5M4,16v4h16v-4",
            "folder" => "M3,7V5h6l2,2h10v12H3z",
            "chat" => "M4,4h16v12H9l-5,4z M8,9h8m-8,3h5",
            "left" => "M14,6l-6,6 6,6",
            "right" => "M10,6l6,6 -6,6",
            "close" => "M6,14l6,-6 6,6",
            "mini" => "M6,12h12",
            "file" => "M6,3h8l4,4v14H6z M14,3v5h4 M9,12h6m-6,4h6",
            "note" => "M4,5h16v11l-5,4H4z M9,9h7m-7,4h5",
            "book" => "M12,6c-3,-2 -6,-2 -10,-1v13c4,-1 7,-1 10,1 3,-2 6,-2 10,-1V5c-4,-1 -7,-1 -10,1zm0,0v13",
            "push" => "M6,5v14m0,-7c6,0 11,-2 11,-8m-3,3 3,-3 3,3",
            _ => throw new ArgumentOutOfRangeException(nameof(glyph))
        };
        var path = new Path
        {
            Data = Geometry.Parse(vector), Stroke = ink ?? SchoolIconInk, StrokeThickness = 1.65,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round
        };
        return new Viewbox { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center, Child = new Canvas { Width = 24, Height = 24, Children = { path } } };
    }

    private Button SchoolTab(string label, bool selected, Action action) =>
        SchoolPaintButton(new Border { Height = 32, Padding = new Thickness(11, 0, 11, 0), CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 0, 8, 0), Background = selected ? Frozen("#272729") : Brushes.Transparent,
            Child = SchoolText(label, 14, selected ? Brushes.White : SchoolLabel) }, label, action, SchoolHover);

    private void ComposeSchoolDrawer(PiDeadline[] deadlines, PiExam[] exams, DateTimeOffset now)
    {
        var liveKeys = deadlines.Select(d => $"school:hand:{AssignmentKey(d)}").Concat(exams.Select(e => $"school:test:{ExamKey(e)}")).ToHashSet();
        foreach (string key in _schoolRowKeys.Except(liveKeys)) Seen.Forget(key);
        _schoolRowKeys.Clear();
        _schoolRowKeys.UnionWith(liveKeys);
        var body = new StackPanel { Margin = new Thickness(23, 17, 23, 18), MinHeight = 205 };
        _schoolDayClock = null;
        _schoolDayColumns = [];
        Array.Clear(_schoolNowMarkers);

        var tabs = new StackPanel { Orientation = Orientation.Horizontal };
        tabs.Children.Add(SchoolTab(Say("Abgaben", "Work"), _schoolTab == SchoolDrawerTab.Work, () => ChangeSchoolTab(SchoolDrawerTab.Work)));
        tabs.Children.Add(SchoolTab("Tests", _schoolTab == SchoolDrawerTab.Tests, () => ChangeSchoolTab(SchoolDrawerTab.Tests)));
        tabs.Children.Add(SchoolTab(Say("Tag", "Day"), _schoolTab == SchoolDrawerTab.Day, () => ChangeSchoolTab(SchoolDrawerTab.Day)));
        var header = SchoolColumns(tabs, SchoolIconButton("close", Say("Erweiterte Ansicht schließen", "Close extended view"), () =>
        {
            SetDrawerOpen(false);
        }));
        body.Children.Add(new Border { BorderBrush = SchoolRim, BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 0, 0, 10), Margin = new Thickness(0, 0, 0, 12), Child = header });

        if (_schoolTab == SchoolDrawerTab.Day) BuildSchoolDay(body, now);
        else if (_schoolTab == SchoolDrawerTab.Tests && exams.FirstOrDefault(e => ExamKey(e) == _schoolSelectedExam) is { } exam)
            BuildStudyPanel(body, exam);
        else if (_schoolTab == SchoolDrawerTab.Work && deadlines.FirstOrDefault(d => AssignmentKey(d) == _schoolSelectedTask) is { } task)
            BuildTaskPanel(body, task);
        else
        {
            if (_schoolTab == SchoolDrawerTab.Work)
            {
                AddSchoolGroup(body, Say("Abgaben", "Hand-ins"), deadlines.Length, ref _schoolHandInPage,
                    page => { _schoolHandInPage = page; RedrawPi(); });
                if (deadlines.Length == 0) body.Children.Add(SchoolText(Say("Alles abgegeben", "All handed in"), 13));
                foreach (var item in deadlines.Skip(_schoolHandInPage * SchoolPageRows).Take(SchoolPageRows))
                {
                    string key = AssignmentKey(item);
                    var actions = new StackPanel { Orientation = Orientation.Horizontal };
                    var row = SchoolListRow(DeadlineSubject(item), item.Name, TaskWhen(item.Due), SchoolColor(DeadlineSubject(item)),
                        () => OpenTaskPanel(item), actions);
                    AddSchoolTaskActions(item, actions, row, compact: true);
                    row.IsVisibleChanged += (_, _) => { if (row.IsVisible) _ = Dispatcher.BeginInvoke(() => LoadPiAssignment(item.Cmid)); };
                    WatchSchoolRow(row, "school:hand:" + key, DeadlineRevision(item));
                    body.Children.Add(row);
                }
            }
            AddSchoolGroup(body, "Tests", exams.Length, ref _schoolExamPage, page => { _schoolExamPage = page; RedrawPi(); });
            if (exams.Length == 0) body.Children.Add(SchoolText(Say("Keine anstehenden Tests", "No upcoming tests"), 13));
            foreach (var item in exams.Skip(_schoolExamPage * SchoolPageRows).Take(SchoolPageRows))
            {
                string subject = ExamSubject(item);
                Color color = SchoolColor(subject, item.Color);
                var actions = new StackPanel { Orientation = Orientation.Horizontal, Children =
                {
                    SchoolIconButton("folder", Say("Lernordner öffnen", "Open study folder"), () => OpenStudyFolder(item), 26, 28, 15),
                    SchoolIconButton("chat", Say("Claude-Sitzung fortsetzen", "Resume Claude session"), () => ResumeStudy(item), 26, 28, 15)
                } };
                string when = ExamWhen(item);
                var row = SchoolListRow(subject, ExamTitle(item), when, color, () => SelectStudy(item), actions);
                HookStudyDrop(row, item);
                WatchSchoolRow(row, "school:test:" + ExamKey(item), $"{color}|{item.Date.Date == now.Date.AddDays(1)}");
                body.Children.Add(row);
            }
        }
        MountSchoolDrawer(body);
    }

    private static string TaskWhen(DateTimeOffset date) => date.ToLocalTime().ToString("ddd HH:mm", SchoolDateCulture);

    private static string ExamWhen(PiExam exam) => exam.Date.ToString("ddd", SchoolDateCulture)
        + (exam.Start.Length > 0 ? $" {exam.Start}" : "");

    private void ChangeSchoolTab(SchoolDrawerTab tab)
    {
        _schoolTab = tab;
        _schoolSelectedExam = _schoolSelectedTask = _schoolConfirmTask = _schoolArmedPush = null;
        RedrawPi();
    }

    private void MountSchoolDrawer(StackPanel body)
    {
        AddDrawer(body);
        _drawerBody.Margin = new Thickness(0);
        _drawerFrame!.BorderThickness = new Thickness(0);
        _drawerFrame.Background = Brushes.Black;
        var grabber = (Border)_drawerGrabber!;
        var line = (Border)grabber.Child;
        line.Width = 32;
        line.Background = SchoolHandle;
        var collapse = SchoolIconButton("mini", Say("Einklappen", "Collapse Schule"), () => SchoolCollapseRequested?.Invoke(), 36, 24);
        collapse.HorizontalAlignment = HorizontalAlignment.Left;
        collapse.Margin = new Thickness(25, 0, 0, 0);
        var handle = new Grid { Height = 24 };
        grabber.Child = handle;
        handle.Children.Add(line);
        handle.Children.Add(collapse);
        grabber.Margin = new Thickness(0, 0, 0, 14);
        grabber.MouseLeave += (_, _) =>
        {
            line.Background = SchoolHandle;
            if (AnimateSchool) Motion.To(line, WidthProperty, 32, Motion.Press);
            else Motion.Stop(line, WidthProperty, 32);
        };
        grabber.IsVisibleChanged += (_, _) => { if (!grabber.IsVisible) line.Background = SchoolHandle; };

        var tween = _drawerHeight!;
        var paint = tween.Step!;
        tween.Step = height =>
        {
            paint(height);
            ShowSchoolDrawer(height);
        };
        _drawerExtent = MeasureDrawer();
        if (!IsLayoutFollowing) tween.Value = DrawerOpen ? _drawerExtent : 0;
        ShowSchoolDrawer(tween.Value);
    }

    private void ShowSchoolDrawer(double height)
    {
        double shown = Math.Clamp(height / Math.Max(1, _drawerExtent), 0, 1);
        if (shown == 0) _schoolFaceSlot.Height = double.NaN;
        else
        {
            _schoolTestDrop.Measure(new Size(ActualWidth > 0 ? ActualWidth : 600, double.PositiveInfinity));
            _schoolFaceSlot.Height = _schoolTestDrop.DesiredSize.Height * (1 - shown);
        }
        _schoolFaceSlot.ClipToBounds = shown > 0;
        _schoolFaceSlot.IsHitTestVisible = shown < .01;
        _schoolFaceSlot.Visibility = shown >= 1 || !SchoolDataAvailable ? Visibility.Collapsed : Visibility.Visible;
        _drawerGrabber!.Height = 24 * (1 - shown);
        _drawerGrabber.Margin = new Thickness(0, 0, 0, 14 * (1 - shown));
        _drawerGrabber.ClipToBounds = true;
        _drawerGrabber.IsHitTestVisible = shown < 1 || _drawerGrabber.IsMouseCaptured;
        _drawerGrabber.Opacity = 1 - shown;
        bool covered = height >= 1;
        if (covered == _schoolDrawerCoversFace) return;
        _schoolDrawerCoversFace = covered;
        if (covered)
        {
            StopSchoolDial();
            Motion.Stop(_schoolHeroShift, TranslateTransform.XProperty, 0);
            _schoolBreathing = false;
            _schoolLight.Stop(_schoolLightTarget);
        }
        else if (SchoolDataAvailable && IsVisible)
        {
            UpdateSchoolClock(_schoolNow());
            QueueSchoolLight();
        }
    }

    private void AddSchoolGroup(StackPanel body, string name, int count, ref int page, Action<int> change)
    {
        int pages = Math.Max(1, (count + SchoolPageRows - 1) / SchoolPageRows);
        page = Math.Clamp(page, 0, pages - 1);
        var heading = SchoolColumns(SchoolText(name, 12, SchoolLabel, FontWeights.Medium), SchoolPageButtons(page, pages, change));
        heading.Height = 18;
        heading.Margin = new Thickness(0, body.Children.Count == 1 ? 0 : 8, 0, 4);
        body.Children.Add(heading);
    }

    private StackPanel SchoolPageButtons(int page, int pages, Action<int> change)
    {
        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        if (pages <= 1) return controls;
        var back = SchoolIconButton("left", Say("Vorherige Seite", "Previous page"), () => change(page - 1), 24, 18, 14);
        var next = SchoolIconButton("right", Say("Nächste Seite", "Next page"), () => change(page + 1), 24, 18, 14);
        back.IsEnabled = page > 0;
        next.IsEnabled = page + 1 < pages;
        controls.Children.Add(back);
        controls.Children.Add(SchoolText($"{page + 1} / {pages}", 12));
        controls.Children.Add(next);
        return controls;
    }

    private Grid SchoolListRow(string subject, string title, string when, Color color, Action open, StackPanel actions)
    {
        var mark = new StackPanel { Orientation = Orientation.Horizontal, Children =
        {
            SchoolDot(color, 6), SchoolText(subject, 12, Frozen("#CDCDCF"))
        } };
        var text = SchoolText(title, 15, CompactInk, FontWeight.FromOpenTypeWeight(530));
        var info = new StackPanel { Orientation = Orientation.Horizontal, Children = { SchoolText(when, 13), SchoolIcon("right", 15) } };
        info.Children.Insert(1, new Border { Width = 13 });
        var line = new Grid();
        foreach (var width in new[] { new GridLength(62), new GridLength(1, GridUnitType.Star), new GridLength(114) })
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        foreach (var child in new FrameworkElement[] { mark, text, info })
        {
            Grid.SetColumn(child, line.Children.Count);
            line.Children.Add(child);
        }
        info.HorizontalAlignment = HorizontalAlignment.Right;

        var row = new Grid { Height = 33, Background = Brushes.Transparent, Children =
        {
            SchoolPaintButton(new Border { Padding = new Thickness(6, 0, 6, 0), CornerRadius = new CornerRadius(5),
                Background = Brushes.Transparent, Child = line }, $"{subject} {title} · {when}", open, SchoolHover)
        } };
        actions.HorizontalAlignment = HorizontalAlignment.Right;
        actions.VerticalAlignment = VerticalAlignment.Center;
        actions.Margin = new Thickness(0, 0, 6, 0);
        row.Children.Add(actions);
        void Reveal()
        {
            bool show = row.IsVisible && (row.IsMouseOver || row.IsKeyboardFocusWithin) && actions.Children.Count > 0;
            actions.Visibility = show ? Visibility.Visible : Visibility.Hidden;
            info.Visibility = show ? Visibility.Hidden : Visibility.Visible;
        }
        row.MouseEnter += (_, _) => Reveal();
        row.MouseLeave += (_, _) => Reveal();
        row.IsKeyboardFocusWithinChanged += (_, _) => Reveal();
        row.IsVisibleChanged += (_, _) => Reveal();
        Reveal();
        return row;
    }

    private void WatchSchoolRow(FrameworkElement row, string key, string revision)
    {
        row.IsVisibleChanged += (_, _) =>
        {
            if (row.IsVisible && Seen.Changed(key, revision)) SchoolPulse(row);
        };
    }

    private void BuildSchoolDay(StackPanel body, DateTimeOffset now)
    {
        string date = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var school = _piSchool!;
        _schoolDayLessons = (school.Days.TryGetValue(date, out var found) ? found : school.Date == date ? school.Lessons : [])
            .OrderBy(l => l.StartMinutes).ToArray();
        _schoolDayClock = SchoolText("", 13);
        Typography.SetNumeralAlignment(_schoolDayClock, FontNumeralAlignment.Tabular);
        var nowText = new StackPanel { Orientation = Orientation.Horizontal, Children =
        {
            SchoolText(Say("Jetzt", "Now"), 13), new Border { Width = 13 }, _schoolDayClock
        } };
        var head = SchoolColumns(SchoolHeading(now.ToString("dddd, d MMM", SchoolDateCulture), 19, 1.45, .94, 550), nowText);
        head.Margin = new Thickness(0, 0, 0, 10);
        body.Children.Add(head);
        if (_schoolDayLessons.Length == 0)
        {
            body.Children.Add(SchoolText(school.Break ?? Say("Heute kein Unterricht", "No lessons today"), 13));
            UpdateSchoolDayClock(now);
            return;
        }

        _schoolDayColumns = [new Grid(), new Grid()];
        var timeline = new Grid { Height = LessonStep * 2 + LessonHeight };
        timeline.ColumnDefinitions.Add(new ColumnDefinition());
        timeline.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(15) });
        timeline.ColumnDefinitions.Add(new ColumnDefinition());
        timeline.Children.Add(_schoolDayColumns[0]);
        Grid.SetColumn(_schoolDayColumns[1], 2);
        timeline.Children.Add(_schoolDayColumns[1]);
        body.Children.Add(timeline);
        int pages = (_schoolDayLessons.Length + SchoolDayPageSize - 1) / SchoolDayPageSize;
        int preceding = Array.FindLastIndex(_schoolDayLessons, lesson => lesson.StartMinutes <= now.Hour * 60 + now.Minute);
        _schoolLessonOffset = Math.Clamp(_schoolLessonOffset ?? preceding / SchoolDayPageSize, 0, pages - 1);
        var paging = SchoolPageButtons(_schoolLessonOffset.Value, pages, page => { _schoolLessonOffset = page; RedrawPi(); });
        paging.HorizontalAlignment = HorizontalAlignment.Right;
        if (pages > 1) body.Children.Add(paging);
        _schoolDayRevision = _schoolMarkerRevision = null;
        timeline.IsVisibleChanged += (_, _) => { if (timeline.IsVisible) UpdateSchoolDayClock(_schoolNow()); };
        UpdateSchoolDayClock(now);
    }

    private void UpdateSchoolDayClock(DateTimeOffset now)
    {
        if (_schoolDayClock == null) return;
        string clock = now.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (_schoolDayClock.IsVisible) Seen.Changed("school:today-clock", clock);
        _schoolDayClock.Text = clock;
        if (_schoolDayColumns.Length == 0) return;
        int minute = now.Hour * 60 + now.Minute;
        var lessons = _schoolDayLessons.Skip((_schoolLessonOffset ?? 0) * SchoolDayPageSize).Take(SchoolDayPageSize).ToArray();
        string revision = string.Join("|", lessons.Select(l => $"{l}:{SchoolColor(LessonSubject(l), l.Color)}"));
        var markerRevision = $"{revision}|{clock}";
        bool repaint = _schoolDayRevision != revision;
        if (!repaint && _schoolMarkerRevision == markerRevision)
        {
            if (_schoolDayColumns[0].IsVisible) Seen.Changed("school:lessons", revision);
            return;
        }
        _schoolDayRevision = revision;
        _schoolMarkerRevision = markerRevision;
        if (_schoolDayColumns[0].IsVisible && Seen.Changed("school:lessons", revision))
            foreach (var column in _schoolDayColumns) SchoolPulse(column);

        for (int c = 0; c < _schoolDayColumns.Length; c++)
        {
            var column = _schoolDayColumns[c];
            var periods = lessons.Skip(c * 3).Take(3).ToArray();
            if (repaint)
            {
                column.Children.Clear();
                for (int row = 0; row < periods.Length; row++)
                {
                    var lesson = SchoolLesson(periods[row]);
                    lesson.Margin = new Thickness(0, row * LessonStep, 0, 0);
                    column.Children.Add(lesson);
                }
            }
            if (_schoolNowMarkers[c]?.Parent is Panel parent) parent.Children.Remove(_schoolNowMarkers[c]);
            _schoolNowMarkers[c] = null;
            if (periods.Length == 0 || minute < periods[0].StartMinutes || minute > periods[^1].EndMinutes) continue;

            int index = Array.FindLastIndex(periods, lesson => lesson.StartMinutes <= minute);
            var period = periods[index];
            bool duringLesson = minute <= period.EndMinutes;
            double y = duringLesson
                ? LessonHeight * (minute - period.StartMinutes) / Math.Max(1, period.EndMinutes - period.StartMinutes)
                : index * LessonStep + LessonHeight + (LessonStep - LessonHeight) * (minute - period.EndMinutes)
                    / Math.Max(1, periods[index + 1].StartMinutes - period.EndMinutes);
            var marker = new Grid { IsHitTestVisible = false, Height = 5, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(33, y - 2, 0, 0), Children =
                {
                    new Border { Height = 1, Background = Frozen("#8CF5F5F7"), Margin = new Thickness(3, 0, 0, 0) },
                    new Ellipse { Width = 5, Height = 5, Fill = CompactInk, HorizontalAlignment = HorizontalAlignment.Left }
                } };
            _schoolNowMarkers[c] = marker;
            if (duringLesson && column.Children[index] is Grid lessonRow)
            {
                Grid.SetColumnSpan(marker, 2);
                lessonRow.Children.Insert(lessonRow.Children.Count - 1, marker);
            }
            else column.Children.Add(marker);
        }
    }

    private static string LessonSubject(PiLesson lesson) => SchoolSubject(lesson.SubjectKey.Length > 0 ? lesson.SubjectKey : lesson.Subject);

    private Grid SchoolLesson(PiLesson lesson)
    {
        string subject = LessonSubject(lesson);
        var time = SchoolText(lesson.Start, 11);
        time.VerticalAlignment = VerticalAlignment.Top;
        var title = SchoolText(subject, 17, lesson.Cancelled ? SchoolLabel : CompactInk, FontWeight.FromOpenTypeWeight(550));
        if (lesson.Cancelled) title.TextDecorations = TextDecorations.Strikethrough;
        var room = SchoolText("", 12);
        if (lesson.Cancelled) room.Text = Say("Entfällt", "Cancelled");
        else if (lesson.OriginalRoom.Length > 0 && lesson.OriginalRoom != lesson.Room)
        {
            room.Inlines.Add(new Run(lesson.OriginalRoom) { TextDecorations = TextDecorations.Strikethrough });
            room.Inlines.Add(new Run($" → {lesson.Room}"));
        }
        else room.Text = lesson.Room;
        var stripe = new Border { Width = 3, Height = 17, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 9, 0),
            Background = new SolidColorBrush(SchoolColor(subject, lesson.Color)) };
        var roomGroup = new Border { Padding = new Thickness(9, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Child = room };
        var contents = new DockPanel { VerticalAlignment = VerticalAlignment.Center, Children = { stripe, title, roomGroup } };
        var box = new Border { CornerRadius = new CornerRadius(6),
            Background = Frozen(lesson.Cancelled ? "#080808" : "#141415"),
            BorderBrush = lesson.Cancelled ? null : SchoolRim, BorderThickness = new Thickness(1) };
        title.Background = roomGroup.Background = box.Background;
        var copy = new Border { Padding = new Thickness(9, 0, 9, 0), Child = contents, IsHitTestVisible = false };
        var row = new Grid { Height = LessonHeight, VerticalAlignment = VerticalAlignment.Top, ToolTip = $"{lesson.Subject} · {lesson.Start}–{lesson.End} · {lesson.Change}" };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.Children.Add(time);
        Grid.SetColumn(box, 1);
        row.Children.Add(box);
        if (lesson.Cancelled)
        {
            var dash = new Rectangle { RadiusX = 6, RadiusY = 6, Stroke = SchoolRim, StrokeDashArray = new DoubleCollection { 2, 2 },
                StrokeThickness = 1, IsHitTestVisible = false };
            Grid.SetColumn(dash, 1);
            row.Children.Add(dash);
        }
        Grid.SetColumn(copy, 1);
        row.Children.Add(copy);
        return row;
    }
}
