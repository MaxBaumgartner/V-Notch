using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Controllers;
using VNotch.Models;
using VNotch.Presenters;
using VNotch.Services;
using VNotch.Tests.Fakes;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class ShellGestureTests
{
    [Fact]
    public void EmptySurfacesIncludingTheHeaderIgnoreTheShellsInheritedHandCursor() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture();
        var w = fixture.Window;
        foreach (FrameworkElement surface in new FrameworkElement[] { w.PiCenter, w.SchoolCenter, w.ControlCenter,
            w.ClaudeCenter, w.TimerContent, w.ExpandedContent, w.CalendarInnerContent,
            w.SecondaryContent, w.CameraContent, w.AudioContent, w.NavIconsPanel })
            Assert.False(w.IsShellDragControl(surface));
        Assert.True(w.IsShellDragControl(new Button()));
        Assert.True(w.IsShellDragControl(new Slider()));
        Assert.True(w.IsShellDragControl(new TextBox()));
        var scroll = new ScrollViewer { Height = 60, Width = 100, Content = new Border { Height = 200 } };
        scroll.Measure(new Size(100, 60)); scroll.Arrange(new Rect(0, 0, 100, 60)); scroll.UpdateLayout();
        Assert.False(w.IsShellDragControl(scroll));
        Assert.Same(scroll, w.FindShellScrollCandidate(scroll));
        Assert.True(MainWindow.ShouldClaimScrollDrag(scroll, new Point(0, -8)));
        Assert.False(MainWindow.ShouldClaimScrollDrag(scroll, new Point(0, 8)));
        Assert.False(MainWindow.ShouldClaimScrollDrag(scroll, new Point(12, -8)));
        scroll.ScrollToVerticalOffset(40); scroll.UpdateLayout();
        Assert.True(w.IsShellDragControl(scroll));
        Assert.Null(w.FindShellScrollCandidate(scroll));
        var shortList = new ListBox { Width = 100, Height = 100,
            Template = new ControlTemplate(typeof(ListBox)) { VisualTree = new FrameworkElementFactory(typeof(ScrollViewer)) } };
        shortList.ApplyTemplate(); shortList.Measure(new Size(100, 100));
        shortList.Arrange(new Rect(0, 0, 100, 100)); shortList.UpdateLayout();
        var listScroll = HomeSchoolTabTests.Descendants<ScrollViewer>(shortList).First();
        Assert.Equal(0, listScroll.ScrollableHeight);
        Assert.False(w.IsShellDragControl(listScroll));
        Assert.Same(listScroll, w.FindShellScrollCandidate(listScroll));
        Assert.True(MainWindow.ShouldClaimScrollDrag(listScroll, new Point(0, -8)));
        await Task.CompletedTask;
    });

    [Fact]
    public void AttachmentUsesShellProgressAndRetainsItsFrameWhenReattached() => SharedStaTestRunner.Run(() =>
    {
        var shell = new Border { Width = 300, Height = 40 };
        var page = new Grid();
        var compact = new Grid();
        using var attachment = new NotchShellAttachment(shell, page, () => 40, compact);
        attachment.Attach(600, 240);
        shell.Width = 450;
        shell.Height = 90;
        var scale = Assert.IsType<ScaleTransform>(page.RenderTransform);
        Assert.Equal(.75, scale.ScaleX, 6);
        Assert.Equal(.375, scale.ScaleY, 6);
        Assert.Equal(.374, page.Opacity, 6);
        Assert.Equal(0, compact.Opacity);
        attachment.Attach(450, 90);
        Assert.Equal(600, page.Width);
        Assert.Equal(.75, scale.ScaleX);
        Assert.Equal(.374, page.Opacity, 6);
        attachment.Detach(true);
        Assert.True(double.IsNaN(page.Width));
        Assert.Equal(1, scale.ScaleX);
        Assert.Equal(1, page.Opacity);
        shell.Height = 40;
        Assert.Equal(1, page.Opacity);
    });

    [Theory]
    [InlineData(30, 200, false)]
    [InlineData(50, 200, true)]
    [InlineData(30, 40, true)]
    public void ClosedPullFollowsPointerAndCommitsByDistanceOrVelocity(double distance, int milliseconds, bool opens)
        => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        await using var fixture = CreateFixture();
        var window = fixture.Window;
        try
        {
            AnimationConfig.SetReduceMotion(true);
            window.Left = -20000;
            window.Top = -20000;
            window.Show();
            var coordinator = Coordinator(window);
            AnimationConfig.SetReduceMotion(false);
            double height = window.NotchBorder.Height;
            var start = DateTime.UtcNow;
            window.BeginShellGestureTracking(new Point(), start);
            window.UpdateGestureTracking(new Point(0, distance), start.AddMilliseconds(milliseconds));
            Assert.Equal(Math.Max(40, height + distance), window.NotchBorder.Height, 6);
            Assert.Equal(Visibility.Visible, window.SecondaryContent.Visibility);
            double releasedWidth = window.NotchBorder.Width, releasedHeight = window.NotchBorder.Height;
            window.FinishShellDrag(false, start.AddMilliseconds(milliseconds));
            Assert.Equal(opens ? NotchView.Secondary : NotchView.Compact, coordinator.TargetView);
            if (opens) ExecutePending(window);
            Assert.Equal(releasedWidth, window.NotchBorder.Width, 6);
            Assert.Equal(releasedHeight, window.NotchBorder.Height, 6);
            Assert.InRange(Motion.VelocityOf(window.NotchBorder, FrameworkElement.HeightProperty),
                distance * 1000 / milliseconds * .9, distance * 1000 / milliseconds * 1.1);
            await WpfFrameWaiter.UntilAsync(() => !coordinator.IsTransitionActive, "pull settles", ct);
            await WpfFrameWaiter.UntilAsync(() => !Motion.IsRunning(window.NotchBorder, FrameworkElement.HeightProperty), "return spring settles", ct);
            if (!opens) Assert.Equal(height, window.NotchBorder.Height, 6);
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    [Theory]
    [InlineData(-25, false)]
    [InlineData(-60, true)]
    [InlineData(90, false)]
    public void OpenPushClosesOrReturnsWithoutChangingViews(double distance, bool closes) => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        await using var fixture = CreateFixture();
        var window = fixture.Window;
        try
        {
            AnimationConfig.SetReduceMotion(true);
            window.Left = -20000;
            window.Top = -20000;
            window.Show();
            window.NavigateToNotchView(NotchView.Timer);
            var coordinator = Coordinator(window);
            await WpfFrameWaiter.UntilAsync(() => !coordinator.IsTransitionActive, "clock open", ct);
            double height = window.NotchBorder.Height;
            var start = DateTime.UtcNow;
            window.BeginShellGestureTracking(new Point(), start);
            window.UpdateGestureTracking(new Point(0, distance), start.AddMilliseconds(250));
            Assert.Equal(Math.Max(40, height + distance), window.NotchBorder.Height, 6);
            window.FinishShellDrag(false, start.AddMilliseconds(250));
            Assert.Equal(closes ? NotchView.Compact : NotchView.Timer, coordinator.TargetView);
            await WpfFrameWaiter.UntilAsync(() => !coordinator.IsTransitionActive, "push settles", ct);
            if (distance > 0) Assert.True(window.ControlCenter.DrawerOpen);
            else if (!closes) Assert.Equal(height, window.NotchBorder.Height, 6);
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    [Fact]
    public void VerticalAxisLocksAfterSlopAndDoesNotTriggerNativeSwipeDown()
    {
        var controller = new GestureController();
        int down = 0;
        controller.SwipeDown += () => down++;
        var start = DateTime.UtcNow;
        controller.BeginTracking(new Point(), allowShellDrag: true, timestamp: start);
        controller.UpdateTracking(new Point(1, 4), start.AddMilliseconds(10));
        Assert.False(controller.HasDragAxis);
        controller.UpdateTracking(new Point(1, 8), start.AddMilliseconds(20));
        Assert.True(controller.IsVerticalDrag);
        controller.UpdateTracking(new Point(120, 100), start.AddMilliseconds(80));
        Assert.True(controller.IsVerticalDrag);
        Assert.True(controller.VerticalVelocity > 380);
        controller.EndTracking(new Point(120, 100));
        Assert.Equal(0, down);
    }

    [Fact]
    public void ReversingAClosingShellPreservesItsAttachedScaleSizeAndOpacity() => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        await using var fixture = CreateFixture();
        var window = fixture.Window;
        try
        {
            AnimationConfig.SetReduceMotion(true);
            window.Left = -20000;
            window.Top = -20000;
            window.Show();
            window.NavigateToNotchView(NotchView.Media);
            var coordinator = Coordinator(window);
            await WpfFrameWaiter.UntilAsync(() => !coordinator.IsTransitionActive, "media open", ct);
            AnimationConfig.SetReduceMotion(false);
            window.NavigateToNotchView(NotchView.Compact);
            await WpfFrameWaiter.UntilAsync(() => window.NotchBorder.Height < 130 && coordinator.IsTransitionActive, "shell closing", ct);
            var layer = (FrameworkElement)window.ExpandedContent.Parent;
            var scale = (ScaleTransform)layer.RenderTransform;
            double width = window.NotchBorder.Width, height = window.NotchBorder.Height;
            double sx = scale.ScaleX, sy = scale.ScaleY, opacity = layer.Opacity;
            window.NavigateToNotchView(NotchView.Media);
            ExecutePending(window);
            Assert.Equal(width, window.NotchBorder.Width, 6);
            Assert.Equal(height, window.NotchBorder.Height, 6);
            Assert.Equal(sx, scale.ScaleX, 6);
            Assert.Equal(sy, scale.ScaleY, 6);
            Assert.Equal(opacity, layer.Opacity, 6);
            await WpfFrameWaiter.UntilAsync(() => !coordinator.IsTransitionActive, "reversal settled", ct);
            Assert.Equal(1, scale.ScaleX);
            Assert.Equal(1, scale.ScaleY);
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    [Fact]
    public void GrabbingAMovingShellDoesNotRewindItWhenTheDragCrossesSlop() => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        await using var fixture = CreateFixture();
        var window = fixture.Window;
        try
        {
            AnimationConfig.SetReduceMotion(true);
            window.Left = -20000;
            window.Top = -20000;
            window.Show();
            AnimationConfig.SetReduceMotion(false);
            window.NavigateToNotchView(NotchView.Media);
            await WpfFrameWaiter.UntilAsync(() => window.NotchBorder.Height > 60 && Coordinator(window).IsTransitionActive, "opening moves", ct);
            var start = DateTime.UtcNow;
            window.BeginShellGestureTracking(new Point(), start);
            await WpfFrameWaiter.NextAsync(ct);
            double width = window.NotchBorder.Width, height = window.NotchBorder.Height;
            var layer = (FrameworkElement)window.ExpandedContent.Parent;
            double opacity = layer.Opacity;
            window.UpdateGestureTracking(new Point(0, -8), start.AddMilliseconds(40));
            Assert.Equal(width, window.NotchBorder.Width, 6);
            Assert.Equal(height, window.NotchBorder.Height, 6);
            Assert.Equal(opacity, layer.Opacity, 6);
            window.UpdateGestureTracking(new Point(0, -28), start.AddMilliseconds(80));
            Assert.Equal(Math.Max(40, height - 20), window.NotchBorder.Height, 6);
            window.FinishShellDrag(true, start.AddMilliseconds(80));
            await WpfFrameWaiter.UntilAsync(() => !Coordinator(window).IsTransitionActive, "grab returns open", ct);
            Assert.Equal(NotchView.Media, Coordinator(window).CurrentView);
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    private static void ExecutePending(MainWindow window)
    {
        var pending = typeof(MainWindow).GetField("_pendingTransitionRequest", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var request = pending.GetValue(window);
        Assert.NotNull(request);
        pending.SetValue(window, null);
        typeof(MainWindow).GetMethod("ExecuteTransitionRequest", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [request]);
    }

    private static GreetingAcceptanceTests.MainWindowFixture CreateFixture() => new("en", false,
        settings => { settings.EnableLocalOnlyMode = true; settings.DisableMouseLeaveAutoClose = true; },
        services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));

    private static NotchTransitionCoordinator Coordinator(MainWindow window) =>
        (NotchTransitionCoordinator)typeof(MainWindow).GetField("_transitionCoordinator", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
}
