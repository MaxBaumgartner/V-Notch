using System.Globalization;
using VNotch.Controls;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class SchoolFaceTests
{
    [Fact]
    public void CountdownUsesDaysHoursAndMinutes() => SharedStaTestRunner.Run(() =>
    {
        string previous = Loc.CurrentLanguage;
        Loc.SetLanguage("de");
        try
        {
            var now = DateTimeOffset.Parse("2026-11-16T12:00:00+01:00", CultureInfo.InvariantCulture);
            Assert.Equal(("3", "d"), ControlCenterView.SchoolCountdown(now.AddHours(72), now));
            Assert.Equal(("32", "h"), ControlCenterView.SchoolCountdown(now.AddHours(32), now));
            Assert.Equal(("6", "h"), ControlCenterView.SchoolCountdown(now.AddHours(5).AddMinutes(4).AddSeconds(3), now));
            Assert.Equal(("42", "m"), ControlCenterView.SchoolCountdown(now.AddMinutes(42), now));
            Assert.Equal(("0", "m"), ControlCenterView.SchoolCountdown(now, now));
        }
        finally { Loc.SetLanguage(previous); }
    });

}
