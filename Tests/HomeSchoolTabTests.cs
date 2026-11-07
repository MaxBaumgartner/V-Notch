using System.Text.Json.Nodes;
using System.Windows.Media;
using VNotch.Controls;
using VNotch.Services.ControlCenter;
using Xunit;

namespace VNotch.Tests;

public sealed class HomeSchoolTabTests
{
    private const string HomeJson = """
    {
      "lights": [
        { "id": "switch.main_light", "name": "Hauptlicht", "rgb": false, "available": true, "on": false, "color": null, "brightness": null },
        { "id": "light.strip", "name": "Govee Strip", "rgb": true, "available": true, "on": true, "color": [160, 60, 230], "brightness": 70 }
      ],
      "stripColors": [ { "name": "Rot", "rgb": [255, 40, 20] } ],
      "climate": { "mode": "heat", "action": "heating", "target": 23, "min": 4, "max": 35, "current": 21.6, "humidity": 44.7, "windowOpen": false },
      "ac": [ { "id": "script.klima_power_toggle", "name": "An/Aus", "icon": "power" } ],
      "plugs": [ { "id": "switch.pc", "name": "PC", "available": true, "on": true, "guard": true, "watts": 146.9, "todayKwh": 1.376 } ],
      "scenes": [ { "id": "nacht", "name": "Gute Nacht", "icon": "moon" } ],
      "pi": { "temp": 57.9, "uptimeS": 1380493, "issues": [ { "title": "Miner", "detail": "2 Fehler", "status": "warn", "fixId": null, "fixLabel": null } ] }
    }
    """;

    [Fact]
    public void TheDashboardsHomeIsReadIntoTheTab()
    {
        var home = PiDashboardClient.ParseHome((JsonObject)JsonNode.Parse(HomeJson)!);
        Assert.Equal(2, home.Lights.Count);
        Assert.Equal(Color.FromRgb(160, 60, 230), home.Lights[1].Color);
        Assert.Equal(70, home.Lights[1].Brightness);
        Assert.True(home.Climate!.IsHeating);
        Assert.Equal(23, home.Climate.Target);
        Assert.Equal(21.6, home.Climate.Current);
        Assert.True(home.Plugs[0].Guard);
        Assert.Equal(1.376, home.Plugs[0].TodayKwh);
        Assert.Equal("power", home.AirConditioner[0].Icon);
        Assert.Equal("moon", home.Scenes[0].Icon);
        Assert.Null(home.Pi!.Issues[0].FixId);
        Assert.Equal(1380493, home.Pi.UptimeSeconds);
    }

    [Fact]
    public void TheHomeTabWearsTheRoomsColour()
    {
        var off = new HomeLight("a", "A", false, true, false, null, null);
        var lamp = off with { On = true };
        var strip = new HomeLight("s", "Strip", true, true, true, Color.FromRgb(160, 60, 230), 100);
        var night = ControlCenterView.HomeMood(new[] { off });
        var lit = ControlCenterView.HomeMood(new[] { lamp });
        var coloured = ControlCenterView.HomeMood(new[] { lamp, strip });
        Assert.NotEqual(night.Main, lit.Main);
        // The strip's colour wins, muted toward grey (never the raw saturated colour).
        Assert.NotEqual(lit.Main, coloured.Main);
        Assert.NotEqual(Color.FromRgb(160, 60, 230), coloured.Main);
        Assert.True(coloured.Main.B > coloured.Main.G, "still reads as purple");
    }

    [Fact]
    public void TheSchoolTabTurnsWarmAsThingsComeDue()
    {
        var calm = ControlCenterView.SchoolMood(null);
        Assert.Equal(calm, ControlCenterView.SchoolMood(TimeSpan.FromDays(6)));
        var soon = ControlCenterView.SchoolMood(TimeSpan.FromDays(2));
        var now = ControlCenterView.SchoolMood(TimeSpan.FromHours(5));
        Assert.NotEqual(calm, soon);
        Assert.NotEqual(soon, now);
    }

    [Theory]
    [InlineData("0D", "Deutschschularbeit", "D")]
    [InlineData("1WMC", "Webprogrammierung", "WMC")]
    [InlineData("0POSETHI", "Theoretische Informatik", "POSE")]
    [InlineData("0SYPPRE", "Test 1", "SYP")]
    [InlineData("0BWMBO", "BO IF", "BWM")]
    [InlineData("", "DBI TEST", "DBI")]
    public void ExamsShowTheirSubjectLikeTheDashboard(string subject, string title, string expected) =>
        Assert.Equal(expected, ControlCenterView.ExamSubject(new PiExam(DateTime.Today, "08:00", "Test", title, subject)));
}
