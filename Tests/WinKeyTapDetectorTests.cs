using VNotch.Controllers;
using Xunit;

namespace VNotch.Tests;

public sealed class WinKeyTapDetectorTests
{
    private const uint KeyE = 0x45;
    private const uint Shift = 0xA0;

    [Theory]
    [InlineData(WinKeyTapDetector.VkLWin)]
    [InlineData(WinKeyTapDetector.VkRWin)]
    public void LoneWinPressIsATap(uint win)
    {
        var detector = new WinKeyTapDetector();
        Assert.Equal(WinKeyTapAction.PassThrough, detector.Process(win, isDown: true));
        Assert.Equal(WinKeyTapAction.Tap, detector.Process(win, isDown: false));
    }

    [Fact]
    public void AutoRepeatWhileHeldStillEndsInATap()
    {
        var detector = new WinKeyTapDetector();
        detector.Process(WinKeyTapDetector.VkLWin, true);
        detector.Process(WinKeyTapDetector.VkLWin, true);
        detector.Process(WinKeyTapDetector.VkLWin, true);
        Assert.Equal(WinKeyTapAction.Tap, detector.Process(WinKeyTapDetector.VkLWin, false));
    }

    [Fact]
    public void WinCombinationIsLeftToWindows()
    {
        var detector = new WinKeyTapDetector();
        detector.Process(WinKeyTapDetector.VkLWin, true);
        detector.Process(Shift, true);
        detector.Process(KeyE, true);
        detector.Process(KeyE, false);
        detector.Process(Shift, false);
        Assert.Equal(WinKeyTapAction.PassThrough, detector.Process(WinKeyTapDetector.VkLWin, false));
    }

    [Fact]
    public void KeyReleasedFromBeforeWinDoesNotSpoilTheTap()
    {
        var detector = new WinKeyTapDetector();
        detector.Process(WinKeyTapDetector.VkLWin, true);
        detector.Process(KeyE, isDown: false);
        Assert.Equal(WinKeyTapAction.Tap, detector.Process(WinKeyTapDetector.VkLWin, false));
    }

    [Fact]
    public void MouseClickWhileHeldIsACombination()
    {
        var detector = new WinKeyTapDetector();
        detector.Process(WinKeyTapDetector.VkLWin, true);
        detector.NoteOtherInput();
        Assert.Equal(WinKeyTapAction.PassThrough, detector.Process(WinKeyTapDetector.VkLWin, false));
    }

    [Fact]
    public void NextTapAfterACombinationWorksAgain()
    {
        var detector = new WinKeyTapDetector();
        detector.Process(WinKeyTapDetector.VkLWin, true);
        detector.Process(KeyE, true);
        detector.Process(WinKeyTapDetector.VkLWin, false);
        detector.Process(WinKeyTapDetector.VkLWin, true);
        Assert.Equal(WinKeyTapAction.Tap, detector.Process(WinKeyTapDetector.VkLWin, false));
    }

    [Fact]
    public void WinUpWithoutDownIsIgnored()
    {
        var detector = new WinKeyTapDetector();
        Assert.Equal(WinKeyTapAction.PassThrough, detector.Process(WinKeyTapDetector.VkLWin, false));
    }
}
