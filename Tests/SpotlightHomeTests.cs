using VNotch.Models;
using VNotch.Services.Spotlight;
using Xunit;

namespace VNotch.Tests;

public sealed class SpotlightHomeTests
{
    private static SpotlightSearchItem App(string title, string target, SpotlightResultKind kind = SpotlightResultKind.Application) =>
        new($"app:{target}", kind, title, "Application", target, target);

    [Fact]
    public void SpotlightPinsComeFirstThenTaskbarPinsThenRecentApps()
    {
        var apps = SpotlightHome.BuildApps(
            [App("Spotify", @"C:\s.lnk")],
            [App("Google Chrome", @"C:\tb\chrome.lnk"), App("File Explorer", @"C:\tb\explorer.lnk")],
            [App("Discord", @"C:\d.lnk")]);
        Assert.Equal(["Spotify", "Google Chrome", "File Explorer", "Discord"], apps.Select(a => a.Title));
    }

    [Fact]
    public void SameAppFromTwoPlacesShowsOnce()
    {
        var apps = SpotlightHome.BuildApps(
            [],
            [App("Google Chrome", @"C:\tb\chrome.lnk")],
            [App("google chrome", @"C:\start\chrome.lnk"), App("Steam", @"C:\tb\chrome.lnk")]);
        Assert.Equal(["Google Chrome"], apps.Select(a => a.Title));
    }

    [Fact]
    public void RecentFilesAndSettingsAreNotApps()
    {
        var apps = SpotlightHome.BuildApps([], [],
            [App("notes.txt", @"C:\notes.txt", SpotlightResultKind.File), App("Bluetooth", "ms-settings:bluetooth", SpotlightResultKind.Settings), App("Steam", @"C:\steam.lnk")]);
        Assert.Equal(["Steam"], apps.Select(a => a.Title));
    }

    [Fact]
    public void ListStopsAtTheMaximum()
    {
        var many = Enumerable.Range(0, 30).Select(i => App($"App {i}", $@"C:\{i}.lnk"));
        Assert.Equal(SpotlightHome.MaxApps, SpotlightHome.BuildApps([], many, []).Count);
    }

    [Theory]
    [InlineData("Tailscale: Connected. Click for options.", "Tailscale", true)]
    [InlineData("Wallpaper Engine", "Wallpaper Engine", true)]
    [InlineData("Steam", "steam", true)]
    [InlineData("Spotify", "Spotify Music", true)]
    [InlineData("Steam", "Spotify", false)]
    [InlineData("Go", "Google Drive", false)]
    [InlineData("Discord", "", false)]
    public void TrayNamesMatchProgramLabels(string trayName, string label, bool expected) =>
        Assert.Equal(expected, TrayIconBridge.NameMatches(trayName, label));

    [Fact]
    public void TrayRowLeavesOutVNotchItself()
    {
        Assert.False(SpotlightHome.ShowInTrayRow("V-Notch"));
        Assert.True(SpotlightHome.ShowInTrayRow("Steam"));
    }

    [Fact]
    public void TooltipKeepsOnlyItsFirstLine() =>
        Assert.Equal("Elgato Wave Link", TrayIconBridge.FirstLine("Elgato Wave Link\nMikrofon aktiv"));

    [Fact]
    public void UserAssistNamesDecodeWithRot13() =>
        Assert.Equal(@"{9E3995AB}\TaskBar\Google Chrome.lnk", SpotlightHome.Rot13(@"{9R3995NO}\GnfxOne\Tbbtyr Puebzr.yax"));

    [Theory]
    [InlineData("Tailscale: Connected. Click for options.", "Tailscale", "Tailscale")]
    [InlineData("Wallpaper Engine", "Wallpaper Engine", "Wallpaper Engine")]
    [InlineData("Spotify", "Spotify Music", "Spotify")]
    [InlineData("RustDesk: service running", "rustdesk", "RustDesk")]
    public void TrayChipsShowTheProgramName(string trayName, string label, string expected) =>
        Assert.Equal(expected, TrayIconBridge.DisplayName(trayName, label));
}
