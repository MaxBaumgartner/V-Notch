using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows.Media;

namespace VNotch.Services.ControlCenter;

/// <summary>A lamp in the room; the strip (<paramref name="Rgb"/>) also has a colour and a brightness in percent.</summary>
internal sealed record HomeLight(string Id, string Name, bool Rgb, bool Available, bool On, Color? Color, int? Brightness);

internal sealed record HomeColor(string Name, Color Color);

/// <summary>The heating: its target, the room's own thermometer, and the window it pauses for.</summary>
internal sealed record HomeClimate(string Mode, string? Action, double? Target, double Min, double Max, double? Current, double? Humidity, bool WindowOpen)
{
    public bool IsHeating => Action == "heating";
}

/// <summary>A button of the air conditioner's IR remote, or one of the dashboard's own scenes.</summary>
internal sealed record HomeAction(string Id, string Name, string Icon);

/// <summary>A switched socket with its draw; <paramref name="Guard"/> ones (PC, Pi) ask before they switch off.</summary>
internal sealed record HomePlug(string Id, string Name, bool Available, bool On, bool Guard, double? Watts, double? TodayKwh);

internal sealed record HomePiIssue(string Title, string Detail, string Status, string? FixId, string? FixLabel);

internal sealed record HomePi(double? Temp, long? UptimeSeconds, IReadOnlyList<HomePiIssue> Issues);

/// <summary>A reading a planner dashboard's start page offers (the weather, when to leave, a game's players), as a tile.</summary>
internal sealed record HomeReading(string Id, string Label, string Display);

/// <summary>
/// Everything the home tab shows, in one read of the dashboard's /api/notch/zuhause. A planner dashboard has no room
/// to switch: it brings <paramref name="Readings"/> and its checks as <paramref name="Pi"/> instead.
/// </summary>
internal sealed record PiHome(IReadOnlyList<HomeLight> Lights, IReadOnlyList<HomeColor> StripColors, HomeClimate? Climate,
    IReadOnlyList<HomeAction> AirConditioner, IReadOnlyList<HomePlug> Plugs, IReadOnlyList<HomeAction> Scenes, HomePi? Pi,
    IReadOnlyList<HomeReading>? Readings = null);

internal sealed record PiLesson(string Start, string End, string Subject, string Room, bool Cancelled, string Change, int StartMinutes, int EndMinutes);

/// <summary>
/// A Moodle assignment; <paramref name="Cmid"/> is its course-module id (its page's "?id="). <paramref name="Kind"/>
/// "classroom" is a WMC task on Classroom 50 (a Moodle link, its page in <paramref name="Url"/>); handed-in ones carry
/// when (<paramref name="SubmittedAt"/>) and stay listed for a week after their due date.
/// </summary>
internal sealed record PiDeadline(string Name, string Course, DateTimeOffset Due, bool Submitted, int Cmid = 0,
    string Kind = "", string Url = "", DateTimeOffset? SubmittedAt = null);

/// <summary>What the Pi's hand-in panel knows about an assignment: "pose" (a code project at the PC), "photo" (maths: photos as one PDF), "classroom" (WMC repo on Classroom 50) or "link".</summary>
internal sealed record PiAssignment(int Cmid, string Name, string Mode, string Nr, IReadOnlyList<PiAttachment> Attachments,
    string Status, string? Blocked, string Filename);

internal sealed record PiAttachment(string Name, long Size);

internal sealed record PiSubmitResult(bool Ok, string? SubmittedName, string? Error);

internal sealed record PiExam(DateTime Date, string Start, string Kind, string Title, string Subject = "");

internal sealed record PiSchool(string Date, bool IsToday, IReadOnlyList<PiLesson> Lessons, IReadOnlyList<PiDeadline> Deadlines,
    IReadOnlyList<PiExam> Exams, string? Break);

internal sealed record PiTodo(string Id, string Text, bool Done);

internal sealed record PiReminder(string Key, string Title, string Message, DateTimeOffset At);

