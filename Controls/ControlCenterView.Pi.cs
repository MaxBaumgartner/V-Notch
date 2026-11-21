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
            MountCompactPage();
            _compactPage.Children.Add(_piSchoolSection);
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
            if (IsSchoolPage) UpdateCountdownVisibility();
        }
        else
        {
            _piTimer?.Stop();
            _schoolCountdownTimer?.Stop();
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
        catch (Exception ex)
        {
            // An async void: anything that escapes here would end the whole app, not just this refresh.
            RuntimeLog.Error("PI-DASHBOARD", ex, "Home/school refresh failed");
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
        ComposeSchoolDrawer();
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

    // Parts of the school tab that follow each other in when the tab opens (see RevealSchoolDetails).
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
            // Cards without feedback share the same press and hover motion as the other tabs.
            if (card.RenderTransform is not ScaleTransform)
            {
                Brush rest = card.Background;
                Pressable(card, () => rest, CardHover);
            }
        }
        else
        {
            // Transparent, not null: the whole row takes the click, not just its text.
            if (element is Panel { Background: null } panel) panel.Background = Brushes.Transparent;
            var scale = new ScaleTransform(1, 1);
            element.RenderTransformOrigin = new Point(0.5, 0.5);
            element.RenderTransform = scale;
            void Grow(double value, SpringMotion spring)
            {
                Motion.To(scale, ScaleTransform.ScaleXProperty, value, spring);
                Motion.To(scale, ScaleTransform.ScaleYProperty, value, spring);
            }
            element.MouseEnter += (_, _) => Grow(HoverScale(element, 1.02), Motion.Press);
            element.MouseLeave += (_, _) => Grow(1, Motion.Press);
            element.PreviewMouseLeftButtonDown += (_, _) => Grow(0.96, Motion.PressDown);
            element.PreviewMouseLeftButtonUp += (_, _) => Grow(element.IsMouseOver ? HoverScale(element, 1.02) : 1, Motion.Press);
            element.LostMouseCapture += (_, _) => Grow(1, Motion.Press);
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

    /// <summary>The glyph at the start of a <see cref="NewRow"/>.</summary>
    private static TextBlock? RowGlyph(Grid row) =>
        row.Children.OfType<StackPanel>().FirstOrDefault()?.Children.OfType<TextBlock>().FirstOrDefault();

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
            Background = Brushes.Transparent,
            BorderBrush = ComboBorder,
            BorderThickness = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(0),
            Padding = new Thickness(0, 5, 0, 5),
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
        _piKey = "";
        RefreshPi();
    }

    #endregion

    #endregion
}
