using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VNotch.Services;
using VNotch.Services.ControlCenter;

namespace VNotch.Controls;

/// <summary>The <see cref="ControlCenterPage.Pi"/> page: the user's Raspberry Pi dashboard in the notch.</summary>
public partial class ControlCenterView
{
    #region Pi dashboard

    private static readonly TimeSpan PiRefreshInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PiSchoolMaxAge = TimeSpan.FromMinutes(5);
    private const int PiMaxTiles = 6;
    private const int PiMaxDeadlines = 4;
    private const int PiMaxExams = 2;
    private const int PiMaxTodos = 6;
    private static readonly Brush DueSoon = Frozen("#FF9F0A");
    private static readonly Brush DueToday = Frozen("#FF453A");

    private PiDashboardClient? _pi;
    private DispatcherTimer? _piTimer;
    private bool _piRefreshing;
    private List<PiEntity>? _piHome;
    private PiSchool? _piSchool;
    private DateTime _piSchoolAtUtc = DateTime.MinValue;
    private List<PiTodo>? _piTodos;
    private bool _piReachable = true;
    private string _piKey = "";
    private string _todoDraft = "";
    private TextBox? _todoBox;

    private readonly StackPanel _piHomeSection = new();
    private readonly StackPanel _piSchoolSection = new();
    private readonly StackPanel _piTodoSection = new();

    private Func<string> _moodleUrl = () => "";
    private Func<string> _untisUrl = () => "";

