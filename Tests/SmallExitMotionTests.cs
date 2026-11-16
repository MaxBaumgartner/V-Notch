using System.Reflection;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Services;
using VNotch.Tests.Fakes;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class SmallExitMotionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void HiddenIdleGlanceDoesNotAnimateWhileLeaving() => Run((window, ct) =>
    {
        window.CollapsedContent.Visibility = Visibility.Collapsed;
        SeedVisible(window, "ShowIdleGlance");
        Invoke(window, "ShowIdleGlance", false);
        Assert.Equal(Visibility.Collapsed, window.IdleGlance.Visibility);
        Assert.Equal(0, window.IdleGlance.Opacity);
        Assert.False(window.IdleGlance.HasAnimatedProperties);
        Assert.Equal(-4, window.IdleGlanceShift.Y, 3);
        return Task.CompletedTask;
    });

    [Fact]
    public void CallGlyphShrinksOut() => Run(async (window, ct) =>
    {
        SeedVisible(window, "SetMicMutedGlyph");
        Invoke(window, "SetMicMutedGlyph", false);
        Assert.True(window.MicMutedGlyph.Opacity > 0);
        Assert.Equal(Visibility.Visible, window.MicMutedGlyph.Visibility);
        await WpfFrameWaiter.UntilAsync(() => window.MicMutedGlyph.Visibility == Visibility.Collapsed, "call glyph exits", ct);
        Assert.Equal(Visibility.Collapsed, window.MicMutedGlyph.Visibility);
        await WpfFrameWaiter.UntilAsync(() => !Motion.IsRunning(window.MicMutedGlyphScale, System.Windows.Media.ScaleTransform.ScaleXProperty), "call glyph shrinks", ct);
        Assert.Equal(Motion.ExitToScale, window.MicMutedGlyphScale.ScaleX, 3);
        Assert.Equal(Motion.ExitToScale, window.MicMutedGlyphScale.ScaleY, 3);
    });

    [Fact]
    public void CrossFadeTextSwapsMidway() => Run(async (window, ct) =>
    {
        var target = window.ClaudeNotificationStatus;
        target.Text = "Before";
        Invoke(window, "CrossFadeText", target, "After", 120);
        Assert.Equal("Before", target.Text);
        await WpfFrameWaiter.UntilAsync(() => target.Text == "After", "text swaps at fade midpoint", ct);
        Assert.Equal("After", target.Text);
        await WpfFrameWaiter.UntilAsync(() => !target.HasAnimatedProperties, "text fade settles", ct);
        Assert.Equal(1, target.Opacity, 3);
    });

    [Theory]
    [InlineData("ShowIdleGlance")]
    [InlineData("SetMicMutedGlyph")]
    public void ShowingAgainCancelsTheExit(string method) => Run(async (window, ct) =>
    {
        SeedVisible(window, method);
        Invoke(window, method, false);
        await Task.Delay(60, ct);
        Invoke(window, method, true);
        FrameworkElement target = method == "ShowIdleGlance" ? window.IdleGlance : window.MicMutedGlyph;
        await WpfFrameWaiter.UntilAsync(() => !Motion.IsRunning(target, UIElement.OpacityProperty), "reversed entrance settles", ct);
        Assert.Equal(Visibility.Visible, target.Visibility);
        Assert.Equal(1, target.Opacity, 3);
    });

    [Fact]
    public void RepeatedTextRequestsDoNotRestartOrOverwriteTheLatestSwap() => Run(async (window, ct) =>
    {
        var target = window.ClaudeNotificationStatus;
        target.Text = "Before";
        Invoke(window, "CrossFadeText", target, "After", 120);
        await Task.Delay(40, ct);
        Invoke(window, "CrossFadeText", target, "After", 120);
        await WpfFrameWaiter.UntilAsync(() => target.Text == "After", "repeated text reaches its midpoint", ct);
        Assert.Equal("After", target.Text);
        Invoke(window, "CrossFadeText", target, "Final", 120);
        await WpfFrameWaiter.UntilAsync(() => target.Text == "Final" && !target.HasAnimatedProperties, "latest text fade settles", ct);
        Assert.Equal("Final", target.Text);
        Assert.Equal(1, target.Opacity, 3);
    });

    [Fact]
    public void ReducedMotionChangesStateImmediatelyAndCancelsPendingSwaps() => Run(async (window, ct) =>
    {
        SeedVisible(window, "ShowIdleGlance");
        SeedVisible(window, "SetMicMutedGlyph");
        var target = window.ClaudeNotificationStatus;
        Invoke(window, "CrossFadeText", target, "Stale", 120);
        AnimationConfig.SetReduceMotion(true);
        Invoke(window, "ShowIdleGlance", false);
        Invoke(window, "SetMicMutedGlyph", false);
        Invoke(window, "CrossFadeText", target, "Now", 120);
        Assert.Equal(Visibility.Collapsed, window.IdleGlance.Visibility);
        Assert.Equal(Visibility.Collapsed, window.MicMutedGlyph.Visibility);
        Assert.Equal("Now", target.Text);
        Assert.Equal(1, target.Opacity);
        await Task.Delay(160, ct);
        Assert.Equal("Now", target.Text);
    });

    [Fact]
    public void ReducedMotionDropConfirmationKeepsItsHoldAndStopsWhenHidden() => Run(async (window, ct) =>
    {
        AnimationConfig.SetReduceMotion(true);
        typeof(MainWindow).GetField("_cleanedUp", Private)!.SetValue(window, false);
        typeof(MainWindow).GetProperty("_isSecondaryView", Private)!.SetValue(window, true);
        try
        {
            Invoke(window, "PlayClipboardDropFeedback");
            Assert.Equal(1, window.ClipboardDropBadge.Opacity);
            Assert.False(window.ClipboardDropBadge.HasAnimatedProperties);
            var timerField = typeof(MainWindow).GetField("_clipboardDropBadgeHold", Private)!;
            Assert.Equal(TimeSpan.FromMilliseconds(650), Assert.IsType<UiScheduledTimer>(timerField.GetValue(window)).Interval);
            await WpfFrameWaiter.UntilAsync(() => window.ClipboardDropBadge.Opacity == 0,
                "reduced-motion confirmation reading time finishes", ct);
            Assert.Null(timerField.GetValue(window));

            Invoke(window, "PlayClipboardDropFeedback");
            window.ClipboardDropBadge.Visibility = Visibility.Collapsed;
            Assert.Equal(0, window.ClipboardDropBadge.Opacity);
            Assert.Null(timerField.GetValue(window));
        }
        finally { typeof(MainWindow).GetField("_cleanedUp", Private)!.SetValue(window, true); }
    });

    private static void SeedVisible(MainWindow window, string method)
    {
        AnimationConfig.SetReduceMotion(true);
        Invoke(window, method, false);
        Invoke(window, method, true);
        AnimationConfig.SetReduceMotion(false);
    }

    private static void Run(Func<MainWindow, CancellationToken, Task> test) => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool reduced = AnimationConfig.ReduceMotion;
        await using var fixture = new GreetingAcceptanceTests.MainWindowFixture("en", false,
            settings => { settings.EnableLocalOnlyMode = true; settings.DisableMouseLeaveAutoClose = true; },
            services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));
        var renderHost = new BackgroundWindow { Left = -20000, Top = -20000, Width = 720, Height = 240 };
        var content = fixture.Window.Content;
        try
        {
            var window = fixture.Window;
            // Exercise the visuals without starting desktop watchers or notification access.
            await window.CleanupAsync();
            window.Content = null;
            renderHost.Content = content;
            renderHost.Show();
            renderHost.UpdateLayout();
            AnimationConfig.SetReduceMotion(false);
            await test(window, ct);
        }
        finally
        {
            renderHost.Content = null;
            renderHost.Close();
            fixture.Window.Content = content;
            AnimationConfig.SetReduceMotion(reduced);
        }
    });

    private static void Invoke(MainWindow window, string name, params object[] arguments)
    {
        var method = typeof(MainWindow).GetMethod(name, Private);
        Assert.NotNull(method);
        method.Invoke(window, arguments);
    }
}
