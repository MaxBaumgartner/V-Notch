using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public class TransparencyFallbackTests
{
    private static readonly string[] Hybrid = { "Intel(R) UHD Graphics", "NVIDIA GeForce RTX 4070 Laptop GPU" };

    [Fact]
    public void LaptopPanelOnIntelInAHybridMachineGetsTheLayeredWindow() =>
        Assert.True(DwmTransparentWindow.Decide(null, "Intel(R) UHD Graphics", Hybrid));

    [Fact]
    public void MonitorOnTheDiscreteGpuKeepsDwm() =>
        Assert.False(DwmTransparentWindow.Decide(null, "NVIDIA GeForce RTX 4070 Laptop GPU", Hybrid));

    [Fact]
    public void ALoneIntegratedGpuKeepsDwm() =>
        Assert.False(DwmTransparentWindow.Decide(null, "Intel(R) Iris(R) Xe Graphics", new[] { "Intel(R) Iris(R) Xe Graphics" }));

    [Fact]
    public void UnknownScreenKeepsDwm() => Assert.False(DwmTransparentWindow.Decide(null, null, Hybrid));

    [Theory]
    [InlineData("1", "NVIDIA GeForce RTX 4070 Laptop GPU", true)]
    [InlineData("0", "Intel(R) UHD Graphics", false)]
    public void EnvironmentSwitchWins(string forced, string adapter, bool layered) =>
        Assert.Equal(layered, DwmTransparentWindow.Decide(forced, adapter, Hybrid));

    [Theory]
    [InlineData("Intel(R) UHD Graphics", true)]
    [InlineData("Intel(R) Arc(TM) A770 Graphics", false)]
    [InlineData("AMD Radeon(TM) Graphics", true)]
    [InlineData("AMD Radeon RX 7600", false)]
    [InlineData("NVIDIA GeForce RTX 4070 Laptop GPU", false)]
    public void TellsIntegratedAdaptersApart(string adapter, bool integrated) =>
        Assert.Equal(integrated, DwmTransparentWindow.IsIntegrated(adapter));
}
