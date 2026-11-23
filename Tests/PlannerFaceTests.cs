using System.Reflection;
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
