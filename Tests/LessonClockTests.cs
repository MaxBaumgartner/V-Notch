using System;
using System.Collections.Generic;
using VNotch.Services;
using VNotch.Services.ClaudeCode;
using VNotch.Services.ControlCenter;
using Xunit;

namespace VNotch.Tests;

/// <summary>The school lesson in the orb: minutes to the break, the break itself, and nothing outside school.</summary>
public class LessonClockTests
{
    private static readonly DateTime Day = new(2026, 11, 3);

    private static PiLesson L(string start, string end, string subject, bool cancelled = false, string room = "R101") =>
        new(start, end, subject, room, cancelled, "", PlannerDashboard.Minutes(start), PlannerDashboard.Minutes(end));

    private static PiSchool School(params PiLesson[] lessons) => new("2026-11-03", true, lessons, new List<PiDeadline>(), new List<PiExam>(), null);

    private static DateTime At(string time) => Day.AddMinutes(PlannerDashboard.Minutes(time));

    [Fact]
    public void ALessonCountsDownToTheBreak()
    {
        var now = LessonClock.At(School(L("08:00", "08:50", "WMC"), L("08:55", "09:45", "DBI")), At("08:38"));

        Assert.Equal(LessonPhase.Lesson, now.Phase);
        Assert.Equal(("WMC", "DBI", 12), (now.Subject, now.Next, now.MinutesLeft));
        Assert.Equal(12 / 50.0, now.Remaining, 3);
    }

    [Fact]
    public void LessonsWithoutAGapCountAsOneAndTheLastRunsToTheEndOfSchool()
    {
        var now = LessonClock.At(School(L("08:00", "08:50", "POSE"), L("08:50", "09:40", "POSE")), At("08:30"));

        Assert.Equal(LessonPhase.Lesson, now.Phase);
        Assert.Equal(70, now.MinutesLeft);
        Assert.Equal("", now.Next);
    }

    [Fact]
    public void ABreakCountsDownToTheNextLesson()
    {
        var now = LessonClock.At(School(L("08:00", "08:50", "WMC"), L("08:55", "09:45", "DBI", room: "R202")), At("08:52"));

        Assert.Equal(LessonPhase.Break, now.Phase);
        Assert.Equal(("DBI", "R202", 3), (now.Subject, now.Room, now.MinutesLeft));
        Assert.Equal(0.6, now.Remaining, 3);
    }

    [Theory]
    [InlineData("07:30")]
    [InlineData("09:45")]
    [InlineData("13:00")]
    public void OutsideSchoolNothingRuns(string time)
    {
        var now = LessonClock.At(School(L("08:00", "08:50", "WMC"), L("08:55", "09:45", "DBI")), At(time));
        Assert.Equal(LessonPhase.Off, now.Phase);
    }

    [Fact]
    public void CancelledLessonsAndOtherDaysDoNotCount()
    {
        var school = School(L("08:00", "08:50", "WMC", cancelled: true), L("08:55", "09:45", "DBI"));
        Assert.Equal(LessonPhase.Off, LessonClock.At(school, At("08:20")).Phase);
        Assert.Equal(LessonPhase.Off, LessonClock.At(school, At("09:00").AddDays(1)).Phase);
        Assert.Equal(LessonPhase.Off, LessonClock.At(null, At("09:00")).Phase);
    }

    [Fact]
    public void TheMorningCountsDownToLeavingThenToTheFirstLesson()
    {
        var school = School(L("08:00", "08:50", "Geschichte", room: "R202-2"));
        var commute = new PiCommute("2026-11-03", "07:30", 5, 20);

        var before = LessonClock.At(school, At("07:18"), commute);
        Assert.Equal(LessonPhase.Leave, before.Phase);
        Assert.Equal(12, before.MinutesLeft);
        Assert.Equal(12.0 / LessonClock.LeaveLeadMinutes, before.Remaining, 3);

        var way = LessonClock.At(school, At("07:40"), commute);
        Assert.Equal(LessonPhase.Way, way.Phase);
        Assert.Equal(("Geschichte", "R202-2", 20), (way.Subject, way.Room, way.MinutesLeft));
        Assert.Equal(20 / 30.0, way.Remaining, 3);

        Assert.Equal(LessonPhase.Lesson, LessonClock.At(school, At("08:05"), commute).Phase);
    }

    [Fact]
    public void TheMorningStaysQuietTooEarlyOnAnotherDayOrWithoutTheWay()
    {
        var school = School(L("08:00", "08:50", "Geschichte"));
        Assert.Equal(LessonPhase.Off, LessonClock.At(school, At("06:30"), new PiCommute("2026-11-03", "07:30", 5, 0)).Phase);
        Assert.Equal(LessonPhase.Off, LessonClock.At(school, At("07:20"), new PiCommute("2026-11-04", "07:30", 5, 0)).Phase);
        Assert.Equal(LessonPhase.Off, LessonClock.At(school, At("07:20")).Phase);
    }

    [Fact]
    public void TheWayToSchoolComesFromTheStartPage()
    {
        var home = (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse("""
            { "weather": { "school": { "date": "2026-11-03", "there": { "time": "07:30", "weather": { "temp": 5.4, "rain": 20 } } } } }
            """)!;
        Assert.Equal(new PiCommute("2026-11-03", "07:30", 5, 20), PlannerDashboard.Commute(home));
        Assert.Null(PlannerDashboard.Commute((System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse("""{ "weather": {} }""")!));
    }

    [Theory]
    // Alone a lesson only has the orb, like a timer.
    [InlineData(false, null, false, "None", "Lesson")]
    [InlineData(true, null, false, "Music", "Lesson")]
    // A running timer comes first; Claude at work holds the notch and leaves the orb to the lesson.
    [InlineData(false, null, true, "None", "Countdown")]
    [InlineData(false, "Working", false, "Claude", "Lesson")]
    [InlineData(true, "Waiting", false, "Claude", "Music")]
    public void TheLessonTakesTheOrbAfterTheTimer(bool music, string? claude, bool countdown, string main, string bubble)
    {
        ClaudeSessionStatus? status = claude == null ? null : Enum.Parse<ClaudeSessionStatus>(claude);
        var (actualMain, actualBubble) = LiveActivities.Arrange(music, status, countdown, lesson: true);
        Assert.Equal((main, bubble), (actualMain.ToString(), actualBubble.ToString()));
    }
}
