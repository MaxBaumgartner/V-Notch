using System.Text.Json.Nodes;
using VNotch.Controls;
using VNotch.Services.ControlCenter;
using Xunit;

namespace VNotch.Tests;

/// <summary>What the OMEN helper answers, as the Laptop section of the controls tab reads it.</summary>
public class LaptopControlTests
{
    private static JsonObject Json(string text) => (JsonObject)JsonNode.Parse(text)!;

    [Fact]
    public void AFullAnswerBecomesTheLaptopsState()
    {
        var status = LaptopControl.Parse(Json("""
            { "ok": true, "mode": "performance", "maxFan": false, "fans": [3100, 2900], "temp": 52,
              "keyboard": true, "zones": 4, "color": "#FF3B30" }
            """));

        Assert.Equal(new LaptopStatus("performance", false, 3100, 2900, 52, true, 4, "#FF3B30"), status);
    }

    [Fact]
    public void WhatTheBiosDidNotSayStaysUnknown()
    {
        var status = LaptopControl.Parse(Json("""{ "ok": true, "fans": [3100] }"""))!;

        Assert.Equal(new LaptopStatus(null, null, null, null, null, null, 0, null), status);
    }

    [Theory]
    [InlineData("""{ "ok": false, "error": "unknown command" }""")]
    [InlineData("""{ "mode": "eco" }""")]
    public void ARefusalIsNoState(string reply) => Assert.Null(LaptopControl.Parse(Json(reply)));

    [Fact]
    public void TheReadingsLineLeavesOutWhatIsMissing()
    {
        Assert.StartsWith("52 °C · ", ControlCenterView.Readings(new LaptopStatus(null, null, 3100, 2900, 52, null, 0, null)));
        Assert.Equal("52 °C", ControlCenterView.Readings(new LaptopStatus(null, null, null, null, 52, null, 0, null)));
        Assert.Contains("2", ControlCenterView.Readings(new LaptopStatus(null, null, 3100, 2900, null, null, 0, null)));
    }
}
