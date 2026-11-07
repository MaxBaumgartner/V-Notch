using System;
using System.Collections.Generic;
using System.Linq;
using VNotch.Controls;
using VNotch.Services.ClaudeCode;
using Xunit;

namespace VNotch.Tests;

public sealed class ClaudeTabFocusTests
{
    private static readonly DateTime T0 = new(2026, 11, 7, 20, 0, 0, DateTimeKind.Utc);

    private static ClaudeSession Session(string id, ClaudeSessionStatus status, int startedMin = 0, int lastMin = 0, int? finishedMin = null) => new()
    {
        Id = id,
        Status = status,
        TurnStartedUtc = T0.AddMinutes(startedMin),
        LastEventUtc = T0.AddMinutes(lastMin),
        FinishedUtc = finishedMin is { } f ? T0.AddMinutes(f) : null
    };

    private static string[] Ids(List<ClaudeSession> order) => order.Select(s => s.Id).ToArray();

    [Fact]
    public void AWaitingSessionTakesTheStageBeforeOneAtWork()
    {
        var sessions = new[]
        {
            Session("idle", ClaudeSessionStatus.Idle, lastMin: 9),
            Session("work", ClaudeSessionStatus.Working, startedMin: 1),
            Session("done", ClaudeSessionStatus.Done, finishedMin: 5),
            Session("wait", ClaudeSessionStatus.Waiting, startedMin: 3)
        };
        Assert.Equal(new[] { "wait", "work", "done", "idle" }, Ids(ControlCenterView.FocusOrder(sessions, pinned: null)));
    }

    [Fact]
    public void TheLongestRunningComesFirstAndTheLatestFinished()
    {
        var sessions = new[]
        {
            Session("late", ClaudeSessionStatus.Working, startedMin: 8),
            Session("early", ClaudeSessionStatus.Working, startedMin: 2),
            Session("doneOld", ClaudeSessionStatus.Done, finishedMin: 1),
            Session("doneNew", ClaudeSessionStatus.Done, finishedMin: 6)
        };
        Assert.Equal(new[] { "early", "late", "doneNew", "doneOld" }, Ids(ControlCenterView.FocusOrder(sessions, pinned: null)));
    }

    [Fact]
    public void ATappedChipComesUpFirst()
    {
        var sessions = new[]
        {
            Session("wait", ClaudeSessionStatus.Waiting),
            Session("done", ClaudeSessionStatus.Done, finishedMin: 4)
        };
        Assert.Equal(new[] { "done", "wait" }, Ids(ControlCenterView.FocusOrder(sessions, pinned: "done")));
        // A pin to a session that is gone changes nothing.
        Assert.Equal(new[] { "wait", "done" }, Ids(ControlCenterView.FocusOrder(sessions, pinned: "gone")));
    }

    [Fact]
    public void TheMoodFollowsWhatClaudeIsAt()
    {
        var waiting = new[] { Session("a", ClaudeSessionStatus.Waiting), Session("b", ClaudeSessionStatus.Working) };
        var working = new[] { Session("a", ClaudeSessionStatus.Working) };
        var quiet = new[] { Session("a", ClaudeSessionStatus.Done) };
        var nearLimit = new ClaudeLimits(new ClaudeLimitWindow(91, null), new ClaudeLimitWindow(40, null));
        var plenty = new ClaudeLimits(new ClaudeLimitWindow(30, null), new ClaudeLimitWindow(40, null));

        var amber = ControlCenterView.ClaudeMoodFor(waiting, nearLimit).Main;
        var terracotta = ControlCenterView.ClaudeMoodFor(working, plenty).Main;
        var rose = ControlCenterView.ClaudeMoodFor(working, nearLimit).Main;
        var grey = ControlCenterView.ClaudeMoodFor(quiet, null).Main;

        Assert.Equal(4, new[] { amber, terracotta, rose, grey }.Distinct().Count());
        // Waiting outranks a full limit; a full limit tints even a quiet tab.
        Assert.Equal(rose, ControlCenterView.ClaudeMoodFor(quiet, nearLimit).Main);
        Assert.True(ControlCenterView.NearLimit(new ClaudeLimits(null, new ClaudeLimitWindow(ControlCenterView.NearLimitPercent, null))));
        Assert.False(ControlCenterView.NearLimit(plenty));
    }
}
