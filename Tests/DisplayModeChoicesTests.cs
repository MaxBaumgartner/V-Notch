using VNotch.Services.Displays;
using System.Reflection;
using System.Runtime.InteropServices;
using Xunit;

namespace VNotch.Tests;

public sealed class DisplayModeChoicesTests
{
    [Fact]
    public void RefreshRatesRoundAndCapAtSix()
    {
        // DisplayMode stores whole Hz; fractional native rates are rounded at the boundary.
        var modes = new[] { 59.94, 60, 100, 120, 144, 165, 240, 360 }
            .Select(hz => new DisplayMode(2560, 1440, ModeChoices.RoundRefreshRate(hz)))
            .Append(new DisplayMode(1920, 1080, 500)).ToArray();

        Assert.Equal(new[] { 60, 120, 144, 165, 240, 360 },
            ModeChoices.RefreshRates(modes, 2560, 1440));
    }

    [Fact]
    public void NearbySizesSameAspectBelowCurrent()
    {
        DisplayMode[] modes = [new(3440, 1440, 60), new(2560, 1080, 60),
            new(2560, 1440, 60), new(1920, 1080, 60)];

        Assert.Equal(new[] { (3440, 1440), (2560, 1080) },
            ModeChoices.NearbySizes(modes, 3440, 1440));
    }

    [Fact]
    public void NearbySizesAreDistinctAndLimitedToTwoSmallerSizes()
    {
        DisplayMode[] modes = [new(2560, 1440, 60), new(2560, 1440, 144),
            new(1920, 1080, 60), new(1600, 900, 60), new(1280, 720, 60),
            new(3840, 2160, 60), new(0, 0, 60)];

        Assert.Equal(new[] { (2560, 1440), (1920, 1080), (1600, 900) },
            ModeChoices.NearbySizes(modes, 2560, 1440));
    }

    [Fact]
    public void NativeDisplayStructuresMatchWindowsAbi()
    {
        (string Name, int Size)[] sizes = [("Luid", 8), ("PathSource", 20),
            ("PathTarget", 48), ("PathInfo", 72), ("ModeInfo", 64), ("VideoSignal", 48),
            ("DeviceHeader", 20), ("TargetDeviceName", 420), ("SourceDeviceName", 84),
            ("TargetPreferred", 80), ("AdvancedColor", 32), ("DpiScale", 32),
            ("DeviceValue", 24), ("DevMode", 220)];
        foreach (var (name, size) in sizes)
        {
            var type = typeof(Win32DisplayApi).GetNestedType(name, BindingFlags.NonPublic)!;
            Assert.Equal(size, Marshal.SizeOf(type));
        }
        var devMode = typeof(Win32DisplayApi).GetNestedType("DevMode", BindingFlags.NonPublic)!;
        Assert.Equal(76, Marshal.OffsetOf(devMode, "X").ToInt32());
        Assert.Equal(172, Marshal.OffsetOf(devMode, "Width").ToInt32());
        Assert.Equal(184, Marshal.OffsetOf(devMode, "Frequency").ToInt32());
    }

    [Fact]
    public void RoundedFractionalSnapshotStillSelectsNativeModeAfterChangingRate()
    {
        var rates = new Win32DisplayApi.RefreshRateMap();
        var nativeFractional = new DisplayMode(2560, 1440, 59);
        var nativeFast = new DisplayMode(2560, 1440, 144);
        rates.Observe("monitor", nativeFractional, ModeChoices.RoundRefreshRate(59.94));
        var snapshot = rates.Normalize("monitor", nativeFractional);
        Assert.Equal(60, snapshot.RefreshHz);
        DisplayMode[] rawModes = [nativeFractional, new(2560, 1440, 60), nativeFast];
        Assert.Equal(new[] { 60, 144 }, ModeChoices.RefreshRates(
            rawModes.Select(mode => rates.Normalize("monitor", mode)).ToArray(), 2560, 1440));

        rates.Observe("monitor", nativeFast, 144);
        Assert.Contains(rawModes, native => rates.Normalize("monitor", native) == snapshot);
        Assert.Equal(59, rates.Normalize("other monitor", nativeFractional).RefreshHz);
    }
}
