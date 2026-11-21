using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Models;
using VNotch.Controllers;
using VNotch.Presenters;
using VNotch.Services;
using VNotch.Tests.Fakes;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class NotchTabMotionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void NavigationRetargetIsAppliedBeforeTheNextRender() => SharedStaTestRunner.RunAsync(async _ =>
    {
        await using var fixture = MusicMotionTests.Create();
        var window = fixture.Window;
        window.NavigateToNotchView(NotchView.Media);
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);
        Assert.Null(typeof(MainWindow).GetField("_pendingTransitionRequest", Private)!.GetValue(window));
        Assert.Equal(NotchView.Media, (NotchView?)typeof(MainWindow).GetField("_navigationVisualTarget", Private)!.GetValue(window));
    });

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void LocalizingNavigationPreservesTheIconVisuals(string language) => SharedStaTestRunner.RunAsync(async _ =>
    {
        await using var fixture = new GreetingAcceptanceTests.MainWindowFixture(language, false,
            settings => settings.EnableLocalOnlyMode = true,
            services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));
        var window = fixture.Window;
        foreach (var button in window.NavTabsStackPanel.Children.OfType<Border>())
        {
            Assert.NotEmpty(HomeSchoolTabTests.Descendants<System.Windows.Shapes.Path>(button));
            Assert.DoesNotContain(HomeSchoolTabTests.Descendants<TextBlock>(button), text => !string.IsNullOrEmpty(text.Text));
        }
    });

    [Fact]
    public void TabClickDuringOpeningRetargetsImmediately() => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        await using var fixture = new GreetingAcceptanceTests.MainWindowFixture("en", false,
            settings => { settings.EnableLocalOnlyMode = true; settings.DisableMouseLeaveAutoClose = true; },
            services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));
        try
        {
            var window = fixture.Window;
            AnimationConfig.SetReduceMotion(true);
            window.Show();
            var coordinator = (NotchTransitionCoordinator)typeof(MainWindow).GetField("_transitionCoordinator", Private)!.GetValue(window)!;
            AnimationConfig.SetReduceMotion(false);
            window.NavigateToNotchView(NotchView.Media);
            await WpfFrameWaiter.UntilAsync(() => window.NotchBorder.Width > 330 && coordinator.IsTransitionActive,
                "opening shell moves", ct);
            window.NavigateToNotchView(NotchView.Secondary);
            Assert.Equal(NotchView.Secondary, coordinator.TargetView);
            await WpfFrameWaiter.UntilAsync(() => !coordinator.IsTransitionActive, "latest tab settles", ct);
            Assert.Equal(NotchView.Secondary, coordinator.CurrentView);
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    [Fact]
    public void HoverDuringOpeningAndClosingNeverLeavesTheNotchBusy() => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        await using var fixture = new GreetingAcceptanceTests.MainWindowFixture("en", false,
            settings => { settings.EnableLocalOnlyMode = true; settings.DisableMouseLeaveAutoClose = true; },
            services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));
        try
        {
            var window = fixture.Window;
            AnimationConfig.SetReduceMotion(true);
            window.Show();
            var coordinator = (NotchTransitionCoordinator)typeof(MainWindow).GetField("_transitionCoordinator", Private)!.GetValue(window)!;
            var hover = typeof(MainWindow).GetMethod("AnimateNotchHover", Private)!;
            AnimationConfig.SetReduceMotion(false);

            async Task Settles(string what)
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                await WpfFrameWaiter.UntilAsync(() => !coordinator.IsTransitionActive, what, ct);
                Assert.True(clock.ElapsedMilliseconds < 2500, $"{what} took {clock.ElapsedMilliseconds} ms");
            }

            for (int round = 0; round < 2; round++)
            {
                window.NavigateToNotchView(NotchView.Media);
                await WpfFrameWaiter.UntilAsync(() => window.NotchBorder.Width > 330 && coordinator.IsTransitionActive,
                    "opening shell moves", ct);
                // The pointer that clicked is still over the notch, leaves and comes back while it opens.
                hover.Invoke(window, [false]);
                hover.Invoke(window, [true]);
                await Settles("opening with a hover on the way");
                Assert.Equal(NotchView.Media, coordinator.CurrentView);

                Assert.True(coordinator.RequestView(NotchView.Compact, "HoverTest"));
                await Task.Delay(60, ct);
                hover.Invoke(window, [true]);
                hover.Invoke(window, [false]);
                await Settles("closing with a hover on the way");
                Assert.Equal(NotchView.Compact, coordinator.CurrentView);
            }
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    [Fact]
    public void RetargetingVisibleChromePreservesTheRenderedFrame() => SharedStaTestRunner.Run(() =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        using var fixture = new GreetingAcceptanceTests.MainWindowFixture("en", false,
            settings => settings.EnableLocalOnlyMode = true);
        var window = fixture.Window;
        try
        {
            AnimationConfig.SetReduceMotion(false);
            AnimationConfig.ClientAreaAnimationOverride = true;
            typeof(MainWindow).GetField("_navigationVisualTarget", Private)!.SetValue(window, NotchView.Media);
            window.NavIconsPanel.Visibility = Visibility.Visible;
            window.SettingsButton.Visibility = Visibility.Visible;
            HoldAt(window.NavIconsPanel, UIElement.OpacityProperty, 0, 1);
            HoldAt(window.NavIconsTranslate, TranslateTransform.YProperty, -6, 0);
            HoldAt(window.SettingsScale, ScaleTransform.ScaleXProperty, .96, 1);
            HoldAt(window.SettingsScale, ScaleTransform.ScaleYProperty, .96, 1);
            HoldAt(window.SettingsRotate, RotateTransform.AngleProperty, 32, 45);
            double opacity = window.NavIconsPanel.Opacity, y = window.NavIconsTranslate.Y;
            double scale = window.SettingsScale.ScaleX, angle = window.SettingsRotate.Angle;

            Invoke(window, "AnimateStatusBarReveal", true);

            Assert.Equal(opacity, window.NavIconsPanel.Opacity, 8);
            Assert.Equal(y, window.NavIconsTranslate.Y, 8);
            Assert.Equal(scale, window.SettingsScale.ScaleX, 8);
            Assert.Equal(angle, window.SettingsRotate.Angle, 8);
        }
        finally
        {
            AnimationConfig.ClientAreaAnimationOverride = null;
            AnimationConfig.SetReduceMotion(previous);
        }
    });

    [Fact]
    public void ClickingATabDoesNotPlayADragDropSettle() => SharedStaTestRunner.Run(() =>
    {
        using var fixture = new GreetingAcceptanceTests.MainWindowFixture("en", false,
            settings => settings.EnableLocalOnlyMode = true);
        var window = fixture.Window;
        typeof(MainWindow).GetField("_navDragItem", Private)!.SetValue(window, window.HomeIconButton);
        Invoke(window, "EndNavDrag", false);
        var transforms = Assert.IsType<TransformGroup>(window.HomeIconButton.RenderTransform);
        Assert.All(transforms.Children, transform => Assert.False(transform.HasAnimatedProperties));
    });

    private static void Invoke(MainWindow window, string method, params object[] args) =>
        typeof(MainWindow).GetMethod(method, Private)!.Invoke(window, args);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LatestTabClickWinsWithoutRestartingTheNavigationIcons(bool island) => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        await using var fixture = new GreetingAcceptanceTests.MainWindowFixture("en", false,
            settings =>
            {
                settings.EnableLocalOnlyMode = true; settings.DisableMouseLeaveAutoClose = true;
                settings.EnableDynamicIslandMode = island;
            },
            services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));
        var window = fixture.Window;
        var coordinator = (NotchTransitionCoordinator)typeof(MainWindow).GetField("_transitionCoordinator", Private)!.GetValue(window)!;
        try
        {
            AnimationConfig.SetReduceMotion(true);
            window.Show();
            window.NavigateToNotchView(NotchView.Media);
            await WpfFrameWaiter.UntilAsync(() => coordinator.CurrentView == NotchView.Media && !coordinator.IsTransitionActive,
                "initial media view", ct);
            AnimationConfig.SetReduceMotion(false);
            double navigationY = window.NavIconsTranslate.Y;
            foreach (var target in new[] { NotchView.Timer, NotchView.AudioMixer, NotchView.Secondary, NotchView.Media, NotchView.Timer })
            {
                window.NavigateToNotchView(target);
                Assert.Equal(target, coordinator.TargetView);
                await WpfFrameWaiter.NextAsync(ct);
                Assert.True(window.NotchBorder.IsHitTestVisible);
                Assert.Equal(navigationY, window.NavIconsTranslate.Y, 8);
                Assert.Equal(1, window.NavIconsPanel.Opacity, 8);
                Assert.Equal(1, window.SettingsScale.ScaleX, 8);
                Assert.Equal(45, window.SettingsRotate.Angle, 8);
            }
            await WpfFrameWaiter.UntilAsync(() => !coordinator.IsTransitionActive, "latest tab settled", ct);
            Assert.Equal(NotchView.Timer, coordinator.CurrentView);
            Assert.Equal(Visibility.Visible, window.TimerContent.Visibility);
            Assert.Equal(1, window.TimerContent.Opacity);
            Assert.True(window.TimerContent.IsHitTestVisible);
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void ReversingAPageTransitionPreservesTheRenderedFrameAndRestoresLayout(int direction) => SharedStaTestRunner.RunAsync(async ct =>
    {
        var mediaTransform = new TranslateTransform(0, 6);
        var media = new Canvas { Name = "ExpandedContent", RenderTransform = mediaTransform };
        var timer = new Canvas { Visibility = Visibility.Collapsed, Opacity = 0 };
        using var presenter = new NotchContentTransitionPresenter(new() { ExpandedContent = media, TimerContent = timer });
        var motion = new TransitionMotionConfig(TimeSpan.FromMilliseconds(250), AnimationPrimitives._easeUiOut, 60, false);
        var plan = new TransitionPlan(1, NotchView.Media, NotchView.Timer, NotchShapeState.Expanding, 300, 200, 20, motion, direction);
        presenter.TransitionContent(plan, _ => { });
        Assert.Equal(8 * direction, timer.RenderTransform.Value.OffsetX, 8);
        Assert.Equal(1, timer.RenderTransform.Value.M11);
        Assert.Equal(1, timer.RenderTransform.Value.M22);
        Assert.Equal(2, Assert.IsType<System.Windows.Media.Effects.BlurEffect>(timer.Effect).Radius);
        Assert.False(timer.IsHitTestVisible);
        var mediaOffset = Assert.IsType<TranslateTransform>(Assert.IsType<TransformGroup>(media.RenderTransform).Children[^1]);
        var timerOffset = Assert.IsType<TranslateTransform>(Assert.IsType<TransformGroup>(timer.RenderTransform).Children[^1]);
        HoldAt(mediaOffset, TranslateTransform.XProperty, 0, -6 * direction);
        HoldAt(timerOffset, TranslateTransform.XProperty, 8 * direction, 0);
        HoldAt(media, UIElement.OpacityProperty, 1, 0);
        HoldAt(timer, UIElement.OpacityProperty, 0, 1);
        presenter.CancelActiveTransition();
        Matrix mediaFrame = media.RenderTransform.Value, timerFrame = timer.RenderTransform.Value;
        double mediaOpacity = media.Opacity, timerOpacity = timer.Opacity;
        bool completed = false;

        presenter.TransitionContent(plan with
        {
            SessionId = 2,
            FromView = NotchView.Timer,
            TargetView = NotchView.Media,
            NavigationDirection = -direction
        }, _ => completed = true);

        Assert.Equal(mediaFrame, media.RenderTransform.Value);
        Assert.Equal(timerFrame, timer.RenderTransform.Value);
        Assert.Equal(mediaOpacity, media.Opacity, 8);
        Assert.Equal(timerOpacity, timer.Opacity, 8);
        await WpfFrameWaiter.UntilAsync(() => completed, "reversed page transition settled", ct);
        Assert.Same(mediaTransform, media.RenderTransform);
        Assert.Equal(6, media.RenderTransform.Value.OffsetY);
        Assert.Equal(0, media.RenderTransform.Value.OffsetX);
        Assert.True(media.IsHitTestVisible);
        Assert.Equal(Visibility.Collapsed, timer.Visibility);
    });

    [Theory]
    [InlineData(NotchView.Claude)]
    [InlineData(NotchView.Pi)]
    [InlineData(NotchView.School)]
    public void ReturningToADepartingDashboardKeepsItsPageTransform(NotchView view) => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        bool? animations = AnimationConfig.ClientAreaAnimationOverride;
        await using var fixture = MusicMotionTests.Create();
        var window = fixture.Window;
        try
        {
            AnimationConfig.ClientAreaAnimationOverride = true;
            AnimationConfig.SetReduceMotion(true);
            window.Left = window.Top = -20000;
            typeof(MainWindow).GetField("_cleanedUp", Private)!.SetValue(window, true);
            window.Show();
            typeof(MainWindow).GetField("_cleanedUp", Private)!.SetValue(window, false);
            window.NavigateToNotchView(view);
            var coordinator = (NotchTransitionCoordinator)typeof(MainWindow).GetField("_transitionCoordinator", Private)!.GetValue(window)!;
            await WpfFrameWaiter.UntilAsync(() => !coordinator.IsTransitionActive, "initial dashboard", ct);
            AnimationConfig.SetReduceMotion(false);
            window.NavigateToNotchView(NotchView.Media);
            FrameworkElement page = view == NotchView.Claude ? window.ClaudeContent : view == NotchView.Pi ? window.PiContent : window.SchoolContent;
            await WpfFrameWaiter.UntilAsync(() => page.RenderTransform is TransformGroup, "departing dashboard", ct);
            var transform = Assert.IsType<TransformGroup>(page.RenderTransform);
            HoldAt(Assert.IsType<TranslateTransform>(transform.Children[^1]), TranslateTransform.XProperty, 0, -6);
            Matrix frame = transform.Value;
            window.NavigateToNotchView(view);
            var pending = typeof(MainWindow).GetField("_pendingTransitionRequest", Private)!;
            var request = pending.GetValue(window);
            Assert.NotNull(request);
            pending.SetValue(window, null);
            Invoke(window, "ExecuteTransitionRequest", request!);
            Assert.Same(transform, page.RenderTransform);
            Assert.Equal(frame, page.RenderTransform.Value);
            await WpfFrameWaiter.UntilAsync(() => !coordinator.IsTransitionActive, "returning dashboard settles", ct);
        }
        finally { AnimationConfig.SetReduceMotion(previous); AnimationConfig.ClientAreaAnimationOverride = animations; }
    });

    private static void HoldAt(IAnimatable target, DependencyProperty property, double from, double to)
    {
        var clock = (AnimationClock)new DoubleAnimation(from, to, TimeSpan.FromSeconds(1)).CreateClock(true);
        target.ApplyAnimationClock(property, clock);
        clock.Controller!.Begin();
        clock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(350), TimeSeekOrigin.BeginTime);
    }
}
