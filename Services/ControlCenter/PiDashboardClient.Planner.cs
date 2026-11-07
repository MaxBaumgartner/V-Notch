using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace VNotch.Services.ControlCenter;

/// <summary>
/// A planner dashboard (see <see cref="PlannerDashboard"/>) behind the same address: told apart by the lesson timer
/// only it has, then read through its own routes.
/// </summary>
internal sealed partial class PiDashboardClient
{
    // Null until the dashboard has answered; then whether it is a planner. Asked again when the address changes.
    private bool? _isPlanner;
    private string _probedAddress = "";
    private readonly PlannerReminders _plannerReminders = new();

    private async Task<bool> IsPlannerAsync()
    {
        string address = _baseUrl().Trim();
        if (!string.Equals(address, _probedAddress, StringComparison.OrdinalIgnoreCase))
        {
            _probedAddress = address;
            _isPlanner = null;
        }
        if (_isPlanner is bool known) return known;
        if (!IsConfigured) return false;
        try
        {
            using var response = await _http.GetAsync(Url("/api/lesson-timer")).ConfigureAwait(false);
            // Only a clear answer counts: a sign-in page or a hiccup is asked about again next time.
            if (response.StatusCode == HttpStatusCode.OK) _isPlanner = true;
            else if (response.StatusCode == HttpStatusCode.NotFound) _isPlanner = false;
            return _isPlanner == true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return false;
        }
    }

    private async Task<PiHome?> PlannerHomeAsync()
    {
        var home = GetAsync("/api/home");
        var health = GetAsync("/api/health");
        await Task.WhenAll(home, health).ConfigureAwait(false);
        if (home.Result == null && health.Result == null) return null;
        return PlannerDashboard.Home(home.Result as JsonObject, health.Result as JsonObject, DateTime.Now);
    }

    private async Task<PiSchool?> PlannerSchoolAsync()
    {
        var timetable = GetAsync("/api/timetable");
        var events = GetAsync("/api/events");
        await Task.WhenAll(timetable, events).ConfigureAwait(false);
        if (timetable.Result is not JsonArray lessons) return null;
        var now = DateTime.Now;
        var school = PlannerDashboard.School(lessons, events.Result as JsonArray, now);
        return school with { Deadlines = await WithMoodleIdsAsync(PlannerDashboard.MoodleDeadlines(events.Result as JsonArray, now)).ConfigureAwait(false) };
    }

    #region Hand-ins

    // The planner keeps no Moodle ids. Each open deadline's course-module id is looked up once on the Pi (it asks
    // Moodle), together with its event, so the hand-in buttons find their way back to the planner's routes.
    private static readonly TimeSpan AssignmentRetry = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan AssignmentFresh = TimeSpan.FromMinutes(1);
    private const int MaxLookedUpDeadlines = 6;
    private readonly ConcurrentDictionary<string, (int Cmid, DateTime AtUtc)> _cmidByEvent = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<int, string> _eventByCmid = new();
    private readonly ConcurrentDictionary<int, (JsonObject Info, DateTime AtUtc)> _assignmentInfo = new();

    private async Task<List<PiDeadline>> WithMoodleIdsAsync(List<(string EventId, PiDeadline Deadline)> deadlines)
    {
        var now = DateTimeOffset.Now;
        var lookups = new Task<int>[deadlines.Count];
        int looked = 0;
        for (int i = 0; i < deadlines.Count; i++)
        {
            var (eventId, deadline) = deadlines[i];
            bool open = !deadline.Submitted && deadline.Due > now && eventId.Length > 0;
            lookups[i] = open && looked++ < MaxLookedUpDeadlines ? CmidForAsync(eventId) : Task.FromResult(0);
        }
        await Task.WhenAll(lookups).ConfigureAwait(false);
        var result = new List<PiDeadline>(deadlines.Count);
        for (int i = 0; i < deadlines.Count; i++)
            result.Add(lookups[i].Result > 0 ? deadlines[i].Deadline with { Cmid = lookups[i].Result } : deadlines[i].Deadline);
        return result;
    }

    /// <summary>The real course-module id from the assignment's Moodle address; 0 (asked again later) when the Pi cannot tell.</summary>
    private async Task<int> CmidForAsync(string eventId)
    {
        if (_cmidByEvent.TryGetValue(eventId, out var known) && (known.Cmid > 0 || DateTime.UtcNow - known.AtUtc < AssignmentRetry))
            return known.Cmid;
        var info = await AssignmentInfoAsync(eventId).ConfigureAwait(false);
        int cmid = info == null ? 0 : PlannerDashboard.CmidFromUrl(PlannerDashboard.Str(info, "url"));
        _cmidByEvent[eventId] = (cmid, DateTime.UtcNow);
        if (cmid > 0)
        {
            _eventByCmid[cmid] = eventId;
            _assignmentInfo[cmid] = (info!, DateTime.UtcNow);
        }
        return cmid;
    }

