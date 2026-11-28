using System.Reflection;
using System.Windows;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Controllers;
using VNotch.Models;
using VNotch.Services;
using VNotch.Tests.Fakes;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class CompactPillMotionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void IdleGlanceSettlesOnShellInterruption(bool leaving, bool fullscreen) => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = Create();
        var window = fixture.Window;
        using var host = new RenderHost(window);
        var settings = MusicMotionTests.Field<NotchSettings>(window, "_settings");
        settings.SlimIdleNotch = false;
        Invoke(window, "UpdateIdleShape");
        double fullWidth = MusicMotionTests.Field<double>(window, "_collapsedWidth");
        Motion.Stop(window.NotchBorder, FrameworkElement.WidthProperty, fullWidth);
        Invoke(window, "UpdateIdleShape");
        settings.SlimIdleNotch = true;
        Invoke(window, "UpdateIdleShape");
        var layer = (FrameworkElement)window.IdleGlance.Parent;
        if (leaving)
        {
            await WpfFrameWaiter.UntilAsync(() => double.IsNaN(layer.Width), "idle face settles", ct);
            settings.SlimIdleNotch = false;
            Invoke(window, "UpdateIdleShape");
        }
        await WpfFrameWaiter.UntilAsync(() => double.IsFinite(layer.Width) &&
            Math.Abs(Motion.VelocityOf(window.NotchBorder, FrameworkElement.WidthProperty)) > 10,
            "idle face moves with the shell", ct);

        if (fullscreen)
        {
            window.ShellState.IsHiddenByFullscreen = true;
            window.NotchLiveContent.Visibility = Visibility.Collapsed;
            Invoke(window, "RefreshAmbientAnimations");
            Assert.False(Motion.IsRunning(window.NotchBorder, FrameworkElement.WidthProperty));
        }
        else await OpenAndReverse(window, ct);

        Assert.Equal(leaving ? Visibility.Collapsed : Visibility.Visible, window.IdleGlance.Visibility);
        Assert.Equal(leaving ? 0 : 1, layer.Opacity);
        Assert.Equal(leaving ? fullWidth : Math.Min(fullWidth, 150), window.NotchBorder.Width, 3);
        Assert.True(double.IsNaN(layer.Width));
    });

    [Theory]
    [InlineData(CompactPillSlot.Clipboard, false)]
    [InlineData(CompactPillSlot.Clipboard, true)]
    [InlineData(CompactPillSlot.Claude, false)]
    [InlineData(CompactPillSlot.Claude, true)]
    [InlineData(CompactPillSlot.Bluetooth, false)]
    [InlineData(CompactPillSlot.Bluetooth, true)]
    public void OpenCloseReversalRetiresAnInterruptedPill(CompactPillSlot slot, bool leaving) => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = Create();
        var window = fixture.Window;
        using var host = new RenderHost(window);
        var arbiter = MusicMotionTests.Field<CompactPillArbiter>(window, "_compactPillArbiter");
        FrameworkElement face;
        FrameworkElement layer;
        Action beginExit;
        if (slot == CompactPillSlot.Clipboard)
        {
            window.SetDebugViewState("CompactMusicPill");
            window.MusicCompactContent.Visibility = Visibility.Visible;
            window.MusicCompactContent.Opacity = 1;
            Invoke(window, "ShowClipboardCopiedState");
            face = window.ClipboardCopiedText;
            layer = (FrameworkElement)window.MusicCompactContent.Parent;
            beginExit = () => Invoke(window, "RevertClipboardCopiedState", false);
        }
        else if (slot == CompactPillSlot.Claude)
        {
            Assert.True((bool)typeof(MainWindow).GetMethod("ShowNotchPill", Private)!.Invoke(window,
                ["Build complete", "Ready for review", null, true, null, null, null, TimeSpan.FromSeconds(10), null])!);
            face = window.ClaudeNotification;
            layer = (FrameworkElement)face.Parent;
            beginExit = () => Invoke(window, "AnimateClaudeNotificationOut");
        }
        else
        {
            Invoke(window, "AnimateBluetoothNotificationIn", true);
            face = window.BluetoothNotification;
            layer = (FrameworkElement)face.Parent;
            beginExit = () => Invoke(window, "AnimateBluetoothNotificationOut");
        }
        Assert.Equal(slot, arbiter.ActiveSlot);
        Assert.Equal(Visibility.Visible, face.Visibility);
        if (slot == CompactPillSlot.Clipboard)
        {
            await WpfFrameWaiter.NextAsync(ct);
            Assert.True(window.MusicViz.IsCopiedFeedbackActive);
        }
        if (leaving)
        {
            await WpfFrameWaiter.UntilAsync(() => double.IsNaN(layer.Width), "pill arrives", ct);
            beginExit();
        }
        await WpfFrameWaiter.UntilAsync(() => double.IsFinite(layer.Width) &&
            Math.Abs(Motion.VelocityOf(window.NotchBorder, FrameworkElement.WidthProperty)) > 10,
            "pill shell is moving", ct);

        await OpenAndReverse(window, ct);

        Assert.Equal(CompactPillSlot.None, arbiter.ActiveSlot);
        Assert.Equal(Visibility.Collapsed, face.Visibility);
        Assert.Equal(slot == CompactPillSlot.Clipboard ? 1 : 0, layer.Opacity);
        Assert.True(double.IsNaN(layer.Width));
        Assert.False(window.MusicViz.IsCopiedFeedbackActive);
        Assert.Null(typeof(MainWindow).GetField("_clipboardRevertTimer", Private)!.GetValue(window));
    });

    private static async Task OpenAndReverse(MainWindow window, CancellationToken ct)
    {
        var shell = window.NotchBorder;
        double width = shell.Width;
        double height = shell.Height;
        double widthSpeed = Motion.VelocityOf(shell, FrameworkElement.WidthProperty);
        double heightSpeed = Motion.VelocityOf(shell, FrameworkElement.HeightProperty);
        window.NavigateToNotchView(NotchView.Timer);
        Assert.Equal(width, shell.Width, 3);
        Assert.Equal(height, shell.Height, 3);
        Assert.Equal(widthSpeed, Motion.VelocityOf(shell, FrameworkElement.WidthProperty), 3);
        Assert.Equal(heightSpeed, Motion.VelocityOf(shell, FrameworkElement.HeightProperty), 3);
        await WpfFrameWaiter.UntilAsync(() => shell.Height > height + 8, "opening takes over the shell", ct);
        window.NavigateToNotchView(NotchView.Compact);
        var coordinator = MusicMotionTests.Field<NotchTransitionCoordinator>(window, "_transitionCoordinator");
        await WpfFrameWaiter.UntilAsync(() => coordinator.CurrentView == NotchView.Compact &&
            !coordinator.IsTransitionActive && !MusicMotionTests.Field<bool>(window, "_isAnimating"), "reversed shell settles compact", ct);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CopiedFeedbackRestoresTheMusicThumbnail(bool fullscreen) => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = Create();
        var window = fixture.Window;
        using var host = new RenderHost(window);
        window.SetDebugViewState("CompactMusicPill");
        window.MusicCompactContent.Visibility = Visibility.Visible;
        window.CompactThumbnailBorder.Visibility = Visibility.Visible;
        double width = MusicMotionTests.Field<double>(window, "_collapsedWidth");
        Invoke(window, "ShowClipboardCopiedState");
        await WpfFrameWaiter.UntilAsync(() => window.CompactThumbnailBorder.Visibility == Visibility.Collapsed,
            "copied feedback replaces the thumbnail", ct);
        Invoke(window, "RevertClipboardCopiedState", false);

        if (fullscreen)
        {
            window.ShellState.IsHiddenByFullscreen = true;
            Invoke(window, "RefreshAmbientAnimations");
            Assert.False(Motion.IsRunning(window.NotchBorder, FrameworkElement.WidthProperty));
        }
        await WpfFrameWaiter.UntilAsync(() => window.ClipboardCopiedText.Visibility == Visibility.Collapsed,
            "copied feedback exits", ct);

        Assert.Equal(Visibility.Visible, window.CompactThumbnailBorder.Visibility);
        Assert.Equal(1, window.CompactThumbnailBorder.Opacity);
        Assert.Equal(width, window.NotchBorder.Width, 3);
        Assert.False(window.MusicViz.IsCopiedFeedbackActive);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OpeningTheNotchRetiresBothAnEnteringAndALeavingHud(bool leaving) => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(false);
        try
        {
            await using var fixture = Create();
            var window = fixture.Window;
            using var host = new RenderHost(window);
            window.SetDebugViewState("CompactMusicPill");
            Invoke(window, "ShowVolumeIndicator", .62f);
            Assert.True(MusicMotionTests.Field<bool>(window, "_isVolumeIndicatorActive"));
            await Task.Delay(100, ct);
            if (leaving)
            {
                Invoke(window, "HideVolumeIndicator");
                await Task.Delay(40, ct);
                Assert.True(MusicMotionTests.Field<bool>(window, "_isVolumeIndicatorExiting"));
            }
            window.NavigateToNotchView(VNotch.Models.NotchView.Media);
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);
            Assert.Equal(Visibility.Collapsed, window.VolumeIndicatorContainer.Visibility);
            Assert.Equal(0, ((FrameworkElement)window.VolumeIndicatorContainer.Parent).Opacity);
            Assert.False(MusicMotionTests.Field<bool>(window, "_isVolumeIndicatorExiting"));
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NotificationFacesReverseWithTheirShellAndRetireTheStaleExit(bool bluetooth) => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(false);
        try
        {
            await using var fixture = Create();
            var window = fixture.Window;
            using var host = new RenderHost(window);
            var arbiter = (CompactPillArbiter)typeof(MainWindow).GetField("_compactPillArbiter", Private)!.GetValue(window)!;
            void Open()
            {
                if (bluetooth) Invoke(window, "AnimateBluetoothNotificationIn", true);
                else
                {
                    int token = arbiter.TryAcquire(CompactPillSlot.Claude).Token;
                    typeof(MainWindow).GetField("_claudeNotificationToken", Private)!.SetValue(window, token);
                    typeof(MainWindow).GetField("_isClaudeNotificationVisible", Private)!.SetValue(window, true);
                    window.ClaudeNotificationTitle.Text = "Build complete";
                    Invoke(window, "AnimateClaudeNotificationIn");
                    Invoke(window, "FitNotchToPill");
                }
            }
            var face = bluetooth ? window.BluetoothNotification : window.ClaudeNotification;
            double closedWidth = window.NotchBorder.Width;
            Open();
            await WpfFrameWaiter.UntilAsync(() => window.NotchBorder.Width > closedWidth + 6, "notification shell grows", ct);
            var layer = (FrameworkElement)face.Parent;
            Assert.Equal(new Point(.5, 0), layer.RenderTransformOrigin);
            var scale = Assert.IsType<ScaleTransform>(layer.RenderTransform);
            Assert.Equal(window.NotchBorder.Width / layer.Width, scale.ScaleX, 5);
            Assert.False(window.NotchScale.HasAnimatedProperties);
            Invoke(window, bluetooth ? "AnimateBluetoothNotificationOut" : "AnimateClaudeNotificationOut");
            await Task.Delay(100, ct);
            double width = window.NotchBorder.Width;
            double velocity = Motion.VelocityOf(window.NotchBorder, FrameworkElement.WidthProperty);
            Open();
            Assert.Equal(width, window.NotchBorder.Width, 3);
            Assert.InRange(Math.Abs(Motion.VelocityOf(window.NotchBorder, FrameworkElement.WidthProperty) - velocity), 0, 35);
            await WpfFrameWaiter.UntilAsync(() => double.IsNaN(layer.Width), "latest notification settles", ct);
            Assert.Equal(Visibility.Visible, face.Visibility);
            Assert.Equal(1, layer.Opacity);
            Assert.True(arbiter.IsActive(bluetooth ? CompactPillSlot.Bluetooth : CompactPillSlot.Claude));
            AnimationConfig.SetReduceMotion(true);
            Invoke(window, bluetooth ? "AnimateBluetoothNotificationOut" : "AnimateClaudeNotificationOut");
            Assert.Equal(Visibility.Collapsed, face.Visibility);
            Assert.Equal(CompactPillSlot.None, arbiter.ActiveSlot);
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

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
    public void LostHudCaptureEndsTheDragAndResumesDismissal() => SharedStaTestRunner.RunAsync(async _ =>
    {
        await using var fixture = Create();
        var window = fixture.Window;
        window.SetDebugViewState("CompactMusicPill");
        Invoke(window, "ShowVolumeIndicator", .5f);
        typeof(MainWindow).GetField("_isDraggingVolumeIndicator", Private)!.SetValue(window, true);
        window.VolumeIndicatorContainer.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0)
            { RoutedEvent = System.Windows.Input.Mouse.LostMouseCaptureEvent, Source = window.VolumeIndicatorContainer });
        Assert.False((bool)typeof(MainWindow).GetField("_isDraggingVolumeIndicator", Private)!.GetValue(window)!);
        Assert.True(((UiScheduledTimer)typeof(MainWindow).GetField("_volumeIndicatorHideTimer", Private)!.GetValue(window)!).IsEnabled);
    });

    [Fact]
    public void DepartureKeepsTheAttachedFacesScaleAndOpacity() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = Create();
        var window = fixture.Window;
        using var host = new RenderHost(window);
        var face = window.AppNotificationPill;
        face.Visibility = Visibility.Visible;
        face.Opacity = 1;
        var attachment = new VNotch.Presenters.CompactSurfaceAttachment(window.NotchBorder, face, () => new Size(300, 40));
        Motion.Stop(window.NotchBorder, FrameworkElement.WidthProperty, 340);
        Motion.Stop(window.NotchBorder, FrameworkElement.HeightProperty, 48);
        attachment.Attach(380, 56);
        host.Window.UpdateLayout();
        var bounds = face.TransformToAncestor(window.NotchLiveContent).TransformBounds(new Rect(face.RenderSize));
        double opacity = ((FrameworkElement)face.Parent).Opacity;
        Invoke(window, "KeepCompactDeparture", face);
        var snapshot = Assert.IsType<System.Windows.Controls.Image>(window.NotchLiveContent.Children[^1]);
        Assert.Equal(bounds.Width, snapshot.Width, 3);
        Assert.Equal(bounds.Height, snapshot.Height, 3);
        Assert.Equal(opacity, snapshot.Opacity, 3);
        attachment.Detach(false);
        await WpfFrameWaiter.UntilAsync(() => !window.NotchLiveContent.Children.Contains(snapshot), "snapshot leaves", ct);
    });

    [Fact]
    public void HudRubberFollowsTheDragAndReleasesWithItsVelocity() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = Create();
        var window = fixture.Window;
        using var host = new RenderHost(window);
        var start = DateTime.UtcNow;
        Invoke(window, "SetVolumeIndicatorRubber", 2d, start);
        Invoke(window, "SetVolumeIndicatorRubber", 8d, start.AddMilliseconds(40));
        Assert.Equal(8, window.VolumeIndicatorContentTranslate.X);
        double width = window.NotchBorder.Width;
        Invoke(window, "ReleaseVolumeIndicatorRubber");
        Assert.Equal(8, window.VolumeIndicatorContentTranslate.X, 3);
        Assert.InRange(Motion.VelocityOf(window.VolumeIndicatorContentTranslate, TranslateTransform.XProperty), 145, 155);
        Assert.Equal(width, window.NotchBorder.Width);
        await WpfFrameWaiter.UntilAsync(() => !Motion.IsRunning(window.VolumeIndicatorContentTranslate, TranslateTransform.XProperty),
            "HUD rubber returns to the bar", ct);
        Assert.Equal(0, window.VolumeIndicatorContentTranslate.X, 3);
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
            // Hosting the content bypasses MainWindow.Show's initial shell sizing.
            if (!double.IsFinite(owner.NotchBorder.Width))
                Motion.Stop(owner.NotchBorder, FrameworkElement.WidthProperty, MusicMotionTests.Field<double>(owner, "_collapsedWidth"));
            if (!double.IsFinite(owner.NotchBorder.Height))
                Motion.Stop(owner.NotchBorder, FrameworkElement.HeightProperty, MusicMotionTests.Field<double>(owner, "_collapsedHeight"));
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
