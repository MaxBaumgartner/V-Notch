using System;
using System.Threading.Tasks;
using System.Windows.Threading;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.ControlCenter;

namespace VNotch;

public partial class MainWindow
{
    #region School lesson in the orb

    // The ring moves by the minute; the timetable comes from the Pi now and then.
    private static readonly TimeSpan LessonTick = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan LessonTimetableMaxAge = TimeSpan.FromMinutes(10);

    private DispatcherTimer? _lessonTimer;
    private PiSchool? _lessonSchool;
    private PiCommute? _lessonCommute;
    private DateTime _lessonSchoolAtUtc;
    private bool _lessonFetching;
    private LessonNow _lessonNow = LessonNow.Off;

    /// <summary>A lesson or a break is running at school: it takes the orb (see <see cref="LiveActivities"/>).</summary>
    private bool IsLessonLive => _lessonNow.Phase != LessonPhase.Off;

    private void InitializeLessonTimer()
    {
        _lessonTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = LessonTick };
        _lessonTimer.Tick += async (_, _) => await UpdateLessonAsync();
        _lessonTimer.Start();
        // Settings load after the window attaches its parts: the first look waits for them.
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => UpdateLessonAsync().SafeFireAndForget("LESSON-START"));
    }

    private void DisposeLessonTimer() => _lessonTimer?.Stop();

    /// <summary>
    /// Works out where the school day stands from today's timetable (see <see cref="LessonClock"/>) and puts the
    /// lesson into the orb, or takes it out when school is over, the setting is off or the Pi is not set up.
    /// </summary>
    private async Task UpdateLessonAsync()
    {
        if (_cleanedUp || _piClient == null) return;
        bool enabled = _settings is { ShowLessonTimer: true, EnableLocalOnlyMode: false } && _piClient.IsConfigured;
        string today = PlannerDashboard.Ymd(DateTime.Now);
        bool stale = _lessonSchool == null || string.CompareOrdinal(_lessonSchool.Date, today) < 0 ||
                     DateTime.UtcNow - _lessonSchoolAtUtc > LessonTimetableMaxAge;
        if (enabled && stale && !_lessonFetching)
        {
            _lessonFetching = true;
            try
            {
                // Out of reach: try again once the timetable is due anyway.
                _lessonSchoolAtUtc = DateTime.UtcNow;
                var school = _piClient.GetSchoolAsync();
                var commute = _piClient.GetCommuteAsync();
                await Task.WhenAll(school, commute);
                if (school.Result != null) _lessonSchool = school.Result;
                if (commute.Result != null) _lessonCommute = commute.Result;
            }
            finally
            {
                _lessonFetching = false;
            }
            if (_cleanedUp) return;
        }

        var now = enabled ? LessonClock.At(_lessonSchool, DateTime.Now, _lessonCommute) : LessonNow.Off;
        bool changed = now.Phase != _lessonNow.Phase;
        _lessonNow = now;
        if (changed) ArrangeLiveActivities(_currentMediaInfo, applyCompact: true);
        else if (_bubbleShown && _bubbleActivity == LiveActivity.Lesson) UpdateBubbleContent();
    }

    /// <summary>The resting notch, the lesson timer or the Pi's address changed in settings: show it now.</summary>
    private void RefreshPiSchoolIfSettingsChanged(NotchSettings oldSettings, NotchSettings newSettings)
    {
        if (oldSettings.SlimIdleNotch != newSettings.SlimIdleNotch) UpdateIdleShape();
        if (oldSettings.ShowClaudeInNotch != newSettings.ShowClaudeInNotch)
        {
            UpdateClaudeLive();
            ClaudeCenter.ClaudeNotchSwitchChanged();
        }
        if (oldSettings.ShowLessonTimer == newSettings.ShowLessonTimer &&
            string.Equals(oldSettings.PiDashboardUrl, newSettings.PiDashboardUrl, StringComparison.Ordinal)) return;
        _lessonSchool = null;
        _lessonCommute = null;
        UpdateLessonAsync().SafeFireAndForget("LESSON-SETTINGS");
    }

    /// <summary>
    /// "Chemie · bis zur Pause", in a break "Pause · danach Chemie · R101", in the morning "Losgehen · 5° · 20 % Regen"
    /// and on the way "Chemie · Erste Stunde · R101".
    /// </summary>
    private (string Title, string Subtitle) LessonWords(LessonNow now)
    {
        string room = now.Room.Length > 0 ? $" · {now.Room}" : "";
        switch (now.Phase)
        {
            case LessonPhase.Leave:
                string weather = _lessonCommute is not { Temperature: { } temp } commute ? ""
                    : commute.RainChance > 0 ? Loc.Get("lesson.rain", temp, commute.RainChance)
                    : $"{temp}°";
                return (Loc.Get("lesson.leave"), weather);
            case LessonPhase.Way:
                return (now.Subject, Loc.Get("lesson.firstLesson") + room);
            case LessonPhase.Break:
                return (Loc.Get("lesson.break"), Loc.Get("lesson.then", now.Subject) + room);
            default:
                return (now.Subject, Loc.Get(now.Next.Length > 0 ? "lesson.untilBreak" : "lesson.untilHome"));
        }
    }

    #endregion
}
