using System;
using System.Collections.Generic;
using VNotch.Services;
using VNotch.Services.ClaudeCode;

namespace VNotch;

/// <summary>
/// When a Claude plan limit (5 hours or the week) runs out, the closed notch says so with a short pill.
/// Worked out from the reset time the last limits read already brought: nothing is sent to Claude for it, so it
/// never starts a new 5-hour window by itself.
/// </summary>
public partial class MainWindow
{
    // Reset times already announced (or let go), per window.
    private readonly HashSet<string> _claudeResetsSeen = new(StringComparer.Ordinal);
    private static readonly TimeSpan ClaudeResetNewsFor = TimeSpan.FromMinutes(30);

    /// <summary>Runs with the Claude sweep (every 15 s): only compares times, no reads, no drawing.</summary>
    private void CheckClaudeLimitResets()
    {
        if (_cleanedUp || _claudeUsage?.Latest is not { } limits) return;
        Check("5h", limits.FiveHour, "control.claude.limit5h");
        Check("week", limits.Week, "control.claude.limitWeek");

        void Check(string kind, ClaudeLimitWindow? window, string labelKey)
        {
            if (window?.ResetsAt is not { } resets || resets > DateTimeOffset.UtcNow) return;
            string key = kind + "|" + resets.ToUnixTimeSeconds();
            if (_claudeResetsSeen.Contains(key)) return;
            // A window with nothing used has nothing to come back; after half an hour it is old news.
            bool worthIt = window.UsedPercent >= 1 && DateTimeOffset.UtcNow - resets < ClaudeResetNewsFor;
            // Busy notch (open, greeting, fullscreen): try again on the next sweep.
            if (worthIt && (_isGreetingActive || _isAnimating || _isExpanded || _isMusicExpanded || !CanShowClaudeAttention)) return;
            if (_claudeResetsSeen.Count > 32) _claudeResetsSeen.Clear();
            _claudeResetsSeen.Add(key);
            if (!worthIt) return;
            RuntimeLog.Info("CLAUDE-USAGE", $"{kind} limit reset ({window.UsedPercent:0} % used)");
            ShowNotchPill("Claude", Loc.Get("control.claude.limitBack", Loc.Get(labelKey)), appIcon: null, isClaude: true, tap: null);
        }
    }
}
