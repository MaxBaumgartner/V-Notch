using VNotch.Services.ControlCenter;
using Xunit;

namespace VNotch.Tests;

public sealed class WindowsNotificationTextTests
{
    [Fact]
    public void ToastTextKeepsTitleOnlyAndLongLines()
    {
        var titleOnly = ToastText.Split(new[] { "Anna" });
        Assert.Equal("Anna", titleOnly.Title);
        Assert.Empty(titleOnly.Lines);

        string longLine = new('x', 400);
        var withBody = ToastText.Split(new[] { "Anna", longLine });
        Assert.Equal("Anna", withBody.Title);
        Assert.Equal(longLine, Assert.Single(withBody.Lines));
    }

    [Fact]
    public void ToastTextSkipsBlankLines()
    {
        var toast = ToastText.Split(new[] { "Anna", "", "hi", " " });
        Assert.Equal("Anna", toast.Title);
        Assert.Equal("hi", Assert.Single(toast.Lines));
    }
}
