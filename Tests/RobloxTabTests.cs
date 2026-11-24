using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using VNotch.Controls;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.ControlCenter;
using Xunit;
using static VNotch.Tests.HomeSchoolTabTests;

namespace VNotch.Tests;

/// <summary>
/// This fork's Roblox tab: the Pi's sums of all the creator's games read into a board, shown as a face with who
/// plays now, the last 24 hours and four numbers, the rest in the drawer. Made-up numbers and games.
/// </summary>
[Collection("Localization")]
public sealed class RobloxTabTests
{
    private static JsonObject Board(int playing = 0) => (JsonObject)JsonNode.Parse($$"""
        { "configured": true,
          "total": { "games": 12, "playing": {{playing}}, "playingGames": {{(playing > 0 ? 1 : 0)}}, "visits": 1234, "visitsRoblox": 1200,
                     "favorites": 5, "up": 9, "down": 1, "creator": "someone" },
          "icons": [ "/api/roblox/gameicon/1?v=1", "/api/roblox/gameicon/2?v=1", "https://example.com/x.png", "/api/roblox/gameicon/../../etc" ],
          "profile": "https://www.roblox.com/users/1/profile",
          "peak": { "value": 7, "at": 1790000000000 },
          "minutes": 276,
          "today": { "d": "2026-11-24", "visits": 3, "peak": 2, "minutes": 45 },
          "days": [ { "d": "2026-11-23", "visits": 4, "peak": 1, "minutes": 10 }, { "d": "2026-11-24", "visits": 3, "peak": 2, "minutes": 45 } ],
          "daily": { "today": { "d": "2026-11-24", "teams": 2, "players": 5, "games": { "Obby One": 1, "Obby Two": 1 } } } }
        """)!;

    private static JsonObject History() => new()
    {
        ["points"] = new JsonArray(Enumerable.Range(0, 144).Select(i => (JsonNode)new JsonObject { ["max"] = i % 30 == 0 ? 3 : 0 }).ToArray())
    };

    private static JsonObject People() => (JsonObject)JsonNode.Parse("""{ "players": 4, "online": 0, "robux": 25, "visitors": [ { "name": "Somebody" } ] }""")!;

    [Fact]
    public void ThePisSumsBecomeOneBoard()
    {
        var board = RobloxBoard.Parse(Board(playing: 2), History(), People())!;

        Assert.Equal((2, 1, 12, 1234L), (board.Playing, board.PlayingGames, board.Games, board.Visits));
        Assert.Equal((7, 276, 90), (board.Record, board.Minutes, board.Rating!.Value));
        Assert.Equal(("2026-11-24", 3, 45), (board.Today.Date, board.Today.Visits, board.Today.Minutes));
        Assert.Equal("2026-11-23", board.Yesterday!.Date);
        Assert.Equal(144, board.Last24Hours.Count);
        Assert.Equal((4, 25), (board.DifferentPlayers!.Value, board.Robux!.Value));
        Assert.Equal((2, 5, 2), (board.Daily!.Teams, board.Daily.Players, board.Daily.Games.Count));
        // Only the Pi's own icon route; a profile only on roblox.com.
        Assert.Equal(new[] { "/api/roblox/gameicon/1?v=1", "/api/roblox/gameicon/2?v=1" }, board.Icons);
        Assert.Equal("https://www.roblox.com/users/1/profile", board.Profile);
        Assert.Equal("", RobloxBoard.Parse(new JsonObject { ["total"] = new JsonObject(), ["profile"] = "https://evil.example/" }, null, null)!.Profile);
    }

    [Fact]
    public void NothingSetUpOnThePiIsNoBoard()
    {
        Assert.Null(RobloxBoard.Parse(null, null, null));
        Assert.Null(RobloxBoard.Parse(new JsonObject { ["configured"] = false, ["total"] = new JsonObject() }, null, null));
        Assert.Null(RobloxBoard.Parse(new JsonObject { ["configured"] = true }, null, null));
        // Without votes there is no rating, and without the people list no player count.
        var bare = RobloxBoard.Parse(new JsonObject { ["total"] = new JsonObject { ["games"] = 1 } }, null, null)!;
        Assert.Null(bare.Rating);
        Assert.Null(bare.DifferentPlayers);
        Assert.Empty(bare.Last24Hours);
    }

    [Fact]
    public void PlaytimeReadsAsHoursAndMinutes()
    {
        string language = Loc.CurrentLanguage;
        Loc.SetLanguage("en");
        try
        {
            Assert.Equal("4.6 h", RobloxBoard.Playtime(276));
            Assert.Equal("120 h", RobloxBoard.Playtime(7200));
            Assert.Equal("45 min", RobloxBoard.Playtime(45));
            Assert.Equal("1,234", RobloxBoard.Count(1234));
        }
        finally { Loc.SetLanguage(language); }
    }

