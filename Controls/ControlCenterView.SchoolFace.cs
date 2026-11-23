using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using VNotch.Services;
using VNotch.Services.ControlCenter;

namespace VNotch.Controls;

public partial class ControlCenterView
{
    private static readonly Brush SchoolLabel = Frozen("#8FFFFFFF");
    private static readonly Brush SchoolTile = Frozen("#0FFFFFFF");
    private static readonly Brush SchoolRim = Frozen("#14FFFFFF");
    private static readonly Color SchoolMist = Color.FromRgb(127, 166, 204);
    private readonly WeatherTween _schoolLight = new();
    private readonly HashSet<FrameworkElement> _schoolMotion = [];
    private Func<DateTimeOffset> _schoolNow = () => DateTimeOffset.Now;
    private DispatcherTimer? _schoolCountdownTimer;
    private Grid _schoolFace = null!, _schoolHero = null!;
    private StackPanel _schoolActions = null!;
    private TextBlock _schoolSubject = null!, _schoolTitle = null!, _schoolMeta = null!, _schoolCountdown = null!,
        _schoolUnit = null!, _schoolSlipLabel = null!, _schoolUnavailable = null!, _schoolSummary = null!;
    private Ellipse _schoolHeroMark = null!;
    private Border _schoolSlip = null!;
    private Grid _schoolTestChips = null!;
    private string? _schoolActionRevision, _schoolTestRevision;
    private PiDeadline? _schoolCover;
    private Color? _schoolLightColor;
    private double _schoolLightTarget;
    private bool _schoolBreathing;
    private long _schoolBloom, _schoolVisibilityRevision;
    private DateTime _schoolBuiltDay;
    internal StackPanel SchoolStatus { get; } = new() { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed };

    private bool SchoolDataAvailable => !_isLocalOnly() && _pi?.IsConfigured == true && _piSchool != null;
    private bool AnimateSchool => IsVisible && !AnimationConfig.ReduceMotion;

