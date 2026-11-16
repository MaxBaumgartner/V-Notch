using System.Collections.Generic;
using System.Threading;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class KeepAwakeTests
{
    [Fact]
    public void OffNeverKeepsAwake() =>
        Assert.Equal(KeepAwakeReason.None, KeepAwakePolicy.Decide(KeepAwakeMode.Off, true, true, true, false, 100));

    [Fact]
    public void AlwaysKeepsAwake() =>
        Assert.Equal(KeepAwakeReason.Always, KeepAwakePolicy.Decide(KeepAwakeMode.Always, false, false, false, true, 5));

    [Theory]
    [InlineData(true, false, false, KeepAwakeReason.Claude)]
    [InlineData(false, true, false, KeepAwakeReason.Download)]
    [InlineData(false, false, true, KeepAwakeReason.Timer)]
    [InlineData(true, true, true, KeepAwakeReason.Claude)]
    [InlineData(false, false, false, KeepAwakeReason.None)]
    public void AutomaticFollowsReasons(bool claude, bool download, bool timer, KeepAwakeReason expected) =>
        Assert.Equal(expected, KeepAwakePolicy.Decide(KeepAwakeMode.Automatic, claude, download, timer, false, 100));

    [Fact]
    public void AutomaticYieldsOnLowBattery()
    {
        Assert.Equal(KeepAwakeReason.None, KeepAwakePolicy.Decide(KeepAwakeMode.Automatic, true, false, false, true, 19));
        Assert.Equal(KeepAwakeReason.Claude, KeepAwakePolicy.Decide(KeepAwakeMode.Automatic, true, false, false, true, 20));
        Assert.Equal(KeepAwakeReason.Claude, KeepAwakePolicy.Decide(KeepAwakeMode.Automatic, true, false, false, false, 5));
    }

    [Fact]
    public void ReleasesOnDisposeAndSkipsRepeats()
    {
        var calls = new List<uint>();
        var service = new KeepAwakeService(flags => { lock (calls) calls.Add(flags); return 1; });
        service.Set(true);
        service.Set(true);
        service.Set(false);
        service.Set(true);
        service.Dispose();
        lock (calls)
            Assert.Equal(new uint[] { 0x80000001, 0x80000000, 0x80000001, 0x80000000 }, calls);
    }
}
