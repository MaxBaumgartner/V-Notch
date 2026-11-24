using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using VNotch.Controls;
using VNotch.Services;
using VNotch.Services.ClaudeCode;
using VNotch.Services.ControlCenter;
using Xunit;
using static VNotch.Tests.HomeSchoolTabTests;

namespace VNotch.Tests;

/// <summary>This branch's parts on David's faces: the planner's readings on the home face, the eye on the Claude face.</summary>
[Collection("Localization")]
public sealed class PlannerFaceTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    private static PiHome Planner(string weather, string leave) => new([], [], null, [], [], [], new(null, null, []),
    [
        new("planner.weather", "Town", weather),
        new("planner.leave", "Leave", leave),
        new("planner.roblox1", "Some game", "3 playing")
    ]);

    [Fact]
    public void APlannersReadingsAreTilesOnTheHomeFaceAndChangeInPlace() => SharedStaTestRunner.RunAsync(async ct =>
    {
        string language = Loc.CurrentLanguage;
        Loc.SetLanguage("en");
        using var client = new PiDashboardClient(() => "http://127.0.0.1:1");
        var view = new ControlCenterView { Page = ControlCenterPage.Pi };
        SetField(view, "_piRefreshing", true);
        view.AttachPi(client, () => false, () => false, () => "", () => "");
        var window = new BackgroundWindow { Width = 608, Height = 330, Left = -20000, Top = -20000,
            Content = new Grid { Background = Brushes.Black, Children = { view } } };
        try
        {
            void Show(PiHome home)
            {
                SetField(view, "_home", home);
                Invoke(view, "BuildHome");
                window.UpdateLayout();
            }
            Show(Planner("4°", "07:12"));
            window.Show();
            window.UpdateLayout();
            await Task.Yield();

            var face = (Grid)Field(view, "_homeFace");
            TextBlock Text(string text) => Descendants<TextBlock>(face).Single(t => t.Text == text);
            var weather = Text("4°");
            foreach (string text in new[] { "Town", "Leave", "07:12", "Some game", "3 playing" }) Assert.NotNull(Text(text));
            var faceBounds = new Rect(face.RenderSize);
            foreach (var tile in Descendants<Border>(face).Where(b => b.Child is StackPanel { Children.Count: 2 }))
                Assert.True(faceBounds.Contains(tile.TransformToAncestor(face).TransformBounds(new Rect(tile.RenderSize))), "a tile leaves the face");
            CornerShapes.AssertNoneStretched(face);

            // New numbers, same readings: the face stays and the words change in place.
            Show(Planner("6°", "07:20"));
            Assert.Same(face, Field(view, "_homeFace"));
            Assert.Same(weather, Text("6°"));
            Assert.NotNull(Text("07:20"));
        }
        finally
        {
            window.Close();
            view.Dispose();
            Loc.SetLanguage(language);
        }
    });

    [Fact]
    public void SixReadingsFillTwoRowsInsideTheHomeFace() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var client = new PiDashboardClient(() => "http://127.0.0.1:1");
        var view = new ControlCenterView { Page = ControlCenterPage.Pi };
        SetField(view, "_piRefreshing", true);
        view.AttachPi(client, () => false, () => false, () => "", () => "");
        var window = new BackgroundWindow { Width = 608, Height = 330, Left = -20000, Top = -20000,
            Content = new Grid { Background = Brushes.Black, Children = { view } } };
        try
        {
            SetField(view, "_home", new PiHome([], [], null, [], [], [], new(null, null, []),
            [
                new("planner.weather", "Town", "-4°"), new("planner.feels", "Feels like -7°", "H:1° L:-4°"),
                new("planner.back", "Way home", "16:20"), new("planner.school", "School", "until 16:20"),
                new("planner.sun", "Sunset", "16:15"), new("planner.roblox", "Roblox", "22 visits")
            ]));
            Invoke(view, "BuildHome");
            window.Show();
            window.UpdateLayout();
            await Task.Yield();

            var face = (Grid)Field(view, "_homeFace");
            var bounds = new Rect(face.RenderSize);
            var labels = new[] { "Town", "Feels like -7°", "Way home", "School", "Sunset", "Roblox" };
            var tiles = Descendants<Border>(face)
                .Where(b => b.Child is StackPanel { Children.Count: 2 } panel && panel.Children[0] is TextBlock label && labels.Contains(label.Text))
                .ToList();
            Assert.Equal(6, tiles.Count);
            var places = tiles.Select(t => t.TransformToAncestor(face).TransformBounds(new Rect(t.RenderSize))).ToList();
            Assert.All(places, p => Assert.True(bounds.Contains(p), $"a tile at {p} leaves the face {bounds}"));
            Assert.Equal(2, places.Select(p => Math.Round(p.Top)).Distinct().Count());
            Assert.Equal(3, places.Select(p => Math.Round(p.Left)).Distinct().Count());
        }
        finally
        {
            window.Close();
            view.Dispose();
        }
    });

    [Fact]
    public void APlannersSchoolFillsDavidsSchoolTabInTheAppsLanguage() => SharedStaTestRunner.Run(() =>
    {
        var now = new DateTimeOffset(2026, 11, 24, 9, 0, 0, TimeSpan.FromHours(1));
        var timetable = (JsonArray)JsonNode.Parse("""
            [
              { "date": "2026-11-24", "start": "08:00", "end": "08:50", "subject": "0WMC", "name": "Web Apps", "room": "R101", "cancelled": false, "change": "" },
              { "date": "2026-11-24", "start": "08:55", "end": "09:45", "subject": "0AM", "name": "Mathe", "room": "R112", "cancelled": false, "change": "" },
              { "date": "2026-11-24", "start": "10:00", "end": "10:50", "subject": "0DBI", "name": "", "room": "R202", "cancelled": false, "change": "" }
            ]
            """)!;
        var events = (JsonArray)JsonNode.Parse("""
            [
              { "id": "m1", "title": "Abgabe: Lab 7", "sub": "WMC 1ABC", "date": "2026-11-26", "start": "23:00", "end": "23:59", "kind": "hausuebung", "repeat": "none" },
              { "id": "t1", "title": "Mathe", "date": "2026-11-28", "start": "08:00", "end": "08:50", "kind": "schularbeit", "repeat": "none" },
              { "id": "t2", "title": "DBI", "date": "2026-12-01", "start": "10:00", "end": "10:50", "kind": "test", "repeat": "none" }
            ]
            """)!;
        string language = Loc.CurrentLanguage;
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        Loc.SetLanguage("en");
        // Windows in German, the app in English: the tab follows the app.
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("de-DE");
        using var client = new PiDashboardClient(() => "http://127.0.0.1:1");
        using var view = new ControlCenterView { Page = ControlCenterPage.School };
        try
        {
            view.AttachPi(client, () => false, () => false, () => "", () => "");
            SetField(view, "_piRefreshing", true);
            SetField(view, "_schoolNow", (Func<DateTimeOffset>)(() => now));
            SetField(view, "_piSchool", PlannerDashboard.School(timetable, events, now.DateTime));
            Invoke(view, "BuildPiPage");
            view.SetDrawerOpen(true);
            var size = new Size(552, view.MeasureContentHeight(552));
            view.Arrange(new Rect(size));
            view.UpdateLayout();

            Assert.Equal("Lab 7", ((TextBlock)Field(view, "_schoolTitle")).Text);
            Assert.Equal("WMC", ((TextBlock)Field(view, "_schoolSubject")).Text);
            string meta = ((TextBlock)Field(view, "_schoolMeta")).Text;
            Assert.StartsWith("Moodle", meta);
            Assert.Contains("Thu", meta);
            Assert.DoesNotContain("Do.", meta);
            // Tests named after their subject get its short name and colour, not "MATHE".
            var chips = Descendants<TextBlock>((Grid)Field(view, "_schoolTestChips")).Select(x => x.Text).ToList();
            Assert.Contains("AM", chips);
            Assert.Contains("DBI", chips);
            Assert.DoesNotContain("MATHE", chips);
            // Today's lessons in the drawer carry their subject codes.
            var drawer = Descendants<TextBlock>((StackPanel)Field(view, "_drawerBody")).Select(x => x.Text).ToList();
            Assert.Contains("WMC", drawer);
            Assert.DoesNotContain("WEB", drawer);
        }
        finally
        {
            Loc.SetLanguage(language);
            System.Globalization.CultureInfo.CurrentCulture = culture;
        }
    });

    [Fact]
    public void TheEyeSitsInTheClaudeFacesCornerAndSwitchesTheNotch() => SharedStaTestRunner.Run(() =>
    {
        var view = new ControlCenterView { Page = ControlCenterPage.Claude };
        using var usage = new ClaudeUsageClient();
        void Set(string name, object value) => typeof(ControlCenterView).GetField(name, Private)!.SetValue(view, value);
        Set("_claude", new ClaudeSessionTracker());
        Set("_usage", usage);
        Set("_claudeInstalled", true);
        Set("_codexReading", true);
        Set("_isLocalOnly", (Func<bool>)(() => false));
        usage.Latest = new(new(32, null), new(46, null), Plan: "Pro");
        bool inNotch = true;
        view.AttachNotchSwitch(() => inNotch, on => inNotch = on);
        var window = new BackgroundWindow { Width = 628, Height = 500, Content = view, WindowStyle = WindowStyle.None, Left = -20000, Top = -20000 };
        try
        {
            window.Show();
            Invoke(view, "BuildClaudeSection");
            window.UpdateLayout();

            var face = (Grid)Field(view, "_claudeFace");
            var front = (Grid)Field(view, "_accountFront");
            var eye = (Button)Field(view, "_claudeEye");
            var glyph = (TextBlock)Field(view, "_claudeEyeGlyph");
            Assert.Same(front, eye.Parent);
            Rect Bounds(FrameworkElement element) => element.TransformToAncestor(face).TransformBounds(new Rect(element.RenderSize));
            var place = Bounds(eye);
            Assert.True(new Rect(face.RenderSize).Contains(place), $"eye at {place} leaves the face");
            foreach (var other in front.Children.OfType<FrameworkElement>().Where(c => c != eye && c.IsVisible && c.ActualWidth > 0 && c.ActualHeight > 1))
                Assert.False(Bounds(other).IntersectsWith(place), $"eye at {place} covers {other.GetType().Name} {Tag(other)} at {Bounds(other)}");
            CornerShapes.AssertNoneStretched(face);
            Assert.Equal("", glyph.Text);
            Assert.Equal(Loc.Get("control.claude.notchOn"), eye.ToolTip);

            eye.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.False(inNotch);
            Assert.Equal("", glyph.Text);
            Assert.Equal(Loc.Get("control.claude.notchOff"), eye.ToolTip);
            eye.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.True(inNotch);
            Assert.Same(eye, Field(view, "_claudeEye"));
        }
        finally
        {
            view.Dispose();
            window.Close();
        }

        static string Tag(FrameworkElement element) => element is TextBlock text ? $"'{text.Text}'" : element.Tag as string ?? "";
    });
}
