using System.Reflection;
using System.Windows;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Controllers;
using VNotch.Services;
using VNotch.Tests.Fakes;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class CompactPillMotionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void WidthReversalPreservesPositionAndVelocity() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = Create();
        var window = fixture.Window;
        using var host = new RenderHost(window);
        Invoke(window, "AnimateCompactWidth", 430d, TimeSpan.FromMilliseconds(350), new System.Windows.Media.Animation.CubicEase(), 0);
        await Task.Delay(100, ct);
        double position = window.NotchBorder.Width;
        double velocity = Motion.VelocityOf(window.NotchBorder, FrameworkElement.WidthProperty);
        Invoke(window, "AnimateCompactWidth", 300d, TimeSpan.FromMilliseconds(350), new System.Windows.Media.Animation.CubicEase(), 0);
        Assert.Equal(position, window.NotchBorder.Width, 3);
        Assert.InRange(Math.Abs(Motion.VelocityOf(window.NotchBorder, FrameworkElement.WidthProperty) - velocity), 0, 35);
        await WpfFrameWaiter.UntilAsync(() => !Motion.IsRunning(window.NotchBorder, FrameworkElement.WidthProperty),
            "closed width spring settles", ct);
        Assert.Equal(300, window.NotchBorder.Width, 3);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreemptionKeepsAnExitFaceAndDoesNotReturnTheShellToRest(bool banner) => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = Create();
        var window = fixture.Window;
        using var host = new RenderHost(window);
        var arbiter = (CompactPillArbiter)typeof(MainWindow).GetField("_compactPillArbiter", Private)!.GetValue(window)!;
        arbiter.TryAcquire(CompactPillSlot.Volume);
        window.VolumeIndicatorContainer.Visibility = Visibility.Visible;
        window.VolumeIndicatorContainer.Opacity = 1;
        Motion.Stop(window.NotchBorder, FrameworkElement.WidthProperty, 420);
        host.Window.UpdateLayout();
        int before = window.NotchLiveContent.Children.Count;
        if (banner) window.ShowAppNotification(new Models.AppNotificationInfo
            { AppName = "Test", Title = "Download ready", Body = "Open the app to continue." });
        else
        {
            object[] args = { CompactPillSlot.AppNotification, 0 };
            Assert.True((bool)Invoke(window, "TryAcquireCompactSlot", args)!);
        }
        Assert.Equal(420, window.NotchBorder.Width, 3);
        Assert.True(window.NotchLiveContent.Children.Count > before, "The outgoing face must remain visible through an Exit.");
        await WpfFrameWaiter.UntilAsync(() => window.NotchLiveContent.Children.Count == before,
            "departure face is removed once its exit settles", ct);
    });

    [Fact]
    public void ReducedMotionLeavesNoDepartureOrRunningWidthSpring() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = Create();
        var window = fixture.Window;
        using var host = new RenderHost(window);
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(true);
        try
        {
            int before = window.NotchLiveContent.Children.Count;
            Invoke(window, "KeepCompactDeparture", window.CollapsedContent);
            Invoke(window, "AnimateCompactWidth", 380d, TimeSpan.FromMilliseconds(350), new System.Windows.Media.Animation.CubicEase(), 0);
            Assert.Equal(before, window.NotchLiveContent.Children.Count);
            Assert.Equal(380, window.NotchBorder.Width);
            Assert.Equal(0, Motion.VelocityOf(window.NotchBorder, FrameworkElement.WidthProperty));
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
        await Task.CompletedTask;
    });

    private static GreetingAcceptanceTests.MainWindowFixture Create() => new("en", false,
        settings => { settings.EnableLocalOnlyMode = true; settings.DisableMouseLeaveAutoClose = true;
            settings.ShowSystemNotifications = true; },
        services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));

    private static object? Invoke(MainWindow window, string name, params object[] args)
    {
        var method = typeof(MainWindow).GetMethod(name, Private, null, args.Select(arg => arg.GetType()).ToArray(), null)
            ?? typeof(MainWindow).GetMethods(Private).Single(candidate => candidate.Name == name && candidate.GetParameters().Length == args.Length);
        return method.Invoke(window, args);
    }

    private sealed class RenderHost : IDisposable
    {
        private readonly MainWindow _owner;
        internal Window Window { get; }
        internal RenderHost(MainWindow owner)
        {
            _owner = owner;
            var root = (FrameworkElement)owner.Content;
            owner.Content = null;
            Window = new BackgroundWindow { Left = -20000, Top = 0, Width = 720, Height = 240,
                Content = root, ShowActivated = false, ShowInTaskbar = false };
            Window.Show();
            Window.UpdateLayout();
        }
        public void Dispose()
        {
            var root = Window.Content;
            Window.Content = null;
            Window.Close();
            _owner.Content = root;
        }
    }
}
