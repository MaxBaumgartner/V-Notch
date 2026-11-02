using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace VNotch.Services.ControlCenter;

/// <summary>A Home Assistant entity as the dashboard lists it: a switch to flip or a reading to show.</summary>
internal sealed record PiEntity(string Id, string Label, string Icon, string State, string Display, string Type)
{
    public bool IsOn => State == "on";
    /// <summary>The dashboard's toggle endpoint takes these domains only.</summary>
    public bool CanToggle => Type == "switch" && (Id.StartsWith("switch.", StringComparison.Ordinal) ||
        Id.StartsWith("light.", StringComparison.Ordinal) || Id.StartsWith("input_boolean.", StringComparison.Ordinal));
}

internal sealed record PiLesson(string Start, string End, string Subject, string Room, bool Cancelled, string Change, int StartMinutes, int EndMinutes);

/// <summary>A Moodle assignment; <paramref name="Cmid"/> is its course-module id (its page's "?id=").</summary>
internal sealed record PiDeadline(string Name, string Course, DateTimeOffset Due, bool Submitted, int Cmid = 0);

internal sealed record PiExam(DateTime Date, string Start, string Kind, string Title);

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
    private readonly Func<string> _baseUrl;

    public PiDashboardClient(Func<string> baseUrl)
    {
        _baseUrl = baseUrl;
    }

    public bool IsConfigured => Uri.TryCreate(_baseUrl().Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    public void Dispose() => _http.Dispose();

    public async Task<List<PiEntity>?> GetHomeAsync()
    {
        if (await IsPlannerAsync().ConfigureAwait(false)) return await PlannerOverviewAsync().ConfigureAwait(false);
        if (await GetAsync("/api/homeassistant") is not JsonObject home || home["entities"] is not JsonArray entities) return null;
        var list = new List<PiEntity>(entities.Count);
        foreach (var node in entities)
        {
            if (node is not JsonObject e) continue;
            list.Add(new PiEntity(Str(e, "id"), Str(e, "label"), Str(e, "icon"), Str(e, "state"), Str(e, "display"), Str(e, "type")));
        }
        return list;
    }

    public Task<bool> ToggleAsync(string entityId) =>
        PostAsync("/api/homeassistant/toggle", new JsonObject { ["entity_id"] = entityId });

    public async Task<PiSchool?> GetSchoolAsync()
    {
        if (await IsPlannerAsync().ConfigureAwait(false)) return await PlannerSchoolAsync().ConfigureAwait(false);
        if (await GetAsync("/api/school") is not JsonObject school) return null;
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
                deadlines.Add(new PiDeadline(Str(d, "name"), Str(d, "course"), DateTimeOffset.FromUnixTimeSeconds(seconds), Bool(d, "submitted"), Int(d, "cmid")));
            }
        }
        var exams = new List<PiExam>();
        if (school["exams"] is JsonArray examList)
        {
            foreach (var node in examList)
            {
                if (node is not JsonObject x) continue;
                if (!DateTime.TryParseExact(Str(x, "date"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) continue;
                exams.Add(new PiExam(date, Str(x, "startTime"), Str(x, "kind"), Str(x, "title")));
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

    private static string Str(JsonObject obj, string key) =>
        obj[key] is JsonValue value && value.TryGetValue(out string? text) ? text : "";

    private static bool Bool(JsonObject obj, string key) =>
        obj[key] is JsonValue value && value.TryGetValue(out bool flag) && flag;

    private static int Int(JsonObject obj, string key) =>
        obj[key] is JsonValue value && value.TryGetValue(out int number) ? number : 0;
}
