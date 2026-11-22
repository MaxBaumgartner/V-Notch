using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VNotch.Controls;
using VNotch.Services;
using VNotch.Services.ControlCenter;
using Xunit;

namespace VNotch.Tests;

/// <summary>The notch never switches the laptop's performance mode on one tap: the first only asks.</summary>
[Collection("Localization")]
public sealed class LaptopModeGuardTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    // Only the first tap is pressed here: a second one would really switch this PC through the OMEN helper.
    [Fact]
    public void AFirstTapOnAnotherModeOnlyAsks() => SharedStaTestRunner.RunAsync(ct =>
    {
        var view = new ControlCenterView { Page = ControlCenterPage.System };
        var type = typeof(ControlCenterView);
        ((Dictionary<string, bool>)type.GetField("_expanded", Private)!.GetValue(view)!)["laptop"] = true;
        var status = new LaptopStatus("balanced", false, 3100, 2900, 52, null, 0, null);
        type.GetMethod("BuildLaptopSectionIfChanged", Private)!.Invoke(view, [status]);
        var section = (StackPanel)type.GetField("_laptopSection", Private)!.GetValue(view)!;

        Press(Button(section, Loc.Get("control.laptop.performance")));

        Assert.Equal("performance", type.GetField("_laptopModeArmed", Private)!.GetValue(view));
        Assert.NotNull(Button(section, Loc.Get("home.sure")));
        // The mode the laptop is in does nothing at all.
        Press(Button(section, Loc.Get("control.laptop.balanced")));
        Assert.Equal("performance", type.GetField("_laptopModeArmed", Private)!.GetValue(view));
        return Task.CompletedTask;
    });

    [Fact]
    public void TheSectionIsAModuleOfTheSystemDrawerWhileTheHelperAnswers() => SharedStaTestRunner.RunAsync(async ct =>
    {
        BackgroundTestWindows.EnsureApplicationResources();
        using var view = SystemTabTests.CreateView(legion: false);
        var host = new BackgroundWindow { Width = 600, Height = 760, Left = -20000, Top = -20000,
            Content = new Border { Padding = new Thickness(16, 30, 16, 0), Child = view } };
        try
        {
            var snapshot = HomeSchoolTabTests.Field(view, "_snapshot");
            void Set(string name, object? value) => snapshot.GetType().GetField(name)!.SetValue(snapshot, value);
            void Show() => HomeSchoolTabTests.Invoke(view, "BuildCompactSystem", snapshot);
            Set("Laptop", new LaptopStatus("balanced", false, 3100, 2900, 52, true, 4, null));
            Show();
            host.Show();
            view.SetDrawerOpen(true);
            host.UpdateLayout();

            var main = (StackPanel)HomeSchoolTabTests.Field(view, "_drawerMain");
            var module = (Border)HomeSchoolTabTests.Field(view, "_laptopModule");
            var section = (StackPanel)HomeSchoolTabTests.Field(view, "_laptopSection");
            Assert.Same(section, module.Child);
            Assert.Equal(main.Children.IndexOf((UIElement)HomeSchoolTabTests.Field(view, "_footer")) - 1, main.Children.IndexOf(module));
            Assert.Equal(Visibility.Visible, module.Visibility);
            var bounds = new Rect(module.RenderSize);
            foreach (string key in new[] { "eco", "balanced", "performance" })
            {
                var button = Button(section, Loc.Get("control.laptop." + key));
                var place = button.TransformToAncestor(module).TransformBounds(new Rect(button.RenderSize));
                Assert.True(place.Width > 0 && bounds.Contains(place), $"{key} is cut off: {place} in {bounds}");
            }

            async Task DrawerFits()
            {
                double have = 0, want = 1;
                for (int i = 0; i < 60 && Math.Abs(have - want) > 1; i++)
                {
                    await Task.Delay(50, ct);
                    have = ((FrameworkElement)HomeSchoolTabTests.Field(view, "_drawerClip")).Height;
                    want = (double)HomeSchoolTabTests.Invoke(view, "MeasureDrawer")!;
                }
                Assert.InRange(have, want - 1, want + 1);
            }

            await DrawerFits();
            ((Dictionary<string, bool>)HomeSchoolTabTests.Field(view, "_expanded"))["laptop"] = false;
            Show();
            await DrawerFits();

            // Read without numbers (tab hidden): stays. A read without an answer from the helper: gone.
            var stats = snapshot.GetType().GetField("Stats")!.GetValue(snapshot);
            Set("Laptop", null);
            Set("Stats", null);
            Show();
            Assert.Equal(Visibility.Visible, module.Visibility);
            Set("Stats", stats);
            Show();
            Assert.Equal(Visibility.Collapsed, module.Visibility);
            await DrawerFits();
        }
        finally
        {
            HomeSchoolTabTests.SetField(view, "_gpuSampling", false);
            host.Close();
        }
    });

    private static Border Button(DependencyObject root, string text)
    {
        foreach (var border in Descendants(root).OfType<Border>())
            if (border.Child is TextBlock label && label.Text == text) return border;
        throw new Xunit.Sdk.XunitException($"no button '{text}'");
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject node) continue;
            yield return node;
            foreach (var deeper in Descendants(node)) yield return deeper;
        }
    }

    private static void Press(UIElement button) =>
        button.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
}
