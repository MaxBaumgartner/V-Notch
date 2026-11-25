using System.Windows;
using System.Windows.Automation;
using VNotch.Controls;
using VNotch.Services;
using VNotch.Tests.Fakes;
using VNotch.ViewModels;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class MediaPlaybackViewTests
{
    [Fact]
    public void TrackUpdatesWhileHiddenSettleOnTheLatestTextWithoutClocks() => SharedStaTestRunner.Run(() =>
    {
        using var view = new MediaPlaybackView();
        view.TransitionTrackText("First", "First artist");
        view.TransitionTrackText("Latest", "Latest artist", -1);
        view.UpdateArtistText("Corrected artist");
        Assert.Equal("Latest", view.TrackTitle.Text);
        Assert.Equal("Corrected artist", view.TrackArtist.Text);
        Assert.False(view.TitleMorphTranslate.HasAnimatedProperties);
        Assert.False(view.TitleMarqueeTranslate.HasAnimatedProperties);
        Assert.False(view.TrackTitleLayer.HasAnimatedProperties);
    });

    [Fact]
    public void PlaybackMorphReversesFromItsCurrentShapeAndHidingStopsItsClock() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var view = new MediaPlaybackView();
        var host = new Window { Content = view, Width = 400, Height = 200, Left = -20000, Top = -20000, ShowInTaskbar = false };
        try
        {
            host.Show();
            view.PresentPlayback(true);
            view.PresentPlayback(false);
            await WpfFrameWaiter.UntilAsync(() => (double)view.GetValue(MediaPlaybackView.PlaybackMorphProperty) > .1, "bars start folding", ct);
            double shape = (double)view.GetValue(MediaPlaybackView.PlaybackMorphProperty);
            view.PresentPlayback(true);
            Assert.Equal(shape, (double)view.GetValue(MediaPlaybackView.PlaybackMorphProperty), 4);
            Assert.Equal(Visibility.Collapsed, view.PlayIcon.Visibility);
            Assert.Equal(Visibility.Visible, view.PauseIcon.Visibility);
            view.Visibility = Visibility.Collapsed;
            Assert.False(Motion.IsRunning(view, MediaPlaybackView.PlaybackMorphProperty));
            Assert.Equal(0d, view.GetValue(MediaPlaybackView.PlaybackMorphProperty));
        }
        finally { host.Close(); }
    });

    [Fact]
    public void HidingCancelsRewindAndItsAnimationClock() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var view = new MediaPlaybackView();
        var host = new Window { Content = view, Width = 400, Height = 200, ShowInTaskbar = false };
        try
        {
            host.Show();
            await WpfFrameWaiter.UntilAsync(() => view.IsLoaded && view.ActualWidth > 0, "media view loaded and laid out", ct);
            view.CurrentTimeText.Text = "1:00";
            view.AnimateTrackChangeRewindToZero(0.5, TimeSpan.FromMinutes(2));
            Assert.True(view.ProgressBarScale.HasAnimatedProperties);
            view.Visibility = Visibility.Collapsed;
            Assert.False(view.ProgressBarScale.HasAnimatedProperties);
            Assert.Equal(0, view.DisplayRatio);
            view.Visibility = Visibility.Visible;
            // A cancelled rewind must not resume once the view is visible again. Observe several
            // rendered frames across the original rewind window (the 500 ms rewind clock would have
            // re-attached by then) instead of sleeping blindly.
            var observed = System.Diagnostics.Stopwatch.StartNew();
            await WpfFrameWaiter.UntilAsync(() =>
            {
                Assert.False(view.ProgressBarScale.HasAnimatedProperties);
                return observed.ElapsedMilliseconds >= 500;
            }, "rewind window passes without the animation resuming", ct);
            Assert.False(view.ProgressBarScale.HasAnimatedProperties);
        }
        finally { host.Close(); }
    });

    [Fact]
    public void TransportWorksWithoutMainWindowOrTheApplicationServiceLocator() => SharedStaTestRunner.RunAsync(async ct =>
    {
        Loc.SetLanguage("en");
        var media = new FakeMediaDetectionService();
        var viewModel = new MediaViewModel(media);
        using var view = new MediaPlaybackView();
        view.Attach(viewModel, media);
        view.PlayPauseButton.InvokeAction();
        await TestWait.UntilAsync(() => media.PlayPauseCount == 1, "play/pause command", ct);
        Assert.Equal(Loc.Get("media.play"), AutomationProperties.GetName(view.PlayPauseButton));
        // MediaViewModel throttles transport commands for 200 ms; expire it rather than sleeping.
        TestWait.ExpireMediaThrottle(viewModel);
        view.NextButton.InvokeAction();
        await TestWait.UntilAsync(() => media.NextTrackCount == 1, "next track command", ct);
        TestWait.ExpireMediaThrottle(viewModel);
        view.PrevButton.InvokeAction();
        await TestWait.UntilAsync(() => media.PreviousTrackCount == 1, "previous track command", ct);
        view.Dispose();
        // With the throttle expired, only the disposed guard can reject this click.
        TestWait.ExpireMediaThrottle(viewModel);
        view.PlayPauseButton.InvokeAction();
        await TestWait.DrainDispatcherAsync();
        Assert.Equal(1, media.PlayPauseCount);
    });
}