    [Theory]
    [InlineData("Media,Secondary,Timer,Claude,Pi,School,AudioMixer,Camera", "Media,School,Camera",
        "Media,Secondary,Timer,Claude,Pi,School,Roblox,AudioMixer,Camera", "Media,School,Roblox,Camera")]
    [InlineData("Media,Timer", "Media,Timer", "Media,Timer,Roblox", "Media,Timer,Roblox")]
    public void AnOlderTabOrderGetsTheRobloxTabOnceNextToSchool(string order, string visible, string newOrder, string newVisible)
    {
        var settings = new NotchSettings { NavTabOrder = order, VisibleNavTabs = visible };
        Assert.True(ForkTabs.AddRobloxOnce(settings));
        Assert.Equal((newOrder, newVisible), (settings.NavTabOrder, settings.VisibleNavTabs));

        // Hidden later in the settings: it stays hidden.
        settings.VisibleNavTabs = visible;
        Assert.False(ForkTabs.AddRobloxOnce(settings));
        Assert.Equal(visible, settings.VisibleNavTabs);
    }

    [Fact]
    public void TheFaceShowsWhoPlaysTheDayAndFourNumbersAndTheDrawerTheRest() => SharedStaTestRunner.RunAsync(async ct =>
    {
        string language = Loc.CurrentLanguage;
        Loc.SetLanguage("en");
        BackgroundTestWindows.EnsureApplicationResources();
        using var client = new PiDashboardClient(() => "http://127.0.0.1:1");
        var view = new ControlCenterView { Page = ControlCenterPage.Roblox };
        view.AttachRoblox(client, () => false, () => false);
        SetField(view, "_robloxRefreshing", true);
        var window = new BackgroundWindow { Width = 584, Height = 600, Left = -20000, Top = -20000, WindowStyle = WindowStyle.None,
            Content = new Grid { Background = Brushes.Black, Children = { view } } };
        try
        {
            SetField(view, "_roblox", RobloxBoard.Parse(Board(playing: 2), History(), People())!);
            Invoke(view, "BuildRoblox");
            window.Show();
            window.UpdateLayout();
            await Task.Yield();

            Assert.Equal("2", ((TextBlock)Field(view, "_robloxPlaying")).Text);
            Assert.Equal("in 1 of 12 games", ((TextBlock)Field(view, "_robloxSub")).Text);
            Assert.Equal("max 3", ((TextBlock)Field(view, "_robloxDayPeak")).Text);
            Assert.Equal(144, ((Polyline)Field(view, "_robloxLine")).Points.Count);
            var kpis = (ValueTuple<TextBlock, TextBlock, TextBlock>[])Field(view, "_robloxKpis");
            Assert.Equal(new[] { "1,234", "7", "4.6 h", "90 %" }, kpis.Select(k => k.Item2.Text));
            Assert.Equal(new[] { "+3 today", "+45 min today", "5 favorites" }, new[] { kpis[0].Item3.Text, kpis[2].Item3.Text, kpis[3].Item3.Text });
            Assert.Equal("12 games · 1,234 visits", view.CompactStatus);

            // Everything on the face stays inside it, side by side without overlapping.
            var face = (Grid)Field(view, "_robloxFace");
            var bounds = new Rect(face.RenderSize);
            Assert.InRange(face.ActualWidth, 500, 584);
            Assert.InRange(face.ActualHeight, 150, 260);
            foreach (var tile in Descendants<Border>(face).Where(b => b.CornerRadius.TopLeft == 10))
                Assert.True(bounds.Contains(tile.TransformToAncestor(face).TransformBounds(new Rect(tile.RenderSize))), "a tile leaves the face");
            CornerShapes.AssertNoneStretched(face);

            // The drawer: at a glance and today's daily levels, counts only.
            view.SetDrawerOpen(true);
            window.UpdateLayout();
            var drawer = Descendants<TextBlock>((StackPanel)Field(view, "_drawerBody")).Select(t => t.Text).ToList();
            foreach (string text in new[] { "At a glance", "90 %  ·  9 likes · 1 dislikes", "Different players", "4", "Robux earned", "25",
                         "Daily levels today", "2 teams · 5 players", "Obby One", "Open on Roblox" })
                Assert.Contains(text, drawer);
            Assert.DoesNotContain("Somebody", drawer);

            // Nobody playing: the dot goes quiet. Nothing from the Pi: a line instead of the face.
            SetField(view, "_roblox", RobloxBoard.Parse(Board(playing: 0), History(), People())!);
            Invoke(view, "BuildRoblox");
            Assert.Equal("0", ((TextBlock)Field(view, "_robloxPlaying")).Text);
            SetField(view, "_roblox", null!);
            SetField(view, "_robloxReachable", false);
            Invoke(view, "BuildRoblox");
            Assert.Equal(Visibility.Collapsed, face.Visibility);
            Assert.Equal("No Roblox data from the Pi", ((TextBlock)Field(view, "_robloxUnavailable")).Text);
            Assert.Equal("", view.CompactStatus);
        }
        finally
        {
            window.Close();
            view.Dispose();
            Loc.SetLanguage(language);
        }
    });
}
