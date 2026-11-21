using System;
using System.Collections.Generic;
using VNotch.Services;
using System.Windows.Media;
using VNotch.Controllers;
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

    // Amber at half left, rosé when it gets tight: the Claude tab's own colours for "waits" and "near the limit".
    private static readonly Brush AllowanceHalfInk = Frozen(Color.FromRgb(0xD6, 0xB0, 0x7E));
    private static readonly Brush AllowanceLowInk = Frozen(Color.FromRgb(0xD2, 0x85, 0x7E));

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// Runs with the Claude sweep (every 15 s): when a Claude or Codex limit drops below 50 % or 20 % left, a short pill
    /// says so, once per threshold and window. Uses the limits the Claude tab already read; nothing extra is fetched.
    /// </summary>
    private void CheckAllowanceAlerts()
    {
        if (_cleanedUp || _settings == null) return;
        var sent = _settings.AllowanceAlertsSent ??= new();
        bool changed = AllowanceAlerts.Prune(sent, DateTimeOffset.UtcNow);
        var claude = _claudeUsage?.Latest;
        var codex = _codexUsage?.Latest;
        // One pill per sweep; the next sweep picks up whatever else is due.
        bool shown = Try("Claude", "5h", claude?.FiveHour, "control.claude.limit5h", isClaude: true)
            || Try("Claude", "week", claude?.Week, "control.claude.limitWeek", isClaude: true)
            || Try("Codex", "5h", codex?.FiveHour, "control.claude.limit5h", isClaude: false)
            || Try("Codex", "week", codex?.Week, "control.claude.limitWeek", isClaude: false);
        if (changed || shown) _settingsService.SaveAsync(_settings).SafeFireAndForget("SETTINGS-SAVE");

        bool Try(string source, string kind, ClaudeLimitWindow? window, string labelKey, bool isClaude)
        {
            if (AllowanceAlerts.Due(source, kind, window, sent) is not { } due) return false;
            // Busy notch, or another pill is up: never push it away; keep the warning for later instead of losing it.
            if (_compactPillArbiter.ActiveSlot != CompactPillSlot.None) return false;
            if (!ShowNotchPill(source, Loc.Get("control.claude.allowanceLow", Loc.Get(labelKey), due.Threshold), appIcon: null,
                    isClaude: isClaude, tap: null, glyph: isClaude ? null : "\uE7BA",
                    accent: due.Threshold <= 20 ? AllowanceLowInk : AllowanceHalfInk)) return false;
            // Remembered only once it was really on screen.
            sent.AddRange(due.Keys);
            RuntimeLog.Info("CLAUDE-USAGE", $"{source} {kind} below {due.Threshold} % left ({100 - window!.UsedPercent:0} %)");
            return true;
        }
    }
}
