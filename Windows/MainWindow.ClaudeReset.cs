using System;
using System.Collections.Generic;
using System.Globalization;
using VNotch.Services;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
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

    private static readonly Brush AllowanceHalfInk = Frozen(Color.FromRgb(0xD2, 0xAE, 0x7C));
    private static readonly Brush AllowanceLowInk = Frozen(Color.FromRgb(0xD2, 0x85, 0x7E));
    private static readonly Brush AllowanceWhite = Frozen(Color.FromRgb(0xF5, 0xF5, 0xF7));
    private static readonly Brush DefaultPillStatus = Frozen(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));
    private bool _allowancePill;
    private bool _allowancePillPaused;

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// Runs on usage updates and during the Claude sweep, using the same history for the tab and the compact pill.
    /// </summary>
    private void CheckAllowanceAlerts()
    {
        if (_cleanedUp || _settings == null) return;
        var sent = _settings.AllowanceAlertsSent ??= new();
        var now = DateTimeOffset.UtcNow;
        bool changed = AllowanceAlerts.Prune(sent, now);
        var claude = _claudeUsage?.Latest;
        bool shown = Try("Claude", "5h", claude?.FiveHour) || Try("Claude", "week", claude?.Week);
        foreach (var account in ClaudeCenter.AccountSlots)
        {
            if (shown) break;
            shown = Try(account.DisplayName, "5h", account.FiveHour) || Try(account.DisplayName, "week", account.Week);
        }
        if (changed || shown) _settingsService.SaveAsync(_settings).SafeFireAndForget("SETTINGS-SAVE");

        bool Try(string source, string kind, ClaudeLimitWindow? window)
        {
            if (window?.ResetsAt is not { } reset || reset <= now) return false;
            if (AllowanceAlerts.Due(source, kind, window, sent) is not { } due) return false;
            bool weekly = kind == "week";
            string title = weekly ? Loc.Get("control.claude.allowanceWeek", source) : source;
            var culture = CultureInfo.GetCultureInfo(Loc.CurrentLanguage == "de" ? "de-DE" : "en-US");
            string resetText = reset.ToLocalTime().ToString(weekly ? "ddd HH:mm" : "HH:mm", culture);
            string status = Loc.Get(weekly ? "control.claude.allowanceWeeklyLeft" : "control.claude.allowanceHourlyLeft",
                Math.Ceiling(Math.Clamp(100 - window.UsedPercent, 0, 100)), resetText);
            if (_isClaudeView && !_isAnimating)
            {
                if (!ClaudeCenter.TryShowAllowanceNotice(title + " " + status, due.Threshold)) return false;
            }
            else
            {
                if ((_compactPillArbiter.ActiveSlot != CompactPillSlot.None && !IsCodexJobPillShown) ||
                    !ShowNotchPill(title, status, appIcon: null, isClaude: false, tap: null, duration: AllowanceAlerts.DisplayTime))
                    return false;
                SetAllowancePill(due.Threshold <= 20 ? AllowanceLowInk : AllowanceHalfInk);
            }
            sent.AddRange(due.Keys);
            RuntimeLog.Info("CLAUDE-USAGE", $"{source} {kind} reached {due.Threshold} % left");
            return true;
        }
    }

    private void SetAllowancePill(Brush? mark)
    {
        _allowancePill = mark != null;
        _allowancePillPaused = false;
        NotificationAllowanceMark.Visibility = mark == null ? Visibility.Collapsed : Visibility.Visible;
        ClaudeNotificationStatus.Foreground = mark == null ? DefaultPillStatus : AllowanceWhite;
        if (mark is not SolidColorBrush color) return;
        ClaudeNotificationTitle.Foreground = AllowanceWhite;
        NotificationBellIcon.Visibility = Visibility.Collapsed;
        NotificationAllowanceMark.Fill = mark;
        NotificationAllowanceMark.Effect = new DropShadowEffect
        {
            Color = color.Color, BlurRadius = 12, ShadowDepth = 0, Opacity = .75
        };
    }

    private void ResumeAllowancePill()
    {
        if (!_allowancePillPaused) return;
        _allowancePillPaused = false;
        _claudeNotificationTimer?.Start();
    }
}
