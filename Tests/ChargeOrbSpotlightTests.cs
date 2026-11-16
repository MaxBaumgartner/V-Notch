using System.Reflection;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Models;
using VNotch.Services;
using VNotch.Tests.Fakes;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class ChargeOrbSpotlightTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(0)]
    [InlineData(150)]
    [InlineData(400)]
    [InlineData(700)]
    [InlineData(1500)]
    public void SpotlightHidesTheChargingOrbAndCableWheneverItOpens(int openAfterMs) => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = Create();
        var window = fixture.Window;
        window.ShowActivated = false;
        window.Show();
        await WpfFrameWaiter.UntilAsync(() => window.IsLoaded && window.CollapsedContent.IsVisible, "collapsed notch", ct);

        Invoke(window, "UpdateChargeOrb", new BatteryInfo { Percentage = 60, IsPluggedIn = false, IsCharging = false });
        Invoke(window, "UpdateChargeOrb", new BatteryInfo { Percentage = 60, IsPluggedIn = true, IsCharging = true });
        await WpfFrameWaiter.UntilAsync(() => window.ActivityBubble.Visibility == Visibility.Visible, "charging orb buds", ct);
        if (openAfterMs > 0) await Task.Delay(openAfterMs, ct);

        window.SetSpotlightMorphSessionActive(true);
        window.SetSpotlightMorphActive(true);
        await Task.Delay(50, ct);
        AssertHidden(window, "right after Spotlight opened");

        // The charge moment and its timers run out while Spotlight is still open.
        await Task.Delay(4200, ct);
        AssertHidden(window, "while Spotlight stays open");

        window.SetSpotlightMorphActive(false);
        window.SetSpotlightMorphSessionActive(false);
    });

    private static void AssertHidden(MainWindow window, string when)
    {
        Assert.True(window.ActivityBubble.Visibility != Visibility.Visible, $"orb visible {when}");
        Assert.True(window.ChargeCable.Visibility != Visibility.Visible, $"cable visible {when}");
    }

    private static GreetingAcceptanceTests.MainWindowFixture Create() => new("en", false,
        settings => { settings.EnableLocalOnlyMode = true; settings.DisableMouseLeaveAutoClose = true; },
        services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));

    private static void Invoke(MainWindow window, string name, params object?[] arguments) =>
        typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, arguments);
}
