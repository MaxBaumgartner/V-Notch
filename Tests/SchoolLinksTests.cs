using System;
using VNotch.Services.ControlCenter;
using Xunit;

namespace VNotch.Tests;

public sealed class SchoolLinksTests
{
    private const string Moodle = "https://edufs.edu.htl-leonding.ac.at/moodle";
    private const string Untis = "https://htl-leonding.webuntis.com/today";
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 19, 0, 0, TimeSpan.FromHours(1));

    private static PiSchool School(params PiDeadline[] deadlines) =>
        new("2026-11-02", true, Array.Empty<PiLesson>(), deadlines, Array.Empty<PiExam>(), null);

    [Theory]
    [InlineData(Moodle, 232546, Moodle + "/mod/assign/view.php?id=232546")]
    [InlineData(Moodle + "/ ", 7, Moodle + "/mod/assign/view.php?id=7")]
    [InlineData("", 7, null)]
    [InlineData(Moodle, 0, null)]
    public void Assignment_LinksTheAssignmentPage(string moodle, int cmid, string? expected)
    {
        Assert.Equal(expected, SchoolLinks.Assignment(moodle, cmid));
    }

    [Fact]
    public void NewAssignmentReminder_OpensThatAssignment()
    {
        Assert.Equal(Moodle + "/mod/assign/view.php?id=238422", SchoolLinks.ForReminder("neu:238422", null, Now, Moodle, Untis));
    }

    [Fact]
    public void DueReminder_OpensTheAssignmentDueNext()
    {
        var school = School(
            new PiDeadline("Later", "POSE", Now.AddHours(30), false, 2),
            new PiDeadline("Handed in", "AM", Now.AddHours(2), true, 3),
            new PiDeadline("Next", "AM", Now.AddHours(5), false, 4),
            new PiDeadline("Next week", "DBI", Now.AddDays(5), false, 5));

        Assert.Equal(Moodle + "/mod/assign/view.php?id=4", SchoolLinks.ForReminder("abend:2026-11-02", school, Now, Moodle, Untis));
        Assert.Equal(Moodle + "/mod/assign/view.php?id=4", SchoolLinks.ForReminder("morgen:2026-11-02", school, Now, Moodle, Untis));
    }

    [Fact]
    public void WmcTask_OpensItsMoodleLinkNotAnAssignmentPage()
    {
        const string link = Moodle + "/mod/url/view.php?id=238415";
        var wmc = new PiDeadline("04 Tool Lending", "WMC 3AHIF", Now.AddHours(3), false, 238415, "classroom", link);
        var school = School(wmc);

        Assert.Equal(link, SchoolLinks.For(wmc, Moodle));
        Assert.Equal(link, SchoolLinks.ForReminder("neu:238415", school, Now, Moodle, Untis));
        Assert.Equal(link, SchoolLinks.ForReminder("abend:2026-11-02", school, Now, Moodle, Untis));
        Assert.Equal(Moodle + "/mod/assign/view.php?id=7", SchoolLinks.For(new PiDeadline("DBI", "DBI", Now, false, 7, Url: "javascript:x"), Moodle));
    }

    [Fact]
    public void DueReminderWithNothingDue_OpensUntis()
    {
        var school = School(new PiDeadline("Next week", "DBI", Now.AddDays(5), false, 5));
        Assert.Equal(Untis, SchoolLinks.ForReminder("abend:2026-11-02", school, Now, Moodle, Untis));
        Assert.Equal(Untis, SchoolLinks.ForReminder("morgen:2026-11-02", null, Now, Moodle, Untis));
    }

    [Theory]
    [InlineData("aenderung:2026-11-02:1792737492279")]
    [InlineData("msg:123")]
    public void TimetableNews_OpenUntis(string key)
    {
        Assert.Equal(Untis, SchoolLinks.ForReminder(key, null, Now, Moodle, Untis));
    }

    [Theory]
    [InlineData("woche:2026-11-01")]
    [InlineData("pi:cpu:2026-11-02")]
    [InlineData("test:1")]
    [InlineData("neu:")]
    public void OtherReminders_OpenNothing(string key)
    {
        Assert.Null(SchoolLinks.ForReminder(key, null, Now, Moodle, Untis));
    }

    [Theory]
    [InlineData("abend:2026-11-02", true)]
    [InlineData("morgen:2026-11-02", true)]
    [InlineData("neu:5", false)]
    [InlineData("msg:1", false)]
    public void OnlyDueRemindersNeedTheDeadlines(string key, bool expected)
    {
        Assert.Equal(expected, SchoolLinks.NeedsSchool(key));
    }
}
