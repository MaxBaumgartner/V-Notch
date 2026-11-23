using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VNotch.Services;
using VNotch.Controls;
using VNotch.Services.ControlCenter;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
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
    public void AGreenStripStaysCalmAndMusicSyncTakesTheCoversColour()
    {
        var lamp = new HomeLight("a", "A", false, true, true, null, null);
        var strip = new HomeLight("s", "Strip", true, true, true, Color.FromRgb(0, 255, 0), 100);
        var green = ControlCenterView.HomeMood(new[] { lamp, strip }).Main;
        Assert.True(green.G > green.R && green.G > green.B, "still a hint of green");
        Assert.True(green.G - Math.Min(green.R, green.B) < 80, $"muted, not loud: {green}");

        var cover = new[] { Color.FromRgb(200, 40, 40), Color.FromRgb(30, 30, 30) };
        var synced = ControlCenterView.AsShown(strip, musicSync: true, cover);
        Assert.Equal(cover[0], synced.Color);
        Assert.True(ControlCenterView.HomeMood(new[] { lamp, synced }).Main.R > ControlCenterView.HomeMood(new[] { lamp, synced }).Main.G, "reads as the cover's red");
        Assert.Equal(strip, ControlCenterView.AsShown(strip, musicSync: true, Array.Empty<Color>()));  // nothing playing: the picked colour
        Assert.Equal(strip, ControlCenterView.AsShown(strip, musicSync: false, cover));
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

    [Fact]
    public void GermanThermostatUsesTheLocale() => SharedStaTestRunner.Run(() =>
    {
        string previous = Loc.CurrentLanguage;
        Loc.SetLanguage("de");
        try
        {
            using var view = new ControlCenterView { Page = ControlCenterPage.Pi };
            using var client = new PiDashboardClient(() => "http://127.0.0.1:1");
            SetField(view, "_piRefreshing", true);
            view.AttachPi(client, () => false, () => false, () => "", () => "");
            var home = PiDashboardClient.ParseHome((JsonObject)JsonNode.Parse(HomeJson)!);
            home = home with { Lights = home.Lights.Select(light => light with { Brightness = null }).ToArray() };
            SetField(view, "_home", home);
            Invoke(view, "BuildHome");
            view.Measure(new Size(520, double.PositiveInfinity));
            view.Arrange(new Rect(0, 0, 520, 120));
            view.UpdateLayout();
            var labels = Descendants<TextBlock>(view).Select(text => text.Text).ToArray();
            Assert.Contains("heizt", labels);
            Assert.Contains("Ziel", labels);
            Assert.DoesNotContain("Heating", labels);
            Assert.DoesNotContain("Target", labels);
        }
        finally { Loc.SetLanguage(previous); }
    });

    [Fact]
    public void HomeIsCompactAndProtectedSocketsAskInlineBeforeSending() => SharedStaTestRunner.Run(() =>
    {
        bool before = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(true);
        using var client = new PiDashboardClient(() => "http://127.0.0.1:1");
        using var view = new ControlCenterView { Page = ControlCenterPage.Pi };
        try
        {
            SetField(view, "_piRefreshing", true);
            view.AttachPi(client, () => false, () => false, () => "", () => "");
            var home = PiDashboardClient.ParseHome((JsonObject)JsonNode.Parse(HomeJson)!);
            SetField(view, "_home", home);
            Invoke(view, "BuildHome");
            view.Measure(new Size(520, double.PositiveInfinity)); view.Arrange(new Rect(0, 0, 520, 120)); view.UpdateLayout();
            var compact = (StackPanel)Field(view, "_homeLightSection");
            var labels = Descendants<TextBlock>(compact).Select(t => t.Text).ToList();
            Assert.Contains("Gute Nacht", labels);
            Assert.Contains(labels, label => label is "Farbe" or "Colour");
            Assert.Contains(labels, label => label is "Musik" or "Music");
            Assert.False(view.DrawerOpen);
            view.SetDrawerOpen(true);
            var section = (StackPanel)Field(view, "_homePlugSection");
            var row = section.Children.OfType<Border>().Single(b => Equals(b.Tag, "drawer-row"));
            row.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
            Assert.Equal("switch.pc", Field(view, "_guardArmed"));
            Assert.Contains(Descendants<TextBlock>(section), t => t.Text is "Wirklich aus?" or "Turn off?");
            Assert.Contains(Descendants<TextBlock>(section), t => t.Text is "Nein" or "Cancel");
            Assert.True(view.DrawerOpen);
        }
        finally { AnimationConfig.SetReduceMotion(before); }
    });

    [Fact]
    public void DrawerFollowsThePointerAndRemembersItsStateAcrossRebuilds() => SharedStaTestRunner.Run(() =>
    {
        bool before = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(true);
        using var client = new PiDashboardClient(() => "http://127.0.0.1:1");
        using var view = new ControlCenterView { Page = ControlCenterPage.Pi };
        try
        {
            SetField(view, "_piRefreshing", true);
            view.AttachPi(client, () => false, () => false, () => "", () => "");
            SetField(view, "_home", PiDashboardClient.ParseHome((JsonObject)JsonNode.Parse(HomeJson)!));
            Invoke(view, "BuildHome");
            Assert.True(view.MoveDrawer(24)); Assert.Equal(24, view.DrawerHeight, 5);
            Assert.True(view.ReleaseDrawer(400, false)); Assert.True(view.DrawerOpen);
            SetField(view, "_homeKey", ""); Invoke(view, "BuildHome"); Assert.True(view.DrawerOpen);
            double height = view.DrawerHeight;
            Assert.True(view.MoveDrawer(-20)); Assert.Equal(height - 20, view.DrawerHeight, 5);
            view.ReleaseDrawer(0, true); Assert.True(view.DrawerOpen);
            view.MoveDrawer(-50); view.ReleaseDrawer(-400, false); Assert.False(view.DrawerOpen);
        }
        finally { AnimationConfig.SetReduceMotion(before); }
    });

    [Fact]
    public void SchoolReadsEveryDaysLessonsAndKeepsOneSubjectColour()
    {
        var school = PiDashboardClient.ParseSchool((JsonObject)JsonNode.Parse("""
        {"date":"2026-11-16","timetable":[],"days":{
          "2026-11-16":[{"startTime":"08:00","endTime":"08:50","subject":"Mathematik","subjectShort":"0AM","room":"E58","startMinutes":480,"endMinutes":530}],
          "2026-11-17":[{"startTime":"09:00","endTime":"09:50","subject":"OO-PR","subjectShort":"0POSETHI","room":"E12","cancelled":true}],
          "invalid":[{}]}}
        """)!);
        Assert.Equal(2, school.Days.Count);
        Assert.Equal("E58", school.Days["2026-11-16"][0].Room);
        Assert.Equal("0AM", school.Days["2026-11-16"][0].SubjectKey);
        Assert.True(school.Days["2026-11-17"][0].Cancelled);
        Assert.Equal(ControlCenterView.SubjectColor("AM"), ControlCenterView.SubjectColor("0AM"));
        Assert.Equal(ControlCenterView.SubjectColor("Mathematik"), ControlCenterView.SubjectColor("0AM"));
        Assert.Equal(ControlCenterView.SubjectColor("POSE"), ControlCenterView.SubjectColor("0POSETHI"));
        Assert.Equal(ControlCenterView.SubjectColor("unknown subject"), ControlCenterView.SubjectColor("UNKNOWN SUBJECT"));
        var unknown = ControlCenterView.SubjectColor("unknown subject");
        Assert.InRange(Math.Max(unknown.R, Math.Max(unknown.G, unknown.B)) - Math.Min(unknown.R, Math.Min(unknown.G, unknown.B)), 0, 90);
    }

    [Fact]
    public void HomeKeepsOnlyNonGuardSocketsAndPlacesTheColourPickerOnTheFace() => SharedStaTestRunner.Run(() =>
    {
        bool before = AnimationConfig.ReduceMotion; AnimationConfig.SetReduceMotion(true);
        using var client = new PiDashboardClient(() => "http://127.0.0.1:1");
        using var view = new ControlCenterView { Page = ControlCenterPage.Pi };
        try
        {
            SetField(view, "_piRefreshing", true);
            view.AttachPi(client, () => false, () => false, () => "", () => "");
            var home = PiDashboardClient.ParseHome((JsonObject)JsonNode.Parse(HomeJson)!);
            var extra = new HomePlug("microphone", "Desk microphone", true, true, false, 5, null);
            home = home with { Plugs = home.Plugs.Append(extra).ToList() };
            SetField(view, "_home", home); SetField(view, "_musicSync", false); Invoke(view, "BuildHome");
            view.Measure(new Size(528, 400)); view.Arrange(new Rect(0, 0, 528, 400)); view.UpdateLayout();
            var compact = (StackPanel)Field(view, "_homeLightSection");
            Assert.Contains(Descendants<TextBlock>(compact), t => t.Text == extra.Name);
            Assert.DoesNotContain(Descendants<TextBlock>(compact), t => t.Text == "PC");
            var hue = Descendants<Border>(compact).Single(b => b.ToolTip is "Farbe wählen" or "Choose strip colour");
            Assert.True(hue.IsEnabled);
            Assert.DoesNotContain(Descendants<TextBlock>((StackPanel)Field(view, "_homeClimateSection")),
                label => label.Text is "Musik" or "Music");
            home = home with { Plugs = home.Plugs.Select(p => p.Id == extra.Id ? p with { Guard = true } : p).ToList() };
            SetField(view, "_home", home); Invoke(view, "BuildHome");
            Assert.DoesNotContain(Descendants<TextBlock>(compact), t => t.Text == extra.Name);
        }
        finally { AnimationConfig.SetReduceMotion(before); }
    });

    [Fact]
    public void RebuildingAMovingDrawerStopsThePreviousHeightClock() => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool before = AnimationConfig.ReduceMotion;
        bool? animations = AnimationConfig.ClientAreaAnimationOverride;
        AnimationConfig.ClientAreaAnimationOverride = true; AnimationConfig.SetReduceMotion(false);
        BackgroundTestWindows.EnsureApplicationResources();
        using var client = new PiDashboardClient(() => "http://127.0.0.1:1");
        using var view = new ControlCenterView { Page = ControlCenterPage.Pi };
        var window = new Window { Content = view, Width = 560, Height = 420, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            SetField(view, "_piRefreshing", true);
            view.AttachPi(client, () => false, () => false, () => "", () => "");
            SetField(view, "_home", PiDashboardClient.ParseHome((JsonObject)JsonNode.Parse(HomeJson)!));
            Invoke(view, "BuildHome"); window.Show(); view.SetDrawerOpen(true);
            var previous = (WeatherTween)Field(view, "_drawerHeight");
            await WpfFrameWaiter.UntilAsync(() => previous.Value > 5, "drawer starts opening", ct);
            Assert.True(Motion.IsRunning(previous, WeatherTween.ValueProperty));
            Invoke(view, "RedrawHome");
            Assert.False(Motion.IsRunning(previous, WeatherTween.ValueProperty));
            Assert.False(previous.HasAnimatedProperties);
            Assert.False(((WeatherTween)Field(view, "_drawerHeight")).HasAnimatedProperties);
            Assert.True(view.DrawerOpen);
            Assert.False(view.IsLayoutFollowing);
            double height = view.DrawerHeight;
            await WpfFrameWaiter.NextAsync(ct);
            Assert.Equal(height, view.DrawerHeight, 6);
        }
        finally { window.Close(); AnimationConfig.ClientAreaAnimationOverride = animations; AnimationConfig.SetReduceMotion(before); }
    });

    [Fact]
    public void HiddenDrawerChangesCommitWithoutStartingAClock() => SharedStaTestRunner.Run(() =>
    {
        bool before = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(false);
        using var view = new ControlCenterView { Page = ControlCenterPage.School };
        try
        {
            Invoke(view, "MountCompactPage");
            Invoke(view, "AddDrawer", new Border { Height = 100 }, false);
            view.SetDrawerOpen(true);
            var height = (WeatherTween)Field(view, "_drawerHeight");
            Assert.True(view.DrawerHeight >= 100);
            Assert.False(Motion.IsRunning(height, WeatherTween.ValueProperty));
            Assert.False(view.IsLayoutFollowing);
            view.SetDrawerOpen(false);
            Assert.Equal(0, view.DrawerHeight);
            Assert.False(height.HasAnimatedProperties);
        }
        finally { AnimationConfig.SetReduceMotion(before); }
    });

    internal static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    internal static object Field(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(target)!;
    internal static void SetField(object target, string name, object value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    internal static object? Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
}
