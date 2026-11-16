using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class SystemLevelWatcherTests
{
    [Theory]
    [InlineData(false, 0, 0, true)]
    [InlineData(false, 1, 0, false)]
    [InlineData(true, 1, 0, true)]
    [InlineData(true, 0, 0, false)]
    [InlineData(true, 1, 2, false)]
    public void CaptureWatcherFollowsOnlyCaptureDefaultChanges(bool capture, int flow, int role, bool expected) =>
        Assert.Equal(expected, MasterVolumeWatcher.WatchesDefaultChange(capture, flow, role));
}
