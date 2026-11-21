using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Models;
using VNotch.Services;
using VNotch.Tests.Fakes;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class AppNotificationMotionTests
{
    [Fact]
    public void ContentAppearsImmediatelyWithTheMainViewOpeningMotion() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture();
        var window = fixture.Window;
        using var host = new RenderHost(window);
        Motion.Stop(window.NotchBorder, FrameworkElement.WidthProperty, 300);
        Motion.Stop(window.NotchBorder, FrameworkElement.HeightProperty, 40);
        window.ShowAppNotification(Notification());
        await Task.Delay(120, ct);
        await WpfFrameWaiter.NextAsync(ct);
        Assert.True(((FrameworkElement)window.AppNotificationBanner.Parent).Opacity > .2, "The notification must reveal with the shell, without a separate waiting phase.");
        await WpfFrameWaiter.UntilAsync(() => !Motion.IsRunning(window.NotchBorder, FrameworkElement.WidthProperty),
            "notification finishes the main view geometry motion", ct);
    });

    [Fact]
    public void EntranceStartsFromTheCurrentNotchWithoutFlashingTheFinalFrame() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture();
        var window = fixture.Window;
        using var host = new RenderHost(window);
        await WpfFrameWaiter.NextAsync(ct);
        // The fixture has not shown its window, so initialize the resting geometry explicitly.
        Motion.Stop(window.NotchBorder, FrameworkElement.WidthProperty, 300);
        Motion.Stop(window.NotchBorder, FrameworkElement.HeightProperty, 40);
        double initialWidth = window.NotchBorder.Width;
        double initialHeight = window.NotchBorder.Height;
        window.ShowAppNotification(Notification());
        Assert.Equal(initialWidth, window.NotchBorder.Width);
        Assert.Equal(initialHeight, window.NotchBorder.Height);
        var layer = (FrameworkElement)window.AppNotificationBanner.Parent;
        var scale = Assert.IsType<ScaleTransform>(layer.RenderTransform);
        Assert.Equal(0, layer.Opacity);
        Assert.Equal(initialWidth / layer.Width, scale.ScaleX, 6);
        Assert.Equal(initialHeight / layer.Height, scale.ScaleY, 6);
        Assert.Equal(1, window.AppNotificationBanner.Opacity);
        await WpfFrameWaiter.UntilAsync(() => layer.Opacity > .2,
            "notification reveals its content during the morph", ct);
        Assert.Equal(1, window.AppNotificationAppNameText.Opacity);
        Assert.Equal(1, window.AppNotificationTitleText.Opacity);
        Assert.Equal(1, window.AppNotificationBodyText.Opacity);
        Assert.Equal(1, window.AppNotificationIconScale.ScaleX);
        Assert.Equal(0, window.AppNotificationIconRotate.Angle);
    });

    [Fact]
    public void HoverDuringEntranceKeepsTheSameSizeAndNeverFloatsTheContent() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture();
        var window = fixture.Window;
        window.ShowAppNotification(Notification());
        double width = Get<double>(window, "_appNotificationBaseWidth");
        window.AnimateAppNotificationHover(true);
        await WpfFrameWaiter.UntilAsync(() => window.NotchBorder.Width == width && window.AppNotificationBannerTranslate.Y == 0,
            "notification entrance settles at its original size", ct);
        window.AnimateAppNotificationHover(false);
        window.AnimateAppNotificationHover(true);
        await Task.Delay(350, ct);
        Assert.Equal(width, window.NotchBorder.Width);
        Assert.Equal(0, window.AppNotificationBannerTranslate.Y);
        Assert.Equal(1, window.AppNotificationBannerScale.ScaleX);
        Assert.Equal(1, window.NotchScale.ScaleX);
        Assert.Equal(1, window.AppNotificationTitleText.Opacity);
        Assert.InRange(window.AppNotificationHoverSurface.Opacity, .03, .04);
        Assert.False(window.AppNotificationHoverSurface.IsHitTestVisible);
        Assert.Equal(1, window.AppNotificationIconScale.ScaleX);
        Assert.Null(window.FindName("AppNotificationCloseButton"));
        Assert.Equal(Colors.White, Assert.IsType<SolidColorBrush>(window.AppNotificationHoverSurface.Background).Color);
        Assert.Equal(new Thickness(0), window.AppNotificationHoverSurface.BorderThickness);
        Assert.Null(window.AppNotificationHoverSurface.BorderBrush);
        Assert.Equal(new Thickness(0), window.HoverGlow.BorderThickness);
        Assert.Null(window.HoverGlow.BorderBrush);

        window.AnimateAppNotificationHover(false);
        await WpfFrameWaiter.UntilAsync(() => window.AppNotificationHoverSurface.Opacity == 0 &&
            window.AppNotificationIconScale.ScaleX == 1,
            "notification hover feedback returns to rest", ct);
        Assert.Equal(width, window.NotchBorder.Width);
    });

    [Fact]
    public void PressAndReleaseKeepTextAtFullSizeAndRestoreTheHoveredSurface() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture();
        var window = fixture.Window;
        window.ShowAppNotification(Notification());
        await WpfFrameWaiter.UntilAsync(() => window.AppNotificationBanner.Opacity == 1 &&
            window.AppNotificationBannerTranslate.Y == 0, "notification is ready to interact with", ct);
        window.AnimateAppNotificationHover(true);
        window.BeginAppNotificationInteraction(new Point(0, 0), 0);
        await WpfFrameWaiter.UntilAsync(() => window.AppNotificationPressTranslate.Y == 1,
            "press receives a small positional response", ct);
        Assert.Equal(1, window.AppNotificationBanner.Opacity);
        Assert.Equal(1, window.AppNotificationBannerScale.ScaleX);
        Assert.Equal(1, window.AppNotificationIconScale.ScaleX);
        Assert.InRange(window.AppNotificationHoverSurface.Opacity, .06, .07);
        Assert.True(window.EndAppNotificationInteraction(new Point(1, 0), 250));
        window.AnimateAppNotificationHover(false);
        window.AnimateAppNotificationHover(true);
        await WpfFrameWaiter.UntilAsync(() => window.AppNotificationPressTranslate.Y == 0 &&
            window.AppNotificationHoverSurface.Opacity == .035, "release settles into the current hover state", ct);
        Assert.Equal(1, window.AppNotificationBannerScale.ScaleX);
        Assert.Equal(1, window.AppNotificationBanner.Opacity);
        Assert.True(window.IsAppNotificationBannerActive);
    });

    [Fact]
    public void HoverPausesAndResumesTheRemainingDismissTime() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture(autoDismiss: true);
        var window = fixture.Window;
        using var host = new RenderHost(window);
        window.ShowAppNotification(Notification());
        await WpfFrameWaiter.UntilAsync(() => window.AppNotificationProgressScale.ScaleX < .98, "dismiss countdown starts", ct);
        window.AnimateAppNotificationHover(true);
        double progress = window.AppNotificationProgressScale.ScaleX;
        int remaining = Get<int>(window, "_appNotificationRemainingMs");
        Assert.Null(Get<object?>(window, "_appNotificationDismissTimer"));
        await Task.Delay(180, ct);
        Assert.Equal(progress, window.AppNotificationProgressScale.ScaleX, 5);
        window.AnimateAppNotificationHover(false);
        Assert.NotNull(Get<object?>(window, "_appNotificationDismissTimer"));
        Assert.InRange(Get<int>(window, "_appNotificationRemainingMs"), remaining - 10, remaining);
        await Task.Delay(220, ct);
        await WpfFrameWaiter.NextAsync(ct);
        Assert.True(window.AppNotificationProgressScale.ScaleX < progress - .01,
            $"progress={progress}->{window.AppNotificationProgressScale.ScaleX}; hover={Get<bool>(window, "_isAppNotificationHovered")}; remaining={Get<int>(window, "_appNotificationRemainingMs")}; timer={Get<object?>(window, "_appNotificationDismissTimer") != null}");
    });

    [Fact]
    public void ShortDragReturnsTheShellAndShadowWithoutActivatingOrDismissing() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture();
        var window = fixture.Window;
        window.ShowAppNotification(Notification());
        window.BeginAppNotificationInteraction(new Point(0, 0), 0);
        window.UpdateAppNotificationInteraction(new Point(28, 10), 200);
        Assert.True(window.AppNotificationShellTranslate.X > 0);
        Assert.Equal(window.AppNotificationShellTranslate.X, window.AppNotificationShadowTranslate.X);
        Assert.False(window.EndAppNotificationInteraction(new Point(28, 10), 400));
        await WpfFrameWaiter.UntilAsync(() => window.AppNotificationShellTranslate.X == 0 &&
            window.AppNotificationShadowTranslate.X == 0 && window.AppNotificationBannerScale.ScaleX == 1,
            "short notification drag springs back", ct);
        Assert.True(window.IsAppNotificationBannerActive);
        Assert.Equal(0, window.AppNotificationShellTranslate.Y);
        Assert.Equal(1, window.AppNotificationBanner.Opacity);
    });

    [Theory]
    [InlineData(100, 300)]
    [InlineData(-100, 300)]
    [InlineData(24, 20)]
    public void ALongDragOrQuickFlickDismissesTheNotification(double distance, long elapsed) => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture();
        var window = fixture.Window;
        window.ShowAppNotification(Notification());
        window.BeginAppNotificationInteraction(new Point(0, 0), 0);
        window.UpdateAppNotificationInteraction(new Point(distance, 0), elapsed);
        Assert.False(window.EndAppNotificationInteraction(new Point(distance, 0), elapsed + 1));
        await WpfFrameWaiter.UntilAsync(() => !window.IsAppNotificationBannerActive, "notification swipe dismisses", ct);
        Assert.Equal(Visibility.Collapsed, window.AppNotificationBanner.Visibility);
        Assert.Equal(0, window.AppNotificationShellTranslate.X);
        Assert.Equal(0, window.AppNotificationShadowTranslate.X);
    });

    [Fact]
    public void ReplacementDuringExitSurvivesThePreviousCompletion() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture();
        var window = fixture.Window;
        window.ShowAppNotification(Notification());
        window.AnimateAppNotificationHover(true);
        await WpfFrameWaiter.UntilAsync(() => window.AppNotificationHoverSurface.Opacity > .02,
            "notification hover surface appears before dismissal", ct);
        window.DismissAppNotificationBanner();
        window.ShowAppNotification(new AppNotificationInfo { AppName = "Second app", Title = "Latest notification", Body = "New content" });
        await WpfFrameWaiter.UntilAsync(() => window.AppNotificationBanner.Opacity == 1 && window.AppNotificationBodyText.Opacity == 1,
            "replacement notification enters", ct);
        await Task.Delay(250, ct);
        Assert.True(window.IsAppNotificationBannerActive);
        Assert.Equal("Latest notification", window.AppNotificationTitleText.Text);
        Assert.Equal(Visibility.Visible, window.AppNotificationBanner.Visibility);
        Assert.False(Get<bool>(window, "_isAppNotificationBannerExiting"));
        Assert.Equal(0, window.AppNotificationHoverSurface.Opacity);
        Assert.Equal(1, window.AppNotificationIconScale.ScaleX);
    });

    [Fact]
    public void ReducedMotionKeepsTranslationsAndScaleStillAndEscapeDismissesImmediately() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture();
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(true);
        try
        {
            var window = fixture.Window;
            window.ShowAppNotification(Notification());
            window.AnimateAppNotificationHover(true);
            Assert.Equal(0, window.AppNotificationBannerTranslate.Y);
            Assert.Equal(0, window.AppNotificationTitleTranslate.Y);
            Assert.Equal(0, window.AppNotificationIconRotate.Angle);
            Assert.Equal(1, window.AppNotificationIconScale.ScaleX);
            window.BeginAppNotificationInteraction(new Point(0, 0), 0);
            Assert.Equal(0, window.AppNotificationPressTranslate.Y);
            var keyboard = new KeyEventArgs(Keyboard.PrimaryDevice, new TestPresentationSource(), 0, Key.Escape)
                { RoutedEvent = Keyboard.KeyDownEvent };
            window.AppNotificationBanner.RaiseEvent(keyboard);
            Assert.False(window.IsAppNotificationBannerActive);
            Assert.Equal(Visibility.Collapsed, window.AppNotificationBanner.Visibility);
            Assert.Equal(Visibility.Collapsed, window.AppNotificationHoverSurface.Visibility);
            Assert.Equal(0, window.AppNotificationHoverSurface.Opacity);
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OpeningDuringEntranceOrExitClearsTheAttachmentWithoutAnOutgoingGhost(bool leaving) => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture(autoDismiss: true);
        var window = fixture.Window;
        using var host = new RenderHost(window);
        Motion.Stop(window.NotchBorder, FrameworkElement.WidthProperty, 300);
        Motion.Stop(window.NotchBorder, FrameworkElement.HeightProperty, 40);
        window.ShowAppNotification(Notification());
        var layer = (FrameworkElement)window.AppNotificationBanner.Parent;
        if (leaving)
        {
            await WpfFrameWaiter.UntilAsync(() => double.IsNaN(layer.Width), "banner settles before leaving", ct);
            window.DismissAppNotificationBanner();
        }
        await Task.Delay(100, ct);
        Assert.False(double.IsNaN(layer.Width));
        window.NavigateToNotchView(NotchView.Timer);
        await WpfFrameWaiter.UntilAsync(() => !Motion.IsRunning(window.NotchBorder, FrameworkElement.HeightProperty), "notch opens", ct);
        Assert.False(window.IsAppNotificationBannerActive);
        Assert.Equal(Visibility.Collapsed, window.AppNotificationBanner.Visibility);
        Assert.Equal(0, layer.Opacity);
        Assert.True(double.IsNaN(layer.Width));
        Assert.Equal(1, ((ScaleTransform)layer.RenderTransform).ScaleX);
        Assert.Null(Get<object?>(window, "_appNotificationDismissTimer"));
        Assert.DoesNotContain(window.NotchLiveContent.Children.Cast<UIElement>(), e => e is Image && Panel.GetZIndex(e) == 100);
    });

    [Fact]
    public void DismissalScalesTheFinalLayoutBackWithTheShell() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture();
        var window = fixture.Window;
        using var host = new RenderHost(window);
        window.ShowAppNotification(Notification());
        var layer = (FrameworkElement)window.AppNotificationBanner.Parent;
        await WpfFrameWaiter.UntilAsync(() => double.IsNaN(layer.Width), "banner fully presented", ct);
        window.DismissAppNotificationBanner();
        double width = layer.Width, height = layer.Height;
        await Task.Delay(70, ct);
        var scale = Assert.IsType<ScaleTransform>(layer.RenderTransform);
        Assert.Equal(window.NotchBorder.Width / width, scale.ScaleX, 4);
        Assert.Equal(window.NotchBorder.Height / height, scale.ScaleY, 4);
        Assert.True(scale.ScaleY < 1);
        Assert.Equal(1, window.AppNotificationBanner.Opacity);
        await WpfFrameWaiter.UntilAsync(() => !window.IsAppNotificationBannerActive, "attached leave finishes", ct);
        Assert.Equal(0, layer.Opacity);
        Assert.True(double.IsNaN(layer.Width));
    });

    [Theory]
    [InlineData("hidden")]
    [InlineData("fullscreen")]
    [InlineData("notch-hidden")]
    public void DismissProgressStopsOffscreenAndResumesFromItsRemainingTime(string reason) => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture(autoDismiss: true);
        var window = fixture.Window;
        using var host = new RenderHost(window);
        window.ShowAppNotification(Notification());
        await WpfFrameWaiter.UntilAsync(() => window.AppNotificationProgressScale.ScaleX < .98, "dismiss progress running", ct);
        if (reason == "hidden") host.Window.Hide();
        else typeof(MainWindow).GetProperty(reason == "fullscreen" ? "_isHiddenByFullscreen" : "_isNotchVisible",
            BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, reason == "fullscreen");
        typeof(MainWindow).GetMethod("RefreshAmbientAnimations", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
        double progress = window.AppNotificationProgressScale.ScaleX;
        int remaining = Get<int>(window, "_appNotificationRemainingMs");
        Assert.False(window.AppNotificationProgressScale.HasAnimatedProperties);
        Assert.Null(Get<object?>(window, "_appNotificationDismissTimer"));
        await Task.Delay(120, ct);
        Assert.Equal(progress, window.AppNotificationProgressScale.ScaleX);
        if (reason == "hidden") host.Window.Show();
        else typeof(MainWindow).GetProperty(reason == "fullscreen" ? "_isHiddenByFullscreen" : "_isNotchVisible",
            BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, reason != "fullscreen");
        typeof(MainWindow).GetMethod("RefreshAmbientAnimations", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
        Assert.NotNull(Get<object?>(window, "_appNotificationDismissTimer"));
        Assert.InRange(Get<int>(window, "_appNotificationRemainingMs"), remaining - 10, remaining);
        await WpfFrameWaiter.UntilAsync(() => window.AppNotificationProgressScale.ScaleX < progress - .01, "dismiss progress resumes", ct);
    });

    [Fact]
    public void DragFollowsThePointerAndHandsItsVelocityToTheReturnWithoutJumpingOnRegrab() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture();
        var window = fixture.Window;
        using var host = new RenderHost(window);
        window.ShowAppNotification(Notification());
        window.BeginAppNotificationInteraction(new Point(), 0);
        window.UpdateAppNotificationInteraction(new Point(20, 0), 100);
        Assert.Equal(20, window.AppNotificationShellTranslate.X);
        window.UpdateAppNotificationInteraction(new Point(40, 0), 140);
        Assert.False(window.EndAppNotificationInteraction(new Point(40, 0), 141));
        Assert.Equal(40, window.AppNotificationShellTranslate.X, 4);
        Assert.InRange(Motion.VelocityOf(window.AppNotificationShellTranslate, TranslateTransform.XProperty), 480, 510);
        await Task.Delay(40, ct);
        double position = window.AppNotificationShellTranslate.X;
        window.BeginAppNotificationInteraction(new Point(), 200);
        Assert.Equal(position, window.AppNotificationShellTranslate.X, 4);
        window.UpdateAppNotificationInteraction(new Point(8, 0), 300);
        Assert.Equal(position + 8, window.AppNotificationShellTranslate.X, 4);
        window.EndAppNotificationInteraction(new Point(8, 0), 450);
        await WpfFrameWaiter.UntilAsync(() => !Motion.IsRunning(window.AppNotificationShellTranslate, TranslateTransform.XProperty), "drag returns", ct);
        Assert.Equal(0, window.AppNotificationShellTranslate.X, 4);
    });

    [Fact]
    public void ReducedMotionCanReuseAnAttachmentAfterItsAnimatedPresentationWasCancelled() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture();
        var window = fixture.Window;
        using var host = new RenderHost(window);
        window.ShowAppNotification(Notification());
        await Task.Delay(100, ct);
        window.CancelAppNotificationBannerImmediate();
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(true);
        try
        {
            window.ShowAppNotification(Notification());
            var layer = (FrameworkElement)window.AppNotificationBanner.Parent;
            Assert.Equal(1, layer.Opacity);
            Assert.True(double.IsNaN(layer.Width));
            Assert.Equal(1, ((ScaleTransform)layer.RenderTransform).ScaleX);
            Assert.False(Motion.IsRunning(window.NotchBorder, FrameworkElement.WidthProperty));
            window.DismissAppNotificationBanner();
            Assert.Equal(0, layer.Opacity);
            Assert.False(window.IsAppNotificationBannerActive);
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    [Fact]
    public void ReplacementKeepsTheNewFaceReadableWhileTheOldContentLeaves() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture();
        var window = fixture.Window;
        using var host = new RenderHost(window);
        window.ShowAppNotification(Notification());
        var layer = (FrameworkElement)window.AppNotificationBanner.Parent;
        await WpfFrameWaiter.UntilAsync(() => double.IsNaN(layer.Width) && window.AppNotificationBlurCover.Source == null,
            "first message settles", ct);
        host.Window.UpdateLayout();
        window.ShowAppNotification(new AppNotificationInfo { AppName = "Files", Title = "Review completed", Body = "Ready to open." });
        Assert.Equal(1, window.AppNotificationBanner.Opacity);
        Assert.Equal(1, layer.Opacity);
        var outgoing = Assert.Single(window.NotchLiveContent.Children.OfType<Image>());
        Assert.True(outgoing.Width > 100);
        Assert.True(outgoing.Height > 20);
        var bitmap = Assert.IsAssignableFrom<System.Windows.Media.Imaging.BitmapSource>(outgoing.Source);
        Assert.True(bitmap.IsFrozen);
        byte[] pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        Assert.Contains(pixels.Where((_, i) => i % 4 == 3), alpha => alpha > 8);
        await WpfFrameWaiter.UntilAsync(() => !window.NotchLiveContent.Children.Contains(outgoing), "old message retires", ct);
        Assert.True(window.IsAppNotificationBannerActive);
    });

    private static AppNotificationInfo Notification() => new()
        { AppName = "Test notification app", Title = "Your download is ready", Body = "Open the application to continue." };
    private static GreetingAcceptanceTests.MainWindowFixture CreateFixture(bool autoDismiss = false) => new("en", greeting: false,
        configureSettings: settings =>
        {
            settings.EnableLocalOnlyMode = true;
            settings.ShowSystemNotifications = true;
            settings.AutoDismissNotifications = autoDismiss;
            settings.NotificationDuration = 5000;
        }, configureServices: services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));
    private static T Get<T>(MainWindow window, string field) => (T)typeof(MainWindow)
        .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;

    private sealed class RenderHost : IDisposable
    {
        private readonly MainWindow _owner;
        private readonly Window _window;
        internal Window Window => _window;
        internal RenderHost(MainWindow owner)
        {
            _owner = owner;
            var content = owner.Content;
            owner.Content = null;
            _window = new BackgroundWindow { Left = -20000, Width = 720, Height = 260, Content = content };
            _window.Show();
            _window.UpdateLayout();
        }
        public void Dispose()
        {
            var content = _window.Content;
            _window.Content = null;
            _window.Close();
            _owner.Content = content;
        }
    }

    private sealed class TestPresentationSource : PresentationSource
    {
        public override System.Windows.Media.Visual RootVisual { get; set; } = null!;
        public override bool IsDisposed => false;
        protected override System.Windows.Media.CompositionTarget GetCompositionTargetCore() => null!;
    }
}
