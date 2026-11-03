using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class LegionServiceTests
{
    [Fact]
    public void TheHelpersAnswerBecomesTheState()
    {
        var state = LegionService.ParseState("{\"version\":2,\"mode\":224,\"winKey\":1,\"touchpad\":0,\"cpuTemp\":75,\"gpuTemp\":57}");
        Assert.Equal(new LegionState((int)LegionMode.Extreme, WinKeyLocked: true, TouchpadLocked: false, 75, 57), state);
    }

    [Theory]
    [InlineData("{\"mode\":224,\"winKey\":0,\"touchpad\":0,\"cpuTemp\":75,\"gpuTemp\":57}")] // helper from before versions
    [InlineData("{\"version\":1,\"mode\":2}")]
    [InlineData("{\"error\":\"bad value\"}")]
    [InlineData("[]")]
    [InlineData("nope")]
    public void ErrorsAndJunkGiveNoState(string answer) => Assert.Null(LegionService.ParseState(answer));
}
