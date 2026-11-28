using System.Reflection;
using System.Windows;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.ClaudeCode;
using VNotch.Tests.Fakes;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class OrbPhoneMotionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void BudReversalKeepsPositionAndMomentum() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = Create();
        var window = fixture.Window;
        window.Left = -20000;
        window.Show();
        await WpfFrameWaiter.UntilAsync(() => window.CollapsedContent.IsVisible, "closed notch", ct);
        Set(window, "_bubbleShown", true);
        Invoke(window, "BudBubble");
        await Task.Delay(100, ct);
        double position = window.BubbleSlide.X;
        double velocity = Motion.VelocityOf(window.BubbleSlide, TranslateTransform.XProperty);
        Invoke(window, "RetractBubble", false);
        Assert.InRange(Math.Abs(window.BubbleSlide.X - position), 0, 0.5);
        Assert.InRange(Math.Abs(Motion.VelocityOf(window.BubbleSlide, TranslateTransform.XProperty) - velocity), 0, 25);
        await Task.Delay(80, ct);
        position = window.BubbleSlide.X;
        Invoke(window, "BudBubble");
        Assert.InRange(Math.Abs(window.BubbleSlide.X - position), 0, 0.5);
        await WpfFrameWaiter.UntilAsync(() => !Motion.IsRunning(window.BubbleSlide, TranslateTransform.XProperty),
            "orb settles after its reversal", ct);
        Assert.Equal(Visibility.Visible, window.ActivityBubble.Visibility);
        Assert.InRange(Math.Abs(window.BubbleSlide.X), 0, 0.1);
    });

    [Fact]
    public void SessionListReplacementKeepsOutgoingRowsUntilTheyFadeAndResizesForTheNewSet() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = Create();
        var window = fixture.Window;
        window.Left = -20000;
        window.Show();
        await WpfFrameWaiter.UntilAsync(() => window.CollapsedContent.IsVisible, "closed notch", ct);
        var sessions = new List<ClaudeSession> { new() { Id = "one", Title = "Build" }, new() { Id = "two", Title = "Review" } };
        Set(window, "_bubbleClaudeSessions", sessions);
        Set(window, "_bubbleShown", true);
        Invoke(window, "SetBubbleFace", LiveActivity.Claude);
        Invoke(window, "BudBubble");
        Invoke(window, "OpenBubble");
        await WpfFrameWaiter.UntilAsync(() => !Motion.IsRunning(window.BubbleList, UIElement.OpacityProperty), "session list arrives", ct);
        var outgoing = window.BubbleList.Children[0];
        sessions = new List<ClaudeSession> { new() { Id = "three", Title = "Design" }, new() { Id = "four", Title = "Test" }, new() { Id = "five", Title = "Ship" } };
        Set(window, "_bubbleClaudeSessions", sessions);
        Invoke(window, "UpdateBubbleList", false);
        Assert.Same(outgoing, window.BubbleList.Children[0]);
        await WpfFrameWaiter.UntilAsync(() => window.BubbleList.Children.Count == 3 &&
            !Motion.IsRunning(window.ActivityBubble, FrameworkElement.HeightProperty), "new session rows and shell settle", ct);
        Assert.DoesNotContain(outgoing, window.BubbleList.Children.Cast<UIElement>());
        Assert.True(window.ActivityBubble.ActualHeight >= 96);
        Invoke(window, "SwapBubbleFace", LiveActivity.Music);
        await WpfFrameWaiter.UntilAsync(() => window.BubbleList.Visibility == Visibility.Collapsed &&
            window.BubbleMusicFace.Visibility == Visibility.Visible, "session rows leave before the next face arrives", ct);
        Assert.Empty(window.BubbleList.Children.Cast<UIElement>());
    });

    [Fact]
    public void ReplugKeepsTheSnappingCablePositionAndVelocity() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = Create();
        var window = fixture.Window;
        window.Left = -20000;
        window.Show();
        await WpfFrameWaiter.UntilAsync(() => window.CollapsedContent.IsVisible, "closed notch", ct);
        Set(window, "_chargeBattery", new BatteryInfo { Percentage = 60, IsPluggedIn = true, IsCharging = true });
        Set(window, "_bubbleShown", true);
        Invoke(window, "SetBubbleFace", LiveActivity.Charging);
        Invoke(window, "BudBubble");
        await WpfFrameWaiter.UntilAsync(() => window.ChargeCable.IsVisible, "charge cable arrives", ct);
        Invoke(window, "SnapChargeCable");
        await Task.Delay(100, ct);
        double position = window.ChargeCableLeftScale.ScaleX;
        double velocity = Motion.VelocityOf(window.ChargeCableLeftScale, ScaleTransform.ScaleXProperty);
        Invoke(window, "PlayChargeArrival", true);
        Assert.InRange(Math.Abs(window.ChargeCableLeftScale.ScaleX - position), 0, 0.001);
        Assert.InRange(Math.Abs(Motion.VelocityOf(window.ChargeCableLeftScale, ScaleTransform.ScaleXProperty) - velocity), 0, 0.2);
        await WpfFrameWaiter.UntilAsync(() => !Motion.IsRunning(window.ChargeCableLeftScale, ScaleTransform.ScaleXProperty),
            "replugged cable settles", ct);
        Assert.Equal(1, window.ChargeCableLeftScale.ScaleX, 3);
    });

    [Fact]
    public void DockedPhoneTracksAnAnchorThatReversesDuringArrivalAndRetraction() => SharedStaTestRunner.RunAsync(async ct =>
    {
        var anchor = new NotchAnchor(-19500, -19960, 300, 1,
            new Int32Rect(-20000, -20000, 1000, 800), new Int32Rect(-20000, -20000, 1000, 800));
        var phone = new PhoneMirrorWindow(() => anchor);
        try
        {
            var start = new Int32Rect(-19620, -19966, 240, 6);
            phone.Left = start.X;
            phone.Top = start.Y;
            phone.Width = start.Width;
            phone.Height = start.Height;
            phone.Show();
            typeof(PhoneMirrorWindow).GetProperty(nameof(PhoneMirrorWindow.State))!.SetValue(phone, PhoneMirrorWindow.MirrorState.Docked);
            var dock = typeof(PhoneMirrorWindow).GetMethod("DockedRect", Private)!;
            Invoke(phone, "Spring", start, (Int32Rect)dock.Invoke(phone, [anchor])!, Motion.Pop, null, new Vector());
            await WpfFrameWaiter.UntilAsync(() => Get<double>(phone, "_rectangleFrameTime") >= .06,
                "mirror is growing out of the notch", ct);
            var previous = Get<SpringDoubleAnimation[]>(phone, "_rectangleSprings");
            double time = Get<double>(phone, "_rectangleFrameTime");
            var currentRect = typeof(PhoneMirrorWindow).GetMethod("CurrentRect", Private)!;
            int before = ((Int32Rect)currentRect.Invoke(phone, null)!).Y - anchor.Bottom;
            anchor = anchor with { Bottom = -19820, Width = 600 };
            phone.RefreshNotchAnchor();
            Assert.Equal(before, ((Int32Rect)currentRect.Invoke(phone, null)!).Y - anchor.Bottom);
            Assert.Equal((Int32Rect)dock.Invoke(phone, [anchor])!, Get<Int32Rect>(phone, "_springTarget"));
            var next = Get<SpringDoubleAnimation[]>(phone, "_rectangleSprings");
            Assert.NotSame(previous, next);
            for (int i = 0; i < next.Length; i++)
                Assert.Equal(previous[i].VelocityAt(time), next[i].VelocityAt(0), 5);

            int closed = 0;
            phone.MirrorClosed += () => closed++;
            phone.PullIn();
            await WpfFrameWaiter.UntilAsync(() => Get<double>(phone, "_rectangleFrameTime") >= .06,
                "mirror is retracting", ct);
            anchor = anchor with { Bottom = -19960, Width = 300 };
            phone.RefreshNotchAnchor();
            Assert.Equal(start, Get<Int32Rect>(phone, "_springTarget"));
            await WpfFrameWaiter.UntilAsync(() => !phone.IsVisible, "mirror tucks into the latest notch", ct);
            Assert.Equal(1, closed);
            Assert.Null(Get<EventHandler?>(phone, "_frame"));
        }
        finally { phone.Close(); }
    });

    [Fact]
    public void PhoneRetargetKeepsAllRectangleVelocitiesAndStopsWhenHidden() => SharedStaTestRunner.RunAsync(async ct =>
    {
        var window = new PhoneMirrorWindow(() => null);
        try
        {
            window.Left = -20000;
            window.Top = -20000;
            window.Width = 80;
            window.Height = 20;
            window.Show();
            var start = new Int32Rect(-20000, -20000, 80, 20);
            var target = new Int32Rect(-20050, -19980, 180, 360);
            Invoke(window, "Spring", start, target, Motion.Pop, null, new Vector());
            await WpfFrameWaiter.UntilAsync(() => Get<double>(window, "_rectangleFrameTime") >= .06,
                "the native rectangle is moving", ct);
            var previous = Get<SpringDoubleAnimation[]>(window, "_rectangleSprings");
            double elapsed = Get<double>(window, "_rectangleFrameTime");
            double[] velocities = previous.Select(x => x.VelocityAt(elapsed)).ToArray();
            Invoke(window, "Spring", start, start, Motion.PopBack, null, new Vector());
            var next = Get<SpringDoubleAnimation[]>(window, "_rectangleSprings");
            for (int i = 0; i < 4; i++)
                Assert.Equal(velocities[i], next[i].VelocityAt(0), 5);
            window.Hide();
            Assert.Null(Get<EventHandler?>(window, "_frame"));
        }
        finally { window.Close(); }
    });

    internal static GreetingAcceptanceTests.MainWindowFixture Create() => new("en", false,
        settings => { settings.EnableLocalOnlyMode = true; settings.DisableMouseLeaveAutoClose = true; },
        services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));

    internal static void Invoke(object instance, string name, params object?[] arguments) =>
        instance.GetType().GetMethod(name, Private)!.Invoke(instance, arguments);
    internal static void Set(object instance, string name, object value) =>
        instance.GetType().GetField(name, Private)!.SetValue(instance, value);
    internal static T Get<T>(object instance, string name) => (T)instance.GetType().GetField(name, Private)!.GetValue(instance)!;
}
