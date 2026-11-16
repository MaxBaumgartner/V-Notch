using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Services;
using VNotch.Tests.Fakes;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class NotificationDropTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void HidingTheDropLayerStopsItsFrameLoopAndSpringClocks() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = new GreetingAcceptanceTests.MainWindowFixture("en", greeting: false,
            configureSettings: settings => settings.EnableLocalOnlyMode = true,
            configureServices: services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));
        var window = fixture.Window;
        window.Show();
        var logo = new DrawingImage(new GeometryDrawing(Brushes.Gray, null, new RectangleGeometry(new Rect(0, 0, 14, 14))));
        Invoke(window, "ShowNotchPill", "Discord", "Alex: hi", logo, false, new Func<bool>(() => true), null, null, null);
        await WpfFrameWaiter.UntilAsync(() => window.ClaudeNotification.Opacity > 0.99, "pill shown", ct);
        Invoke(window, "PlayNotificationDrop", IntPtr.Zero);
        var layer = Field<System.Windows.Controls.Canvas>(window, "_dropLayer");
        Assert.NotNull(Field<EventHandler?>(window, "_dropFrame"));
        layer.Visibility = Visibility.Collapsed;
        Assert.Null(Field<EventHandler?>(window, "_dropFrame"));
        Assert.False(Field<TranslateTransform>(window, "_dropMove").HasAnimatedProperties);
        Assert.False(Field<ScaleTransform>(window, "_dropScale").HasAnimatedProperties);
    });

    [Fact]
    public void TappingAnAppPillDropsItsLogoOutOfTheNotchOnATearingNeck() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = new GreetingAcceptanceTests.MainWindowFixture("en", greeting: false,
            configureSettings: settings =>
            {
                settings.EnableLocalOnlyMode = true;
                settings.DisableMouseLeaveAutoClose = true;
            },
            configureServices: services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));
        var window = fixture.Window;
        window.Show();
        var logo = new DrawingImage(new GeometryDrawing(Brushes.MediumPurple, null,
            new RectangleGeometry(new Rect(0, 0, 14, 14), 3, 3)));
        int taps = 0;
        Invoke(window, "ShowNotchPill", "Discord", "Alex: hi", logo, false, new Func<bool>(() => { taps++; return true; }), null, null, null);
        await WpfFrameWaiter.UntilAsync(() => window.ClaudeNotification.Opacity > 0.99, "pill shown", ct);

        Assert.True((bool)Invoke(window, "TryHandleNotificationTap")!);
        Assert.Equal(1, taps);

        var layer = Field<System.Windows.Controls.Canvas>(window, "_dropLayer");
        var move = Field<TranslateTransform>(window, "_dropMove");
        var neck = Field<System.Windows.Shapes.Path>(window, "_dropNeck");
        Assert.Equal(Visibility.Visible, layer.Visibility);
        // Behind the notch, so the neck comes out from under it.
        Assert.True(window.NotchContainer.Children.IndexOf(layer) < window.NotchContainer.Children.IndexOf(window.NotchWrapper));

        bool sawNeck = false, sawTorn = false;
        double firstY = double.NaN, lastY = double.NaN;
        await WpfFrameWaiter.UntilAsync(() =>
        {
            if (layer.Visibility != Visibility.Visible) return true;
            if (double.IsNaN(firstY)) firstY = move.Y;
            lastY = move.Y;
            if (neck.Visibility == Visibility.Visible) sawNeck = true;
            else if (sawNeck) sawTorn = true;
            return false;
        }, "drop finished", ct);

        Assert.True(sawNeck, "the drop never hung on a neck");
        Assert.True(sawTorn, "the neck never tore");
        Assert.True(lastY > firstY + 40, $"the drop did not fall ({firstY:F1} -> {lastY:F1})");
        Assert.Null(Field<EventHandler?>(window, "_dropFrame"));
        await WpfFrameWaiter.UntilAsync(() => window.ClaudeNotification.Visibility != Visibility.Visible, "pill gone", ct);
    });

    [Fact]
    public void ACopyWithoutMusicShowsAShortCopiedPillThatHandsTheBellBack() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = new GreetingAcceptanceTests.MainWindowFixture("en", greeting: false,
            configureSettings: settings => settings.EnableLocalOnlyMode = true,
            configureServices: services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));
        var window = fixture.Window;
        window.Show();
        Invoke(window, "PlayClipboardPeek");
        Assert.True(Field<bool>(window, "_isCopiedPillShown"));
        Assert.Equal("Copied", window.ClaudeNotificationTitle.Text);
        Assert.Equal("", window.NotificationBellIcon.Text);
        Assert.Equal(TimeSpan.FromMilliseconds(2200), Field<System.Windows.Threading.DispatcherTimer>(window, "_claudeNotificationTimer").Interval);

        // The next pill is an ordinary one again.
        Invoke(window, "ShowNotchPill", "Pi", "Done", null, false, null, null, null, null);
        Assert.False(Field<bool>(window, "_isCopiedPillShown"));
        Assert.Equal("", window.NotificationBellIcon.Text);
        Assert.Equal(TimeSpan.FromSeconds(10), Field<System.Windows.Threading.DispatcherTimer>(window, "_claudeNotificationTimer").Interval);
        ct.ThrowIfCancellationRequested();
    });

    private static object? Invoke(MainWindow window, string name, params object?[] arguments)
    {
        var method = typeof(MainWindow).GetMethod(name, Private)!;
        return method.Invoke(window, arguments.Concat(Enumerable.Repeat<object?>(Type.Missing,
            method.GetParameters().Length - arguments.Length)).ToArray());
    }

    private static T Field<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
}
