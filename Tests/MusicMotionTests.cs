using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Controllers;
using VNotch.Models;
using VNotch.Services;
using VNotch.Tests.Fakes;
using VNotch.ViewModels;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class MusicMotionTests
{
    internal const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(-54, 200, false)]
    [InlineData(-56, 200, true)]
    [InlineData(25, 40, true)]
    [InlineData(25, 200, false)]
    public void OpenSwipeCommitsOnReleaseByDistanceOrRecentVelocity(double distance, int milliseconds, bool skips)
    {
        var controller = new GestureController();
        int earlySkips = 0;
        controller.SwipeLeft += () => earlySkips++;
        controller.SwipeRight += () => earlySkips++;
        var at = DateTime.UtcNow;
        controller.BeginTracking(new Point(), true, true, at);
        controller.UpdateTracking(new Point(distance, 0), at.AddMilliseconds(milliseconds));
        Assert.Equal(0, earlySkips);
        Assert.Equal(skips, controller.ShouldSkipTrack);
        Assert.Equal(distance * 1000 / milliseconds, controller.HorizontalVelocity, 5);
    }

    [Fact]
    public void CancelledSwipeSharesShellFeedbackAndKeepsThePointersPositionAndVelocity() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = Create();
        var window = fixture.Window;
        await Open(window, ct);
        var at = DateTime.UtcNow;
        window.BeginShellGestureTracking(new Point(), at);
        window.UpdateGestureTracking(new Point(30, 0), at.AddMilliseconds(150));
        var layer = Field<object>(window, "_musicCover");
        var translate = (TranslateTransform)layer.GetType().GetField("Translate", Private)!.GetValue(layer)!;
        Assert.Equal(30, translate.X + window.NotchGestureTranslate.X, 5);
        Assert.Equal(9, window.NotchGestureTranslate.X);
        window.FinishMusicSwipe(true, at.AddMilliseconds(150));
        Assert.Equal(30, translate.X + window.NotchGestureTranslate.X, 5);
        Assert.InRange(Motion.VelocityOf(translate, TranslateTransform.XProperty) +
            Motion.VelocityOf(window.NotchGestureTranslate, TranslateTransform.XProperty), 190, 210);
        await WpfFrameWaiter.UntilAsync(() => !Field<bool>(window, "_musicCoverActive"), "cancelled cover returns", ct);
        Assert.True(Field<UIElement>(window, "_musicCanonicalCover").IsHitTestVisible);
    });

    [Fact]
    public void DragAndWheelUseExistingCommandsAndRapidArtworkAlwaysEndsOnLatestTrack() => SharedStaTestRunner.RunAsync(async ct =>
    {
        var media = new FakeMediaDetectionService();
        await using var fixture = Create(media);
        var window = fixture.Window;
        await Open(window, ct);
        var at = DateTime.UtcNow;
        window.BeginShellGestureTracking(new Point(), at);
        window.UpdateGestureTracking(new Point(-75, 0), at.AddMilliseconds(150));
        window.FinishMusicSwipe(false, at.AddMilliseconds(150));
        Assert.Equal(1, media.NextTrackCount);
        var second = Track("Second", Colors.Teal);
        Apply(window, second);
        var third = Track("Third", Colors.SlateBlue);
        Apply(window, third);
        var corrected = Track("Third", Colors.Coral);
        corrected.IsThumbnailOnlyUpdate = true;
        Apply(window, corrected);
        await WpfFrameWaiter.UntilAsync(() => !Field<bool>(window, "_isThumbnailSwitchActive") &&
            !Field<bool>(window, "_musicCoverActive"), "coalesced artwork and cover motion finish", ct);
        Assert.Same(corrected.Thumbnail, window.ThumbnailImage.Source);
        Assert.Same(corrected.Thumbnail, window.CompactThumbnail.Source);
        Assert.Equal(0, window.NotchGestureTranslate.X);
        TestWait.ExpireMediaThrottle(((ShellViewModel)window.DataContext).Media);
        Assert.True((bool)Invoke(window, "MusicWheelInput", 140)!);
        Invoke(window, "FinishMusicWheel", null, EventArgs.Empty);
        Assert.Equal(1, media.PreviousTrackCount);
    });

    [Fact]
    public void InterruptingBackdropDissolveRetainsBothVisibleColours() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = Create();
        var window = fixture.Window;
        await Open(window, ct);
        window.MediaBackgroundImage.BeginAnimation(UIElement.OpacityProperty, null);
        window.MediaBackgroundImageBack.BeginAnimation(UIElement.OpacityProperty, null);
        window.MediaBackgroundImage.Source = Artwork(Colors.Red);
        window.MediaBackgroundImage.Opacity = .4;
        window.MediaBackgroundImageBack.Source = Artwork(Colors.Blue);
        window.MediaBackgroundImageBack.Opacity = .6;
        typeof(MainWindow).GetField("_blurBackIsActive", Private)!.SetValue(window, false);
        Invoke(window, "CrossfadeBlurredBackground", Artwork(Colors.Green));
        var composite = Assert.IsAssignableFrom<BitmapSource>(window.MediaBackgroundImage.Source);
        var pixel = new byte[4];
        composite.CopyPixels(new Int32Rect(0, 0, 1, 1), pixel, 4, 0);
        Assert.InRange(pixel[0], 90, 93);
        Assert.InRange(pixel[2], 101, 103);
        Assert.InRange(pixel[3], 192, 195);
        Assert.Equal(1, window.MediaBackgroundImage.Opacity);
    });

    [Fact]
    public void ReducedMotionAndHiddenTabRetireCoverClocksAndWheelTimer() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = Create();
        var window = fixture.Window;
        await Open(window, ct);
        bool previous = AnimationConfig.ReduceMotion;
        try
        {
            AnimationConfig.SetReduceMotion(true);
            Invoke(window, "MusicWheelInput", -140);
            Invoke(window, "FinishMusicWheel", null, EventArgs.Empty);
            Apply(window, Track("Reduced", Colors.Teal));
            window.MediaPlayback.Visibility = Visibility.Collapsed;
            Assert.False(Field<bool>(window, "_musicCoverActive"));
            Assert.False(Field<System.Windows.Threading.DispatcherTimer>(window, "_musicWheelEnd").IsEnabled);
            Assert.Equal(1, window.MediaPlayback.TrackTextBlock.Opacity);
            Assert.False(window.MediaPlayback.HasAnimatedProperties);
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    [Theory]
    [InlineData("_isNotchVisible", false)]
    [InlineData("_isHiddenByFullscreen", true)]
    public void HiddenNotchAndFullscreenRetireMusicTimersAndCoverClocks(string field, bool value) => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = Create();
        var window = fixture.Window;
        await Open(window, ct);
        Invoke(window, "MusicTransportRequested", 1);
        Assert.True((bool)Invoke(window, "MusicWheelInput", 140)!);
        var timeout = Field<System.Windows.Threading.DispatcherTimer>(window, "_musicRequestTimeout");
        var wheel = Field<System.Windows.Threading.DispatcherTimer>(window, "_musicWheelEnd");
        Assert.True(timeout.IsEnabled);
        Assert.True(wheel.IsEnabled);
        typeof(MainWindow).GetProperty(field, Private)!.SetValue(window, value);
        Invoke(window, "RefreshAmbientAnimations");
        Assert.False(timeout.IsEnabled);
        Assert.False(wheel.IsEnabled);
        Assert.False(Field<bool>(window, "_musicCoverActive"));
        Assert.False(Field<bool>(window, "_musicDragging"));
    });

    internal static GreetingAcceptanceTests.MainWindowFixture Create(FakeMediaDetectionService? media = null) => new("en", false,
        settings => { settings.EnableLocalOnlyMode = true; settings.DisableMouseLeaveAutoClose = true; },
        services => services.AddSingleton<IMediaDetectionService>(media ?? new FakeMediaDetectionService()));

    internal static async Task Open(MainWindow window, CancellationToken ct)
    {
        window.SetDebugViewLock(true);
        window.Left = window.Top = -20000;
        window.Show();
        await WpfFrameWaiter.UntilAsync(() => Field<bool>(window, "_isStartupLayoutReady") && !Field<bool>(window, "_isAnimating"), "startup", ct);
        Apply(window, Track("First", Colors.IndianRed));
        window.SetDebugViewState("MediaExpanded");
        await WpfFrameWaiter.UntilAsync(() => !Field<bool>(window, "_isAnimating") && window.MediaPlayback.IsVisible, "music open", ct);
        await WpfFrameWaiter.UntilAsync(() => !Field<bool>(window, "_isThumbnailSwitchActive"), "initial artwork settled", ct);
    }

    internal static MediaInfo Track(string name, Color colour) => new()
    {
        CurrentTrack = name, CurrentArtist = "Motion study", MediaSource = "Test Player",
        IsAnyMediaPlaying = true, IsPlaying = true, Thumbnail = Artwork(colour),
        Duration = TimeSpan.FromMinutes(4), Position = TimeSpan.FromSeconds(42)
    };

    internal static BitmapImage Artwork(Color colour)
    {
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(colour), null, new Rect(0, 0, 128, 128));
            context.DrawEllipse(new SolidColorBrush(Color.FromArgb(100, 245, 245, 247)), null, new Point(64, 64), 35, 35);
        }
        var bitmap = new RenderTargetBitmap(128, 128, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        stream.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    internal static void Apply(MainWindow window, MediaInfo info) => Invoke(window, "ApplyMediaUpdate", info);
    internal static object? Invoke(object target, string method, params object?[] args) => target.GetType().GetMethod(method, Private)!.Invoke(target, args);
    internal static T Field<T>(object target, string name) => (T)(target.GetType().GetField(name, Private)?.GetValue(target)
        ?? target.GetType().GetProperty(name, Private)!.GetValue(target))!;
}
