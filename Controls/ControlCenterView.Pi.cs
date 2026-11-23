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
/// see ControlCenterView.Home.cs), <see cref="ControlCenterPage.School"/> the deadlines, tests and timetable. Both
/// wear one muted colour like the system tab: home from the room's lights, school from the nearest deadline.
/// </summary>
public partial class ControlCenterView
{
    #region Pi dashboard

    private static readonly TimeSpan PiRefreshInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PiSchoolMaxAge = TimeSpan.FromMinutes(5);

    private PiDashboardClient? _pi;
    private DispatcherTimer? _piTimer;
    private bool _piRefreshing;
    private PiSchool? _piSchool;
    private DateTime _piSchoolAtUtc = DateTime.MinValue;
    private bool _piReachable = true;
    private string _piKey = "";

    private readonly StackPanel _piSchoolSection = new();

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
            MountSchoolFace();
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
            if (IsSchoolPage) StopSchoolFaceMotion();
            if (!IsSchoolPage) UpdateHomeRendering();
        }
    }

    /// <summary>Home: the room every refresh. School: the deadlines and timetable every few minutes.</summary>
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
                if (_disposed || _homeRefreshQueued) return;
                _piReachable = home != null;
                if (home != null) _home = home;
                BuildPiPage();
                return;
            }
            bool school = DateTime.UtcNow - _piSchoolAtUtc > PiSchoolMaxAge || _piSchool == null;
            if (school)
            {
                var timetable = await _pi.GetSchoolAsync();
                if (_disposed) return;
                _piReachable = timetable != null;
                if (timetable != null)
                {
                    _piSchool = timetable;
                    _piSchoolAtUtc = DateTime.UtcNow;
                }
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
            if (_homeRefreshQueued)
            {
                _homeRefreshQueued = false;
                RefreshPi();
            }
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
        BuildPiSchool();
    }

    private void BuildPiSchool()
    {
        if (_schoolFace == null) return;
        var now = _schoolNow();
        if (now.Date != _schoolBuiltDay) _schoolLessonOffset = null;
        _schoolBuiltDay = now.Date;
        PiDeadline[] deadlines = SchoolDataAvailable ? _piSchool!.Deadlines.Where(d => !d.Submitted).OrderBy(d => d.Due).ToArray() : [];
        PiExam[] exams = SchoolDataAvailable ? _piSchool!.Exams.Where(e => e.Date.Date >= now.Date)
            .OrderBy(e => e.Date).ThenBy(e => e.Start, StringComparer.Ordinal).Distinct().ToArray() : [];
        RefreshSchoolFace(deadlines, exams, now);

        string key = $"{SchoolDataAvailable}|{now:yyyy-MM-dd}|{_piSchoolAtUtc.Ticks}|{Loc.CurrentLanguage}|"
            + string.Join("|", deadlines.Select(DeadlineRevision)) + "|" + string.Join("|", exams.Select(ExamKey));
        if (_piKey != key)
        {
            _piKey = key;
            if (SchoolDataAvailable) ComposeSchoolDrawer(deadlines, exams, now);
            else RemoveCompactDrawer();
            ContentHeightChanged?.Invoke();
        }

        if (!IsVisible) return;
        var visible = new[] { deadlines.FirstOrDefault() }.OfType<PiDeadline>();
        if (DrawerOpen) visible = visible.Concat(deadlines.Skip(_schoolHandInPage * SchoolPageRows).Take(SchoolPageRows));
        foreach (int cmid in visible.Select(d => d.Cmid).Distinct()) LoadPiAssignment(cmid);
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

    /// <summary>"0D" → "D", "1WMC" → "WMC", "0POSETHI" → "POSE": like the dashboard's subject chips.</summary>
    internal static string ExamSubject(PiExam exam) => SchoolSubject(exam.Subject.Length > 0 ? exam.Subject : exam.Title);

    /// <summary>
    /// Where a deadline row leads: an NW task (NSCS) to the dashboard's panel (its site needs a login the Pi keeps),
    /// a WMC task to its Moodle link, the rest to the assignment page.
    /// </summary>
    private string? DeadlineLink(PiDeadline deadline) =>
        deadline.Kind == "nw" && _pi?.PanelLink(deadline.Cmid) is { } panel ? panel : SchoolLinks.For(deadline, _moodleUrl());

    /// <summary>A Moodle page or Untis, signed in through the Pi (see <see cref="PiDashboardClient.SignedInSchoolLinkAsync"/>).</summary>
    private async Task<string> SignInToSchoolAsync(string url) =>
        _pi == null ? url : await _pi.SignedInSchoolLinkAsync(url, _moodleUrl(), _untisUrl());

    #endregion

    #endregion
}
