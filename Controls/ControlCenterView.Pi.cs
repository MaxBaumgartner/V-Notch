using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using VNotch.Services;
using VNotch.Services.ControlCenter;

namespace VNotch.Controls;

/// <summary>
/// The two tabs fed by the user's Raspberry Pi dashboard: <see cref="ControlCenterPage.Pi"/> is home (the room,
/// see ControlCenterView.Home.cs), <see cref="ControlCenterPage.School"/> the deadlines, tests and to-dos. Both
/// wear one muted colour like the system tab: home from the room's lights, school from how soon things are due.
/// </summary>
public partial class ControlCenterView
{
    #region Pi dashboard

    private static readonly TimeSpan PiRefreshInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PiSchoolMaxAge = TimeSpan.FromMinutes(5);
    private const int PiMaxDeadlines = 4;
    private const int PiMaxSubmitted = 3;
    private const int PiMaxExams = 4;
    private const int PiMaxTodos = 6;
    private static readonly TimeSpan ExamHorizon = TimeSpan.FromDays(21);
    private static readonly Brush DueSoon = Frozen("#D2AE7C");
    private static readonly Brush DueToday = Frozen("#D2857E");

    private PiDashboardClient? _pi;
    private DispatcherTimer? _piTimer;
    private bool _piRefreshing;
    private PiSchool? _piSchool;
    private DateTime _piSchoolAtUtc = DateTime.MinValue;
    private List<PiTodo>? _piTodos;
    private bool _piReachable = true;
    private string _piKey = "";
    private string _todoDraft = "";
    private TextBox? _todoBox;

    private readonly StackPanel _piSchoolSection = new();
    private readonly StackPanel _piExamSection = new();
    private readonly StackPanel _piTodoSection = new();

    private Func<string> _moodleUrl = () => "";
    private Func<string> _untisUrl = () => "";

    private bool IsSchoolPage => Page == ControlCenterPage.School;

