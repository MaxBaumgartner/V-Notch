using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Path = System.Windows.Shapes.Path;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Services;
using VNotch.Tests.Fakes;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class MicMuteHudTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void FirstReadingDoesNotPlayTheDive() => Run(async (window, ct) =>
    {
        Invoke(window, "OnMicMuteChanged", true);
        Assert.False(Get<bool>(window, "_isVolumeIndicatorActive"));
        Assert.True(Get<bool?>(window, "_micMuteKnown"));
        await Task.CompletedTask;
    });

    [Fact]
    public void MuteShowsTheMicHudStruckThroughInRose() => Run(async (window, ct) =>
    {
        Invoke(window, "OnMicMuteChanged", false);
        Invoke(window, "OnMicMuteChanged", true);
        await WpfFrameWaiter.UntilAsync(() => Element<TextBlock>(window, "VolumeIndicatorMicWord").Text == Loc.Get("levelHud.micMuted"), "muted face reaches the hidden midpoint", ct);
        Assert.Equal("MicMute", Get<object>(window, "_levelHudKind").ToString());
        Assert.Equal(Visibility.Visible, Element<Path>(window, "VolumeIndicatorMicSlash").Visibility);
        Assert.Equal(Color.FromRgb(0xD2, 0x85, 0x7E), ((SolidColorBrush)Element<Path>(window, "VolumeIndicatorMicSlash").Stroke).Color);
        Assert.Equal(Loc.Get("levelHud.micMuted"), Element<TextBlock>(window, "VolumeIndicatorMicWord").Text);
        Assert.Equal(Visibility.Visible, Element<TextBlock>(window, "VolumeIndicatorMicWord").Visibility);
        Assert.Equal(Visibility.Collapsed, window.VolumeIndicatorTicksHost.Visibility);
        Assert.Equal(Visibility.Collapsed, window.VolumeIndicatorPercentText.Visibility);
        Assert.False(window.VolumeIndicatorContainer.IsHitTestVisible);
    });

    [Fact]
    public void MicVolumeChangeWithoutMuteChangeShowsNothing() => Run(async (window, ct) =>
    {
        Invoke(window, "OnMicMuteChanged", false);
        Invoke(window, "OnCaptureLevelChanged", 0.3f, false);
        Invoke(window, "OnCaptureLevelChanged", 0.6f, false);
        Assert.False(Get<bool>(window, "_isVolumeIndicatorActive"));
        await Task.CompletedTask;
    });

    [Fact]
    public void QuickDoubleToggleEndsOnFinalState() => Run(async (window, ct) =>
    {
        Invoke(window, "OnMicMuteChanged", false);
        Invoke(window, "OnMicMuteChanged", true);
        await Task.Delay(100, ct);
        Invoke(window, "OnMicMuteChanged", false);
        // Wait for the dive to settle rather than a fixed time: under load the last frames can land late.
        await WpfFrameWaiter.UntilAsync(() =>
            Math.Abs(Element<Canvas>(window, "VolumeIndicatorMic").Opacity - 1) < 0.001 &&
            Math.Abs(Get<ScaleTransform>(window, "VolumeIndicatorMicSquash").ScaleY - 1) < 0.001 &&
            Math.Abs(Get<TranslateTransform>(window, "VolumeIndicatorMicShift").Y) < 0.001, "mic dive settled", ct);
        Assert.Equal(Visibility.Collapsed, Element<Path>(window, "VolumeIndicatorMicSlash").Visibility);
        Assert.Equal(Loc.Get("levelHud.micOn"), Element<TextBlock>(window, "VolumeIndicatorMicWord").Text);
        AssertRest(window);
    });

    [Fact]
    public void ReducedMotionSwapsWithoutMoving() => Run(async (window, ct) =>
    {
        AnimationConfig.SetReduceMotion(true);
        Invoke(window, "OnMicMuteChanged", false);
        Invoke(window, "OnMicMuteChanged", true);
        Assert.Equal(Visibility.Visible, Element<Path>(window, "VolumeIndicatorMicSlash").Visibility);
        AssertRest(window);
        await Task.CompletedTask;
    });

    [Fact]
    public void CaptureDefaultReadingUpdatesQuietly() => Run(async (window, ct) =>
    {
        Invoke(window, "OnCaptureDefaultChanged", 0.3f, false);
        Invoke(window, "OnCaptureDefaultChanged", 0.5f, true);
        Assert.True(Get<bool?>(window, "_micMuteKnown"));
        Assert.False(Get<bool>(window, "_isVolumeIndicatorActive"));
        await Task.CompletedTask;
    });

    [Fact]
    public void AnotherHudCancelsTheDiveAndItsLateSwap() => Run(async (window, ct) =>
    {
        Invoke(window, "OnMicMuteChanged", false);
        Invoke(window, "OnMicMuteChanged", true);
        await Task.Delay(60, ct);
        Invoke(window, "OnSystemLevelChanged", Kind("Brightness"), 0.5f, false);
        await Task.Delay(450, ct);
        Assert.Equal("Brightness", Get<object>(window, "_levelHudKind").ToString());
        Assert.Equal(Visibility.Collapsed, Element<Canvas>(window, "VolumeIndicatorMic").Visibility);
        Assert.Equal(Visibility.Collapsed, Element<TextBlock>(window, "VolumeIndicatorMicWord").Visibility);
        Assert.Equal(Visibility.Visible, window.VolumeIndicatorTicksHost.Visibility);
        AssertRest(window);
    });

    [Fact]
    public void GreetingKeepsKnownMuteWithoutShowingHud() => Run(async (window, ct) =>
    {
        Invoke(window, "OnMicMuteChanged", false);
        Set(window, "_isGreetingActive", true);
        Invoke(window, "OnMicMuteChanged", true);
        Assert.True(Get<bool?>(window, "_micMuteKnown"));
        Assert.False(Get<bool>(window, "_isVolumeIndicatorActive"));
        Set(window, "_isGreetingActive", false);
        await Task.CompletedTask;
    });

    [Fact]
    public void TintRefreshKeepsThePreviousWordAndDiveInProgress() => Run(async (window, ct) =>
    {
        Invoke(window, "OnMicMuteChanged", false);
        Invoke(window, "OnMicMuteChanged", true);
        var shift = Get<TranslateTransform>(window, "VolumeIndicatorMicShift");
        double y = shift.Y;
        Assert.True(shift.HasAnimatedProperties);
        Invoke(window, "SetVolumeHudMediaTint", Colors.Purple, true);
        Assert.Equal(y, shift.Y);
        Assert.True(shift.HasAnimatedProperties);
        Assert.Equal(Loc.Get("levelHud.micOn"), Element<TextBlock>(window, "VolumeIndicatorMicWord").Text);
        await WpfFrameWaiter.UntilAsync(() => !shift.HasAnimatedProperties, "mic dive settles after tint refresh", ct);
        Assert.Equal(Loc.Get("levelHud.micMuted"), Element<TextBlock>(window, "VolumeIndicatorMicWord").Text);
        AssertRest(window);
    });

    [Fact]
    public void ImmediateDismissCancelsThePendingFaceSwap() => Run(async (window, ct) =>
    {
        Invoke(window, "OnMicMuteChanged", false);
        Invoke(window, "OnMicMuteChanged", true);
        Invoke(window, "DismissVolumeIndicatorImmediate", false, false);
        await Task.Delay(450, ct);
        Assert.False(Get<bool>(window, "_isVolumeIndicatorActive"));
        Assert.False(Get<bool>(window, "_micMuteShown"));
        AssertRest(window);
    });

    [Fact]
    public void RestGlyphShowsWhileMuted() => Run(async (window, ct) =>
    {
        Invoke(window, "OnMicMuteChanged", false);
        Invoke(window, "OnMicMuteChanged", true);
        var glyph = Element<FrameworkElement>(window, "MicMutedRestGlyph");
        await WpfFrameWaiter.UntilAsync(() => !Motion.IsRunning(glyph, UIElement.OpacityProperty), "rest glyph appears", ct);
        Assert.Equal(Visibility.Visible, glyph.Visibility);
        Assert.Equal(1, glyph.Opacity, 2);
    });

    [Fact]
    public void RestGlyphHidesWhenNoCaptureDevice() => Run(async (window, ct) =>
    {
        Invoke(window, "OnCaptureDefaultChanged", 0.3f, true);
        Invoke(window, "OnCaptureEndpointGone");
        await WpfFrameWaiter.UntilAsync(() => Element<FrameworkElement>(window, "MicMutedRestGlyph").Visibility == Visibility.Collapsed, "rest glyph hides without capture", ct);
        Assert.Null(Get<bool?>(window, "_micMuteKnown"));
        Assert.Equal(Visibility.Collapsed, Element<FrameworkElement>(window, "MicMutedRestGlyph").Visibility);
        Invoke(window, "OnCaptureDefaultChanged", 0.3f, true);
        await WpfFrameWaiter.UntilAsync(() => !Motion.IsRunning(Element<FrameworkElement>(window, "MicMutedRestGlyph"), UIElement.OpacityProperty), "rest glyph returns", ct);
        Assert.Equal(Visibility.Visible, Element<FrameworkElement>(window, "MicMutedRestGlyph").Visibility);
    });

    [Fact]
    public void CallWarningHidesRestGlyph() => Run(async (window, ct) =>
    {
        Invoke(window, "OnCaptureDefaultChanged", 0.3f, true);
        Set(window, "_micMutedShown", true);
        Invoke(window, "UpdateMicRestGlyph");
        await WpfFrameWaiter.UntilAsync(() => Element<FrameworkElement>(window, "MicMutedRestGlyph").Visibility == Visibility.Collapsed, "call warning replaces rest glyph", ct);
        Assert.Equal(Visibility.Collapsed, Element<FrameworkElement>(window, "MicMutedRestGlyph").Visibility);
    });

    [Fact]
    public void SettingOffHidesRestGlyph() => Run(async (window, ct) =>
    {
        Invoke(window, "OnCaptureDefaultChanged", 0.3f, true);
        Get<VNotch.Models.NotchSettings>(window, "_settings").ShowMutedMicGlyph = false;
        Invoke(window, "UpdateMicRestGlyph");
        await WpfFrameWaiter.UntilAsync(() => Element<FrameworkElement>(window, "MicMutedRestGlyph").Visibility == Visibility.Collapsed, "setting hides rest glyph", ct);
        Assert.Equal(Visibility.Collapsed, Element<FrameworkElement>(window, "MicMutedRestGlyph").Visibility);
    });

    private static void AssertRest(MainWindow window)
    {
        Assert.Equal(1, Element<Canvas>(window, "VolumeIndicatorMic").Opacity, 3);
        Assert.Equal(1, Element<TextBlock>(window, "VolumeIndicatorMicWord").Opacity, 3);
        Assert.Equal(0, Get<TranslateTransform>(window, "VolumeIndicatorMicShift").Y, 3);
        Assert.Equal(1, Get<ScaleTransform>(window, "VolumeIndicatorMicSquash").ScaleY, 3);
    }

    private static void Run(Func<MainWindow, CancellationToken, Task> check) => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool reduce = AnimationConfig.ReduceMotion;
        try
        {
            await using var fixture = new GreetingAcceptanceTests.MainWindowFixture("en", false,
                settings => { settings.EnableLocalOnlyMode = true; settings.DisableMouseLeaveAutoClose = true; },
                services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));
            var window = fixture.Window;
            // Drain the queued WinRT notification query while startup callbacks are suppressed on the test desktop.
            Set(window, "_cleanedUp", true);
            try
            {
                window.ShowActivated = false;
                window.Show();
                await WpfFrameWaiter.UntilAsync(() => window.IsLoaded && window.CollapsedContent.IsVisible, "collapsed notch", ct);
                await Task.Delay(250, ct);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.SystemIdle).Task.WaitAsync(ct);
            }
            finally { Set(window, "_cleanedUp", false); }
            foreach (string field in new[] { "_captureVolumeWatcher", "_masterVolumeWatcher", "_brightnessWatcher", "_powerModeWatcher" })
                (typeof(MainWindow).GetField(field, Private)?.GetValue(window) as IDisposable)?.Dispose();
            await Task.Delay(50, ct);
            Set(window, "_micMuteKnown", null);
            AnimationConfig.SetReduceMotion(false);
            await check(window, ct);
        }
        finally
        {
            AnimationConfig.SetReduceMotion(reduce);
        }
    });

    private static object Kind(string name) => Enum.Parse(typeof(MainWindow).GetNestedType("LevelHudKind", BindingFlags.NonPublic)!, name);
    private static T Element<T>(MainWindow window, string name) => Get<T>(window, name);
    private static T Get<T>(MainWindow window, string name) => (T)Assert.IsAssignableFrom<FieldInfo>(typeof(MainWindow).GetField(name, Private | BindingFlags.Public)).GetValue(window)!;
    private static void Set(MainWindow window, string name, object? value) => Assert.IsAssignableFrom<FieldInfo>(typeof(MainWindow).GetField(name, Private)).SetValue(window, value);
    private static void Invoke(MainWindow window, string name, params object?[] arguments) => Assert.IsAssignableFrom<MethodInfo>(typeof(MainWindow).GetMethod(name, Private)).Invoke(window, arguments);
}
