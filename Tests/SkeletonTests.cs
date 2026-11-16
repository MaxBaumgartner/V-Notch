using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VNotch.Controls;
using VNotch.Services;
using VNotch.Services.ControlCenter;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class SkeletonTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(ControlCenterPage.Pi, "BuildHome", "_homeLightSection")]
    [InlineData(ControlCenterPage.School, "BuildPiSchool", "_piSchoolSection")]
    public void PiPagesUseSkeletonsUntilDataArrivesAndKeepActionableHints(ControlCenterPage page, string builder, string sectionField)
        => SharedStaTestRunner.Run(() =>
    {
        string address = "http://127.0.0.1";
        bool localOnly = false;
        using var client = new PiDashboardClient(() => address);
        using var view = new ControlCenterView { Page = page };
        view.AttachPi(client, () => localOnly, () => false, () => "", () => "");
        var section = (StackPanel)typeof(ControlCenterView).GetField(sectionField, Private)!.GetValue(view)!;
        void Build() => typeof(ControlCenterView).GetMethod(builder, Private)!.Invoke(view, null);

        Build();
        var loading = Assert.IsType<SkeletonPlaceholder>(Assert.Single(section.Children.Cast<UIElement>()));
        Assert.Equal(Loc.Get("pi.loading"), loading.ToolTip);

        SetField(view, "_piReachable", false);
        Build();
        var unreachable = Assert.IsType<SkeletonPlaceholder>(Assert.Single(section.Children.Cast<UIElement>()));
        Assert.Equal(Loc.Get("pi.unreachable"), unreachable.ToolTip);
        view.RevealRows(TimeSpan.Zero);
        Assert.Equal(0.65, unreachable.Opacity);
        Assert.False(unreachable.IsAnimating);

        localOnly = true;
        Build();
        Assert.Empty(section.Children.OfType<SkeletonPlaceholder>());
        Assert.Contains(Descendants(section).OfType<TextBlock>(), text => text.Text == Loc.Get("pi.localOnly"));

        localOnly = false;
        address = "";
        Build();
        Assert.Empty(section.Children.OfType<SkeletonPlaceholder>());
        Assert.Contains(Descendants(section).OfType<TextBlock>(), text => text.Text == Loc.Get("pi.notConfigured"));

        address = "http://127.0.0.1";
        SetField(view, "_piReachable", true);
        Build();
        Assert.Single(section.Children.OfType<SkeletonPlaceholder>());
        if (page == ControlCenterPage.Pi)
            SetField(view, "_home", PiDashboardClient.ParseHome(new JsonObject()));
        else
            SetField(view, "_piSchool", new PiSchool("2026-11-14", true, Array.Empty<PiLesson>(),
                new[] { new PiDeadline("Maths", "Maths", DateTimeOffset.Now.AddDays(3), false) }, Array.Empty<PiExam>(), null));
        Build();
        Assert.Empty(section.Children.OfType<SkeletonPlaceholder>());
        Assert.NotEmpty(section.Children.Cast<UIElement>());
    });

    [Fact]
    public void LegionUsesSkeletonForLoadingAndTextForSetup() => SharedStaTestRunner.Run(() =>
    {
        using var view = new ControlCenterView { Page = ControlCenterPage.System };
        view.AttachLegion();
        SetField(view, "_legionBusy", true);
        var section = (StackPanel)typeof(ControlCenterView).GetField("_legionModeSection", Private)!.GetValue(view)!;
        void Build() => typeof(ControlCenterView).GetMethod("BuildLegionPage", Private)!.Invoke(view, null);

        Build();
        Assert.Single(section.Children.OfType<SkeletonPlaceholder>());
        SetField(view, "_legionReachable", false);
        Build();
        Assert.Empty(section.Children.OfType<SkeletonPlaceholder>());
        Assert.Contains(Descendants(section).OfType<TextBlock>(), text => text.Text == Loc.Get("legion.setupHint"));
    });

    [Fact]
    public void BuildsRequestedBlocksWithoutStartingAnUnattachedAnimation() => SharedStaTestRunner.Run(() =>
    {
        var skeleton = new SkeletonPlaceholder(false, "Loading", 72, 36, 36, 28);

        Assert.Equal(4, skeleton.Children.Count);
        var blocks = skeleton.Children.Cast<Border>().ToArray();
        Assert.Equal(new double[] { 72, 36, 36, 28 }, blocks.Select(block => block.Height));
        Assert.All(blocks, block => Assert.Same(blocks[0].Background, block.Background));
        Assert.False(skeleton.IsVisible);
        Assert.False(skeleton.IsAnimating);
    });

    [Fact]
    public void ShimmerStopsWhenHiddenReducedOrRemoved() => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(false);
        var skeleton = new SkeletonPlaceholder(false, "Loading", 72, 36, 36);
        var surface = new Grid();
        surface.Children.Add(skeleton);
        var host = new BackgroundWindow { Content = surface, Width = 360, Height = 260 };
        try
        {
            host.Show();
            await WpfFrameWaiter.UntilAsync(() => skeleton.IsLoaded && skeleton.IsAnimating, "visible skeleton shimmer starts", ct);

            skeleton.Visibility = Visibility.Collapsed;
            Assert.False(skeleton.IsAnimating);
            skeleton.Visibility = Visibility.Visible;
            await WpfFrameWaiter.UntilAsync(() => skeleton.IsAnimating, "visible skeleton shimmer resumes", ct);

            AnimationConfig.SetReduceMotion(true);
            Assert.False(skeleton.IsAnimating);
            AnimationConfig.SetReduceMotion(false);
            Assert.True(skeleton.IsAnimating);

            surface.Children.Remove(skeleton);
            Assert.False(skeleton.IsAnimating);
            await WpfFrameWaiter.UntilAsync(() => !skeleton.IsLoaded && !skeleton.IsAnimating, "removed skeleton releases its clock", ct);
            AnimationConfig.SetReduceMotion(true);
            AnimationConfig.SetReduceMotion(false);
            Assert.False(skeleton.IsAnimating);
            surface.Children.Add(skeleton);
            await WpfFrameWaiter.UntilAsync(() => skeleton.IsAnimating, "reattached skeleton resumes", ct);
            skeleton.Dispose();
            Assert.False(skeleton.IsAnimating);
            AnimationConfig.SetReduceMotion(true);
            AnimationConfig.SetReduceMotion(false);
            Assert.False(skeleton.IsAnimating);
        }
        finally
        {
            host.Close();
            AnimationConfig.SetReduceMotion(previous);
        }
    });

    [Fact]
    public void UnreachableSkeletonRemainsStaticAndKeepsTheReason() => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(false);
        var skeleton = new SkeletonPlaceholder(true, "Pi nicht erreichbar", 72, 36, 36);
        var host = new BackgroundWindow { Content = skeleton, Width = 360, Height = 260 };
        try
        {
            host.Show();
            await WpfFrameWaiter.UntilAsync(() => skeleton.IsLoaded, "unreachable skeleton loads", ct);
            Assert.True(skeleton.IsVisible);
            Assert.False(skeleton.IsAnimating);
            Assert.Equal("Pi nicht erreichbar", skeleton.ToolTip);
            AnimationConfig.SetReduceMotion(true);
            AnimationConfig.SetReduceMotion(false);
            Assert.False(skeleton.IsAnimating);
        }
        finally
        {
            host.Close();
            AnimationConfig.SetReduceMotion(previous);
        }
    });

    private static void SetField(ControlCenterView view, string name, object value)
        => typeof(ControlCenterView).GetField(name, Private)!.SetValue(view, value);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
