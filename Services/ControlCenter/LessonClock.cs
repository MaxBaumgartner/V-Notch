using System;
using System.Collections.Generic;

namespace VNotch.Services.ControlCenter;

internal enum LessonPhase
{
    Off,
    /// <summary>Before school: the minutes until it is time to leave.</summary>
    Leave,
    /// <summary>On the way: the minutes until the first lesson.</summary>
    Way,
    Lesson,
    Break
}

/// <summary>Where the school day stands: the lesson running (or the break), the minutes left and how much of it is gone.</summary>
/// <param name="Subject">The running lesson; in a break the one that comes next.</param>
/// <param name="Next">The lesson after this one ("" when it is the last of the day).</param>
/// <param name="Remaining">How much of the lesson or break is still to come, 1 at its start and 0 at its end.</param>
internal readonly record struct LessonNow(LessonPhase Phase, string Subject, string Room, string Next, int MinutesLeft, double Remaining)
{
    public static readonly LessonNow Off = new(LessonPhase.Off, "", "", "", 0, 0);
}

/// <summary>
/// The lesson timer of the planner dashboard, in the notch: during a lesson the minutes until the break (lessons
/// that follow without a gap count as one), in a break the minutes until the next lesson, outside school nothing.
/// With the way to school known, the morning counts down too: to leaving, then to the first lesson.
/// Cancelled lessons do not count.
/// </summary>
internal static class LessonClock
{
    /// <summary>How long before leaving the orb starts counting down to it.</summary>
    internal const int LeaveLeadMinutes = 45;

    public static LessonNow At(PiSchool? school, DateTime now, PiCommute? commute = null)
    {
        // The lessons of today only (fetched last night, the "next school day" is today now).
        if (school == null || school.Date != PlannerDashboard.Ymd(now)) return LessonNow.Off;
        var lessons = new List<PiLesson>(school.Lessons.Count);
        foreach (var lesson in school.Lessons)
            if (!lesson.Cancelled && lesson.EndMinutes > lesson.StartMinutes) lessons.Add(lesson);
        if (lessons.Count == 0) return LessonNow.Off;
        lessons.Sort((a, b) => a.StartMinutes.CompareTo(b.StartMinutes));

        double minute = now.Hour * 60 + now.Minute + now.Second / 60.0;
        int first = lessons[0].StartMinutes, last = 0;
        foreach (var lesson in lessons) last = Math.Max(last, lesson.EndMinutes);
        if (minute < first) return Morning(lessons[0], commute, now, minute);
        if (minute >= last) return LessonNow.Off;

        int index = lessons.FindIndex(l => l.StartMinutes <= minute && minute < l.EndMinutes);
        if (index >= 0)
        {
            var current = lessons[index];
            int start = current.StartMinutes, end = current.EndMinutes;
            while (index + 1 < lessons.Count && lessons[index + 1].StartMinutes <= end && lessons[index + 1].EndMinutes > end)
                end = lessons[++index].EndMinutes;
            int after = index + 1;
            while (after < lessons.Count && lessons[after].StartMinutes < end) after++;
            string next = after < lessons.Count ? lessons[after].Subject : "";
            return new LessonNow(LessonPhase.Lesson, current.Subject, current.Room, next,
                MinutesLeft(end - minute), Fraction(end - minute, end - start));
        }

        var upcoming = lessons.Find(l => l.StartMinutes > minute)!;
        int breakStart = first;
        foreach (var lesson in lessons)
            if (lesson.EndMinutes <= minute) breakStart = Math.Max(breakStart, lesson.EndMinutes);
        return new LessonNow(LessonPhase.Break, upcoming.Subject, upcoming.Room, upcoming.Subject,
            MinutesLeft(upcoming.StartMinutes - minute), Fraction(upcoming.StartMinutes - minute, upcoming.StartMinutes - breakStart));
    }

    /// <summary>From <see cref="LeaveLeadMinutes"/> before leaving: to leaving, then on the way to the first lesson.</summary>
    private static LessonNow Morning(PiLesson first, PiCommute? commute, DateTime now, double minute)
    {
        if (commute == null || commute.Date != PlannerDashboard.Ymd(now)) return LessonNow.Off;
        int leave = PlannerDashboard.Minutes(commute.Leave);
        if (leave <= 0 || leave > first.StartMinutes || minute < leave - LeaveLeadMinutes) return LessonNow.Off;
        if (minute < leave)
            return new LessonNow(LessonPhase.Leave, first.Subject, first.Room, first.Subject,
                MinutesLeft(leave - minute), Fraction(leave - minute, LeaveLeadMinutes));
        return new LessonNow(LessonPhase.Way, first.Subject, first.Room, first.Subject,
            MinutesLeft(first.StartMinutes - minute), Fraction(first.StartMinutes - minute, first.StartMinutes - leave));
    }

    /// <summary>Rounded up and at least one, like the Pi's "12 min bis Pause".</summary>
    private static int MinutesLeft(double minutes) => Math.Max(1, (int)Math.Ceiling(minutes - 1e-9));

    private static double Fraction(double left, double total) => total <= 0 ? 0 : Math.Clamp(left / total, 0, 1);
}
