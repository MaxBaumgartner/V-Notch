using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Services;
using VNotch.Tests.Fakes;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class VolumeHudMotionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void MuteDrawsTheSlash() => Run(async (window, ct) =>
    {
        Update(window, 0xE995, false);
        Update(window, 0xE74F, true);
        var slash = Element(window, "VolumeIndicatorMuteSlash");
        Assert.Equal(Visibility.Visible, slash.Visibility);
        Assert.True(slash.StrokeDashOffset > 0);
        await WpfFrameWaiter.UntilAsync(() => Math.Abs(slash.StrokeDashOffset) < 0.005, "speaker slash draws", ct);
        Assert.Equal(0, slash.StrokeDashOffset, 2);
    });

    [Fact]
    public void WavesFadeInsteadOfJumping() => Run(async (window, ct) =>
    {
        Update(window, 0xE992, false);
        var wave = Element(window, "VolumeIndicatorWave2");
        double old = wave.Opacity;
        Update(window, 0xE995, true);
        Assert.Equal(old, wave.Opacity, 3);
        Assert.True(wave.HasAnimatedProperties);
        await WpfFrameWaiter.UntilAsync(() => Math.Abs(wave.Opacity - 1) < 0.005, "speaker wave fades in", ct);
        Assert.Equal(1, wave.Opacity, 2);
    });

    [Fact]
    public void UnmuteWipesSlashAway() => Run(async (window, ct) =>
    {
        Update(window, 0xE74F, false);
        Update(window, 0xE995, true);
        var slash = Element(window, "VolumeIndicatorMuteSlash");
        Assert.Equal(Visibility.Visible, slash.Visibility);
        await WpfFrameWaiter.UntilAsync(() => slash.Visibility == Visibility.Collapsed, "speaker slash wipes away", ct);
        Assert.Equal(Visibility.Collapsed, slash.Visibility);
    });

    private static void Update(MainWindow window, int icon, bool animate) =>
        typeof(MainWindow).GetMethod("UpdateSolidSpeakerIconState", Private)!.Invoke(window, new object[] { icon, Colors.White, animate });

    private static Path Element(MainWindow window, string name) =>
        (Path)typeof(MainWindow).GetField(name, Private | BindingFlags.Public)!.GetValue(window)!;

    private static void Run(Func<MainWindow, CancellationToken, Task> check) => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool reduce = AnimationConfig.ReduceMotion;
        try
        {
            await using var fixture = new GreetingAcceptanceTests.MainWindowFixture("en", false,
                settings => { settings.EnableLocalOnlyMode = true; settings.DisableMouseLeaveAutoClose = true; },
                services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));
            var window = fixture.Window;
            typeof(MainWindow).GetField("_cleanedUp", Private)!.SetValue(window, true);
            window.ShowActivated = false;
            window.Show();
            await WpfFrameWaiter.UntilAsync(() => window.IsLoaded && window.CollapsedContent.IsVisible, "collapsed notch", ct);
            await Task.Delay(250, ct);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.SystemIdle).Task.WaitAsync(ct);
            typeof(MainWindow).GetField("_cleanedUp", Private)!.SetValue(window, false);
            foreach (string field in new[] { "_captureVolumeWatcher", "_masterVolumeWatcher", "_brightnessWatcher", "_powerModeWatcher" })
                (typeof(MainWindow).GetField(field, Private)?.GetValue(window) as IDisposable)?.Dispose();
            AnimationConfig.SetReduceMotion(false);
            await check(window, ct);
        }
        finally { AnimationConfig.SetReduceMotion(reduce); }
    });
}
