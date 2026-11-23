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
    private const int SchoolPageRows = 3;
    private static readonly Brush SchoolHover = Frozen("#12FFFFFF");
    private TextBlock? _schoolDayClock;
    private Grid? _schoolNowLayer;
    private Grid? _schoolLessonRows;
    private PiLesson[] _schoolDayLessons = [];
    private string? _schoolDayRevision, _schoolMarkerRevision;
    private int? _schoolLessonOffset;
    private int _schoolHandInPage, _schoolExamPage;
    private string? _schoolSelectedExam;
    private readonly HashSet<string> _schoolRowKeys = [];

    internal static Color SubjectColor(string subject) => PiDashboardClient.UntisColor(SchoolSubject(subject));

    private Color SchoolColor(string subject)
    {
        if (_piSchool?.SubjectColors.TryGetValue(subject, out var exact) == true) return exact;
        return SubjectColor(subject);
    }

    private static string SchoolSubject(string value) => PiDashboardClient.UntisSubject(value);

    private static string DeadlineSubject(PiDeadline deadline) => SchoolSubject(deadline.Course);
    private static string AssignmentKey(PiDeadline deadline) => deadline.Cmid > 0 ? $"cmid:{deadline.Cmid}" : $"{deadline.Course}|{deadline.Name}";
    private static string ExamKey(PiExam exam) => $"{exam.Date:yyyy-MM-dd}|{exam.Start}|{exam.Subject}|{exam.Kind}|{exam.Title}";
    private static string ExamTitle(PiExam exam) => exam.Title.Length > 0 ? exam.Title : exam.Kind;
    private static string SchoolSource(PiDeadline deadline) => deadline.Kind switch { "classroom" => "Classroom 50", "nw" => "NW", _ => "Moodle" };

    private string DeadlineRevision(PiDeadline deadline)
    {
        _piAssignments.TryGetValue(deadline.Cmid, out var assignment);
        _piTasks.TryGetValue(deadline.Cmid, out var task);
        _schoolPush.TryGetValue(AssignmentKey(deadline), out var push);
        return $"{AssignmentKey(deadline)}|{deadline}|{SchoolColor(DeadlineSubject(deadline))}|{assignment}|{task}|{push}|{_schoolArmedPush == AssignmentKey(deadline)}";
    }

    private FrameworkElement SchoolMark(string subject)
    {
        var label = SchoolText(subject, 12, CompactInk, FontWeights.SemiBold);
        var dot = new Ellipse
        {
            Width = 7, Height = 7, Fill = new SolidColorBrush(SchoolColor(subject)),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0)
        };
        return new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { dot, label } };
    }

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

    private Button SchoolAction(int glyph, string label, Action action, bool iconOnly = true)
    {
        FrameworkElement icon = SchoolIcon(glyph);
        FrameworkElement content = icon;
        if (!iconOnly)
        {
            icon.Margin = new Thickness(0, 0, 6, 0);
            content = new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, SchoolText(label, 12, CompactInk) } };
        }
        var tile = new Border
        {
            MinWidth = 26, Height = iconOnly ? 24 : 28, CornerRadius = new CornerRadius(8),
            Padding = new Thickness(iconOnly ? 4 : 8, 0, iconOnly ? 4 : 8, 0),
            Background = iconOnly ? Brushes.Transparent : SchoolTile,
            Child = content
        };
        var button = SchoolButton(tile, label, action);
        void Paint() => tile.Background = button.IsMouseOver || button.IsKeyboardFocusWithin ? SchoolHover : iconOnly ? Brushes.Transparent : SchoolTile;
        button.IsMouseDirectlyOverChanged += (_, _) => Paint();
        button.MouseEnter += (_, _) => Paint();
        button.MouseLeave += (_, _) => Paint();
        button.IsKeyboardFocusWithinChanged += (_, _) => Paint();
        return button;
    }

    private Button SchoolPager(int glyph, string label, Action click)
    {
        var button = SchoolAction(glyph, label, click);
        button.Margin = new Thickness(0, 0, 4, 0);
        var tile = (Border)button.Content;
        tile.Height = 26;
        tile.MinWidth = 28;
        tile.BorderBrush = SchoolRim;
        tile.BorderThickness = new Thickness(1);
        return button;
    }

    private FrameworkElement SchoolIcon(int glyph)
    {
        string? vector = glyph switch
        {
            0xE896 => "M12,4 V15 M7,10 L12,15 17,10 M5,20 H19",
            0xE898 => "M12,20 V9 M7,14 L12,9 17,14 M5,4 H19",
            0xE74A => "M12,19 V5 M6,11 L12,5 18,11",
            0xE8A7 => "M14,4 H20 V10 M20,4 L11,13 M18,14 V20 H4 V6 H10",
            0xE72B => "M19,12 H5 M11,6 L5,12 11,18",
            0xE756 => "M12,3 V7 M12,17 V21 M3,12 H7 M17,12 H21 M6,6 L8.5,8.5 M15.5,15.5 L18,18 M6,18 L8.5,15.5 M15.5,8.5 L18,6",
            0xE70D => "M6,9 L12,15 18,9",
            0xE70E => "M6,15 L12,9 18,15",
            _ => null
        };
        if (vector == null)
        {
            var glyphBlock = CompactGlyph(glyph, CompactInk);
            glyphBlock.FontSize = 14;
            glyphBlock.Width = 24;
            return glyphBlock;
        }

        var path = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(vector), Stroke = CompactInk, StrokeThickness = 1.7,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round
        };
        return new Viewbox
        {
            Width = 15, Height = 15, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
            Child = new Canvas { Width = 24, Height = 24, Children = { path } }
        };
    }
    private FrameworkElement ExamLight(PiExam exam, DateTimeOffset now)
    {
        var glow = new Border { IsHitTestVisible = false, CornerRadius = new CornerRadius(8) };
        if (exam.Date.Date == now.Date.AddDays(1))
        {
            Color color = SchoolColor(ExamSubject(exam));
            glow.Background = new RadialGradientBrush(Alpha(color, .12), Alpha(color, 0))
                { Center = new Point(.12, .5), GradientOrigin = new Point(.12, .5), RadiusX = .42, RadiusY = 1 };
        }
        return glow;
    }

    private void ComposeSchoolDrawer(PiDeadline[] deadlines, PiExam[] exams, DateTimeOffset now)
    {
        var liveKeys = deadlines.Select(d => $"school:hand:{AssignmentKey(d)}").Concat(exams.Select(e => $"school:test:{ExamKey(e)}")).ToHashSet();
        foreach (string key in _schoolRowKeys.Except(liveKeys)) Seen.Forget(key);
        _schoolRowKeys.Clear();
        _schoolRowKeys.UnionWith(liveKeys);
        var body = new StackPanel { Margin = new Thickness(4, 0, 4, 0) };
        _schoolDayClock = null;
        _schoolNowLayer = null;
        _schoolLessonRows = null;

        if (exams.FirstOrDefault(e => ExamKey(e) == _schoolSelectedExam) is { } selected)
        {
            BuildStudyPanel(body, selected);
        }
        else
        {
            _schoolSelectedExam = null;
            int handPages = PageCount(deadlines.Length, SchoolPageRows);
            int testPages = PageCount(exams.Length, SchoolPageRows);
            _schoolHandInPage = Math.Clamp(_schoolHandInPage, 0, handPages - 1);
            _schoolExamPage = Math.Clamp(_schoolExamPage, 0, testPages - 1);

            body.Children.Add(SchoolSection(Say("Abgaben", "Hand-ins"), _schoolHandInPage, handPages, page => { _schoolHandInPage = page; RedrawPi(); }));
            if (deadlines.Length == 0) body.Children.Add(SchoolText(Say("Keine offenen Abgaben", "No open hand-ins"), 12));
            foreach (var item in deadlines.Skip(_schoolHandInPage * SchoolPageRows).Take(SchoolPageRows))
            {
                string revision = DeadlineRevision(item);
                var actions = new StackPanel { Orientation = Orientation.Horizontal };
                var row = SchoolListRow(DeadlineSubject(item), item.Name, item.Due.ToLocalTime().ToString("ddd d.M.", Loc.GetCulture()), actions);
                AddSchoolTaskActions(item, actions, (Grid)row.Child, compact: true);
                row.IsVisibleChanged += (_, _) => { if (row.IsVisible) _ = Dispatcher.BeginInvoke(() => LoadPiAssignment(item.Cmid)); };
                RevealSchoolActions(row, actions);
                WatchSchoolRow(row, $"school:hand:{AssignmentKey(item)}", revision);
                body.Children.Add(row);
            }

            body.Children.Add(SchoolSection(Say("Tests", "Tests"), _schoolExamPage, testPages, page => { _schoolExamPage = page; RedrawPi(); }));
            if (exams.Length == 0) body.Children.Add(SchoolText(Say("Keine anstehenden Tests", "No upcoming tests"), 12));
            foreach (var exam in exams.Skip(_schoolExamPage * SchoolPageRows).Take(SchoolPageRows))
            {
                string subject = ExamSubject(exam);
                var open = SchoolAction(0xE70D, Say("Lernmaterial öffnen", "Open study materials"), () => SelectStudy(exam));
                var row = SchoolListRow(subject, ExamTitle(exam), exam.Date.ToString("ddd d.M.", Loc.GetCulture()), open);
                var line = (Grid)row.Child;
                line.Children.Insert(0, ExamLight(exam, now));
                Grid.SetColumnSpan(line.Children[0], line.ColumnDefinitions.Count);
                row.Cursor = Cursors.Hand;
                row.MouseLeftButtonUp += (_, e) => { e.Handled = true; SelectStudy(exam); };
                HookStudyDrop(row, exam);
                RevealSchoolActions(row, open);
                WatchSchoolRow(row, $"school:test:{ExamKey(exam)}", $"{subject}|{SchoolColor(subject)}|{exam.Date.Date == now.Date.AddDays(1)}");
                body.Children.Add(row);
            }

            BuildSchoolDay(body, now);
        }
        AddDrawer(body);

        static int PageCount(int count, int size) => Math.Max(1, (count + size - 1) / size);
    }

    private FrameworkElement SchoolSection(string name, int page, int pages, Action<int> change)
    {
        var label = SchoolText(name, 16, CompactInk, FontWeights.SemiBold);
        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        if (pages > 1)
        {
            var back = SchoolAction(0xE76B, Say("Vorherige Seite", "Previous page"), () => change(page - 1));
            var next = SchoolAction(0xE76C, Say("Nächste Seite", "Next page"), () => change(page + 1));
            back.IsEnabled = page > 0;
            next.IsEnabled = page + 1 < pages;
            back.Opacity = back.IsEnabled ? 1 : .35;
            next.Opacity = next.IsEnabled ? 1 : .35;
            controls.Children.Add(back);
            controls.Children.Add(SchoolText($"{page + 1}/{pages}", 12));
            controls.Children.Add(next);
        }
        var heading = SchoolColumns(label, controls);
        heading.Height = 24;
        return heading;
    }

    private Border SchoolListRow(string subject, string title, string when, FrameworkElement actions)
    {
        var text = SchoolText(title, 12, CompactInk);
        text.ToolTip = title;
        var date = SchoolText(when, 12);
        var line = new Grid { Background = Brushes.Transparent };
        foreach (var width in new[] { new GridLength(62), new GridLength(1, GridUnitType.Star), new GridLength(80), new GridLength(64) })
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        foreach (var child in new[] { SchoolMark(subject), text, date, actions })
        {
            Grid.SetColumn(child, line.Children.Count);
            line.Children.Add(child);
        }
        actions.HorizontalAlignment = HorizontalAlignment.Right;
        actions.VerticalAlignment = VerticalAlignment.Center;
        return new Border { Height = 26, BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = SchoolRim, Background = Brushes.Transparent, Child = line };
    }

    private void RevealSchoolActions(Border row, FrameworkElement actions)
    {
        void Reveal()
        {
            bool show = row.IsVisible && (row.IsMouseOver || row.IsKeyboardFocusWithin);
            actions.IsHitTestVisible = show;
            double target = show ? 1 : 0;
            if (AnimateSchool && row.IsVisible && actions.Opacity != target && (show || actions.Opacity > 0))
                Motion.To(actions, OpacityProperty, target, show ? Motion.PressDown : Motion.Exit);
            else Motion.Stop(actions, OpacityProperty, target);
        }
        actions.Opacity = 0;
        actions.IsHitTestVisible = false;
        row.MouseEnter += (_, _) => Reveal();
        row.MouseLeave += (_, _) => Reveal();
        row.IsKeyboardFocusWithinChanged += (_, _) => Reveal();
        row.IsVisibleChanged += (_, _) => Reveal();
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

        var head = SchoolColumns(SchoolText(Say("Heute", "Today"), 16, CompactInk, FontWeights.SemiBold),
            SchoolText(now.ToString("ddd, d MMM", Loc.GetCulture()), 12));
        head.Height = 24;
        body.Children.Add(head);
        if (_schoolDayLessons.Length == 0)
        {
            body.Children.Add(SchoolText(school.Break ?? Say("Heute kein Unterricht", "No lessons today"), 12));
            return;
        }

        _schoolDayClock = SchoolText("", 30, CompactInk, FontWeights.Medium);
        Typography.SetNumeralAlignment(_schoolDayClock, FontNumeralAlignment.Tabular);
        var nowLabel = SchoolText(Say("Jetzt", "Now"), 12);
        var back = SchoolPager(0xE70E, Say("Frühere Stunden", "Earlier lessons"), () => PageLessons(-1));
        var next = SchoolPager(0xE70D, Say("Spätere Stunden", "Later lessons"), () => PageLessons(1));
        var navigation = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 0), Children = { back, next } };
        navigation.Visibility = _schoolDayLessons.Length > 3 ? Visibility.Visible : Visibility.Collapsed;
        var locator = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { nowLabel, _schoolDayClock, navigation } };
        _schoolLessonRows = new Grid();
        _schoolNowLayer = new Grid { IsHitTestVisible = false };
        var layout = new Grid { Height = 108 };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(129) });
        layout.ColumnDefinitions.Add(new ColumnDefinition());
        layout.Children.Add(locator);
        var timeline = new Grid { VerticalAlignment = VerticalAlignment.Center, Children = { _schoolLessonRows, _schoolNowLayer } };
        Grid.SetColumn(timeline, 1);
        layout.Children.Add(timeline);
        body.Children.Add(layout);
        _schoolDayRevision = _schoolMarkerRevision = null;
        _schoolLessonOffset = _schoolLessonOffset is { } offset ? Math.Clamp(offset, 0, Math.Max(0, _schoolDayLessons.Length - 3)) : null;
        UpdateSchoolDayClock(now);

        void PageLessons(int step)
        {
            _schoolLessonOffset = Math.Clamp(VisibleLessonOffset(_schoolNow()) + step, 0, Math.Max(0, _schoolDayLessons.Length - 3));
            UpdateSchoolDayClock(_schoolNow());
        }
    }

    private int VisibleLessonOffset(DateTimeOffset now)
    {
        int minute = now.Hour * 60 + now.Minute;
        int preceding = Array.FindLastIndex(_schoolDayLessons, l => l.StartMinutes <= minute);
        return _schoolLessonOffset ?? Math.Clamp(preceding - 1, 0, Math.Max(0, _schoolDayLessons.Length - 3));
    }

    private void UpdateSchoolDayClock(DateTimeOffset now)
    {
        if (_schoolDayClock == null || _schoolLessonRows == null || _schoolNowLayer == null) return;
        string clock = now.ToString("HH:mm", CultureInfo.InvariantCulture);
        Seen.Changed("school:today-clock", clock);
        _schoolDayClock.Text = clock;
        int minute = now.Hour * 60 + now.Minute;
        int offset = VisibleLessonOffset(now);
        var lessons = _schoolDayLessons.Skip(offset).Take(3).ToArray();
        int active = Array.FindIndex(lessons, l => !l.Cancelled && minute >= l.StartMinutes && minute < l.EndMinutes);
        string revision = $"{offset}|{active}|" + string.Join("|", lessons.Select(l => $"{l}:{SchoolColor(SchoolSubject(l.SubjectKey.Length > 0 ? l.SubjectKey : l.Subject))}"));
        if (_schoolDayRevision != revision)
        {
            _schoolDayRevision = revision;
            _schoolLessonRows.Children.Clear();
            _schoolLessonRows.RowDefinitions.Clear();
            for (int i = 0; i < lessons.Length; i++)
            {
                _schoolLessonRows.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
                var item = SchoolLesson(lessons[i], i == active);
                Grid.SetRow(item, i);
                _schoolLessonRows.Children.Add(item);
            }
        }
        bool changed = _schoolLessonRows.IsVisible && Seen.Changed("school:lessons", revision);
        if (changed) SchoolPulse(_schoolLessonRows);
        string markerRevision = $"{revision}|{clock}";
        if (_schoolMarkerRevision == markerRevision) return;
        _schoolMarkerRevision = markerRevision;
        _schoolNowLayer.Children.Clear();
        if (minute < lessons[0].StartMinutes || minute > lessons[^1].EndMinutes) return;
        // Time within a lesson uses the box height; time in a break uses the gap between boxes.
        int index = Array.FindLastIndex(lessons, l => l.StartMinutes <= minute);
        var period = lessons[index];
        double y = minute <= period.EndMinutes
            ? index * 36 + 2 + 31 * (minute - period.StartMinutes) / Math.Max(1d, period.EndMinutes - period.StartMinutes)
            : index * 36 + 33 + 5 * (minute - period.EndMinutes) / Math.Max(1d, lessons[index + 1].StartMinutes - period.EndMinutes);
        var color = new SolidColorBrush(SchoolMist);
        var marker = new Ellipse { Width = 6, Height = 6, Fill = color };
        var tick = new Border
        {
            Height = 1, Margin = new Thickness(38, 0, 0, 0),
            Background = new LinearGradientBrush
            {
                StartPoint = new Point(0, .5), EndPoint = new Point(1, .5),
                GradientStops = { new(SchoolMist, 0), new(SchoolMist, .025), new(Alpha(SchoolMist, 0), .028),
                    new(Alpha(SchoolMist, 0), .63), new(Alpha(SchoolMist, .16), .64), new(Alpha(SchoolMist, .6), 1) }
            }
        };
        tick.VerticalAlignment = VerticalAlignment.Top;
        tick.Margin = new Thickness(38, Math.Max(0, y - .5), 0, 0);
        marker.HorizontalAlignment = HorizontalAlignment.Left;
        marker.VerticalAlignment = VerticalAlignment.Top;
        marker.Margin = new Thickness(35, Math.Max(0, y - 3), 0, 0);
        _schoolNowLayer.Children.Add(tick);
        _schoolNowLayer.Children.Add(marker);
    }

    private FrameworkElement SchoolLesson(PiLesson lesson, bool active)
    {
        string subject = SchoolSubject(lesson.SubjectKey.Length > 0 ? lesson.SubjectKey : lesson.Subject);
        var time = SchoolText(lesson.Start, 12);
        var title = SchoolText(subject, 21, CompactInk, FontWeights.SemiBold);
        if (lesson.Cancelled) title.TextDecorations = TextDecorations.Strikethrough;
        string roomText = lesson.OriginalRoom.Length > 0 && lesson.OriginalRoom != lesson.Room
            ? $"{lesson.OriginalRoom} → {lesson.Room}" : lesson.Room;
        var room = SchoolText(roomText, 12);
        var left = new StackPanel { Orientation = Orientation.Horizontal, Children =
        {
            title,
            new Border { Padding = new Thickness(9, 0, 0, 0), Child = room }
        } };
        var detail = lesson.Cancelled ? Say("Entfällt", "Cancelled") : lesson.Change switch
        {
            "anderer Raum" => Say("Raumwechsel", "Room change"),
            "Vertretung" => Say("Vertretung", "Substitute"),
            "kein Lehrer" => Say("Kein Lehrer", "No teacher"),
            _ => lesson.Change
        };
        var box = new Border
        {
            CornerRadius = new CornerRadius(9), Background = active ? Frozen("#1DFFFFFF") : SchoolTile,
            BorderBrush = active ? Frozen("#45FFFFFF") : SchoolRim, BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 2, 0, 2), ToolTip = $"{lesson.Subject}  ·  {lesson.Start}–{lesson.End}",
            Child = new Grid { Children =
            {
                new Border { Width = 3, Background = new SolidColorBrush(lesson.Color ?? SchoolColor(subject)), CornerRadius = new CornerRadius(2),
                    HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 5, 0, 5) },
                new Border { Padding = new Thickness(11, 0, 10, 0), Child = SchoolColumns(left, SchoolText(detail, 12)) }
            } }
        };
        if (lesson.Cancelled) box.Opacity = .55;
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.Children.Add(time);
        Grid.SetColumn(box, 1); row.Children.Add(box);
        return row;
    }
}
