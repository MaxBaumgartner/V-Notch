using System;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using VNotch.Services.ControlCenter;
using Xunit;

namespace VNotch.Tests;

/// <summary>
/// A planner dashboard on the Pi, read into the Pi tab: today's lessons or the next school day's, exams and
/// deadlines, homework as to-dos, the start page as tiles, and the reminders that become pills.
/// </summary>
public sealed class PlannerDashboardTests
{
    // A Tuesday.
    private static readonly DateTime Today = new(2026, 11, 3);

    private static JsonArray Timetable() => (JsonArray)JsonNode.Parse("""
        [
          { "date": "2026-11-03", "start": "08:00", "end": "08:50", "subject": "0WMC", "name": "Web Apps", "room": "R101", "cancelled": false, "change": "" },
          { "date": "2026-11-03", "start": "08:55", "end": "09:45", "subject": "0DBI", "name": "", "room": "R202", "cancelled": true, "change": "" },
          { "date": "2026-11-04", "start": "10:00", "end": "10:50", "subject": "0AM", "name": "Mathe", "room": "R112", "cancelled": false, "change": "ABC fehlt" },
          { "date": "2026-11-09", "start": "08:00", "end": "08:50", "subject": "0D", "name": "Deutsch", "room": "R001", "cancelled": false, "change": "XYZ fehlt" }
        ]
        """)!;

    private static JsonArray Events() => (JsonArray)JsonNode.Parse("""
        [
          { "id": "t1", "title": "DBI", "date": "2026-11-04", "start": "10:00", "end": "10:50", "kind": "test", "repeat": "none" },
          { "id": "t2", "title": "Mathe", "date": "2026-12-20", "start": "08:00", "end": "08:50", "kind": "schularbeit", "repeat": "none" },
          { "id": "t3", "title": "Alt", "date": "2026-10-01", "start": "08:00", "end": "08:50", "kind": "test", "repeat": "none" },
          { "id": "h1", "title": "WMC HÜ", "date": "2026-10-02", "start": "08:30", "end": "09:30", "kind": "hausuebung", "repeat": "weekly" },
          { "id": "h2", "title": "Abgabe: Lab 3", "sub": "WMC 1AHIF", "date": "2026-11-03", "start": "23:00", "end": "23:59", "kind": "hausuebung", "repeat": "none" },
          { "id": "h3", "title": "Lesen", "date": "2026-11-05", "start": "07:00", "end": "07:30", "kind": "hausuebung", "repeat": "none", "done": true },
          { "id": "b1", "title": "Geburtstag", "date": "2000-11-10", "start": "00:00", "end": "23:59", "kind": "geburtstag", "repeat": "yearly" }
        ]
        """)!;

    [Fact]
    public void SchoolIsTodayWhileLessonsAreToComeThenTheNextSchoolDay()
    {
        var morning = PlannerDashboard.School(Timetable(), Events(), Today.AddHours(7));
        Assert.Equal(("2026-11-03", true), (morning.Date, morning.IsToday));
        Assert.Equal(new[] { "Web Apps", "0DBI" }, morning.Lessons.Select(l => l.Subject));
        Assert.True(morning.Lessons[1].Cancelled);

        var afternoon = PlannerDashboard.School(Timetable(), Events(), Today.AddHours(13));
        Assert.Equal(("2026-11-04", false), (afternoon.Date, afternoon.IsToday));
        Assert.Equal("ABC fehlt", afternoon.Lessons.Single().Change);
    }

    [Fact]
    public void ExamsOfTheNextThreeWeeksAndMoodleDeadlinesComeWithTheSchool()
    {
        var school = PlannerDashboard.School(Timetable(), Events(), Today.AddHours(7));

        Assert.Equal(new[] { "DBI" }, school.Exams.Select(e => e.Title));
        var deadline = Assert.Single(school.Deadlines);
        Assert.Equal(("Lab 3", "WMC 1AHIF", false), (deadline.Name, deadline.Course, deadline.Submitted));
        Assert.Equal(Today.AddHours(23).AddMinutes(59), deadline.Due.LocalDateTime);
    }

    [Fact]
    public void HomeworkBecomesToDosOnItsNextDay()
    {
        var todos = PlannerDashboard.Todos(Events(), Today.AddHours(7));

        // The weekly homework (Fridays) is next due on Friday; ticked-off homework stays, marked done.
        Assert.Equal(new[] { "h3", "h1" }, todos.Select(t => t.Id));
        Assert.StartsWith("WMC HÜ · ", todos[1].Text);
        Assert.True(todos[0].Done);
        Assert.DoesNotContain(todos, t => t.Id == "h2");
    }

    [Fact]
    public void ANewToDoIsHomeworkForTodayWithoutPhoneReminders()
    {
        var todo = PlannerDashboard.NewTodo("Vokabeln", Today.AddHours(15));
        Assert.Equal(("Vokabeln", "2026-11-03", "hausuebung"), (todo["title"]!.GetValue<string>(), todo["date"]!.GetValue<string>(), todo["kind"]!.GetValue<string>()));
        Assert.Empty(todo["reminders"]!.AsArray());
        Assert.True(string.CompareOrdinal(todo["start"]!.GetValue<string>(), todo["end"]!.GetValue<string>()) < 0);
    }