    private void MountSchoolFace()
    {
        _schoolSubject = SchoolText("", 52, CompactInk, FontWeights.DemiBold);
        _schoolSubject.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        _schoolSubject.LineHeight = 57;
        _schoolHeroMark = new Ellipse { Width = 6, Height = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        _schoolSlipLabel = SchoolText("", 12);
        var slipHeading = new StackPanel { Orientation = Orientation.Horizontal, Children = { _schoolHeroMark, _schoolSlipLabel } };
        _schoolTitle = SchoolText("", 16, CompactInk);
        _schoolMeta = SchoolText("", 16);
        foreach (var line in new[] { _schoolTitle, _schoolMeta })
        {
            line.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            line.LineHeight = 19;
        }
        _schoolActions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 0) };
        var copy = new StackPanel { Margin = new Thickness(0, 2, 14, 0), Children = { _schoolSubject, _schoolTitle, _schoolMeta, _schoolActions } };
        _schoolCountdown = SchoolText("", 24, CompactInk, FontWeights.DemiBold);
        Typography.SetNumeralAlignment(_schoolCountdown, FontNumeralAlignment.Tabular);
        _schoolUnit = SchoolText("", 12);
        var slipCopy = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children =
        {
            slipHeading,
            new Border { Height = 1, Margin = new Thickness(0, 5, 0, 3), Background = SchoolRim },
            _schoolCountdown, _schoolUnit
        } };
        _schoolSlip = new Border
        {
            Width = 156, Height = 102, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 10, 0, 0), Background = SchoolTile,
            BorderBrush = SchoolRim, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 0, 10, 0), Child = slipCopy
        };
        _schoolHero = SchoolColumns(copy, _schoolSlip);
        _schoolHero.Background = Brushes.Transparent;
        _schoolHero.SizeChanged += (_, _) => QueueSchoolLight();
        _schoolTestChips = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        var body = new StackPanel { Children = { _schoolHero, _schoolTestChips } };
        _schoolFace = new Grid { Margin = new Thickness(4, 0, 4, 3), Children = { body } };
        _schoolUnavailable = SchoolText("", 16);
        _schoolUnavailable.Margin = new Thickness(4, 6, 4, 5);
        _piSchoolSection.Children.Add(_schoolFace);
        _piSchoolSection.Children.Add(_schoolUnavailable);
        _schoolSummary = SchoolText("", 12);
        SchoolStatus.Children.Add(_schoolSummary);
        _schoolLight.Step = value => { if (_backdrop != null) _backdrop.Intensity = value; };
        ControlScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
    }

    private TextBlock SchoolText(string text, double size, Brush? brush = null, FontWeight? weight = null)
    {
        var block = CompactText(text, size, brush ?? SchoolLabel);
        block.FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        block.FontWeight = weight ?? FontWeights.Normal;
        block.HorizontalAlignment = HorizontalAlignment.Left;
        return block;
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
        _schoolFace.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        _schoolUnavailable.Visibility = available ? Visibility.Collapsed : Visibility.Visible;
        SchoolStatus.Visibility = available && IsVisible ? Visibility.Visible : Visibility.Collapsed;
        _schoolUnavailable.Text = _isLocalOnly() ? Say("Schule ist im lokalen Modus ausgeblendet.", "School is unavailable in local-only mode.")
            : _pi?.IsConfigured != true ? Say("Pi-Dashboard verbinden", "Connect the Pi dashboard")
            : _piReachable ? Say("Schuldaten werden geladen…", "Loading school data…") : Say("Pi-Dashboard nicht erreichbar", "Pi dashboard unreachable");
        if (!available)
        {
            _schoolCover = null;
            StopSchoolFaceMotion();
            Seen.ForgetAll("school:");
            return;
        }

        _schoolCover = deadlines.FirstOrDefault();
        _schoolSummary.Text = Say($"{deadlines.Length} Abgaben  ·  {exams.Length} Tests", $"{deadlines.Length} hand-ins  ·  {exams.Length} tests");
        Seen.Changed("school:counts", _schoolSummary.Text);
        if (_schoolCover is { } deadline)
        {
            string subject = DeadlineSubject(deadline);
            _schoolSubject.Text = subject;
            _schoolTitle.Text = deadline.Name;
            _schoolTitle.ToolTip = deadline.Name;
            _schoolMeta.Text = $"{SchoolSource(deadline)}  ·  {deadline.Due.ToLocalTime():ddd, d MMM · HH:mm}";
            _schoolSlipLabel.Text = Say("Frist", "Due");
            _schoolHeroMark.Fill = new SolidColorBrush(SchoolColor(subject));
            _schoolHeroMark.Visibility = _schoolSlip.Visibility = Visibility.Visible;
            _schoolActions.Visibility = Visibility.Visible;
            string revision = $"{Loc.CurrentLanguage}|{DeadlineRevision(deadline)}";
            if (_schoolActionRevision != revision)
            {
                _schoolActionRevision = revision;
                _removeSchoolHeroDrop?.Invoke();
                _removeSchoolHeroDrop = null;
                _schoolActions.Children.Clear();
                _schoolActions.ToolTip = null;
                AddSchoolTaskActions(deadline, _schoolActions, _schoolHero, compact: false);
            }
            if (Seen.Changed("school:hero", revision)) SchoolPulse(_schoolHero);
        }
        else
        {
            _schoolSubject.Text = Say("Schule", "School");
            _schoolTitle.Text = Say("Keine offenen Abgaben", "No open hand-ins");
            _schoolTitle.ToolTip = null;
            _schoolMeta.Text = "";
            _schoolActions.Visibility = _schoolHeroMark.Visibility = _schoolSlip.Visibility = Visibility.Collapsed;
            _removeSchoolHeroDrop?.Invoke();
            _removeSchoolHeroDrop = null;
            _schoolActionRevision = null;
            Seen.Changed("school:hero", "");
        }

        var upcoming = exams.Take(2).ToArray();
        string testRevision = Loc.CurrentLanguage + "|" + string.Join("|", upcoming.Select(e => $"{ExamKey(e)}:{SchoolColor(ExamSubject(e))}:{e.Date.Date == now.Date.AddDays(1)}"));
        bool testsChanged = Seen.Changed("school:face-tests", testRevision);
        if (_schoolTestRevision != testRevision)
        {
            _schoolTestRevision = testRevision;
            _schoolTestChips.Children.Clear();
            _schoolTestChips.ColumnDefinitions.Clear();
            foreach (var exam in upcoming)
            {
                string subject = ExamSubject(exam);
                var when = SchoolText($"{Say("Test", "Test")} {exam.Date:ddd d.M.}", 12);
                var chip = new Border
                {
                    Height = 36, CornerRadius = new CornerRadius(9), BorderBrush = SchoolRim, BorderThickness = new Thickness(1),
                    Background = SchoolTile, Padding = new Thickness(10, 0, 10, 0),
                    Margin = new Thickness(_schoolTestChips.Children.Count == 0 ? 0 : 8, 0, 0, 0),
                    Child = new Grid { Children = { ExamLight(exam, now), SchoolColumns(SchoolMark(subject), when) } }
                };
                HookStudyDrop(chip, exam);
                var open = SchoolButton(chip, $"{exam.Title} · {when.Text}", () => { SelectStudy(exam); SetDrawerOpen(true); });
                Grid.SetColumn(open, _schoolTestChips.Children.Count);
                _schoolTestChips.ColumnDefinitions.Add(new ColumnDefinition());
                _schoolTestChips.Children.Add(open);
            }
            _schoolTestChips.Visibility = upcoming.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        if (testsChanged) SchoolPulse(_schoolTestChips);
        UpdateSchoolClock(now);
        UpdateCountdownVisibility();
    }

    internal static (string Digits, string Unit) SchoolCountdown(DateTimeOffset due, DateTimeOffset now)
    {
        TimeSpan left = due - now;
        if (left <= TimeSpan.Zero) return (Say("Vorbei", "Overdue"), "");
        if (left.TotalHours >= 48) return (((int)Math.Ceiling(left.TotalDays)).ToString(CultureInfo.CurrentCulture), Say("Tage", "days"));
        if (left.TotalHours >= 24) return (((int)left.TotalHours).ToString(CultureInfo.CurrentCulture), "h");
        int minutes = (int)Math.Ceiling(left.TotalMinutes);
        return ($"{minutes / 60}:{minutes % 60:00}", "h:mm");
    }

    private void UpdateSchoolClock(DateTimeOffset now)
    {
        if (_schoolCover is { } cover)
        {
            var (digits, unit) = SchoolCountdown(cover.Due, now);
            Seen.Changed("school:clock", digits + unit);
            _schoolCountdown.Text = digits;
            _schoolUnit.Text = unit;
            _schoolSlip.ToolTip = cover.Due.ToLocalTime().ToString("f", Loc.GetCulture());
        }
        UpdateSchoolDayClock(now);
        UpdateSchoolLight(now);
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

    private void UpdateSchoolLight(DateTimeOffset now)
    {
        var left = _schoolCover?.Due - now;
        Color? color = _schoolCover is { } cover ? SchoolColor(DeadlineSubject(cover)) : null;
        if (_schoolLightColor != color)
        {
            _schoolLightColor = color;
            QueueSchoolLight();
        }
        double hours = left?.TotalHours ?? 0;
        double strength = left == null ? 0 : hours <= 0 ? .14 : hours >= 24 ? .12 : .16 + .32 * (1 - hours / 24);
        if (hours is > 0 and <= 1) strength = .48;
        bool changed = Seen.Changed("school:light", $"{color}|{(hours <= 0 ? 0 : hours <= 1 ? 1 : hours < 24 ? 2 : 3)}");
        bool bloom = Seen.Changed("school:bloom", _schoolBloom.ToString(CultureInfo.InvariantCulture));
        _schoolLightTarget = strength;
        if (!AnimateSchool)
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
        else if (hours is > 0 and <= 1 && changed)
        {
            Motion.To(_schoolLight, WeatherTween.ValueProperty, strength, Motion.Shift);
        }
        else if (hours is > 0 and <= 1 && !_schoolBreathing && !Motion.IsRunning(_schoolLight, WeatherTween.ValueProperty))
        {
            _schoolBreathing = true;
            BreatheSchoolLight(true);
        }
        else if (hours is <= 0 or > 1)
        {
            if (_schoolBreathing) { _schoolBreathing = false; _schoolLight.Stop(strength); }
            if (changed) Motion.To(_schoolLight, WeatherTween.ValueProperty, strength, Motion.Shift);
            else if (!Motion.IsRunning(_schoolLight, WeatherTween.ValueProperty)) _schoolLight.Value = strength;
        }
    }

    private void BreatheSchoolLight(bool up)
    {
        if (!_schoolBreathing || !AnimateSchool) return;
        Motion.To(_schoolLight, WeatherTween.ValueProperty, _schoolLightTarget + (up ? .065 : -.035), Motion.Breath,
            done: () => BreatheSchoolLight(!up));
    }

    private void QueueSchoolLight() => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
    {
        if (_disposed || !IsVisible || _backdrop is not { IsVisible: true } backdrop || !_schoolHero.IsVisible) return;
        if (_schoolLightColor is not { } color) { backdrop.Intensity = 0; return; }
        backdrop.SetMood(color, Colors.Transparent, Colors.Transparent, 0);
        backdrop.Gather(_schoolSubject.TranslatePoint(new Point(_schoolSubject.ActualWidth / 2, _schoolSubject.ActualHeight / 2), backdrop), .72);
        backdrop.Intensity = _schoolLight.Value;
    });

    private void SchoolPulse(FrameworkElement row)
    {
        if (!AnimateSchool) return;
        if (!row.IsVisible)
        {
            // The tab visibility event can precede WPF updating its children's IsVisible properties.
            long revision = _schoolVisibilityRevision;
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (!_disposed && revision == _schoolVisibilityRevision && row.IsVisible) SchoolPulse(row);
            });
            return;
        }
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
        _schoolVisibilityRevision++;
        _schoolCountdownTimer?.Stop();
        SchoolStatus.Visibility = Visibility.Collapsed;
        _schoolBreathing = false;
        foreach (var row in _schoolMotion.ToArray()) FinishSchoolPulse(row);
        _schoolLight.Stop(0);
        _schoolArmedPush = null;
    }
}
