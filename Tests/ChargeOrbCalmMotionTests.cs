using System.Windows;
using System.Windows.Media;
using VNotch.Models;
using VNotch.Services;
using Xunit;
using static VNotch.Tests.OrbPhoneMotionTests;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class ChargeOrbCalmMotionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReversingTheOrbsRetractionRestoresTheSameChargingFace(bool full) => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = Create();
        var window = fixture.Window;
        using var host = new RenderHost(window);
        await WpfFrameWaiter.UntilAsync(() => window.CollapsedContent.IsVisible, "resting notch", ct);
        Invoke(window, "UpdateChargeOrb", new BatteryInfo { Percentage = 48 });
        Invoke(window, "UpdateChargeOrb", new BatteryInfo { Percentage = 48, IsPluggedIn = true, IsCharging = true });
        await WpfFrameWaiter.UntilAsync(() => window.ChargeCable.IsVisible && window.ChargeBolt.Opacity == 1, "charging face settles", ct);
        if (full)
        {
            Invoke(window, "UpdateChargeOrb", new BatteryInfo { Percentage = 100, IsPluggedIn = true });
            await WpfFrameWaiter.UntilAsync(() => window.ChargeCheck.Opacity == 1, "full check arrives", ct);
        }
        Invoke(window, "RetractBubbleForExpand");
        await Task.Delay(100, ct);
        Invoke(window, "UpdateActivityBubble");
        await WpfFrameWaiter.UntilAsync(() => !Motion.IsRunning(window.BubbleSlide, TranslateTransform.XProperty) &&
            !Motion.IsRunning(window.ChargeCableGrow, ScaleTransform.ScaleXProperty), "reversed orb and cable settle", ct);
        if (full)
        {
            Assert.False(Get<bool>(window, "_chargeConnected"));
            Assert.Equal(0, window.ChargeBoltBox.Opacity);
            Assert.Equal(1, window.ChargeCheck.Opacity);
        }
        else
        {
            Assert.True(Get<bool>(window, "_chargeConnected"));
            Assert.Equal(Visibility.Visible, window.ChargeCable.Visibility);
            Assert.Equal(1, window.ChargeCableGrow.ScaleX, 3);
        }
    });

    [Fact]
    public void FirstChargingFaceFitsTheOrbWithoutKeepingTheXamlInset() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = Create();
        var window = fixture.Window;
        using var host = new RenderHost(window);
        await WpfFrameWaiter.UntilAsync(() => window.CollapsedContent.IsVisible, "resting notch", ct);
        Invoke(window, "UpdateChargeOrb", new BatteryInfo { Percentage = 48 });
        Invoke(window, "UpdateChargeOrb", new BatteryInfo { Percentage = 48, IsPluggedIn = true, IsCharging = true });
        await WpfFrameWaiter.UntilAsync(() => window.ChargeCable.IsVisible && window.ChargeBolt.Opacity == 1, "charging face settles", ct);
        Assert.Equal(new Thickness(0), window.BubbleIcon.Margin);
        Assert.Equal(window.ActivityBubble.Width, window.BubbleIcon.Width);
        var bounds = window.BubbleIcon.TransformToAncestor(window.ActivityBubble).TransformBounds(new Rect(window.BubbleIcon.RenderSize));
        Assert.InRange(bounds.Left, -.1, .1);
        Assert.True(bounds.Right <= window.ActivityBubble.ActualWidth + .1);
    });

    [Fact]
    public void BatteryEventReplugWhileTheCableWithdrawsRestoresTheExistingFace() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = Create();
        var window = fixture.Window;
        using var host = new RenderHost(window);
        await WpfFrameWaiter.UntilAsync(() => window.CollapsedContent.IsVisible, "resting notch", ct);
        var unplugged = new BatteryInfo { Percentage = 64 };
        var plugged = new BatteryInfo { Percentage = 64, IsPluggedIn = true, IsCharging = true };
        Invoke(window, "UpdateChargeOrb", unplugged);
        Invoke(window, "UpdateChargeOrb", plugged);
        await WpfFrameWaiter.UntilAsync(() => window.ChargeCable.IsVisible && window.ChargeBolt.Opacity > .5,
            "charging face arrives", ct);
        Invoke(window, "UpdateChargeOrb", unplugged);
        await Task.Delay(100, ct);
        double position = window.ChargeCableRightScale.ScaleX;
        Invoke(window, "UpdateChargeOrb", plugged);
        Assert.Equal(position, window.ChargeCableRightScale.ScaleX, 3);
        Assert.True(Get<bool>(window, "_chargeConnected"));
        await WpfFrameWaiter.UntilAsync(() => window.ChargeBolt.Opacity == 1 &&
            window.ChargeCableRightScale.ScaleX == 1, "battery event replug restores the bolt and cable", ct);
    });

    [Fact]
    public void ChargingRestsWithoutSweepsOrAJoltAndReplugReversesTheFullCheck() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = Create();
        var window = fixture.Window;
        using var host = new RenderHost(window);
        await WpfFrameWaiter.UntilAsync(() => window.CollapsedContent.IsVisible, "resting notch", ct);
        Set(window, "_chargeBattery", new BatteryInfo { Percentage = 64, IsPluggedIn = true, IsCharging = true });
        Set(window, "_bubbleShown", true);
        Invoke(window, "SetBubbleFace", LiveActivity.Charging);
        Invoke(window, "BudBubble");
        Invoke(window, "PlayChargeArrival", true);
        await WpfFrameWaiter.UntilAsync(() => !Motion.IsRunning(window.ChargeCableGrow, ScaleTransform.ScaleXProperty) &&
            window.ChargeBolt.Opacity == 1, "charging connection settles", ct);
        await Task.Delay(600, ct);
        Assert.Equal(0, window.ChargeGlint.Opacity);
        Assert.Equal(0, window.ChargeCableGlint.Opacity);
        Assert.Equal(0, window.ChargeNotchGlow.Opacity);
        Assert.Equal(0, window.ChargeNotchRim.Opacity);
        Assert.False(window.ChargeGlint.HasAnimatedProperties);
        Assert.False(window.NotchScale.HasAnimatedProperties);
        Assert.Equal(1, window.NotchScale.ScaleX);
        Invoke(window, "PlayChargeFull");
        await Task.Delay(100, ct);
        double bolt = window.ChargeBoltBox.Opacity;
        Invoke(window, "PlayChargeArrival", true);
        Assert.Equal(bolt, window.ChargeBoltBox.Opacity, 3);
        await WpfFrameWaiter.UntilAsync(() => window.ChargeBoltBox.Opacity == 1 && window.ChargeCheck.Opacity == 0,
            "a new charge restores the bolt and dismisses the full check", ct);
        Assert.Equal(Visibility.Visible, window.ChargeCable.Visibility);
    });

    internal sealed class RenderHost : IDisposable
    {
        private readonly MainWindow _owner;
        private readonly Window _host;
        internal RenderHost(MainWindow owner)
        {
            _owner = owner;
            var root = owner.Content;
            owner.Content = null;
            Motion.Stop(owner.NotchBorder, FrameworkElement.WidthProperty, 300);
            Motion.Stop(owner.NotchBorder, FrameworkElement.HeightProperty, 40);
            owner.CollapsedContent.Visibility = Visibility.Visible;
            _host = new BackgroundWindow { Left = -20000, Top = -20000, Width = 720, Height = 220,
                Content = root, ShowActivated = false, ShowInTaskbar = false };
            _host.Show();
            _host.UpdateLayout();
        }
        public void Dispose()
        {
            var root = _host.Content;
            _host.Content = null;
            _host.Close();
            _owner.Content = root;
        }
    }
}
