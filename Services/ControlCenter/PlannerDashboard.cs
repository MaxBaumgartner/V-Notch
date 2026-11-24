using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;

namespace VNotch.Services.ControlCenter;

/// <summary>When to leave for the next school day and the weather on the way (the start page's "Hinweg").</summary>
/// <param name="Leave">"07:30": the first lesson minus the walk.</param>
internal sealed record PiCommute(string Date, string Leave, int? Temperature, int RainChance);

/// <summary>
/// A planner dashboard on the Pi (Node + Express) answers on other routes than the one the Pi tab was built for:
/// the Untis timetable (<c>/api/timetable</c>), the planner's events (<c>/api/events</c>), its start page
/// (<c>/api/home</c>) and its health checks (<c>/api/health</c>). These turn its answers into the Pi tab's records,
/// so the tab and the reminder pills work with either dashboard.
/// </summary>
internal static class PlannerDashboard
{
    internal const string HomeworkKind = "hausuebung";
    /// <summary>Moodle deadlines come into the planner as homework titled like this.</summary>
    internal const string MoodlePrefix = "Abgabe: ";
    private static readonly TimeSpan ExamWindow = TimeSpan.FromDays(21);
    private static readonly TimeSpan HomeworkWindow = TimeSpan.FromDays(14);

    internal static bool IsExam(string kind) => kind is "test" or "schularbeit" or "pruefung";

    /// <summary>"Test", "Schularbeit", "Prüfung" in the user's language.</summary>
    internal static string ExamLabel(string kind) => Loc.Get("pi.exam." + kind);

    #region School

