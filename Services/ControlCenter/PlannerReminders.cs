using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace VNotch.Services.ControlCenter;

/// <summary>
/// What a planner dashboard is worth a pill in the closed notch, as reminders with keys that stay the same while
/// the thing they are about does: a changed or cancelled lesson today or tomorrow, an exam tomorrow (from noon on)
/// or today, homework due today, someone starting to play one of the user's Roblox games, a failing check on the
/// Pi, and the Pi going out of reach. The shell shows each key once and stays quiet about what is there on its
/// first look.
/// </summary>
internal sealed class PlannerReminders
{
    /// <summary>A failed look every minute: after three in a row the Pi counts as gone.</summary>
    internal const int OfflineAfterFailures = 3;
    private static readonly TimeSpan NoonFrom = TimeSpan.FromHours(12);
    private static readonly TimeSpan MorningFrom = TimeSpan.FromHours(6);

    private readonly Dictionary<string, int> _playing = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _playingSince = new(StringComparer.Ordinal);
    private int _failures;
    private DateTimeOffset _offlineSince;

    /// <summary>The Pi did not answer: null until it has been gone for a while, then one reminder for the outage.</summary>
    public List<PiReminder>? Failed(DateTimeOffset now)
    {
        if (_failures++ == 0) _offlineSince = now;
        if (_failures < OfflineAfterFailures) return null;
        return new List<PiReminder>
        {
            new($"pi-offline:{_offlineSince.ToUnixTimeSeconds()}", "Pi", Loc.Get("pi.reminder.offline"), _offlineSince)
        };
    }

    public List<PiReminder> Build(JsonArray? timetable, JsonArray? events, JsonObject? home, JsonObject? health, DateTimeOffset now)
    {
        _failures = 0;
        var reminders = new List<PiReminder>();
        DateTime local = now.LocalDateTime;
        AddTimetableChanges(reminders, timetable, local, now);
        AddExamsAndHomework(reminders, events, local, now);
        AddRobloxPlayers(reminders, home, now);
        AddFailingChecks(reminders, health, now);
        return reminders;
    }

    private static void AddTimetableChanges(List<PiReminder> reminders, JsonArray? timetable, DateTime local, DateTimeOffset now)
    {
        string today = PlannerDashboard.Ymd(local), tomorrow = PlannerDashboard.Ymd(local.AddDays(1));
        int minute = local.Hour * 60 + local.Minute;
        foreach (var (date, lessons) in PlannerDashboard.LessonsByDay(timetable))
        {
            if (date != today && date != tomorrow) continue;
            foreach (var lesson in lessons)
            {
                if (!lesson.Cancelled && lesson.Change.Length == 0) continue;
                if (date == today && lesson.EndMinutes <= minute) continue;
                string what = lesson.Cancelled ? Loc.Get("pi.cancelled") : lesson.Change;
                string when = Loc.Get(date == today ? "pi.todayAt" : "pi.tomorrowAt", lesson.Start);
                // "aenderung:" opens Untis (see SchoolLinks).
                string key = $"aenderung:{date}:{lesson.Start}:{lesson.Subject}:{(lesson.Cancelled ? "x" : "")}{lesson.Change}";
                reminders.Add(new PiReminder(key, lesson.Subject, $"{when} · {what}", now));
            }
        }
    }

    private static void AddExamsAndHomework(List<PiReminder> reminders, JsonArray? events, DateTime local, DateTimeOffset now)
    {
        int minute = local.Hour * 60 + local.Minute;
        foreach (var ev in PlannerDashboard.Events(events))
        {
            if (PlannerDashboard.Occurrence(ev, local.Date, TimeSpan.FromDays(1)) is not { } date) continue;
            bool today = date == local.Date;
            string ymd = PlannerDashboard.Ymd(date);
            if (PlannerDashboard.IsExam(ev.Kind))
            {
                if (today && (local.TimeOfDay < MorningFrom || PlannerDashboard.Minutes(ev.Start) <= minute)) continue;
                if (!today && local.TimeOfDay < NoonFrom) continue;
                string message = Loc.Get(today ? "pi.reminder.examToday" : "pi.reminder.examTomorrow", PlannerDashboard.ExamLabel(ev.Kind), ev.Start);
                reminders.Add(new PiReminder($"pruefung:{ev.Id}:{ymd}:{(today ? "heute" : "morgen")}", ev.Title, message, now));
            }
            else if (ev.Kind == PlannerDashboard.HomeworkKind && today && !ev.Done &&
                     local.TimeOfDay >= NoonFrom && PlannerDashboard.Minutes(ev.End) > minute)
            {
                bool moodle = ev.Title.StartsWith(PlannerDashboard.MoodlePrefix, StringComparison.Ordinal);
                string title = moodle ? ev.Title[PlannerDashboard.MoodlePrefix.Length..] : ev.Title;
                string message = moodle ? Loc.Get("pi.dueToday", ev.End) : Loc.Get("pi.reminder.homeworkToday");
                reminders.Add(new PiReminder($"hausuebung:{ev.Id}:{ymd}", title, message, now));
            }
        }
    }

    /// <summary>One reminder per stretch of play: from the moment a game goes from nobody to somebody.</summary>
    private void AddRobloxPlayers(List<PiReminder> reminders, JsonObject? home, DateTimeOffset now)
    {
        if (home?["roblox"] is not JsonObject roblox || roblox["games"] is not JsonArray games) return;
        foreach (var node in games)
        {
            if (node is not JsonObject game) continue;
            string name = PlannerDashboard.Str(game, "name");
            int playing = PlannerDashboard.Int(game, "playing");
            bool known = _playing.TryGetValue(name, out int before);
            _playing[name] = playing;
            if (playing <= 0)
            {
                _playingSince.Remove(name);
                continue;
            }
            if (!known || before <= 0 || !_playingSince.ContainsKey(name)) _playingSince[name] = now;
            var since = _playingSince[name];
            reminders.Add(new PiReminder($"roblox:{name}:{since.ToUnixTimeSeconds()}", name, Loc.Get("pi.roblox.playing", playing), since));
        }
    }

    private static void AddFailingChecks(List<PiReminder> reminders, JsonObject? health, DateTimeOffset now)
    {
        if (health?["checks"] is not JsonArray checks) return;
        foreach (var node in checks)
        {
            if (node is not JsonObject check || PlannerDashboard.Str(check, "state") != "bad") continue;
            string id = PlannerDashboard.Str(check, "id"), detail = PlannerDashboard.Str(check, "detail");
            reminders.Add(new PiReminder($"pi:{id}:{detail}", $"Pi · {PlannerDashboard.Str(check, "label")}", detail, now));
        }
    }
}
