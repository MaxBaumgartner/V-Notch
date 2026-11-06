using System;
using System.Globalization;

namespace VNotch.Services.ControlCenter;

/// <summary>
/// Where school things lead from the notch: an assignment straight to its Moodle page (the one with the submit
/// button), lessons, exams and timetable news to Untis.
/// </summary>
internal static class SchoolLinks
{
    // "Heute fällig" and "Für morgen" are about what is due within this window.
    private static readonly TimeSpan DueSoonWindow = TimeSpan.FromHours(36);

    /// <summary>The assignment's page under the Moodle address; null without an address or an id.</summary>
    public static string? Assignment(string moodleBase, int cmid)
    {
        string root = (moodleBase ?? "").Trim().TrimEnd('/');
        return cmid > 0 && root.Length > 0 ? $"{root}/mod/assign/view.php?id={cmid}" : null;
    }

    /// <summary>Where a deadline's row leads: its own page when the Pi gave one (a WMC task's Moodle link), else the assignment.</summary>
    public static string? For(PiDeadline deadline, string moodleBase) =>
        deadline.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? deadline.Url : Assignment(moodleBase, deadline.Cmid);

    public static string? Untis(string untisUrl) => (untisUrl ?? "").Trim() is { Length: > 0 } url ? url : null;

    /// <summary>"Heute fällig" and "Für morgen" name no assignment in their key: these need the deadlines.</summary>
    public static bool NeedsSchool(string reminderKey) => Kind(reminderKey) is "morgen" or "abend";

    /// <summary>
    /// What a reminder pill opens: the new assignment ("neu:&lt;cmid&gt;"), the one due next for "Heute fällig" and
    /// "Für morgen" (Untis when nothing is due: an absence to excuse, a changed lesson), Untis for timetable changes
    /// and Untis messages; null for the rest, which open the Pi tab.
    /// </summary>
    public static string? ForReminder(string reminderKey, PiSchool? school, DateTimeOffset now, string moodleBase, string untisUrl)
    {
        string kind = Kind(reminderKey);
        switch (kind)
        {
            case "neu":
                return reminderKey.Length > kind.Length + 1 &&
                       int.TryParse(reminderKey.AsSpan(kind.Length + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int cmid)
                    ? (Find(school, cmid) is { } known ? For(known, moodleBase) : Assignment(moodleBase, cmid))
                    : null;
            case "morgen":
            case "abend":
                return (NextDue(school, now) is { } due ? For(due, moodleBase) : null) ?? Untis(untisUrl);
            case "aenderung":
            case "msg":
                return Untis(untisUrl);
            default:
                return null;
        }
    }

    /// <summary>The open assignment due soonest within the next day and a half.</summary>
    internal static PiDeadline? NextDue(PiSchool? school, DateTimeOffset now)
    {
        if (school == null) return null;
        PiDeadline? next = null;
        foreach (var deadline in school.Deadlines)
        {
            if (deadline.Submitted || deadline.Cmid <= 0 || deadline.Due <= now || deadline.Due - now > DueSoonWindow) continue;
            if (next == null || deadline.Due < next.Due) next = deadline;
        }
        return next;
    }

    private static PiDeadline? Find(PiSchool? school, int cmid)
    {
        if (school == null) return null;
        foreach (var deadline in school.Deadlines)
            if (deadline.Cmid == cmid) return deadline;
        return null;
    }

    private static string Kind(string key)
    {
        int colon = key.IndexOf(':');
        return colon < 0 ? key : key[..colon];
    }
}