    /// <summary>
    /// Today while lessons are still to come, else the next day with lessons; its deadlines and the exams of
    /// the next three weeks.
    /// </summary>
    public static PiSchool School(JsonArray? timetable, JsonArray? events, DateTime now)
    {
        string today = Ymd(now);
        int minute = now.Hour * 60 + now.Minute;
        var byDay = LessonsByDay(timetable);

        string day = today;
        if (!byDay.TryGetValue(today, out var todays) || todays.TrueForAll(l => l.EndMinutes <= minute))
        {
            string? next = null;
            foreach (var date in byDay.Keys)
                if (string.CompareOrdinal(date, today) > 0 && (next == null || string.CompareOrdinal(date, next) < 0)) next = date;
            if (next != null) day = next;
        }
        var lessons = byDay.TryGetValue(day, out var list) ? list : new List<PiLesson>();

        var subjects = SubjectCodes(byDay);
        var exams = new List<PiExam>();
        foreach (var ev in Events(events))
        {
            if (!IsExam(ev.Kind) || Occurrence(ev, now.Date, ExamWindow) is not { } date) continue;
            exams.Add(new PiExam(date, ev.Start, ExamLabel(ev.Kind), ev.Title, ExamSubjectCode(ev.Title, subjects)));
        }
        exams.Sort((a, b) => (a.Date, a.Start).CompareTo((b.Date, b.Start)));
        var deadlines = MoodleDeadlines(events, now).ConvertAll(d => d.Deadline);
        // Every day the timetable holds, so the tab's own day view finds today even when the cover shows the next school day.
        return new PiSchool(day, day == today, lessons, deadlines, exams, null)
        {
            Days = byDay.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<PiLesson>)pair.Value, StringComparer.Ordinal)
        };
    }

    /// <summary>
    /// The timetable's subject codes ("0AM"), found by a lesson's full name ("Mathe") and by the code itself ("AM"), so a
    /// test the user named after its subject gets that subject's colour and short name.
    /// </summary>
    internal static Dictionary<string, string> SubjectCodes(Dictionary<string, List<PiLesson>> byDay)
    {
        var codes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var lesson in byDay.Values.SelectMany(day => day))
        {
            if (lesson.SubjectKey.Length == 0) continue;
            codes.TryAdd(lesson.Subject.Trim(), lesson.SubjectKey);
            codes.TryAdd(PiDashboardClient.UntisSubject(lesson.SubjectKey), lesson.SubjectKey);
        }
        return codes;
    }

    /// <summary>The subject code a test's title names, as a whole or by one of its words; "" when it names none.</summary>
    internal static string ExamSubjectCode(string title, IReadOnlyDictionary<string, string> codes)
    {
        if (codes.TryGetValue(title.Trim(), out var whole)) return whole;
        foreach (string word in title.Split([' ', '-', ':', ',', '/', '(', ')'], StringSplitOptions.RemoveEmptyEntries))
            if (codes.TryGetValue(word, out var code)) return code;
        return "";
    }

    /// <summary>
    /// The Moodle deadlines of the next two weeks, soonest first, with the planner event each came from: the hand-in
    /// routes take that id. Their Moodle course-module id is not in the planner; the client looks it up.
    /// </summary>
    internal static List<(string EventId, PiDeadline Deadline)> MoodleDeadlines(JsonArray? events, DateTime now)
    {
        var deadlines = new List<(string EventId, PiDeadline Deadline)>();
        foreach (var ev in Events(events))
        {
            if (ev.Kind != HomeworkKind || !ev.Title.StartsWith(MoodlePrefix, StringComparison.Ordinal)) continue;
            if (Occurrence(ev, now.Date, HomeworkWindow) is not { } date) continue;
            var due = new DateTimeOffset(date.Add(TimeSpan.FromMinutes(Minutes(ev.End))));
            deadlines.Add((ev.Id, new PiDeadline(ev.Title[MoodlePrefix.Length..], ev.Sub, due, ev.Done)));
        }
        deadlines.Sort((a, b) => a.Deadline.Due.CompareTo(b.Deadline.Due));
        return deadlines;
    }


    /// <summary>The lessons of every day, sorted by time; a lesson is named by its subject's full name.</summary>
    internal static Dictionary<string, List<PiLesson>> LessonsByDay(JsonArray? timetable)
    {
        var byDay = new Dictionary<string, List<PiLesson>>(StringComparer.Ordinal);
        if (timetable == null) return byDay;
        foreach (var node in timetable)
        {
            if (node is not JsonObject l) continue;
            string date = Str(l, "date"), start = Str(l, "start"), end = Str(l, "end");
            if (date.Length != 10 || start.Length == 0 || end.Length == 0) continue;
            string name = Str(l, "name") is { Length: > 0 } full ? full : Str(l, "subject");
            if (!byDay.TryGetValue(date, out var list)) byDay[date] = list = new List<PiLesson>();
            list.Add(new PiLesson(start, end, name, Str(l, "room"), Bool(l, "cancelled"), Str(l, "change"), Minutes(start), Minutes(end),
                Str(l, "subject")));
        }
        foreach (var list in byDay.Values) list.Sort((a, b) => a.StartMinutes.CompareTo(b.StartMinutes));
        return byDay;
    }

    #endregion

    #region Hand-ins

    /// <summary>The course-module id from an assignment's Moodle address ("…/mod/assign/view.php?id=123456"); 0 without one.</summary>
    internal static int CmidFromUrl(string url)
    {
        var match = System.Text.RegularExpressions.Regex.Match(url ?? "", @"[?&]id=(\d+)");
        return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int cmid) ? cmid : 0;
    }

    /// <summary>
    /// The planner's assignment info as the Pi tab's hand-in buttons read it: photos go in as one PDF where files
    /// may be handed in and PDFs are allowed, everything else is only a link. A hand-in that is locked, already
    /// has files (they would be replaced) or needs a declaration stays with the dashboard, where it is confirmed.
    /// </summary>
    internal static PiAssignment Assignment(int cmid, JsonObject info)
    {
        string name = Str(info, "name");
        bool files = Bool(info, "fileSubmissions") && PdfAllowed(Str(info, "fileTypes"));
        string? blocked =
            Bool(info, "locked") ? Loc.Get("pi.task.locked")
            : Str(info, "status") == "submitted" || info["existingFiles"] is JsonArray { Count: > 0 } ? Loc.Get("pi.task.alreadySubmitted")
            : Bool(info, "needsSubmitButton") && Bool(info, "needsStatement") ? Loc.Get("pi.task.statement")
            : null;
        return new PiAssignment(cmid, name, files ? "photo" : "link", "", [], Str(info, "status"), blocked, DefaultPdfName(name));
    }

    /// <summary>The same check as the dashboard: no list means anything goes.</summary>
    private static bool PdfAllowed(string fileTypes) =>
        fileTypes.Trim().Length == 0 ||
        System.Text.RegularExpressions.Regex.IsMatch(fileTypes, @"(^|[\s,;])(\.pdf|document|application/pdf|\*)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>"Abgabe: Übung 7 bis 4.11.2026" → "Übung 7", as the dashboard names the PDF.</summary>
    internal static string DefaultPdfName(string title)
    {
        string name = System.Text.RegularExpressions.Regex.Replace(title ?? "", @"^Abgabe:\s*", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        name = System.Text.RegularExpressions.Regex.Replace(name, @"\s+bis\s+\d{1,2}\.\d{1,2}\.\d{2,4}\s*$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        name = System.Text.RegularExpressions.Regex.Replace(name, "[\\\\/:*?\"<>|]", "").Trim();
        if (name.Length > 60) name = name[..60];
        return name.Length > 0 ? name : "Abgabe";
    }

    #endregion

    #region To-dos

    /// <summary>Homework the user wrote into the planner (Moodle deadlines show with the school); ticked off there too.</summary>
    public static List<PiTodo> Todos(JsonArray? events, DateTime now)
    {
        var todos = new List<(DateTime Date, PiTodo Todo)>();
        foreach (var ev in Events(events))
        {
            if (ev.Kind != HomeworkKind || ev.Title.StartsWith(MoodlePrefix, StringComparison.Ordinal)) continue;
            if (Occurrence(ev, now.Date, HomeworkWindow) is not { } date) continue;
            string text = date == now.Date ? ev.Title : $"{ev.Title} · {date.ToString("ddd d.M.", Loc.GetCulture())}";
            todos.Add((date, new PiTodo(ev.Id, text, ev.Done)));
        }
        todos.Sort((a, b) => a.Date.CompareTo(b.Date));
        return todos.ConvertAll(t => t.Todo);
    }

    /// <summary>A to-do typed in the notch becomes homework for today, without reminders on the phone.</summary>
    public static JsonObject NewTodo(string text, DateTime now) => new()
    {
        ["title"] = text,
        ["date"] = Ymd(now),
        ["start"] = "07:00",
        ["end"] = "07:30",
        ["kind"] = HomeworkKind,
        ["reminders"] = new JsonArray()
    };

    #endregion

    #region Overview tiles

    /// <summary>
    /// The home tab from the start page: the weather, when to leave for school and each Roblox game as tiles, the
    /// Pi's own checks in its line. There is no room to switch, so lights, heating and sockets stay empty.
    /// </summary>
    public static PiHome Home(JsonObject? home, JsonObject? health, DateTime now) =>
        new([], [], null, [], [], [], Checks(health), Readings(home, now));

    /// <summary>The Pi's checks: one issue per check that is not green, none when all are.</summary>
    public static HomePi? Checks(JsonObject? health)
    {
        if (health?["checks"] is not JsonArray checks) return null;
        var issues = new List<HomePiIssue>();
        foreach (var node in checks)
        {
            if (node is not JsonObject check || Str(check, "state") != "bad") continue;
            issues.Add(new HomePiIssue(Str(check, "label"), Str(check, "detail"), "error", null, null));
        }
        return new HomePi(null, null, issues);
    }

    /// <summary>
    /// The start page as readings: the weather now, how it feels with today's high and low, the way to school (or
    /// home, once the morning's is past), the school day, the next sunrise or sunset and Roblox. Each one only while
    /// the start page has it and it still lies ahead.
    /// </summary>
    public static List<HomeReading> Readings(JsonObject? home, DateTime now)
    {
        var tiles = new List<HomeReading>(6);
        string today = Ymd(now);
        int minute = now.Hour * 60 + now.Minute;
        if (home?["weather"] is JsonObject weather)
        {
            var current = weather["now"] as JsonObject;
            var day = weather["today"] as JsonObject;
            if (current != null && Num(current, "temp") is { } temp)
                tiles.Add(Reading("planner.weather", Str(weather, "place") is { Length: > 0 } place ? place : Loc.Get("pi.weather"), Degrees(temp)));
            if (current != null && Num(current, "feels") is { } feels && day != null && Num(day, "max") is { } max && Num(day, "min") is { } min)
                tiles.Add(Reading("planner.feels", Loc.Get("weather.feelsLike", Degrees(feels)), Loc.Get("weather.highLow", Whole(max), Whole(min))));
            if (weather["school"] is JsonObject school)
            {
                string date = Str(school, "date");
                if (school["there"] is JsonObject there && Str(there, "time") is { Length: > 0 } leave && !Bool(there, "past"))
                    tiles.Add(Reading("planner.leave", Loc.Get("pi.leave"), On(date, leave, now)));
                else if (date == today && school["back"] is JsonObject back && Str(back, "time") is { Length: > 0 } leaving && !Bool(back, "past"))
                    tiles.Add(Reading("planner.back", Loc.Get("pi.back"), leaving));
                if (SchoolDay(date, Str(school, "first"), Str(school, "last"), now) is { } span)
                    tiles.Add(Reading("planner.school", Loc.Get("pi.school"), span));
            }
            if (day != null && Str(day, "d") == today)
            {
                string rise = Clock(Str(day, "sunrise")), set = Clock(Str(day, "sunset"));
                if (Minutes(rise) > minute) tiles.Add(Reading("planner.sun", Loc.Get("weather.sunrise"), rise));
                else if (Minutes(set) > minute) tiles.Add(Reading("planner.sun", Loc.Get("weather.sunset"), set));
            }
        }
        if (home?["roblox"] is JsonObject roblox)
        {
            if (roblox["games"] is JsonArray games)
            {
                // The start page's older form: every game on its own.
                int index = 0;
                foreach (var (name, playing, visits) in RobloxGames(games))
                    tiles.Add(Reading($"planner.roblox{++index}", name, RobloxLine(playing, visits)));
            }
            else if (roblox["visits"] is JsonValue)
                tiles.Add(Reading("planner.roblox", "Roblox", RobloxLine(Int(roblox, "playing"), Int(roblox, "visits"))));
        }
        return tiles;
    }

    /// <summary>The games of the start page's older form, or of the Pi's own game list.</summary>
    internal static IEnumerable<(string Name, int Playing, int Visits)> RobloxGames(JsonArray games)
    {
        foreach (var node in games)
            if (node is JsonObject game) yield return (Str(game, "name"), Int(game, "playing"), Int(game, "visits"));
    }

    private static string RobloxLine(int playing, int visits) =>
        playing > 0 ? Loc.Get("pi.roblox.playing", playing) : Loc.Get("pi.roblox.visits", visits);

    /// <summary>Today "08:00–16:20" until the first lesson, then "until 16:20"; another day's first lesson.</summary>
    private static string? SchoolDay(string date, string first, string last, DateTime now)
    {
        if (Minutes(first) <= 0 || string.CompareOrdinal(date, Ymd(now)) < 0) return null;
        if (date != Ymd(now)) return On(date, first, now);
        int minute = now.Hour * 60 + now.Minute;
        if (minute < Minutes(first)) return Minutes(last) > 0 ? $"{first}–{last}" : first;
        return Minutes(last) > minute ? Loc.Get("school.until", last) : null;
    }

    /// <summary>"07:30" today, "Tomorrow 07:30", else "Thu 5.11. 07:30".</summary>
    private static string On(string date, string time, DateTime now) =>
        date == Ymd(now) ? time
        : date == Ymd(now.AddDays(1)) ? Loc.Get("pi.tomorrowAt", time)
        : $"{DayLabel(date)} {time}";

    /// <summary>"2026-11-24T16:15" → "16:15".</summary>
    private static string Clock(string stamp) => stamp.Length >= 16 && stamp[10] == 'T' ? stamp.Substring(11, 5) : "";

    /// <summary>Whole degrees, never "-0".</summary>
    private static string Whole(double value) => ((int)Math.Round(value)).ToString(Loc.GetCulture());

    private static string Degrees(double value) => $"{Whole(value)}°";

    private static HomeReading Reading(string id, string label, string display) => new(id, label, display);

    /// <summary>The way to school from the start page; null without a next school day.</summary>
    public static PiCommute? Commute(JsonObject? home)
    {
        if (home?["weather"] is not JsonObject weather || weather["school"] is not JsonObject school ||
            school["there"] is not JsonObject there) return null;
        string date = Str(school, "date"), leave = Str(there, "time");
        if (date.Length != 10 || Minutes(leave) <= 0) return null;
        var outside = there["weather"] as JsonObject;
        double? temp = outside == null ? null : Num(outside, "temp");
        double rain = outside == null ? 0 : Num(outside, "rain") ?? 0;
        return new PiCommute(date, leave, temp is { } t ? (int)Math.Round(t) : null, (int)Math.Round(rain));
    }

    #endregion

    #region Events

    internal sealed record PlannerEvent(string Id, string Title, string Sub, string Date, string Start, string End,
        string Repeat, string Kind, bool Done);

    internal static IEnumerable<PlannerEvent> Events(JsonArray? events)
    {
        if (events == null) yield break;
        foreach (var node in events)
        {
            if (node is not JsonObject e) continue;
            string date = Str(e, "date");
            if (date.Length != 10) continue;
            yield return new PlannerEvent(Str(e, "id"), Str(e, "title"), Str(e, "sub"), date, Str(e, "start"), Str(e, "end"),
                Str(e, "repeat"), Str(e, "kind"), Bool(e, "done"));
        }
    }

    /// <summary>The event's next day from <paramref name="from"/> on (weekly and yearly ones repeat), if within the window.</summary>
    internal static DateTime? Occurrence(PlannerEvent ev, DateTime from, TimeSpan window)
    {
        if (!DateTime.TryParseExact(ev.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var first)) return null;
        DateTime date = first;
        if (date < from)
        {
            if (ev.Repeat == "weekly")
                date = first.AddDays((int)Math.Ceiling((from - first).TotalDays / 7) * 7);
            else if (ev.Repeat == "yearly")
            {
                date = SameDayIn(first, from.Year);
                if (date < from) date = SameDayIn(first, from.Year + 1);
            }
            else return null;
        }
        return date - from <= window ? date : null;
    }

    private static DateTime SameDayIn(DateTime day, int year) =>
        new(year, day.Month, Math.Min(day.Day, DateTime.DaysInMonth(year, day.Month)));

    #endregion

    #region JSON

    internal static string Ymd(DateTime day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    internal static string DayLabel(string isoDate) =>
        DateTime.TryParseExact(isoDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day.ToString("ddd d.M.", Loc.GetCulture())
            : isoDate;

    /// <summary>"07:45" → 465; anything else 0.</summary>
    internal static int Minutes(string time) =>
        time.Length == 5 && int.TryParse(time.AsSpan(0, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int h) &&
        int.TryParse(time.AsSpan(3, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int m)
            ? h * 60 + m
            : 0;

    internal static string Str(JsonObject obj, string key) =>
        obj[key] is JsonValue value && value.TryGetValue(out string? text) ? text : "";

    internal static bool Bool(JsonObject obj, string key) =>
        obj[key] is JsonValue value && value.TryGetValue(out bool flag) && flag;

    internal static int Int(JsonObject obj, string key) =>
        obj[key] is JsonValue value && value.TryGetValue(out int number) ? number : 0;

    private static double? Num(JsonObject obj, string key) =>
        obj[key] is JsonValue value && value.TryGetValue(out double number) ? number : null;

    #endregion
}
