using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Services;
using VNotch.Tests.Fakes;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class MainWindowIdleGlanceMotionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void HiddenGlanceDoesNotScheduleClockOrWidthRetries() => SharedStaTestRunner.RunAsync(async _ =>
    {
        await using var fixture = CreateFixture();
        Invoke(fixture.Window, "HookIdleGlance");
        Invoke(fixture.Window, "ScheduleIdleClock");
        Invoke(fixture.Window, "SettleIdleWidth");
        Assert.Null(Field<DispatcherTimer?>(fixture.Window, "_idleClockTimer"));
        Assert.Null(Field<DispatcherTimer?>(fixture.Window, "_idleSettleTimer"));
    });

    [Fact]
    public void WeatherLeavesBeforeCollapsingAndClockKeepsItsScreenPositionOnLayoutChange() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture();
        var window = fixture.Window;
        window.Left = -20000;
        window.Top = -20000;
        window.Show();
        await WpfFrameWaiter.UntilAsync(() => !(bool)typeof(MainWindow).GetProperty("_isAnimating", Private)!.GetValue(window)!, "initial appearance finished", ct);
        Invoke(window, "HookIdleGlance");
        window.NotchLiveContent.Visibility = Visibility.Visible;
        window.IdleGlance.Visibility = Visibility.Visible;
        window.IdleGlance.Opacity = 1;
        window.IdleClock.Text = "12:34";
        window.IdleClock.HorizontalAlignment = HorizontalAlignment.Left;
        window.IdleWeather.Visibility = Visibility.Visible;
        window.IdleWeather.Opacity = 1;
        window.IdleWeatherTemp.Text = "18°";
        window.UpdateLayout();
        double before = window.IdleClock.TranslatePoint(new Point(), window.IdleGlance).X;
        Invoke(window, "UpdateIdleWeather");
        Assert.Equal(Visibility.Visible, window.IdleWeather.Visibility);
        Assert.Equal(before, window.IdleClock.TranslatePoint(new Point(), window.IdleGlance).X, 4);
        Assert.True(window.IdleWeather.HasAnimatedProperties);
        Assert.True(((TranslateTransform)window.IdleClock.RenderTransform).HasAnimatedProperties);
        await WpfFrameWaiter.UntilAsync(() => window.IdleWeather.Visibility == Visibility.Collapsed, "weather exit completes before removal", ct);
        Assert.Equal(HorizontalAlignment.Center, window.IdleClock.HorizontalAlignment);
        await WpfFrameWaiter.UntilAsync(() => ((TranslateTransform)window.IdleClock.RenderTransform).X == 0, "clock glides to its centered rest", ct);
        window.IdleGlance.Visibility = Visibility.Collapsed;
        Assert.False(Field<DispatcherTimer?>(window, "_idleClockTimer")?.IsEnabled == true);
        Assert.False(Field<DispatcherTimer?>(window, "_idleSettleTimer")?.IsEnabled == true);
    });

    private static GreetingAcceptanceTests.MainWindowFixture CreateFixture() => new("en", greeting: false,
        configureServices: services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));
    private static T Field<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
    private static void Invoke(MainWindow window, string name, params object[] args) => typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, args);
}
