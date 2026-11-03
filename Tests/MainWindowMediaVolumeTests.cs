using System.Reflection;
using System.Windows;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Services;
using VNotch.Tests.Fakes;
using Xunit;

namespace VNotch.Tests;

// The music tab's slider for the volume of the app that plays the media (Spotify, ...).
[Collection("Localization")]
public sealed class MainWindowMediaVolumeTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void PullingPastTheEndGivesLessTheFurtherItGoes()
    {
        Assert.Equal(0, MainWindow.RubberBand(0, 100));
        Assert.Equal(0, MainWindow.RubberBand(-5, 100));
        double near = MainWindow.RubberBand(10, 100), far = MainWindow.RubberBand(200, 100), farther = MainWindow.RubberBand(400, 100);
        Assert.True(near > 0 && near < 10, $"near {near}");
        Assert.True(far > near && farther > far, "keeps giving");
        Assert.True(farther - far < far - near, "but stiffer the further it goes");
        Assert.True(MainWindow.RubberBand(1e9, 100) < 50.01, "never more than half the bar");
    }

    [Fact]
    public void MusicTabReadsTheAppVolumeAndTheWheelStepsFromIt() => SharedStaTestRunner.RunAsync(async ct =>
    {
        var media = new FakeMediaDetectionService { ReadSessionVolume = () => (true, .40f, false) };
        await using var fixture = Create(media);
        var window = fixture.Window;
        window.SetDebugViewLock(true);
        window.Show();
        await WpfFrameWaiter.UntilAsync(() => Get<bool>(window, "_isStartupLayoutReady") && !Get<bool>(window, "_isAnimating"), "window startup", ct);

        window.SetDebugViewState("MediaExpanded");
        await WpfFrameWaiter.UntilAsync(() => Get<bool>(window, "_mediaVolumeKnown") && !Get<bool>(window, "_isAnimating"), "app volume read on open", ct);
        Assert.Equal(.40, window.MediaVolumeBar.Value, 4);
        Assert.True(Get<UiScheduledTimer>(window, "_volumeSyncTimer").IsEnabled);

        var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120) { RoutedEvent = UIElement.MouseWheelEvent };
        Invoke(window, "MediaWidget_MouseWheel", window.MediaWidget, wheel);
        Assert.True(wheel.Handled);
        await WpfFrameWaiter.UntilAsync(() => media.LastSessionVolume.HasValue, "wheel write", ct);
        Assert.Equal(.45f, media.LastSessionVolume!.Value, 4);
        Assert.Equal(.45, window.MediaVolumeBar.Value, 4);
        Assert.False(Get<bool>(window, "_volumeSynced"));

        window.SetDebugViewState("CollapsedNotch");
        await WpfFrameWaiter.UntilAsync(() => !Get<UiScheduledTimer>(window, "_volumeSyncTimer").IsEnabled, "polling stops with the tab", ct);
        Assert.False(Get<bool>(window, "_mediaVolumeKnown"));
    });

    [Fact]
    public void TheWheelDoesNothingOutsideTheMusicTab() => SharedStaTestRunner.RunAsync(async ct =>
    {
        var media = new FakeMediaDetectionService { ReadSessionVolume = () => (true, .40f, false) };
        await using var fixture = Create(media);
        var window = fixture.Window;
        var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120) { RoutedEvent = UIElement.MouseWheelEvent };
        Invoke(window, "MediaWidget_MouseWheel", window.MediaWidget, wheel);
        Assert.False(wheel.Handled);
        Assert.Null(media.LastSessionVolume);
        ct.ThrowIfCancellationRequested();
    });

    [Fact]
    public void DraggingTheSliderWritesTheAppVolumeAndReleases() => SharedStaTestRunner.RunAsync(async ct =>
    {
        var media = new FakeMediaDetectionService();
        await using var fixture = Create(media);
        var window = fixture.Window;
        window.MediaVolumeColumn.Measure(new Size(18, 100));
        window.MediaVolumeColumn.Arrange(new Rect(0, 0, 18, 100));
        Assert.True(window.MediaVolumeTrack.ActualHeight > 0);

        var down = Click();
        Invoke(window, "MediaVolumeBar_MouseDown", window, down);
        Assert.True(down.Handled);
        Assert.True(Get<bool>(window, "_isDraggingMediaVolume"));
        await WpfFrameWaiter.UntilAsync(() => media.LastSessionVolume.HasValue, "slider drag write", ct);
        Assert.InRange(media.LastSessionVolume!.Value, 0, 1);

        var up = Click();
        Invoke(window, "MediaVolumeBar_MouseUp", window, up);
        Assert.True(up.Handled);
        Assert.False(Get<bool>(window, "_isDraggingMediaVolume"));
        var ignored = Click();
        Invoke(window, "MediaVolumeBar_MouseUp", window, ignored);
        Assert.False(ignored.Handled);
    });

    [Fact]
    public void ASlowReadCannotUndoANewerChange() => SharedStaTestRunner.RunAsync(async ct =>
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var media = new FakeMediaDetectionService
        {
            ReadSessionVolume = () =>
            {
                started.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
                return (true, .9f, false);
            }
        };
        await using var fixture = Create(media);
        var window = fixture.Window;
        try
        {
            Invoke(window, "RequestMediaVolumeRead");
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            Invoke(window, "SetMediaVolumeFromUser", .2f, false);
            release.Set();
            await WpfFrameWaiter.UntilAsync(() => !Get<bool>(window, "_mediaVolumeReadPending"), "stale read discarded", ct);
            Assert.Equal(.2f, Get<float>(window, "_mediaVolume"), 4);
            Assert.Equal(.2, window.MediaVolumeBar.Value, 4);
            await WpfFrameWaiter.UntilAsync(() => media.LastSessionVolume is float v && Math.Abs(v - .2f) < 1e-4f, "write after the read", ct);
        }
        finally { release.Set(); }
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheIconTogglesMuteAndShowsTheResult(bool mutedAfterToggle) => SharedStaTestRunner.RunAsync(async ct =>
    {
        var media = new FakeMediaDetectionService { ReadSessionVolume = () => (true, .5f, mutedAfterToggle), SessionMuteToggleSucceeded = true };
        await using var fixture = Create(media);
        var window = fixture.Window;
        var click = Click();
        Invoke(window, "MediaVolumeMuteButton_Click", window, click);
        Assert.True(click.Handled);
        await WpfFrameWaiter.UntilAsync(() => media.SessionMuteToggleCount == 1 && Get<bool>(window, "_mediaVolumeKnown") && !Get<bool>(window, "_mediaVolumeReadPending"), "mute toggle and re-read", ct);
        Assert.Equal(mutedAfterToggle ? "" : "", window.MediaVolumeIcon.Text);
        Assert.Equal(mutedAfterToggle ? .35 : 1.0, window.MediaVolumeFill.Opacity, 3);
    });

    [Fact]
    public void AMissingAudioSessionDimsTheSlider() => SharedStaTestRunner.RunAsync(async ct =>
    {
        var media = new FakeMediaDetectionService { ReadSessionVolume = () => (false, 0f, false) };
        await using var fixture = Create(media);
        var window = fixture.Window;
        Invoke(window, "RequestMediaVolumeRead");
        await WpfFrameWaiter.UntilAsync(() => !Get<bool>(window, "_mediaVolumeReadPending"), "failed read", ct);
        Assert.False(Get<bool>(window, "_mediaVolumeKnown"));
        Assert.Equal(.35, window.MediaVolumeColumn.Opacity, 3);
    });

    private static GreetingAcceptanceTests.MainWindowFixture Create(FakeMediaDetectionService media) => new("en", false, settings => { settings.EnableLocalOnlyMode = true; settings.DisableMouseLeaveAutoClose = true; }, services => services.AddSingleton<IMediaDetectionService>(media));
    private static MouseButtonEventArgs Click() => new(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent };
    private static object? Invoke(MainWindow window, string name, params object?[] arguments) => typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, arguments);
    private static T Get<T>(MainWindow window, string name) => (T)(typeof(MainWindow).GetField(name, Private)?.GetValue(window) ?? typeof(MainWindow).GetProperty(name, Private)!.GetValue(window))!;
}