/// <summary>
/// The user's own dashboard on a Raspberry Pi (Express, reached over Tailscale): Home Assistant switches,
/// school timetable and deadlines, to-dos and the reminders it sends. Only ever talks to the configured
/// address; nothing leaves for the internet from here. A planner dashboard, which answers on other routes, is
/// read the same way (see PiDashboardClient.Planner.cs).
/// </summary>
internal sealed partial class PiDashboardClient : IDisposable
{
    private const string LogTag = "PI-DASHBOARD";
    private readonly HttpClient _http = new(new NativeWinHttpHandler()) { Timeout = TimeSpan.FromSeconds(6) };
    // Files of an assignment and photo hand-ins go through Moodle on the Pi: they need longer.
    private readonly HttpClient _transfer = new(new NativeWinHttpHandler()) { Timeout = TimeSpan.FromMinutes(3) };
    private const long MaxAttachmentBytes = 200L * 1024 * 1024;
    private readonly Func<string> _baseUrl;

    public PiDashboardClient(Func<string> baseUrl)
    {
        _baseUrl = baseUrl;
    }

    public bool IsConfigured => Uri.TryCreate(_baseUrl().Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    public void Dispose()
    {
        _http.Dispose();
        _transfer.Dispose();
    }

    public async Task<PiHome?> GetHomeAsync()
    {
        if (await IsPlannerAsync().ConfigureAwait(false)) return await PlannerHomeAsync().ConfigureAwait(false);
        return await GetAsync("/api/notch/zuhause").ConfigureAwait(false) is JsonObject home ? ParseHome(home) : null;
    }

    internal static PiHome ParseHome(JsonObject home)
    {
        var lights = new List<HomeLight>();
        foreach (var l in Objects(home["lights"]))
            lights.Add(new HomeLight(Str(l, "id"), Str(l, "name"), Bool(l, "rgb"), Bool(l, "available"), Bool(l, "on"), Rgb(l["color"]),
                Num(l, "brightness") is { } b ? (int)Math.Round(b) : null));
        var colors = new List<HomeColor>();
        foreach (var c in Objects(home["stripColors"]))
            if (Rgb(c["rgb"]) is { } rgb) colors.Add(new HomeColor(Str(c, "name"), rgb));
        HomeClimate? climate = home["climate"] is JsonObject k
            ? new HomeClimate(Str(k, "mode"), k["action"] is JsonValue ? Str(k, "action") : null, Num(k, "target"), Num(k, "min") ?? 5, Num(k, "max") ?? 30,
                Num(k, "current"), Num(k, "humidity"), Bool(k, "windowOpen"))
            : null;
        var plugs = new List<HomePlug>();
        foreach (var p in Objects(home["plugs"]))
            plugs.Add(new HomePlug(Str(p, "id"), Str(p, "name"), Bool(p, "available"), Bool(p, "on"), Bool(p, "guard"), Num(p, "watts"), Num(p, "todayKwh")));
        HomePi? pi = null;
        if (home["pi"] is JsonObject piNode)
        {
            var issues = new List<HomePiIssue>();
            foreach (var i in Objects(piNode["issues"]))
                issues.Add(new HomePiIssue(Str(i, "title"), Str(i, "detail"), Str(i, "status"),
                    Str(i, "fixId") is { Length: > 0 } fix ? fix : null, Str(i, "fixLabel") is { Length: > 0 } label ? label : null));
            pi = new HomePi(Num(piNode, "temp"), Num(piNode, "uptimeS") is { } up ? (long)up : null, issues);
        }
        return new PiHome(lights, colors, climate, Actions(home["ac"]), plugs, Actions(home["scenes"]), pi);

        static List<HomeAction> Actions(JsonNode? node)
        {
            var list = new List<HomeAction>();
            foreach (var a in Objects(node)) list.Add(new HomeAction(Str(a, "id"), Str(a, "name"), Str(a, "icon")));
            return list;
        }
    }

    /// <summary>
    /// Switches something in the room through the dashboard (which only takes what its list allows): "light"
    /// (on/off, or the strip's colour), "temp", "ac", "plug", "scene" or "fix".
    /// </summary>
    public Task<bool> SendHomeAsync(string action, string? id, JsonNode? value = null) =>
        PostAsync("/api/notch/zuhause", new JsonObject { ["action"] = action, ["id"] = id, ["value"] = value });

    public async Task<PiSchool?> GetSchoolAsync()
    {
        if (await IsPlannerAsync().ConfigureAwait(false)) return await PlannerSchoolAsync().ConfigureAwait(false);
        return await GetAsync("/api/school").ConfigureAwait(false) is JsonObject school ? ParseSchool(school) : null;
    }

    internal static PiSchool ParseSchool(JsonObject school)
    {
        var lessons = new List<PiLesson>();
        if (school["timetable"] is JsonArray timetable)
        {
            foreach (var node in timetable)
            {
                if (node is not JsonObject l) continue;
                lessons.Add(new PiLesson(Str(l, "startTime"), Str(l, "endTime"), Str(l, "subject"), Str(l, "room"),
                    Bool(l, "cancelled"), Str(l, "change"), Int(l, "startMinutes"), Int(l, "endMinutes")));
            }
        }
        var deadlines = new List<PiDeadline>();
        if (school["deadlines"] is JsonArray due)
        {
            foreach (var node in due)
            {
                if (node is not JsonObject d || !(d["duedate"] is JsonValue v && v.TryGetValue(out long seconds))) continue;
                DateTimeOffset? submittedAt = d["submittedAt"] is JsonValue s && s.TryGetValue(out long at) && at > 0 ? DateTimeOffset.FromUnixTimeSeconds(at) : null;
                deadlines.Add(new PiDeadline(Str(d, "name"), Str(d, "course"), DateTimeOffset.FromUnixTimeSeconds(seconds), Bool(d, "submitted"), Int(d, "cmid"),
                    Str(d, "kind"), Str(d, "url"), submittedAt));
            }
        }
        var exams = new List<PiExam>();
        if (school["exams"] is JsonArray examList)
        {
            foreach (var node in examList)
            {
                if (node is not JsonObject x) continue;
                if (!DateTime.TryParseExact(Str(x, "date"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) continue;
                exams.Add(new PiExam(date, Str(x, "startTime"), Str(x, "kind"), Str(x, "title"), Str(x, "subjectShort")));
            }
        }
        string? holiday = school["breaks"] is JsonObject b ? Str(b, "name") : null;
        return new PiSchool(Str(school, "date"), Bool(school, "isToday"), lessons, deadlines, exams, holiday is { Length: > 0 } ? holiday : null);
    }

    public async Task<List<PiTodo>?> GetTodosAsync()
    {
        if (await IsPlannerAsync().ConfigureAwait(false)) return await PlannerTodosAsync().ConfigureAwait(false);
        if (await GetAsync("/api/todos") is not JsonArray todos) return null;
        var list = new List<PiTodo>(todos.Count);
        foreach (var node in todos)
        {
            if (node is not JsonObject t) continue;
            bool done = t["done"] is JsonValue d && d.TryGetValue(out long at) && at != 0;
            list.Add(new PiTodo(Str(t, "id"), Str(t, "text"), done));
        }
        return list;
    }

    public async Task<bool> AddTodoAsync(string text)
    {
        if (await IsPlannerAsync().ConfigureAwait(false))
            return await SendOkAsync(HttpMethod.Post, "/api/events", PlannerDashboard.NewTodo(text, DateTime.Now)).ConfigureAwait(false);
        return await PostAsync("/api/todos", new JsonObject { ["text"] = text }).ConfigureAwait(false);
    }

    public async Task<bool> SetTodoDoneAsync(string id, bool done)
    {
        if (await IsPlannerAsync().ConfigureAwait(false))
            return await SendOkAsync(HttpMethod.Patch, $"/api/events/{Uri.EscapeDataString(id)}/done", new JsonObject { ["done"] = done }).ConfigureAwait(false);
        return await PostAsync($"/api/todos/{Uri.EscapeDataString(id)}", new JsonObject { ["done"] = done }).ConfigureAwait(false);
    }

    /// <summary>What the dashboard last pushed to the phone, newest last.</summary>
    public async Task<List<PiReminder>?> GetRemindersAsync()
    {
        if (await IsPlannerAsync().ConfigureAwait(false)) return await PlannerRemindersAsync().ConfigureAwait(false);
        if (await GetAsync("/api/reminders") is not JsonObject status || status["sent"] is not JsonArray sent) return null;
        var list = new List<PiReminder>(sent.Count);
        foreach (var node in sent)
        {
            if (node is not JsonObject r || !(r["at"] is JsonValue v && v.TryGetValue(out long ms))) continue;
            list.Add(new PiReminder(Str(r, "key"), Str(r, "title"), Str(r, "message"), DateTimeOffset.FromUnixTimeMilliseconds(ms)));
        }
        return list;
    }

    /// <summary>
    /// The Moodle page, signed in: the Pi swaps it for Moodle's one-time sign-in link to it (its password and token
    /// stay there). Anything unexpected, or the Pi out of reach, leaves the page link as it was.
    /// </summary>
    /// <summary>An assignment as the Pi's hand-in panel sees it (mode, the files of the task, whether it can be handed in).</summary>
    public async Task<PiAssignment?> GetAssignmentAsync(int cmid)
    {
        if (await IsPlannerAsync().ConfigureAwait(false)) return await PlannerAssignmentAsync(cmid).ConfigureAwait(false);
        return cmid > 0 && await GetAsync($"/api/abgabe/{cmid}").ConfigureAwait(false) is JsonObject o ? ParseAssignment(o) : null;
    }

    internal static PiAssignment? ParseAssignment(JsonObject o)
    {
        int cmid = Int(o, "cmid");
        if (cmid <= 0) return null;
        var files = new List<PiAttachment>();
        if (o["attachments"] is JsonArray list)
            foreach (var node in list)
                if (node is JsonObject f && Str(f, "name") is { Length: > 0 } name)
                    files.Add(new PiAttachment(name, f["size"] is JsonValue v && v.TryGetValue(out long size) ? size : 0));
        string blocked = Str(o, "blocked");
        return new PiAssignment(cmid, Str(o, "name"), Str(o, "mode"), Str(o, "nr"), files, Str(o, "status"),
            blocked.Length > 0 ? blocked : null, Str(o, "filename"));
    }

    /// <summary>File <paramref name="index"/> of the task, fetched by the Pi (it keeps the Moodle token).</summary>
    public async Task<byte[]?> DownloadAttachmentAsync(int cmid, int index, CancellationToken token = default)
    {
        if (!IsConfigured || cmid <= 0 || index < 0) return null;
        try
        {
            using var response = await _transfer.GetAsync(Url($"/api/abgabe/{cmid}/datei/{index}"), HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxAttachmentBytes) return null;
            byte[] bytes = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
            return bytes.Length <= MaxAttachmentBytes ? bytes : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            RuntimeLog.Warn(LogTag, $"Download {cmid}/{index} failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Hands photos (JPEG pages, base64) in as one PDF, like the dashboard's maths panel.</summary>
    public async Task<PiSubmitResult> SubmitPhotosAsync(int cmid, string filename, IReadOnlyList<string> jpegPages)
    {
        if (!IsConfigured || cmid <= 0 || jpegPages.Count == 0) return new(false, null, null);
        if (await IsPlannerAsync().ConfigureAwait(false)) return await PlannerSubmitPhotosAsync(cmid, filename, jpegPages).ConfigureAwait(false);
        var pages = new JsonArray();
        foreach (string page in jpegPages) pages.Add(page);
        return await HandInAsync(cmid, new JsonObject { ["filename"] = filename, ["pages"] = pages }).ConfigureAwait(false);
    }

    /// <summary>One file for a task on the school's NW hand-in site (NSCS); the Pi holds that site's login.</summary>
    public Task<PiSubmitResult> SubmitFileAsync(int cmid, string filename, byte[] bytes) =>
        bytes.Length == 0 ? Task.FromResult(new PiSubmitResult(false, null, null))
            : HandInAsync(cmid, new JsonObject { ["filename"] = filename, ["file"] = Convert.ToBase64String(bytes) });

    /// <summary>The dashboard's hand-in panel of a task (its page opens it from the address).</summary>
    public string? PanelLink(int cmid) => IsConfigured && cmid > 0 ? $"{_baseUrl().Trim().TrimEnd('/')}/#abgabe-{cmid}" : null;

    private async Task<PiSubmitResult> HandInAsync(int cmid, JsonObject request)
    {
        if (!IsConfigured || cmid <= 0) return new(false, null, null);
        try
        {
            using var content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await _transfer.PostAsync(Url($"/api/abgabe/{cmid}"), content).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            // The Pi explains refusals (too many pages, Moodle closed) in the body, with an error status.
            var reply = JsonNode.Parse(body) as JsonObject;
            string error = reply == null ? "" : Str(reply, "error");
            bool ok = response.IsSuccessStatusCode && reply != null && Bool(reply, "ok");
            return new(ok, reply == null ? null : Str(reply, "submittedName"), error.Length > 0 ? error : null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            RuntimeLog.Warn(LogTag, $"Hand-in {cmid} failed: {ex.Message}");
            return new(false, null, null);
        }
    }

    public async Task<string> SignedInMoodleLinkAsync(string url, string moodleBase)
    {
        string root = (moodleBase ?? "").Trim().TrimEnd('/');
        if (root.Length == 0 || !url.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)) return url;
        var reply = await SendAsync(HttpMethod.Post, "/api/moodle/autologin", new JsonObject { ["url"] = url }).ConfigureAwait(false);
        string link = reply is JsonObject o ? Str(o, "url") : "";
        // Only ever a page of the same Moodle.
        return link.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase) ? link : url;
    }

    /// <summary>
    /// Untis, signed in: Untis has no sign-in link, so the Pi hands out a one-time page of its own that signs the
    /// browser in with the credentials it keeps. Anything unexpected leaves the Untis link as it was.
    /// </summary>
    public async Task<string> SignedInUntisLinkAsync(string untisUrl)
    {
        var reply = await SendAsync(HttpMethod.Post, "/api/untis/login", new JsonObject()).ConfigureAwait(false);
        string path = reply is JsonObject o ? Str(o, "path") : "";
        // Only ever the Pi's own sign-in page.
        if (!path.StartsWith("/untis-login/", StringComparison.Ordinal) || path.Length <= "/untis-login/".Length || path.Contains("..", StringComparison.Ordinal))
            return untisUrl;
        return Url(path).AbsoluteUri;
    }

    /// <summary>A school link signed in where the Pi can: Moodle pages and the Untis link; anything else as it is.</summary>
    public Task<string> SignedInSchoolLinkAsync(string url, string moodleBase, string untisUrl)
    {
        if (SchoolLinks.Untis(untisUrl) is { } untis && string.Equals(url.Trim(), untis, StringComparison.OrdinalIgnoreCase))
            return SignedInUntisLinkAsync(url);
        return SignedInMoodleLinkAsync(url, moodleBase);
    }

    private async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonObject body)
    {
        if (!IsConfigured) return null;
        try
        {
            using var request = new HttpRequestMessage(method, Url(path))
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
            };
            using var response = await _http.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            return JsonNode.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            RuntimeLog.Warn(LogTag, $"{method} {path} failed: {ex.Message}");
            return null;
        }
    }

    private async Task<JsonNode?> GetAsync(string path)
    {
        if (!IsConfigured) return null;
        try
        {
            using var response = await _http.GetAsync(Url(path)).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            return JsonNode.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            RuntimeLog.Warn(LogTag, $"GET {path} failed: {ex.Message}");
            return null;
        }
    }

    private async Task<bool> PostAsync(string path, JsonObject body)
    {
        if (!IsConfigured) return false;
        try
        {
            using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(Url(path), content).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            RuntimeLog.Warn(LogTag, $"POST {path} failed: {ex.Message}");
            return false;
        }
    }

    private Uri Url(string path) => new(new Uri(_baseUrl().Trim().TrimEnd('/') + "/"), path.TrimStart('/'));

    private static IEnumerable<JsonObject> Objects(JsonNode? node)
    {
        if (node is not JsonArray array) yield break;
        foreach (var item in array)
            if (item is JsonObject obj) yield return obj;
    }

    private static double? Num(JsonObject obj, string key) =>
        obj[key] is JsonValue value && value.TryGetValue(out double number) ? number : null;

    private static Color? Rgb(JsonNode? node) =>
        node is JsonArray { Count: 3 } rgb && rgb[0] is JsonValue r && r.TryGetValue(out int red) && rgb[1] is JsonValue g && g.TryGetValue(out int green)
            && rgb[2] is JsonValue b && b.TryGetValue(out int blue)
            ? Color.FromRgb((byte)Math.Clamp(red, 0, 255), (byte)Math.Clamp(green, 0, 255), (byte)Math.Clamp(blue, 0, 255))
            : null;

    private static string Str(JsonObject obj, string key) =>
        obj[key] is JsonValue value && value.TryGetValue(out string? text) ? text : "";

    private static bool Bool(JsonObject obj, string key) =>
        obj[key] is JsonValue value && value.TryGetValue(out bool flag) && flag;

    private static int Int(JsonObject obj, string key) =>
        obj[key] is JsonValue value && value.TryGetValue(out int number) ? number : 0;
}
