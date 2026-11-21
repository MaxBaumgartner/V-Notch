using System;
using System.Collections.Generic;
using System.Linq;
using VNotch.Services.ClaudeCode;
using Xunit;

namespace VNotch.Tests;

public sealed class AllowanceAlertsTests
{
    private static readonly DateTimeOffset Resets = new(2026, 11, 21, 20, 0, 0, TimeSpan.Zero);

    private static ClaudeLimitWindow Used(double percent, DateTimeOffset? resets = null) => new(percent, resets ?? Resets);

    [Fact]
    public void Nothing_while_more_than_half_is_left()
    {
        Assert.Null(AllowanceAlerts.Due("Claude", "5h", Used(49), new List<string>()));
        Assert.Null(AllowanceAlerts.Due("Claude", "5h", null, new List<string>()));
        Assert.Null(AllowanceAlerts.Due("Claude", "5h", new ClaudeLimitWindow(90, null), new List<string>()));
    }

    [Fact]
    public void Crossing_half_fires_once_and_staying_below_does_not_repeat()
    {
        var sent = new List<string>();
        var due = AllowanceAlerts.Due("Claude", "5h", Used(55), sent);
        Assert.Equal(50, due!.Value.Threshold);
        sent.AddRange(due.Value.Keys);
        Assert.Null(AllowanceAlerts.Due("Claude", "5h", Used(60), sent));
        // The endpoint rounds the reset time a little differently between reads: still the same window.
        Assert.Null(AllowanceAlerts.Due("Claude", "5h", Used(61, Resets.AddSeconds(-40)), sent));
    }

    [Fact]
    public void Twenty_fires_on_its_own_after_fifty()
    {
        var sent = new List<string>();
        sent.AddRange(AllowanceAlerts.Due("Codex", "week", Used(52), sent)!.Value.Keys);
        var low = AllowanceAlerts.Due("Codex", "week", Used(81), sent);
        Assert.Equal(20, low!.Value.Threshold);
        Assert.Single(low.Value.Keys);
    }

    [Fact]
    public void Crossing_both_at_once_announces_twenty_and_remembers_both()
    {
        var sent = new List<string>();
        var due = AllowanceAlerts.Due("Claude", "week", Used(92), sent);
        Assert.Equal(20, due!.Value.Threshold);
        Assert.Equal(2, due.Value.Keys.Count);
        sent.AddRange(due.Value.Keys);
        Assert.Null(AllowanceAlerts.Due("Claude", "week", Used(95), sent));
    }

    [Fact]
    public void A_new_window_can_fire_again_and_sources_and_windows_stay_apart()
    {
        var sent = new List<string>();
        sent.AddRange(AllowanceAlerts.Due("Claude", "5h", Used(70), sent)!.Value.Keys);
        Assert.NotNull(AllowanceAlerts.Due("Claude", "5h", Used(70, Resets.AddHours(5)), sent));
        Assert.NotNull(AllowanceAlerts.Due("Codex", "5h", Used(70), sent));
        Assert.NotNull(AllowanceAlerts.Due("Claude", "week", Used(70), sent));
    }

    [Fact]
    public void Saved_keys_survive_a_restart_and_old_windows_are_pruned()
    {
        // What the settings file holds after a restart is just the strings.
        var saved = AllowanceAlerts.Due("Claude", "5h", Used(85), new List<string>())!.Value.Keys.ToList();
        var reloaded = new List<string>(saved);
        Assert.Null(AllowanceAlerts.Due("Claude", "5h", Used(85), reloaded));

        reloaded.Add("garbage");
        Assert.True(AllowanceAlerts.Prune(reloaded, Resets.AddMinutes(-1)));
        Assert.Equal(saved, reloaded);
        Assert.True(AllowanceAlerts.Prune(reloaded, Resets.AddHours(1)));
        Assert.Empty(reloaded);
    }
}