    /// <summary>For the <see cref="ControlCenterPage.Pi"/> page.</summary>
    internal void AttachPi(PiDashboardClient client, Func<bool> isLocalOnly, Func<bool> usesGlass,
        Func<string> moodleUrl, Func<string> untisUrl)
    {
        _pi = client;
        _isLocalOnly = isLocalOnly;
        _usesGlass = usesGlass;
        _moodleUrl = moodleUrl;
        _untisUrl = untisUrl;
        ControlRoot.Children.Add(_piHomeSection);
        ControlRoot.Children.Add(_piSchoolSection);
        ControlRoot.Children.Add(_piTodoSection);
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
            if (_todoBox?.IsKeyboardFocusWithin == true) KeyboardInputRequested?.Invoke(false);
        }
    }

    /// <summary>Home and to-dos every refresh; the timetable, which the Pi caches itself, every few minutes.</summary>
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
            bool school = DateTime.UtcNow - _piSchoolAtUtc > PiSchoolMaxAge || _piSchool == null;
            var home = _pi.GetHomeAsync();
            var todos = _pi.GetTodosAsync();
            var timetable = school ? _pi.GetSchoolAsync() : Task.FromResult(_piSchool);
            await Task.WhenAll(home, todos, timetable);
            if (_disposed) return;
            _piReachable = home.Result != null || todos.Result != null;
            if (home.Result != null) _piHome = home.Result;
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
        // Typing a to-do: a rebuild would take the box away; the next refresh catches up.
        if (_todoBox?.IsKeyboardFocusWithin == true) return;
        string key = PiKey();
        if (key == _piKey) return;
        _piKey = key;

        BuildPiHome();
        BuildPiSchool();
        BuildPiTodos();
    }

    private string PiKey()
    {
        var sb = new StringBuilder();
        sb.Append(_isLocalOnly()).Append(_pi?.IsConfigured).Append(_piReachable).Append('|');
        sb.Append(IsExpanded("pi.home")).Append(IsExpanded("pi.school")).Append(IsExpanded("pi.todos")).Append('|');
        if (_piHome != null) foreach (var e in _piHome) sb.Append(e.Id).Append(e.Display).Append(';');
        sb.Append('|').Append(_piSchoolAtUtc.Ticks).Append('|').Append(DateTime.Now.ToString("HHmm", CultureInfo.InvariantCulture)).Append('|');
        if (_piTodos != null) foreach (var t in _piTodos) sb.Append(t.Id).Append(t.Done).Append(';');
        return sb.ToString();
    }

    #region Home

    private void BuildPiHome()
    {
        _piHomeSection.Children.Clear();
        _piHomeSection.Children.Add(BuildSectionHeader("pi.home", Loc.Get("pi.home"), "", null, topMargin: 0));
        if (!IsExpanded("pi.home")) return;

        if (_isLocalOnly() || _pi?.IsConfigured != true)
        {
            _piHomeSection.Children.Add(NewRow("\uE7BA", Loc.Get(_isLocalOnly() ? "pi.localOnly" : "pi.notConfigured"), muted: true));
            return;
        }
        if (_piHome == null)
        {
            _piHomeSection.Children.Add(NewRow("\uE7BA", Loc.Get(_piReachable ? "pi.loading" : "pi.unreachable"), muted: true));
            return;
        }

        var switches = new List<PiEntity>();
        var readings = new List<PiEntity>(PiMaxTiles);
        foreach (var entity in _piHome)
        {
            if (entity.CanToggle) switches.Add(entity);
            else if (readings.Count < PiMaxTiles && IsReading(entity)) readings.Add(entity);
        }

        // Switches as tiles, like a phone's control center: a tap flips them, lit while on.
        var switchGrid = TileGrid(PiSwitchColumns, switches.Count);
        for (int i = 0; i < switches.Count; i++)
            PlaceTile(switchGrid, SwitchTile(switches[i]), i, PiSwitchColumns);
        _piHomeSection.Children.Add(switchGrid);

        var readingGrid = TileGrid(PiReadingColumns, readings.Count);
        for (int i = 0; i < readings.Count; i++)
        {
            var stack = new StackPanel();
            stack.Children.Add(MakeLabel(readings[i].Label, 10.5, Muted));
            var value = MakeLabel(readings[i].Display, 13.5, VNotch.Services.UiPalette.PrimaryBrush);
            value.Margin = new Thickness(0, 2, 0, 0);
            stack.Children.Add(value);
            var card = Card();
            card.Padding = new Thickness(11, 8, 11, 8);
            card.Child = stack;
            PlaceTile(readingGrid, card, i, PiReadingColumns);
        }
        if (readings.Count > 0) _piHomeSection.Children.Add(readingGrid);
    }

    private const int PiSwitchColumns = 4;
    private const int PiReadingColumns = 5;
    private const string LightGlyph = "\uE82F";
    private static readonly Brush LightOnBg = Frozen("#33FFD60A");
    private static readonly Brush LightOnGlyph = Frozen("#FFD60A");
    private static readonly Brush PowerOnBg = Frozen("#2E3FD15B");

    private static Grid TileGrid(int columns, int count)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 2) };
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

    /// <summary>
    /// A switch as a tile: glyph, name, "on · 157 W". Tapping flips it at once and springs a little; the
    /// dashboard is told in the background and the next refresh shows what really happened.
    /// </summary>
    private FrameworkElement SwitchTile(PiEntity entity)
    {
        bool light = GlyphFor(entity) == LightGlyph;
        var glyph = new TextBlock
        {
            Text = GlyphFor(entity),
            FontFamily = SegoeSymbolFont,
            FontSize = 16,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        var name = MakeLabel(entity.Label, 12.5, VNotch.Services.UiPalette.PrimaryBrush);
        name.Margin = new Thickness(0, 7, 0, 0);
        var state = MakeLabel("", 11, Muted);
        state.Margin = new Thickness(0, 1, 0, 0);
        var stack = new StackPanel();
        stack.Children.Add(glyph);
        stack.Children.Add(name);
        stack.Children.Add(state);

        var card = Card();
        card.Padding = new Thickness(12, 10, 12, 10);
        card.Cursor = Cursors.Hand;
        card.RenderTransformOrigin = new Point(0.5, 0.5);
        card.RenderTransform = new ScaleTransform(1, 1);
        card.Child = stack;
        System.Windows.Automation.AutomationProperties.SetName(card, entity.Label);

        bool on = entity.IsOn;
        string? power = PowerOf(entity);
        void Paint()
        {
            card.Background = on ? (light ? LightOnBg : PowerOnBg) : CardBg;
            glyph.Foreground = on ? (light ? LightOnGlyph : Green) : Muted;
            string word = Loc.Get(on ? "control.on" : "control.off");
            state.Text = on && power != null ? $"{word} · {power}" : word;
        }
        Paint();

        string id = entity.Id;
        card.MouseEnter += (_, _) =>
        {
            if (!on) card.Background = CardHover;
        };
        card.MouseLeave += (_, _) => Paint();
        card.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            on = !on;
            Paint();
            Spring(card);
            TogglePi(id);
        };
        return card;
    }

    /// <summary>A short press and release, so a tap on a tile is felt.</summary>
    private static void Spring(FrameworkElement element)
    {
        if (AnimationConfig.ReduceMotion || element.RenderTransform is not ScaleTransform scale) return;
        var press = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(420) };
        press.KeyFrames.Add(new EasingDoubleKeyFrame(0.93, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(90))));
        press.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(420)),
            new ElasticEase { Oscillations = 1, Springiness = 6, EasingMode = EasingMode.EaseOut }));
        Timeline.SetDesiredFrameRate(press, AnimationConfig.TargetFps);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, press);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, press);
    }

    private async void TogglePi(string entityId)
    {
        if (_pi == null) return;
        await _pi.ToggleAsync(entityId);
        // Home Assistant reports the new state a moment later.
        await Task.Delay(700);
        _piKey = "";
        RefreshPi();
    }

    private static string GlyphFor(PiEntity entity) =>
        entity.Icon == LightBulbIcon || entity.Id.Contains("light", StringComparison.OrdinalIgnoreCase) ? "\uE82F" : "\uE945";

    // The dashboard marks lights with this emoji; WPF draws emoji in outline, so the row gets a glyph instead.
    private const string LightBulbIcon = "U0001F4A1";

    /// <summary>A switched socket's draw, from the power sensor with the same number ("switch_2" ↔ "leistung_2").</summary>
    private string? PowerOf(PiEntity socket)
    {
        if (_piHome == null) return null;
        int cut = socket.Id.LastIndexOf('_');
        if (cut < 0 || !int.TryParse(socket.Id.AsSpan(cut + 1), out _)) return null;
        string suffix = socket.Id[cut..];
        foreach (var entity in _piHome)
        {
            if (entity.Display.EndsWith(" W", StringComparison.Ordinal) && entity.Id.EndsWith(suffix, StringComparison.Ordinal))
                return entity.Display;
        }
        return null;
    }

    /// <summary>Temperature, humidity, a window, the heating…: not people, players, or the sockets' power (shown with them).</summary>
    private static bool IsReading(PiEntity entity) =>
        entity.Type is "sensor" or "binary" &&
        entity.State != "unavailable" &&
        !entity.Display.EndsWith(" W", StringComparison.Ordinal) &&
        !entity.Id.StartsWith("person.", StringComparison.Ordinal) &&
        !entity.Id.StartsWith("media_player.", StringComparison.Ordinal);

    #endregion

    #region School

    private void BuildPiSchool()
    {
        _piSchoolSection.Children.Clear();
        if (_piSchool is not { } school || _isLocalOnly()) return;
        _piSchoolSection.Children.Add(BuildSectionHeader("pi.school", Loc.Get("pi.school"), "", null, topMargin: 14));
        if (!IsExpanded("pi.school")) return;

        var lessonCard = NextLessonCard(school);
        OpenOnClick(lessonCard, () => SchoolLinks.Untis(_untisUrl()), SignInToSchoolAsync);
        _piSchoolSection.Children.Add(lessonCard);
        int shown = 0;
        var now = DateTimeOffset.Now;
        foreach (var deadline in school.Deadlines)
        {
            if (deadline.Submitted || deadline.Due < now || shown == PiMaxDeadlines) continue;
            shown++;
            var left = deadline.Due - now;
            var row = NewRow("\uE916", deadline.Name);
            Brush urgency = left < TimeSpan.FromDays(1) ? DueToday : left < TimeSpan.FromDays(3) ? DueSoon : Muted;
            AddStatus(row, DueText(deadline.Due, left), urgency);
            AddAction(row, InfoChip("\uE7BE", ShortCourse(deadline.Course)));
            // Straight to the assignment's page, where it is handed in.
            int cmid = deadline.Cmid;
            if (cmid > 0) OpenOnClick(row, () => SchoolLinks.Assignment(_moodleUrl(), cmid), SignInToSchoolAsync);
            _piSchoolSection.Children.Add(row);
        }
        int exams = 0;
        foreach (var exam in school.Exams)
        {
            if (exam.Date.Date < DateTime.Today || exams == PiMaxExams) continue;
            exams++;
            var row = NewRow("\uE787", exam.Title);
            AddStatus(row, $"{exam.Kind} · {exam.Date.ToString("ddd d.M.", Loc.GetCulture())} {exam.Start}", Muted);
            OpenOnClick(row, () => SchoolLinks.Untis(_untisUrl()), SignInToSchoolAsync);
            _piSchoolSection.Children.Add(row);
        }
    }

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
            Brush rest = card.Background;
            card.MouseEnter += (_, _) => card.Background = CardHover;
            card.MouseLeave += (_, _) => card.Background = rest;
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

    /// <summary>"Now: Chemistry · E07 until 09:45", "Next: …", or the first lesson of the next school day.</summary>
    private FrameworkElement NextLessonCard(PiSchool school)
    {
        string title, detail;
        if (school.Break is { } holiday && school.Lessons.Count == 0)
        {
            title = holiday;
            detail = Loc.Get("pi.noSchool");
        }
        else if (FindLesson(school, out var lesson, out bool now))
        {
            title = lesson.Subject;
            string room = lesson.Room.Length > 0 ? $" · {lesson.Room}" : "";
            detail = lesson.Cancelled ? Loc.Get("pi.cancelled")
                : now ? Loc.Get("pi.nowUntil", lesson.End) + room
                : school.IsToday ? Loc.Get("pi.nextAt", lesson.Start) + room
                : Loc.Get("pi.nextDay", DayLabel(school.Date), lesson.Start) + room;
        }
        else
        {
            title = Loc.Get("pi.noLessons");
            detail = school.IsToday ? Loc.Get("pi.doneForToday") : "";
        }
        var stack = new StackPanel();
        stack.Children.Add(MakeLabel(title, 13.5, VNotch.Services.UiPalette.PrimaryBrush));
        if (detail.Length > 0)
        {
            var line = MakeLabel(detail, 11.5, Muted);
            line.FontWeight = FontWeights.SemiBold;
            line.Margin = new Thickness(0, 2, 0, 0);
            stack.Children.Add(line);
        }
        var card = Card();
        card.Margin = new Thickness(0, 0, 0, 6);
        card.Child = stack;
        return card;
    }

    private static bool FindLesson(PiSchool school, out PiLesson lesson, out bool now)
    {
        int minutes = DateTime.Now.Hour * 60 + DateTime.Now.Minute;
        foreach (var l in school.Lessons)
        {
            if (school.IsToday && l.EndMinutes <= minutes) continue;
            lesson = l;
            now = school.IsToday && l.StartMinutes <= minutes;
            return true;
        }
        lesson = null!;
        now = false;
        return false;
    }

    private static string DayLabel(string isoDate) =>
        DateTime.TryParseExact(isoDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day.ToString("ddd d.M.", Loc.GetCulture())
            : isoDate;

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
        int shown = 0;
        foreach (var todo in _piTodos)
        {
            if (todo.Done || shown == PiMaxTodos) continue;
            shown++;
            var row = NewRow("\uE739", todo.Text);
            string id = todo.Id;
            AddStatus(row, "", Muted, Toggle(false, done => SetTodoDone(id, done)));
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
