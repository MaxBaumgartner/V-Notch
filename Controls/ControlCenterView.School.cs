using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using VNotch.Services;
using VNotch.Services.ControlCenter;

namespace VNotch.Controls;

public partial class ControlCenterView
{
    private static readonly Color[] SubjectPalette =
    [
        Color.FromRgb(0x8F, 0xA9, 0xC4), Color.FromRgb(0xC9, 0x97, 0x90),
        Color.FromRgb(0x9D, 0xBD, 0xB1), Color.FromRgb(0xA6, 0xA2, 0xC8),
        Color.FromRgb(0xC9, 0xB0, 0x8E), Color.FromRgb(0xCF, 0x9A, 0xB2),
        Color.FromRgb(0xA6, 0xB7, 0x96), Color.FromRgb(0xB5, 0xA0, 0x8F)
    ];
    private static readonly IReadOnlyDictionary<string, int> SubjectColors = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["AM"] = 0, ["D"] = 1, ["E"] = 2, ["POSE"] = 3, ["WMC"] = 4,
        ["NSCS"] = 5, ["DBI"] = 6, ["SYP"] = 7, ["BWM"] = 4, ["ITP"] = 2, ["SEW"] = 3
    };
    private static string SchoolSubject(string value)
    {
        string raw = value.Trim().ToUpperInvariant();
        if (raw.Contains("MATHEMATIK", StringComparison.Ordinal)) return "AM";
        if (raw.StartsWith("DEUTSCH", StringComparison.Ordinal)) return "D";
        if (raw.StartsWith("ENGLISCH", StringComparison.Ordinal)) return "E";
        if (raw.Contains("CLASSROOM", StringComparison.Ordinal)) return "WMC";
        string first = new string(raw.SkipWhile(char.IsDigit).TakeWhile(char.IsLetter).ToArray());
        foreach (string key in SubjectColors.Keys.OrderByDescending(k => k.Length))
            if (first == key || key.Length > 1 && first.StartsWith(key, StringComparison.Ordinal)) return key;
        return first.Length > 0 ? first : raw;
    }

    internal static Color SubjectColor(string subject)
    {
        string key = SchoolSubject(subject);
        if (SubjectColors.TryGetValue(key, out int index)) return SubjectPalette[index];
        uint hash = 2166136261;
        foreach (char c in key) hash = unchecked((hash ^ c) * 16777619);
        return SubjectPalette[hash % SubjectPalette.Length];
    }

    private static string DeadlineSubject(PiDeadline deadline) => SchoolSubject(deadline.Course);
    private string? _selectedAssignment;
    private readonly Dictionary<string, Border> _assignmentPanels = new();
    private DateTime? _schoolSelectedDay;
    private Border? _weekLessons;
    private readonly Dictionary<DateTime, Border> _weekButtons = new();
    private DispatcherTimer? _schoolCountdownTimer;
    private StackPanel? _countdownDigits;
    private DateTimeOffset? _countdownDue;
    private string _countdownShown = "";
    private static readonly Brush HandedIn = Frozen("#9DBDB1");
    private readonly HashSet<PiExam> _schoolExamsOnTop = new();

    private void BuildPiSchool()
    {
        _schoolCountdownTimer?.Stop();
        _countdownDigits = null; _countdownDue = null; _countdownShown = "";
        _assignmentPanels.Clear(); _piSchoolSection.Children.Clear();
        if (_isLocalOnly() || _pi?.IsConfigured != true)
        {
            RemoveCompactDrawer();
            _piSchoolSection.Children.Add(NewRow(Glyph(0xE7BA), Loc.Get(_isLocalOnly() ? "pi.localOnly" : "pi.notConfigured"), muted: true));
            SetCompactStatus(""); return;
        }
        if (_piSchool is not { } school)
        {
            RemoveCompactDrawer();
            _piSchoolSection.Children.Add(new SkeletonPlaceholder(!_piReachable, Loc.Get(_piReachable ? "pi.loading" : "pi.unreachable"), 56, 28, 28, 28));
            SetCompactStatus(""); return;
        }
        var open = school.Deadlines.Where(d => !d.Submitted).OrderBy(d => d.Due).ToList();
        var exams = school.Exams.Where(e => e.Date.Date >= DateTime.Today).OrderBy(e => e.Date).ToList();
        SetCompactStatus($"{open.Count} offen");
        var mood = SchoolMood(open.Count > 0 ? open[0].Due - DateTimeOffset.Now : null);
        SetMoodColors(mood.Main, mood.Warmer, mood.Cooler);
        if (open.Count > 0) _piSchoolSection.Children.Add(SchoolDeadline(open[0], hero: true));
        else
        {
            var empty = new StackPanel { Margin = new Thickness(0, 6, 0, 12) };
            empty.Children.Add(CompactText(Loc.Get("school.nothingDue"), 14.5, CompactInk));
            if (exams.FirstOrDefault() is { } next)
                empty.Children.Add(CompactText($"{ExamSubject(next)} · {next.Kind} · {next.Date:ddd d.M.}", 11.5, CompactLabel));
            _piSchoolSection.Children.Add(empty);
        }
        var merged = open.Skip(1).Select(d => (At: d.Due, Deadline: (PiDeadline?)d, Exam: (PiExam?)null))
            .Concat(exams.Select(e => (At: SchoolExamTime(e), Deadline: (PiDeadline?)null, Exam: (PiExam?)e)))
            .OrderBy(e => e.At).Take(3);
        _schoolExamsOnTop.Clear();
        foreach (var item in merged)
        {
            if (item.Exam is { } exam) _schoolExamsOnTop.Add(exam);
            _piSchoolSection.Children.Add(item.Deadline is { } deadline ? SchoolDeadline(deadline, hero: false) : SchoolExam(item.Exam!));
        }
        UpdateCountdownVisibility();
    }

    private FrameworkElement SchoolDeadline(PiDeadline deadline, bool hero)
    {
        string key = AssignmentKey(deadline);
        var stack = new StackPanel();
        FrameworkElement head;
        if (hero)
        {
            var day = deadline.Due.LocalDateTime;
            var wd = CompactText(day.ToString("ddd", Loc.GetCulture()).ToUpperInvariant(), 9.5, Frozen("#281A18"));
            wd.TextAlignment = TextAlignment.Center;
            var date = CompactText(day.Day.ToString(CultureInfo.CurrentCulture), 19, CompactInk); date.TextAlignment = TextAlignment.Center;
            var badge = new Border { Width = 42, CornerRadius = new CornerRadius(9), Background = Frozen("#1C1C1F"), ClipToBounds = true,
                Child = new StackPanel { Children = { new Border { Background = Frozen("#C99790"), Child = wd, Padding = new Thickness(0, 2, 0, 1) }, date } } };
            var title = CompactText(deadline.Name, 14.5, CompactInk); title.FontWeight = FontWeights.SemiBold;
            var meta = CompactText(deadline.Course, 11.5, CompactLabel); meta.Margin = new Thickness(0, 2, 0, 0); meta.ToolTip = deadline.Course;
            var labels = new StackPanel { Margin = new Thickness(12, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, Children = { title, meta } };
            _countdownDigits = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            _countdownDue = deadline.Due;
            var due = CompactText(Loc.Get("school.until", day.ToString("ddd HH:mm", Loc.GetCulture())), 10.5, CompactLabel);
            due.HorizontalAlignment = HorizontalAlignment.Right;
            var countdown = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { _countdownDigits, due } };
            var grid = new Grid { Margin = new Thickness(0, 3, 0, 8), MinHeight = 48 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(badge); Grid.SetColumn(labels, 1); grid.Children.Add(labels);
            Grid.SetColumn(countdown, 2); grid.Children.Add(countdown);
            head = grid;
            TickSchoolCountdown();
            _countdownDigits.IsVisibleChanged += (_, _) => UpdateCountdownVisibility();
        }
        else head = SchoolEventLine(deadline.Name, deadline.Due.LocalDateTime.ToString("ddd HH:mm", Loc.GetCulture()), SubjectColor(DeadlineSubject(deadline)));
        var hit = new Border { Background = Brushes.Transparent, CornerRadius = new CornerRadius(9), Child = head, Cursor = Cursors.Hand };
        Pressable(hit, () => Brushes.Transparent, Frozen("#0AFFFFFF"), gentle: true);
        hit.MouseLeftButtonUp += (_, e) => { e.Handled = true; SelectAssignment(key); };
        stack.Children.Add(hit);
        var detail = AssignmentPanel(deadline);
        var clip = new Border { Height = _selectedAssignment == key ? double.NaN : 0, ClipToBounds = true, Child = detail };
        _assignmentPanels[key] = clip;
        if (_selectedAssignment == key)
        {
            detail.Measure(new Size(Page == ControlCenterPage.School ? 390 : 520, double.PositiveInfinity));
            clip.Height = detail.DesiredSize.Height;
        }
        stack.Children.Add(clip);
        return stack;
    }

    private FrameworkElement AssignmentPanel(PiDeadline deadline)
    {
        var panel = new StackPanel { Margin = new Thickness(54, 1, 0, 10) };
        var link = CompactPill("Aufgabe öffnen", async () =>
        {
            if (DeadlineLink(deadline) is { } url) Launch(await SignInToSchoolAsync(url));
        });
        panel.Children.Add(link);
        if (deadline.Cmid <= 0) return panel;
        if (_piAssignments.TryGetValue(deadline.Cmid, out var assignment))
        {
            if (assignment.Status.Length > 0) panel.Children.Add(CompactText(assignment.Status, 11, CompactLabel));
            if (assignment.Blocked is { Length: > 0 } blocked)
            {
                var note = CompactText(blocked, 11, CompactLabel); note.TextWrapping = TextWrapping.Wrap; panel.Children.Add(note);
            }
            var attachments = new WrapPanel { Margin = new Thickness(0, 5, 0, 5) };
            foreach (var file in assignment.Attachments)
            {
                var chip = CompactPill(file.Name, () => DownloadPiTask(deadline, assignment));
                chip.Margin = new Thickness(0, 0, 5, 4); attachments.Children.Add(chip);
            }
            panel.Children.Add(attachments);
        }
        var drop = new Grid();
        // All original project/repo, attachment, photo/PDF and file submission paths keep their validation.
        var actions = PiTaskActions(deadline, drop);
        actions.HorizontalAlignment = HorizontalAlignment.Left;
        drop.Children.Add(actions); panel.Children.Add(drop);
        return panel;
    }

    private static string AssignmentKey(PiDeadline deadline) => deadline.Cmid > 0 ? $"cmid:{deadline.Cmid}" :
        $"{deadline.Course}|{deadline.Name}|{deadline.Due.ToUnixTimeSeconds()}";

    private void SelectAssignment(string key)
    {
        _selectedAssignment = _selectedAssignment == key ? null : key;
        foreach (var (id, panel) in _assignmentPanels) FoldInline(panel, _selectedAssignment == id, animate: true);
    }

    private Grid SchoolEventLine(string title, string when, Color color)
    {
        var line = new Grid { MinHeight = 29, Background = Brushes.Transparent, Tag = "drawer-row" };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        line.ColumnDefinitions.Add(new ColumnDefinition()); line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = new SolidColorBrush(color), HorizontalAlignment = HorizontalAlignment.Left });
        var name = CompactText(title, 12, CompactInk); name.Margin = new Thickness(0, 0, 8, 0);
        Grid.SetColumn(name, 1); line.Children.Add(name);
        var date = CompactText(when, 11, CompactLabel); Grid.SetColumn(date, 2); line.Children.Add(date);
        return line;
    }

    private FrameworkElement SchoolExam(PiExam exam)
    {
        var line = SchoolEventLine($"{ExamSubject(exam)} · {exam.Kind}", exam.Date.ToString("ddd d.M.", Loc.GetCulture()), SubjectColor(ExamSubject(exam)));
        OpenOnClick(line, () => SchoolLinks.Untis(_untisUrl()), SignInToSchoolAsync);
        line.ToolTip = exam.Title; return line;
    }

    private static DateTimeOffset SchoolExamTime(PiExam exam) => new(exam.Date.Date +
        (TimeSpan.TryParseExact(exam.Start, @"hh\:mm", CultureInfo.InvariantCulture, out var time) ? time : TimeSpan.Zero));

    private void BuildPiExams()
    {
        _piExamSection.Children.Clear();
        if (_isLocalOnly() || _piSchool is not { } school) return;
        // The tests the page already lists above the drawer are not repeated in it.
        var later = school.Exams.Where(e => e.Date.Date >= DateTime.Today && !_schoolExamsOnTop.Contains(e)).OrderBy(e => e.Date).ToList();
        var handedIn = school.Deadlines.Where(d => d.Submitted && (d.SubmittedAt ?? d.Due) >= DateTimeOffset.Now.AddDays(-7))
            .OrderByDescending(d => d.SubmittedAt ?? d.Due).Take(3).ToList();
        if (later.Count == 0 && handedIn.Count == 0) return;
        _piExamSection.Children.Add(CompactText(_schoolExamsOnTop.Count > 0 ? "Weitere Tests" : "Tests", 11, CompactLabel));
        foreach (var exam in later)
            _piExamSection.Children.Add(SchoolExam(exam));
        foreach (var deadline in handedIn)
        {
            var row = CompactLine(0xE73E, deadline.Name); row.Opacity = .45; row.ToolTip = "Abgegeben";
            OpenOnClick(row, () => DeadlineLink(deadline), SignInToSchoolAsync); _piExamSection.Children.Add(row);
        }
    }

    private void BuildPiTodos()
    {
        _piTodoSection.Children.Clear();
        if (_isLocalOnly()) return;
        _piTodoSection.Children.Add(CompactText(Loc.Get("pi.todos"), 11, CompactLabel));
        foreach (var todo in _piTodos ?? new List<PiTodo>())
        {
            bool done = todo.Done;
            var check = new Border { Width = 15, Height = 15, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
                BorderBrush = CompactLabel, Background = done ? Frozen("#CF9AB2") : Brushes.Transparent,
                Child = CompactGlyph(0xE73E, done ? Frozen("#161616") : Brushes.Transparent) };
            var text = CompactText(todo.Text, 12, done ? CompactLabel : CompactInk);
            if (done) text.TextDecorations = TextDecorations.Strikethrough;
            var row = new Grid { MinHeight = 29, Background = Brushes.Transparent, Cursor = Cursors.Hand, Tag = "drawer-row" };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) }); row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(check); Grid.SetColumn(text, 1); row.Children.Add(text); Squish(check);
            row.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true; done = !done;
                check.Background = done ? Frozen("#CF9AB2") : Brushes.Transparent;
                ((TextBlock)check.Child).Foreground = done ? Frozen("#161616") : Brushes.Transparent;
                text.TextDecorations = done ? TextDecorations.Strikethrough : null;
                DetailReaction(check); SetTodoDone(todo.Id, done);
            };
            _piTodoSection.Children.Add(row);
        }
        _piTodoSection.Children.Add(TodoComposer());
    }

    private void ComposeSchoolDrawer()
    {
        if (_piSchool == null || _isLocalOnly() || _pi?.IsConfigured != true) return;
        foreach (var section in new[] { _piExamSection, _piTodoSection })
            if (section.Parent is Panel parent) parent.Children.Remove(section);
        var body = new StackPanel();
        var week = new Grid { Margin = new Thickness(0, 0, 0, 7), Tag = "drawer-row" };
        _weekButtons.Clear();
        for (int i = 0; i < 7; i++)
        {
            DateTime day = DateTime.Today.AddDays(i); week.ColumnDefinitions.Add(new ColumnDefinition());
            var label = CompactText(day.ToString("ddd", Loc.GetCulture()), 10, CompactLabel); label.TextAlignment = TextAlignment.Center;
            var number = CompactText(day.Day.ToString(CultureInfo.CurrentCulture), 12, i == 0 ? Frozen("#111111") : CompactInk); number.TextAlignment = TextAlignment.Center;
            var circle = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Background = i == 0 ? Brushes.White : Brushes.Transparent, Child = number };
            var dots = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Height = 7 };
            var colors = _piSchool.Deadlines.Where(d => !d.Submitted && d.Due.LocalDateTime.Date == day).Select(d => SubjectColor(DeadlineSubject(d)))
                .Concat(_piSchool.Exams.Where(e => e.Date.Date == day).Select(e => SubjectColor(ExamSubject(e)))).Distinct();
            foreach (var color in colors.Take(5)) dots.Children.Add(new Ellipse { Width = 4, Height = 4, Margin = new Thickness(1), Fill = new SolidColorBrush(color) });
            var button = new Border { Background = day == _schoolSelectedDay ? Frozen("#14FFFFFF") : Brushes.Transparent, CornerRadius = new CornerRadius(8), Padding = new Thickness(2), Cursor = Cursors.Hand,
                Child = new StackPanel { Children = { label, circle, dots } } };
            button.ToolTip = day.ToString("D", Loc.GetCulture()); Squish(button);
            button.MouseLeftButtonUp += (_, e) => { e.Handled = true; SelectSchoolDay(day); };
            _weekButtons[day] = button; Grid.SetColumn(button, i); week.Children.Add(button);
        }
        body.Children.Add(week);
        _weekLessons = new Border { Height = 0, ClipToBounds = true, Child = new Grid() };
        body.Children.Add(_weekLessons);
        body.Children.Add(CompactColumns(_piExamSection, _piTodoSection));
        AddDrawer(body);
        if (_schoolSelectedDay is { } selected) ShowSchoolDay(selected, animate: false);
    }

    private void SelectSchoolDay(DateTime day)
    {
        _schoolSelectedDay = _schoolSelectedDay == day ? null : day;
        if (_schoolSelectedDay is { } selected) ShowSchoolDay(selected, animate: true);
        else if (_weekLessons != null) FoldInline(_weekLessons, false, true);
        foreach (var (date, button) in _weekButtons) button.Background = date == _schoolSelectedDay ? Frozen("#14FFFFFF") : Brushes.Transparent;
    }

    private void ShowSchoolDay(DateTime day, bool animate)
    {
        if (_weekLessons?.Child is not Grid layers || _piSchool == null) return;
        string key = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var lessons = _piSchool.Days.TryGetValue(key, out var found) ? found : key == _piSchool.Date ? _piSchool.Lessons : null;
        var rows = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        if (lessons == null || lessons.Count == 0)
            rows.Children.Add(CompactText(lessons == null ? "Kein Stundenplan verfügbar" : "Kein Unterricht", 11.5, CompactLabel));
        else foreach (var lesson in lessons.OrderBy(l => l.StartMinutes))
        {
            string subject = lesson.SubjectKey.Length > 0 ? lesson.SubjectKey : lesson.Subject;
            var row = SchoolEventLine(lesson.Subject, lesson.Room, SubjectColor(subject));
            row.ColumnDefinitions.Insert(1, new ColumnDefinition { Width = new GridLength(86) });
            foreach (UIElement child in row.Children.Cast<UIElement>().Skip(1).ToArray()) Grid.SetColumn(child, Grid.GetColumn(child) + 1);
            var time = CompactText($"{lesson.Start}–{lesson.End}", 10.5, CompactLabel); Grid.SetColumn(time, 1); row.Children.Add(time);
            row.Opacity = lesson.Cancelled ? .4 : 1;
            row.ToolTip = lesson.Cancelled ? "Entfällt" : lesson.Change;
            if (lesson.Cancelled) ((TextBlock)row.Children[1]).TextDecorations = TextDecorations.Strikethrough;
            rows.Children.Add(row);
        }
        var previous = layers.Children.OfType<StackPanel>().LastOrDefault();
        layers.Children.Clear();
        if (animate && previous != null && IsVisible && !AnimationConfig.ReduceMotion)
        {
            previous.Width = layers.ActualWidth;
            var oldLayer = new Canvas { IsHitTestVisible = false }; oldLayer.Children.Add(previous); layers.Children.Add(oldLayer);
            var away = previous.RenderTransform as TranslateTransform ?? new TranslateTransform(); previous.RenderTransform = away;
            Motion.To(away, TranslateTransform.YProperty, -6, Motion.Exit);
            Motion.To(previous, OpacityProperty, 0, Motion.Exit, done: () => layers.Children.Remove(oldLayer));
        }
        layers.Children.Add(rows);
        if (animate && IsVisible && !AnimationConfig.ReduceMotion)
        {
            var shift = new TranslateTransform(0, 6); rows.RenderTransform = shift;
            rows.Opacity = 0;
            Motion.To(shift, TranslateTransform.YProperty, 0, Motion.Shift);
            Motion.To(rows, OpacityProperty, 1, Motion.Exit);
        }
        FoldInline(_weekLessons, true, animate);
    }

    internal static string SchoolCountdown(DateTimeOffset due, DateTimeOffset now)
    {
        long seconds = Math.Max(0, (long)(due - now).TotalSeconds);
        return $"{seconds / 86400} T {seconds / 3600 % 24:00}:{seconds / 60 % 60:00}:{seconds % 60:00}";
    }

    private void TickSchoolCountdown()
    {
        if (_countdownDigits == null || _countdownDue is not { } due) return;
        string value = SchoolCountdown(due, DateTimeOffset.Now);
        // A running clock just ticks: the digits change in place. Rolling each digit every second read as flicker.
        if (_countdownDigits.Children.Count != 1 || _countdownDigits.Children[0] is not TextBlock label)
        {
            _countdownDigits.Children.Clear();
            label = CompactText(value, 15, CompactInk);
            label.TextTrimming = TextTrimming.None;
            System.Windows.Documents.Typography.SetNumeralAlignment(label, FontNumeralAlignment.Tabular);
            _countdownDigits.Children.Add(label);
        }
        label.Text = value;
        _countdownShown = value;
        if (due <= DateTimeOffset.Now) _schoolCountdownTimer?.Stop();
    }

    private void UpdateCountdownVisibility()
    {
        if (_disposed || !IsVisible || _countdownDigits?.IsVisible != true || _countdownDue == null)
        { _schoolCountdownTimer?.Stop(); return; }
        _schoolCountdownTimer ??= new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _schoolCountdownTimer.Tick -= OnSchoolCountdownTick;
        _schoolCountdownTimer.Tick += OnSchoolCountdownTick;
        TickSchoolCountdown();
        if (_countdownDue > DateTimeOffset.Now) _schoolCountdownTimer.Start();
    }

    private void OnSchoolCountdownTick(object? sender, EventArgs e)
    {
        if (_countdownDigits?.IsVisible != true || !IsVisible || _disposed) { _schoolCountdownTimer?.Stop(); return; }
        TickSchoolCountdown();
    }
}