    [Fact]
    public void TheStartPageShowsAsTiles()
    {
        var home = (JsonObject)JsonNode.Parse("""
            { "weather": { "place": "Springfield", "now": { "temp": 6.4 },
                           "school": { "date": "2026-11-04", "there": { "time": "07:30", "past": false } } },
              "roblox": { "games": [ { "name": "Game One", "playing": 2, "visits": 9 }, { "name": "Game Two", "playing": 0, "visits": 4 } ] } }
            """)!;
        var health = (JsonObject)JsonNode.Parse("""
            { "checks": [ { "id": "untis", "label": "WebUntis", "state": "bad" }, { "id": "push", "label": "Push", "state": "ok" } ] }
            """)!;

        var page = PlannerDashboard.Home(home, health, Today.AddHours(20));
        var tiles = page.Readings!;

        Assert.Equal(new[] { "planner.weather", "planner.leave", "planner.roblox1", "planner.roblox2" }, tiles.Select(t => t.Id));
        Assert.Equal("Springfield", tiles[0].Label);
        Assert.StartsWith("6", tiles[0].Display);
        Assert.Contains("07:30", tiles[1].Display);
        Assert.Contains("2", tiles[2].Display);
        // The Pi's checks go into its line: only the red one is an issue. No room to switch.
        Assert.Equal("WebUntis", Assert.Single(page.Pi!.Issues).Title);
        Assert.Empty(page.Lights);
        Assert.Empty(page.Plugs);
    }

    [Fact]
    public void RemindersKeepTheirKeysAndComeInTheirTime()
    {
        var reminders = new PlannerReminders();
        var morning = new DateTimeOffset(Today.AddHours(9));
        var keys = reminders.Build(Timetable(), Events(), null, null, morning).Select(r => r.Key).ToList();

        // Tomorrow's changed lesson opens Untis; next week's waits; the test tomorrow only shows from noon on.
        Assert.Contains(keys, k => k.StartsWith("aenderung:2026-11-04:10:00:Mathe", StringComparison.Ordinal));
        Assert.DoesNotContain(keys, k => k.Contains("2026-11-09", StringComparison.Ordinal));
        Assert.DoesNotContain(keys, k => k.StartsWith("pruefung:", StringComparison.Ordinal));
        Assert.DoesNotContain(keys, k => k.StartsWith("hausuebung:", StringComparison.Ordinal));

        var afternoon = reminders.Build(Timetable(), Events(), null, null, new DateTimeOffset(Today.AddHours(14))).Select(r => r.Key).ToList();
        Assert.Contains("pruefung:t1:2026-11-04:morgen", afternoon);
        Assert.Contains("hausuebung:h2:2026-11-03", afternoon);
        Assert.Equal(afternoon, reminders.Build(Timetable(), Events(), null, null, new DateTimeOffset(Today.AddHours(14.5))).Select(r => r.Key));
    }

    [Fact]
    public void SomeoneStartingToPlayIsOneReminderPerStretch()
    {
        var reminders = new PlannerReminders();
        JsonObject Home(int playing) => (JsonObject)JsonNode.Parse($$"""{ "roblox": { "games": [ { "name": "Game One", "playing": {{playing}} } ] } }""")!;
        var t = new DateTimeOffset(Today.AddHours(16));

        string first = reminders.Build(null, null, Home(1), null, t).Single().Key;
        Assert.Equal(first, reminders.Build(null, null, Home(3), null, t.AddMinutes(1)).Single().Key);
        Assert.Empty(reminders.Build(null, null, Home(0), null, t.AddMinutes(2)));
        string again = reminders.Build(null, null, Home(1), null, t.AddMinutes(3)).Single().Key;
        Assert.NotEqual(first, again);
    }

    [Fact]
    public void ThePiCountsAsGoneAfterSomeFailedLooks()
    {
        var reminders = new PlannerReminders();
        var t = new DateTimeOffset(Today.AddHours(10));
        for (int i = 1; i < PlannerReminders.OfflineAfterFailures; i++) Assert.Null(reminders.Failed(t.AddMinutes(i)));
        var gone = Assert.Single(reminders.Failed(t.AddMinutes(5))!);
        Assert.Equal(gone.Key, reminders.Failed(t.AddMinutes(6))!.Single().Key);

        // Back, then gone again: a new outage.
        reminders.Build(null, null, null, null, t.AddMinutes(7));
        for (int i = 1; i < PlannerReminders.OfflineAfterFailures; i++) reminders.Failed(t.AddMinutes(7 + i));
        Assert.NotEqual(gone.Key, reminders.Failed(t.AddMinutes(20))!.Single().Key);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheClientTellsAPlannerFromThePiTabsOwnDashboard(bool planner)
    {
        using var server = new FakeDashboard(planner);
        using var client = new PiDashboardClient(() => server.Address);

        var school = await client.GetSchoolAsync();

        Assert.NotNull(school);
        Assert.Equal(planner ? "planner" : "school", server.Served);
    }

    /// <summary>A planner (lesson timer, timetable, events) or the Pi tab's own dashboard (/api/school).</summary>
    private sealed class FakeDashboard : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly bool _planner;

        public string Address { get; }
        public volatile string Served = "";

        public FakeDashboard(bool planner)
        {
            _planner = planner;
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Address = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(Address);
            _listener.Start();
            _ = Task.Run(Serve);
        }

        private async Task Serve()
        {
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync().ConfigureAwait(false); }
                catch (Exception) { return; }
                string path = context.Request.Url!.AbsolutePath;
                string? body = (path, _planner) switch
                {
                    ("/api/lesson-timer", true) => """{ "enabled": true, "now": { "state": "off" } }""",
                    ("/api/timetable", true) => "[]",
                    ("/api/events", true) => "[]",
                    ("/api/school", false) => """{ "date": "2026-11-03", "isToday": true, "timetable": [] }""",
                    _ => null
                };
                if (path == "/api/timetable") Served = "planner";
                if (path == "/api/school") Served = "school";
                context.Response.StatusCode = body == null ? 404 : 200;
                byte[] bytes = Encoding.UTF8.GetBytes(body ?? "{}");
                context.Response.ContentType = "application/json";
                await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                context.Response.Close();
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Close();
        }
    }
}
