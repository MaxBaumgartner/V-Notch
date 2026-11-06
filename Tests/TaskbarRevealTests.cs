using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class TaskbarRevealTests
{
    [Theory]
    [InlineData(1u, true)]  // auto-hide (what Windows 11 reports)
    [InlineData(3u, true)]  // auto-hide and always on top
    [InlineData(0u, false)] // always shown: nothing to bring up
    [InlineData(2u, false)]
    public void OnlyAnAutoHidingTaskbarIsBroughtUp(uint state, bool autoHides) =>
        Assert.Equal(autoHides, TaskbarReveal.AutoHides(state));

    [Fact]
    public void NoWindowIsNeverLent() => Assert.Null(TaskbarReveal.Lend(0));

    [Fact]
    public void AHiddenTaskbarLeavesTwoPixelsOnScreen() =>
        // Shown 1020–1080 on a 1080 px screen; hidden its top rests at 1078.
        Assert.Equal(1078, TaskbarReveal.HiddenTop(1080));

    [Theory]
    [InlineData(500, 1050, true)]   // on the taskbar: it stays up
    [InlineData(500, 1019, false)]  // just above it
    [InlineData(1920, 1050, false)] // right edge is outside
    public void TheMouseOverTheTaskbarKeepsItUp(int x, int y, bool inside) =>
        Assert.Equal(inside, TaskbarReveal.IsInside(x, y, 0, 1020, 1920, 1080));
}
