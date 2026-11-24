using System.Text.Json.Nodes;
using VNotch.Services;
using VNotch.Services.ControlCenter;
using Xunit;

namespace VNotch.Tests;

/// <summary>
/// The planner's start page as it is now: Roblox only as counts, the weather with today's high and low and the sun,
/// the way to school and back. Six tiles fill the home face; each one goes once it lies behind.
/// </summary>
[Collection("Localization")]
public sealed class PlannerHomeTilesTests
{
    private static readonly DateTime Today = new(2026, 11, 3);

    private static JsonObject Start(bool therePast, bool backPast) => (JsonObject)JsonNode.Parse($$"""
        { "weather": { "place": "Springfield", "walkMin": 30,
            "now": { "temp": -3.6, "feels": -7.4 },
            "today": { "d": "2026-11-03", "max": 1.3, "min": -0.4, "sunrise": "2026-11-03T07:05", "sunset": "2026-11-03T16:35" },
            "school": { "date": "2026-11-03", "first": "08:00", "last": "16:20", "lessons": 8,
              "there": { "time": "07:30", "past": {{(therePast ? "true" : "false")}} },
              "back": { "time": "16:20", "past": {{(backPast ? "true" : "false")}} } } },
          "roblox": { "games": 17, "visits": 22, "playing": 0, "today": 0, "playingGames": 0 } }
        """)!;

    private static T InEnglish<T>(Func<T> read)
    {
        string language = Loc.CurrentLanguage;
        Loc.SetLanguage("en");
        try { return read(); }
        finally { Loc.SetLanguage(language); }
    }

    [Fact]
    public void DuringSchoolTheFaceHasSixTiles()
    {
        var tiles = InEnglish(() => PlannerDashboard.Readings(Start(therePast: true, backPast: false), Today.AddHours(9)));

        Assert.Equal(new[] { "planner.weather", "planner.feels", "planner.back", "planner.school", "planner.sun", "planner.roblox" },
            tiles.Select(t => t.Id));
        Assert.Equal(("Springfield", "-4°"), (tiles[0].Label, tiles[0].Display));
        // -0.4 is "0", never "-0".
        Assert.Equal(("Feels like -7°", "H:1° L:0°"), (tiles[1].Label, tiles[1].Display));
        Assert.Equal(("Way home", "16:20"), (tiles[2].Label, tiles[2].Display));
        Assert.Equal(("School", "until 16:20"), (tiles[3].Label, tiles[3].Display));
        Assert.Equal(("Sunset", "16:35"), (tiles[4].Label, tiles[4].Display));
        Assert.Equal(("Roblox", "22 visits"), (tiles[5].Label, tiles[5].Display));
    }

    [Fact]
    public void BeforeSchoolTheWayThereTheWholeDayAndTheSunrise()
    {
        var tiles = InEnglish(() => PlannerDashboard.Readings(Start(therePast: false, backPast: false), Today.AddHours(6)));

        Assert.Equal("07:30", tiles.Single(t => t.Id == "planner.leave").Display);
        Assert.DoesNotContain(tiles, t => t.Id == "planner.back");
        Assert.Equal("08:00–16:20", tiles.Single(t => t.Id == "planner.school").Display);
        var sun = tiles.Single(t => t.Id == "planner.sun");
        Assert.Equal(("Sunrise", "07:05"), (sun.Label, sun.Display));
    }

    [Fact]
    public void InTheEveningWhatLiesBehindIsGone()
    {
        var tiles = InEnglish(() => PlannerDashboard.Readings(Start(therePast: true, backPast: true), Today.AddHours(19)));

        Assert.Equal(new[] { "planner.weather", "planner.feels", "planner.roblox" }, tiles.Select(t => t.Id));
    }

    [Fact]
    public void SomebodyPlayingShowsAsPlayersAndOneReminderNamesTheGame()
    {
        var start = (JsonObject)JsonNode.Parse("""{ "roblox": { "games": 17, "visits": 22, "playing": 2, "playingGames": 1 } }""")!;
        var tile = InEnglish(() => PlannerDashboard.Readings(start, Today.AddHours(16))).Single();
        Assert.Equal(("planner.roblox", "2 playing"), (tile.Id, tile.Display));

        var t = new DateTimeOffset(Today.AddHours(16));
        // Without the Pi's game list the count stands for all games.
        Assert.Equal("Roblox", new PlannerReminders().Build(null, null, start, null, t).Single().Title);
        var games = (JsonArray)JsonNode.Parse("""[ { "name": "Game One", "playing": 0 }, { "name": "Game Two", "playing": 2 } ]""")!;
        var named = Assert.Single(new PlannerReminders().Build(null, null, start, null, t, games));
        Assert.Equal("Game Two", named.Title);
        // Nobody playing: no reminder.
        var quiet = (JsonObject)JsonNode.Parse("""{ "roblox": { "games": 17, "visits": 22, "playing": 0 } }""")!;
        Assert.Empty(new PlannerReminders().Build(null, null, quiet, null, t));
    }
}