    /// <summary>For the <see cref="ControlCenterPage.Pi"/> (home) and <see cref="ControlCenterPage.School"/> pages.</summary>
    internal void AttachPi(PiDashboardClient client, Func<bool> isLocalOnly, Func<bool> usesGlass,
        Func<string> moodleUrl, Func<string> untisUrl)
    {
        _pi = client;
        _isLocalOnly = isLocalOnly;
        _usesGlass = usesGlass;
        _moodleUrl = moodleUrl;
        _untisUrl = untisUrl;
        if (IsSchoolPage)
        {
            ControlRoot.Children.Add(_piSchoolSection);
            ControlRoot.Children.Add(_piExamSection);
            ControlRoot.Children.Add(_piTodoSection);
            SetMoodColors(SchoolCalm.Main, SchoolCalm.Warmer, SchoolCalm.Cooler);
        }
        else
        {
            AttachHome();
        }
        _piTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = PiRefreshInterval };
        _piTimer.Tick += (_, _) => RefreshPi();
    }

    private void OnPiVisibleChanged(bool visible)
    {
        if (visible)
        {
            BuildPiPage();
            RefreshPi();
            _piTimer?.Start();
        }
        else
        {
            _piTimer?.Stop();
            _homeLoops.StopAll();
            if (_todoBox?.IsKeyboardFocusWithin == true) KeyboardInputRequested?.Invoke(false);
        }
    }

    /// <summary>Home: the room every refresh. School: to-dos every refresh, the deadlines (cached on the Pi) every few minutes.</summary>
    private async void RefreshPi()
    {
        if (_pi == null || _piRefreshing || _disposed) return;
        if (_isLocalOnly() || !_pi.IsConfigured)
        {
            BuildPiPage();
            return;
        }
        _piRefreshing = true;
        try
        {
            if (!IsSchoolPage)
            {
                var home = await _pi.GetHomeAsync();
                await RefreshMusicSyncAsync();
                if (_disposed) return;
                _piReachable = home != null;
                if (home != null) _home = home;
                BuildPiPage();
                return;
            }
            bool school = DateTime.UtcNow - _piSchoolAtUtc > PiSchoolMaxAge || _piSchool == null;
            var todos = _pi.GetTodosAsync();
            var timetable = school ? _pi.GetSchoolAsync() : Task.FromResult(_piSchool);
            await Task.WhenAll(todos, timetable);
            if (_disposed) return;
            _piReachable = todos.Result != null || timetable.Result != null;
            if (todos.Result != null) _piTodos = todos.Result;
            if (school && timetable.Result != null)
            {
                _piSchool = timetable.Result;
                _piSchoolAtUtc = DateTime.UtcNow;
            }
            BuildPiPage();
        }
        finally
        {
            _piRefreshing = false;
        }
    }

    private void BuildPiPage()
    {
        if (_disposed || _pi == null) return;
        if (!IsSchoolPage)
        {
            BuildHome();
            ContentHeightChanged?.Invoke();
            return;
        }
        // Typing a to-do: a rebuild would take the box away; the next refresh catches up.
        if (_todoBox?.IsKeyboardFocusWithin == true) return;
        string key = PiKey();
        if (key == _piKey) return;
        _piKey = key;

        BuildPiSchool();
        BuildPiExams();
        BuildPiTodos();
        ContentHeightChanged?.Invoke();
    }

    private string PiKey()
    {
        var sb = new StringBuilder();
        sb.Append(_isLocalOnly()).Append(_pi?.IsConfigured).Append(_piReachable).Append('|');
        sb.Append(IsExpanded("pi.school")).Append(IsExpanded("pi.exams")).Append(IsExpanded("pi.todos")).Append('|');
        sb.Append(_piSchoolAtUtc.Ticks).Append('|').Append(DateTime.Now.ToString("yyyyMMddHH", CultureInfo.InvariantCulture)).Append('|');
        if (_piTodos != null) foreach (var t in _piTodos) sb.Append(t.Id).Append(t.Done).Append(';');
        sb.Append('|').Append(PiTaskKey());
        return sb.ToString();
    }

    private static Grid TileGrid(int columns, int count)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 2), Tag = TileGridTag };
        for (int i = 0; i < columns * 2 - 1; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = i % 2 == 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(8) });
        for (int r = 0; r < (count + columns - 1) / columns; r++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        return grid;
    }

    private static void PlaceTile(Grid grid, FrameworkElement tile, int index, int columns)
    {
        tile.Margin = new Thickness(0, 0, 0, 8);
        Grid.SetColumn(tile, index % columns * 2);
        Grid.SetRow(tile, index / columns);
        grid.Children.Add(tile);
    }

    #region School

    // How soon things are due sets the colour: sage while there is time, sand within three days, rosé within a day.
    private static readonly (Color Main, Color Warmer, Color Cooler) SchoolCalm =
        (Color.FromRgb(0x86, 0xB6, 0xB0), Color.FromRgb(0x9B, 0xB0, 0xCC), Color.FromRgb(0x7F, 0xA6, 0xCC));
    private static readonly (Color Main, Color Warmer, Color Cooler) SchoolSoon =
        (Color.FromRgb(0xD2, 0xAE, 0x7C), Color.FromRgb(0xD2, 0x95, 0x7E), Color.FromRgb(0x86, 0xB6, 0xB0));
    private static readonly (Color Main, Color Warmer, Color Cooler) SchoolNow =
        (Color.FromRgb(0xD2, 0x85, 0x7E), Color.FromRgb(0xC9, 0x8A, 0xB0), Color.FromRgb(0xD2, 0xAE, 0x7C));

    /// <summary>The colour for what is due next: a deadline or a test, whichever comes first.</summary>
    internal static (Color Main, Color Warmer, Color Cooler) SchoolMood(TimeSpan? untilNext) => untilNext switch
    {
        { } left when left < TimeSpan.FromDays(1) => SchoolNow,
        { } left when left < TimeSpan.FromDays(3) => SchoolSoon,
        _ => SchoolCalm
    };

    private void BuildPiSchool()
    {
        _accent = _mood;
        _piSchoolSection.Children.Clear();
        if (_isLocalOnly() || _pi?.IsConfigured != true)
        {
            _piSchoolSection.Children.Add(NewRow("\uE7BA", Loc.Get(_isLocalOnly() ? "pi.localOnly" : "pi.notConfigured"), muted: true));
            return;
        }
        if (_piSchool is not { } school)
        {
            _piSchoolSection.Children.Add(NewRow("\uE7BA", Loc.Get(_piReachable ? "pi.loading" : "pi.unreachable"), muted: true));
            return;
        }

        var now = DateTimeOffset.Now;
        var open = school.Deadlines.Where(d => !d.Submitted && d.Due >= now).OrderBy(d => d.Due).ToList();
        var nextExam = school.Exams.Where(e => e.Date.Date >= DateTime.Today).OrderBy(e => e.Date).FirstOrDefault();
        TimeSpan? untilNext = open.Count > 0 ? open[0].Due - now : null;
        if (nextExam != null && (untilNext == null || nextExam.Date - DateTime.Now < untilNext)) untilNext = nextExam.Date - DateTime.Now;
        var mood = SchoolMood(untilNext);
        SetMoodColors(mood.Main, mood.Warmer, mood.Cooler);

        // What is due next, big, like the player in the music tab.
        if (open.Count > 0) _piSchoolSection.Children.Add(NextDeadlineHero(open[0], now));
        else _piSchoolSection.Children.Add(NewRow("\uE73E", Loc.Get("school.nothingDue"), muted: true));

        if (open.Count > 1 || school.Deadlines.Any(d => d.Submitted))
        {
            _piSchoolSection.Children.Add(BuildSectionHeader("pi.school", Loc.Get("school.deadlines"), "", null, topMargin: 12));
            if (!IsExpanded("pi.school")) return;
        }
        foreach (var deadline in open.Skip(1).Take(PiMaxDeadlines))
        {
            var left = deadline.Due - now;
            var row = NewRow("\uE916", deadline.Name);
            Brush urgency = left < TimeSpan.FromDays(1) ? DueToday : left < TimeSpan.FromDays(3) ? DueSoon : Muted;
            AddStatus(row, $"{ShortCourse(deadline.Course)} · {DueText(deadline.Due, left)}", urgency);
            // Open the project or the WMC repo, fetch the task's files, hand maths photos in: like the dashboard's panel.
            if (deadline.Cmid > 0) AddAction(row, PiTaskActions(deadline, row));
            // Straight to the assignment's page, where it is handed in (a WMC task: its Moodle link to Classroom 50).
            if (deadline.Cmid > 0) OpenOnClick(row, () => DeadlineLink(deadline), SignInToSchoolAsync);
            _piSchoolSection.Children.Add(row);
        }
        // Handed in during the last week, newest first, in a calm mint: the Pi keeps them for 7 days after their due date.
        var weekAgo = now.AddDays(-7);
        foreach (var deadline in school.Deadlines.Where(d => d.Submitted && (d.SubmittedAt ?? d.Due) >= weekAgo)
                     .OrderByDescending(d => d.SubmittedAt ?? d.Due).Take(PiMaxSubmitted))
        {
            var row = NewRow("\uE73E", deadline.Name, dim: true);
            string day = (deadline.SubmittedAt ?? deadline.Due).ToLocalTime().ToString("ddd d.M.", Loc.GetCulture());
            AddStatus(row, Loc.Get("pi.handedIn", ShortCourse(deadline.Course), day), HandedIn);
            if (deadline.Cmid > 0) OpenOnClick(row, () => DeadlineLink(deadline), SignInToSchoolAsync);
            _piSchoolSection.Children.Add(row);
        }
    }

    private static readonly Brush HandedIn = Frozen("#9BCFC4");
    private const double HeroRing = 84;
    // The ring counts down two weeks: a task due in a fortnight or more starts full.
    private static readonly TimeSpan HeroRingSpan = TimeSpan.FromDays(14);

    /// <summary>
    /// The next deadline as the tab's hero: a ring that empties as the due date comes closer, with the days left in
    /// it, the task's name, its subject and date, and the buttons of its row (open the project, the files, hand in).
    /// </summary>
    private FrameworkElement NextDeadlineHero(PiDeadline deadline, DateTimeOffset now)
    {
        var left = deadline.Due - now;
        double share = Math.Clamp(left.TotalMilliseconds / HeroRingSpan.TotalMilliseconds, 0.02, 1);
        const double Stroke = 7;
        double r = (HeroRing - Stroke) / 2, c = HeroRing / 2;
        var value = new Path { Stroke = _mood, StrokeThickness = Stroke, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        bool hours = left < TimeSpan.FromDays(1);
        var number = MakeLabel(hours ? $"{Math.Max(0, (int)left.TotalHours)}" : $"{(int)Math.Ceiling(left.TotalDays)}", 20, VNotch.Services.UiPalette.PrimaryBrush);
        number.HorizontalAlignment = HorizontalAlignment.Center;
        var unit = MakeLabel(Loc.Get(hours ? "school.hours" : "school.days"), 10, Muted);
        unit.HorizontalAlignment = HorizontalAlignment.Center;
        var ring = new Grid
        {
            Width = HeroRing,
            Height = HeroRing,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new Ellipse { Stroke = Track, StrokeThickness = Stroke, Opacity = 0.6 },
                value,
                new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { number, unit } }
            }
        };
        // The ring sweeps to its share when the tab opens.
        var tween = new WeatherTween { Step = t => value.Data = Arc(c, c, r, -90, Math.Min(359.5, 360 * t)) };
        bool animate = IsVisible && !AnimationConfig.ReduceMotion;
        tween.Play(animate ? 0 : share, share, animate ? 1100 : 0, WeatherMotion.OutQuint, 150);

        var title = MakeLabel(deadline.Name, 16, VNotch.Services.UiPalette.PrimaryBrush);
        var meta = MakeLabel($"{ShortCourse(deadline.Course)} · {deadline.Due.ToLocalTime().ToString("ddd d.M. · HH:mm", Loc.GetCulture())}", 12, _moodSoft);
        meta.Margin = new Thickness(0, 3, 0, 0);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0), Children = { title, meta } };

        var hero = new Grid { Margin = new Thickness(0, 0, 0, 6), Background = Brushes.Transparent };
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        hero.Children.Add(ring);
        Grid.SetColumn(text, 1);
        hero.Children.Add(text);
        if (deadline.Cmid > 0)
        {
            var actions = PiTaskActions(deadline, hero);
            actions.HorizontalAlignment = HorizontalAlignment.Left;
            actions.Margin = new Thickness(0, 9, 0, 0);
            text.Children.Add(actions);
            OpenOnClick(title, () => DeadlineLink(deadline), SignInToSchoolAsync);
        }
        return hero;
    }

    /// <summary>Tests and school exams of the next three weeks, as a row of tiles; the next few days glow rosé.</summary>
    private void BuildPiExams()
    {
        _piExamSection.Children.Clear();
        if (_piSchool is not { } school || _isLocalOnly()) return;
        var exams = school.Exams.Where(e => e.Date.Date >= DateTime.Today && e.Date - DateTime.Today <= ExamHorizon)
            .OrderBy(e => e.Date).Take(PiMaxExams).ToList();
        if (exams.Count == 0) return;
        _piExamSection.Children.Add(BuildSectionHeader("pi.exams", Loc.Get("school.exams"), "", null, topMargin: 12));
        if (!IsExpanded("pi.exams")) return;

        var grid = TileGrid(PiMaxExams, PiMaxExams);
        for (int i = 0; i < exams.Count; i++)
        {
            var exam = exams[i];
            int days = (exam.Date.Date - DateTime.Today).Days;
            bool soon = days <= 5;
            string when = exam.Date.ToString("ddd d.M.", Loc.GetCulture())
                + (days == 0 ? " · " + Loc.Get("school.today") : soon ? " · " + Loc.Get("school.inDaysShort", days) : "");
            var date = MakeLabel(when, 10.5, soon ? DueToday : Muted);
            var subject = MakeLabel(ExamSubject(exam), 14, VNotch.Services.UiPalette.PrimaryBrush);
            subject.Margin = new Thickness(0, 3, 0, 0);
            var kind = MakeLabel(exam.Kind, 10.5, Muted);
            var card = Card();
            card.Padding = new Thickness(11, 8, 11, 8);
            card.Background = soon ? Wash(DueToday, 0.18) : new SolidColorBrush(((SolidColorBrush)CardBg).Color);
            card.Child = new StackPanel { Children = { date, subject, kind } };
            card.ToolTip = exam.Title;
            Pressable(card, () => soon ? Wash(DueToday, 0.18) : CardBg, CardHover, grow: true);
            OpenOnClick(card, () => SchoolLinks.Untis(_untisUrl()), SignInToSchoolAsync);
            PlaceTile(grid, card, i, PiMaxExams);
        }
        _piExamSection.Children.Add(grid);
    }

    /// <summary>"0D" → "D", "1WMC" → "WMC", "0POSETHI" → "POSE": like the dashboard's subject chips.</summary>
    internal static string ExamSubject(PiExam exam)
    {
        string raw = exam.Subject.Length > 0 ? exam.Subject : exam.Title;
        string word = raw.Split(new[] { ' ', '_', ',' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        string key = new string(word.ToUpperInvariant().SkipWhile(char.IsDigit).TakeWhile(ch => !char.IsDigit(ch)).Where(char.IsLetter).ToArray());
        foreach (string root in SubjectRoots)
            if (key.StartsWith(root, StringComparison.Ordinal)) return root;
        return key.Length > 0 ? key[..Math.Min(key.Length, 6)] : exam.Title;
    }

    private static readonly string[] SubjectRoots = ["POSE", "SYP", "BWM", "WMC", "DBI", "NSCS"];

    /// <summary>
    /// Where a deadline row leads: an NW task (NSCS) to the dashboard's panel (its site needs a login the Pi keeps),
    /// a WMC task to its Moodle link, the rest to the assignment page.
    /// </summary>
    private string? DeadlineLink(PiDeadline deadline) =>
        deadline.Kind == "nw" && _pi?.PanelLink(deadline.Cmid) is { } panel ? panel : SchoolLinks.For(deadline, _moodleUrl());

    /// <summary>A Moodle page or Untis, signed in through the Pi (see <see cref="PiDashboardClient.SignedInSchoolLinkAsync"/>).</summary>
    private async Task<string> SignInToSchoolAsync(string url) =>
        _pi == null ? url : await _pi.SignedInSchoolLinkAsync(url, _moodleUrl(), _untisUrl());

    /// <summary>
    /// A row or card that opens a page in the browser: the hand, a lighter look under the pointer. With
    /// <paramref name="prepare"/>, the link is swapped first (e.g. for a signed-in one) before it opens.
    /// </summary>
    private static void OpenOnClick(FrameworkElement element, Func<string?> url, Func<string, Task<string>>? prepare = null)
    {
        element.Cursor = Cursors.Hand;
        if (element is Border card)
        {
            // Pressable cards fade their own background; plain ones swap it.
            if (card.RenderTransform is not ScaleTransform)
            {
                Brush rest = card.Background;
                card.MouseEnter += (_, _) => card.Background = CardHover;
                card.MouseLeave += (_, _) => card.Background = rest;
            }
        }
        else
        {
            // Transparent, not null: the whole row takes the click, not just its text.
            if (element is Panel { Background: null } panel) panel.Background = Brushes.Transparent;
            element.MouseEnter += (_, _) => element.Opacity = 0.7;
            element.MouseLeave += (_, _) => element.Opacity = 1;
        }
        element.MouseLeftButtonDown += async (_, e) =>
        {
            if (url() is not { } target) return;
            e.Handled = true;
            SafeLauncher.TryOpenUrl(prepare == null ? target : await prepare(target));
        };
    }

    private static string DueText(DateTimeOffset due, TimeSpan left)
    {
        string at = due.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        if (due.ToLocalTime().Date == DateTime.Today) return Loc.Get("pi.dueToday", at);
        if (due.ToLocalTime().Date == DateTime.Today.AddDays(1)) return Loc.Get("pi.dueTomorrow", at);
        return Loc.Get("pi.dueInDays", Math.Max(1, (int)Math.Ceiling(left.TotalDays)));
    }

    /// <summary>"DBI 3AHIF Aistleitner 2627" → "DBI".</summary>
    private static string ShortCourse(string course)
    {
        int space = course.IndexOf(' ');
        return space > 0 ? course[..space] : course;
    }

    #endregion

    #region To-dos

    private void BuildPiTodos()
    {
        _piTodoSection.Children.Clear();
        if (_isLocalOnly() || _pi?.IsConfigured != true || _piTodos == null) return;
        _piTodoSection.Children.Add(BuildSectionHeader("pi.todos", Loc.Get("pi.todos"), "", null, topMargin: 14));
        if (!IsExpanded("pi.todos")) return;

        _piTodoSection.Children.Add(TodoComposer());
        _accent = _mood;
        int shown = 0;
        foreach (var todo in _piTodos)
        {
            if (todo.Done || shown == PiMaxTodos) continue;
            shown++;
            var row = NewRow("\uE739", todo.Text);
            string id = todo.Id;
            AddStatus(row, "", Muted, Toggle(false, done =>
            {
                // Ticked off: once the switch has landed the row slides away to the right.
                if (done && !AnimationConfig.ReduceMotion)
                {
                    var away = new TranslateTransform();
                    row.RenderTransform = away;
                    row.BeginAnimation(OpacityProperty, WeatherMotion.To(1, 0.15, 360, WeatherMotion.Out, 260));
                    away.BeginAnimation(TranslateTransform.XProperty, WeatherMotion.To(0, 28, 420, WeatherMotion.OutQuint, 260));
                }
                SetTodoDone(id, done);
            }));
            _piTodoSection.Children.Add(row);
        }
        if (shown == 0) _piTodoSection.Children.Add(NewRow("\uE73E", Loc.Get("pi.todosEmpty"), muted: true));
    }

    /// <summary>A one-line box: Enter adds the to-do on the Pi and keeps the box ready for the next.</summary>
    private FrameworkElement TodoComposer()
    {
        var box = new TextBox
        {
            Text = _todoDraft,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = VNotch.Services.UiPalette.PrimaryBrush,
            CaretBrush = VNotch.Services.UiPalette.PrimaryBrush,
            FontFamily = ControlFont,
            FontSize = 12.5,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            MaxLength = 200
        };
        var placeholder = MakeLabel(Loc.Get("pi.todoPlaceholder"), 12.5, Muted);
        placeholder.FontWeight = FontWeights.Normal;
        placeholder.IsHitTestVisible = false;
        placeholder.Margin = new Thickness(2, 0, 0, 0);
        placeholder.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        box.TextChanged += (_, _) =>
        {
            _todoDraft = box.Text;
            placeholder.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        };
        // The notch takes no focus by itself: ask for the keyboard when the box is clicked.
        box.PreviewMouseLeftButtonDown += (_, _) =>
        {
            if (box.IsKeyboardFocusWithin) return;
            KeyboardInputRequested?.Invoke(true);
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => Keyboard.Focus(box));
        };
        box.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            string text = box.Text.Trim();
            if (text.Length == 0 || _pi == null) return;
            box.IsEnabled = false;
            bool added = await _pi.AddTodoAsync(text);
            box.IsEnabled = true;
            if (!added)
            {
                Shake(box);
                return;
            }
            _todoDraft = "";
            box.Text = "";
            Keyboard.Focus(box);
            _todoBox = null;
            _piKey = "";
            RefreshPi();
        };
        box.LostKeyboardFocus += (_, _) =>
        {
            KeyboardInputRequested?.Invoke(false);
            Dispatcher.BeginInvoke(DispatcherPriority.Background, BuildPiPage);
        };
        _todoBox = box;

        var field = new Grid();
        field.Children.Add(placeholder);
        field.Children.Add(box);
        var shell = new Border
        {
            Background = ComboBg,
            BorderBrush = ComboBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 7, 12, 7),
            Margin = new Thickness(0, 0, 0, 6),
            Cursor = Cursors.IBeam,
            Child = field
        };
        shell.MouseLeftButtonDown += (_, e) => e.Handled = true;
        return shell;
    }

    private async void SetTodoDone(string id, bool done)
    {
        if (_pi == null) return;
        await _pi.SetTodoDoneAsync(id, done);
        // Lets the switch finish sliding before the row leaves.
        await Task.Delay(450);
        _piKey = "";
        RefreshPi();
    }

    #endregion

    #endregion
}