    private async Task<JsonObject?> AssignmentInfoAsync(string eventId) =>
        await GetAsync($"/api/moodle/assign/{Uri.EscapeDataString(eventId)}").ConfigureAwait(false) as JsonObject;

    private async Task<PiAssignment?> PlannerAssignmentAsync(int cmid)
    {
        if (!_eventByCmid.TryGetValue(cmid, out var eventId)) return null;
        JsonObject? info = _assignmentInfo.TryGetValue(cmid, out var cached) && DateTime.UtcNow - cached.AtUtc < AssignmentFresh
            ? cached.Info
            : await AssignmentInfoAsync(eventId).ConfigureAwait(false);
        if (info == null) return null;
        _assignmentInfo[cmid] = (info, DateTime.UtcNow);
        return PlannerDashboard.Assignment(cmid, info);
    }

    /// <summary>
    /// The planner takes a finished PDF: the pages are put together here, as the dashboard does in the browser, and
    /// handed in for good. The declaration is never accepted from here (those hand-ins are not offered, see
    /// <see cref="PlannerDashboard.Assignment"/>).
    /// </summary>
    private async Task<PiSubmitResult> PlannerSubmitPhotosAsync(int cmid, string filename, IReadOnlyList<string> jpegPages)
    {
        if (!_eventByCmid.TryGetValue(cmid, out var eventId)) return new(false, null, null);
        byte[]? pdf;
        try
        {
            pdf = PdfPages.FromJpegs(jpegPages.Select(Convert.FromBase64String).ToList());
        }
        catch (FormatException)
        {
            pdf = null;
        }
        if (pdf == null) return new(false, null, Loc.Get("pi.task.noPhotos"));
        string name = PlannerDashboard.DefaultPdfName(filename);
        string path = $"/api/moodle/assign/{Uri.EscapeDataString(eventId)}/submit?name={Uri.EscapeDataString(name)}&final=1&accept=0";
        try
        {
            using var content = new ByteArrayContent(pdf);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            using var response = await _transfer.PostAsync(Url(path), content).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            _assignmentInfo.TryRemove(cmid, out _);
            if (response.IsSuccessStatusCode) return new(true, name + ".pdf", null);
            // The Pi says why in German ("Die Abgabe ist gesperrt", "PDF ist zu groß …").
            string error = TryParseObject(body) is { } reply ? PlannerDashboard.Str(reply, "error") : "";
            return new(false, null, error.Length > 0 ? error : null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            RuntimeLog.Warn(LogTag, $"Planner hand-in {cmid} failed: {ex.Message}");
            return new(false, null, null);
        }
    }

    private static JsonObject? TryParseObject(string text)
    {
        try
        {
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    #endregion

    /// <summary>When to leave for school; only a planner dashboard knows (the Pi tab's own one has no start page).</summary>
    public async Task<PiCommute?> GetCommuteAsync() =>
        await IsPlannerAsync().ConfigureAwait(false) && await GetAsync("/api/home").ConfigureAwait(false) is JsonObject home
            ? PlannerDashboard.Commute(home)
            : null;

    private async Task<List<PiTodo>?> PlannerTodosAsync() =>
        await GetAsync("/api/events").ConfigureAwait(false) is JsonArray events ? PlannerDashboard.Todos(events, DateTime.Now) : null;

    private async Task<List<PiReminder>?> PlannerRemindersAsync()
    {
        var timetable = GetAsync("/api/timetable");
        var events = GetAsync("/api/events");
        var home = GetAsync("/api/home");
        var health = GetAsync("/api/health");
        await Task.WhenAll(timetable, events, home, health).ConfigureAwait(false);
        var now = DateTimeOffset.Now;
        if (timetable.Result == null && events.Result == null && home.Result == null && health.Result == null)
            return _plannerReminders.Failed(now);
        return _plannerReminders.Build(timetable.Result as JsonArray, events.Result as JsonArray,
            home.Result as JsonObject, health.Result as JsonObject, now);
    }

    /// <summary>A change on the planner (a new to-do, one ticked off): true when it took.</summary>
    private async Task<bool> SendOkAsync(HttpMethod method, string path, JsonObject body)
    {
        if (!IsConfigured) return false;
        try
        {
            using var request = new HttpRequestMessage(method, Url(path))
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
            };
            using var response = await _http.SendAsync(request).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            RuntimeLog.Warn(LogTag, $"{method} {path} failed: {ex.Message}");
            return false;
        }
    }
}
