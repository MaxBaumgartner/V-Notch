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
            // The mode is switched on the face's tile now; the module keeps sensors, fans and the keyboard light.
            Assert.DoesNotContain(Descendants(section).OfType<Border>(), b => b.Child is TextBlock t && t.Text == Loc.Get("control.laptop.performance"));
            Assert.Contains(Descendants(section).OfType<TextBlock>(), t => t.Text == Loc.Get("control.laptop.sensors"));

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

    [Fact]
    public void WithoutALegionTheTileTakesTheCoverSpotAndAFirstTapOnlyAsks() => SharedStaTestRunner.RunAsync(async ct =>
    {
        BackgroundTestWindows.EnsureApplicationResources();
        using var view = SystemTabTests.CreateView(legion: false);
        var host = new BackgroundWindow { Width = 600, Height = 400, Left = -20000, Top = -20000,
            Content = new Border { Padding = new Thickness(16, 30, 16, 0), Child = view } };
        try
        {
            var snapshot = HomeSchoolTabTests.Field(view, "_snapshot");
            snapshot.GetType().GetField("Laptop")!.SetValue(snapshot, new LaptopStatus("balanced", false, 3100, 2900, 52, true, 4, null));
            snapshot.GetType().GetField("GpuTemp")!.SetValue(snapshot, 71);
            HomeSchoolTabTests.Invoke(view, "BuildCompactSystem", snapshot);
            host.Show();
            host.UpdateLayout();
            await Task.Yield();

            var face = (Grid)HomeSchoolTabTests.Field(view, "_faceGrid");
            var tile = (Border)HomeSchoolTabTests.Field(view, "_laptopTile");
            Assert.Same(face, tile.Parent);
            Assert.Equal(1, Grid.GetColumn(tile));
            Assert.Equal(Visibility.Visible, tile.Visibility);
            Assert.Same(tile, HomeSchoolTabTests.Field(view, "_modeTile"));
            Rect Bounds(FrameworkElement e) => e.TransformToAncestor(face).TransformBounds(new Rect(e.RenderSize));
            foreach (var column in face.Children.OfType<FrameworkElement>().Where(c => c != tile && c.IsVisible))
                Assert.False(Bounds(column).IntersectsWith(Bounds(tile)), $"{column.GetType().Name} at {Bounds(column)} covers the tile at {Bounds(tile)}");
            var readout = (TextBlock)HomeSchoolTabTests.Field(view, "_laptopTileReadout");
            Assert.Equal("52 °C · " + Loc.Get("control.laptop.fan", 3100.ToString("N0", Loc.GetCulture())), readout.Text);
            // CPU from the OMEN BIOS, GPU from the graphics driver, in the drawer's performance module.
            Assert.Equal("52 °C", ((TextBlock)HomeSchoolTabTests.Field(view, "_cpuSub")).Text);
            Assert.Equal("71 °C", ((TextBlock)HomeSchoolTabTests.Field(view, "_gpuSub")).Text);

            // Only one tap: a second one on the same slot would really switch this PC through the OMEN helper.
            var ring = (Border)HomeSchoolTabTests.Field(view, "_laptopTileRing");
            Assert.Equal(Visibility.Collapsed, ring.Visibility);
            HomeSchoolTabTests.Invoke(view, "TapLaptopMode", 2);
            Assert.Equal("performance", HomeSchoolTabTests.Field(view, "_laptopModeArmed"));
            Assert.Equal(Visibility.Visible, ring.Visibility);
            Assert.Equal(Loc.Get("control.laptop.tapAgain"), readout.Text);
            Assert.Equal(Loc.Get("control.laptop.performance") + "?", ((Grid)HomeSchoolTabTests.Field(view, "_laptopTileName")).Tag);
            // A tap on the mode it is in calls it off.
            HomeSchoolTabTests.Invoke(view, "TapLaptopMode", 1);
            Assert.Null(HomeSchoolTabTests.Field(view, "_laptopModeArmed"));
            Assert.Equal(Visibility.Collapsed, ring.Visibility);

            // The helper stops answering: the tile goes, a read without numbers keeps it.
            snapshot.GetType().GetField("Laptop")!.SetValue(snapshot, null);
            HomeSchoolTabTests.Invoke(view, "BuildCompactSystem", snapshot);
            Assert.Equal(Visibility.Collapsed, tile.Visibility);
        }
        finally
        {
            HomeSchoolTabTests.SetField(view, "_gpuSampling", false);
            host.Close();
        }
    });

    [Fact]
    public void ALegionKeepsItsOwnTile() => SharedStaTestRunner.Run(() =>
    {
        using var view = SystemTabTests.CreateView(legion: true);
        Assert.Null(typeof(ControlCenterView).GetField("_laptopTile", Private)!.GetValue(view));
    });

    [Theory]
    [InlineData(0, 0)]
    [InlineData(51, 0)]
    [InlineData(53, 1)]
    [InlineData(120, 2)]
    [InlineData(400, 2)]
    public void TapsMapToTheirSlot(double x, int slot) => Assert.Equal(slot, ControlCenterView.LaptopSlotAt(x));

    [Theory]
    [InlineData(654u, 65)]
    [InlineData(700u, 70)]
    [InlineData(0u, null)]
    [InlineData(2000u, null)]
    public void DriverTemperaturesAreTenthsOfADegree(uint tenths, int? celsius) => Assert.Equal(celsius, GpuTemperature.Celsius(tenths));

    [Fact]
    public void TheTileReadsTheFasterFanAndLeavesOutWhatIsMissing() => SharedStaTestRunner.Run(() =>
    {
        string fan = Loc.Get("control.laptop.fan", 2900.ToString("N0", Loc.GetCulture()));
        Assert.Equal("48 °C · " + fan, ControlCenterView.TileReadout(new LaptopStatus("eco", null, 2100, 2900, 48, null, 0, null)));
        Assert.Equal(fan, ControlCenterView.TileReadout(new LaptopStatus("eco", null, null, 2900, null, null, 0, null)));
        Assert.Equal("", ControlCenterView.TileReadout(new LaptopStatus("eco", null, null, null, null, null, 0, null)));
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
