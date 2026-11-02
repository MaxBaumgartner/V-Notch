using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
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

    private async Task<List<PiEntity>?> PlannerOverviewAsync()
    {
        var home = GetAsync("/api/home");
        var health = GetAsync("/api/health");
        await Task.WhenAll(home, health).ConfigureAwait(false);
        if (home.Result == null && health.Result == null) return null;
        return PlannerDashboard.Overview(home.Result as JsonObject, health.Result as JsonObject, DateTime.Now);
    }

    private async Task<PiSchool?> PlannerSchoolAsync()
    {
        var timetable = GetAsync("/api/timetable");
        var events = GetAsync("/api/events");
        await Task.WhenAll(timetable, events).ConfigureAwait(false);
        if (timetable.Result is not JsonArray lessons) return null;
        return PlannerDashboard.School(lessons, events.Result as JsonArray, DateTime.Now);
    }

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
