using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using VNotch.Services;
using VNotch.Services.ClaudeCode;

namespace VNotch.Controls;

public partial class ControlCenterView
{
    private static readonly Brush CodexAccent = Frozen("#8FA3B8");
    private List<CodexSession> _codexSessions = new();
    private DispatcherTimer? _codexTimer;
    private bool _codexReading;

    private void StartCodexClock()
    {
        if (_codexTimer == null)
        {
            _codexTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
            _codexTimer.Tick += (_, _) => RefreshCodex();
        }
        _codexTimer.Start();
        RefreshCodex();
    }

    private void StopCodexClock() => _codexTimer?.Stop();

    private async void RefreshCodex()
    {
        if (_disposed || !IsVisible || Page != ControlCenterPage.Claude || _codexReading) return;
        _codexReading = true;
        RefreshUsage(UsageViewMinAge);
        RefreshCodexUsage();
        try
        {
            var sessions = await Task.Run(() => CodexSessionReader.Read());
            if (_disposed || !IsVisible) return;
            _codexSessions = sessions;
            BuildClaudeSection();
        }
        catch (Exception ex) { RuntimeLog.Warn("CODEX", $"Session read failed: {ex.Message}"); }
        finally { _codexReading = false; }
    }

    private async void RefreshCodexUsage()
    {
        if (_codexUsage == null || _disposed || !IsVisible || Page != ControlCenterPage.Claude) return;
        bool allowNetwork = !_isLocalOnly();
        try
        {
            if (await Task.Run(() => _codexUsage.RefreshAsync(UsageViewMinAge, allowNetwork: allowNetwork)) != null && !_disposed)
                BuildClaudeSection();
        }
        catch (Exception ex) { RuntimeLog.Warn("CODEX", $"Usage refresh failed: {ex.Message}"); }
    }

    private Border CodexChip(CodexSession session)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(ChipDot(CodexAccent, session.Status == CodexSessionStatus.Working));
        row.Children.Add(new TextBlock
        {
            Text = session.Title, FontFamily = ControlFont, FontSize = 12, FontWeight = FontWeights.SemiBold,
            Foreground = ChipText, MaxWidth = 210, TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0, 0, 0)
        });
        row.Children.Add(new TextBlock
        {
            Text = Loc.Get("control.claude.codex"), FontFamily = ControlFont, FontSize = 11.5, FontWeight = FontWeights.SemiBold,
            Foreground = CodexAccent, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0, 0, 0)
        });
        var fill = new SolidColorBrush(CardRest);
        var chip = new Border
        {
            Child = row, Background = fill, CornerRadius = new CornerRadius(14),
            Padding = new Thickness(10, 7, 13, 7), Margin = new Thickness(0, 0, 8, 8),
            Cursor = Cursors.Hand, ToolTip = session.Project + " · " + session.Cwd
        };
        HoverLift(chip, fill, null);
        System.Windows.Automation.AutomationProperties.SetName(chip, session.Title + " · " + Loc.Get("control.claude.codex"));
        chip.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            if (CodexSessionReader.FocusApp()) SessionOpened?.Invoke();
        };
        _cardScratch.Add(("codex:" + session.Id, chip));
        return chip;
    }

    private void AddCodexLimits()
    {
        var limits = _codexUsage?.Latest;
        var panel = new StackPanel { Margin = new Thickness(4, 18, 4, 2) };
        panel.Children.Add(QuietText(Loc.Get("control.claude.codex"), CodexAccent));
        var grid = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        if (limits?.FiveHour is { } fiveHour)
            grid.Children.Add(MiniLimit("codex:5h", Loc.Get("control.claude.limit5h"), fiveHour, CodexAccent));
        if (limits?.Week is { } week)
        {
            var mini = MiniLimit("codex:week", Loc.Get("control.claude.limitWeek"), week, CodexAccent);
            Grid.SetColumn(mini, 2);
            grid.Children.Add(mini);
        }
        if (limits?.FiveHour == null && limits?.Week == null)
            grid.Children.Add(QuietText(Loc.Get("control.claude.limitsUnavailable"), ChipSoft));
        panel.Children.Add(grid);
        _claudeSection.Children.Add(panel);
        if (limits != null)
            AddResetRow("codex", limits.Credits.Select(c => new ResetOption(c.Id,
                Loc.Get("control.claude.resetFull"), c.ExpiresAt, true, null)).ToList());
    }

    internal static string FormatResetTime(DateTimeOffset resets, DateTimeOffset now)
    {
        var local = resets.ToLocalTime();
        return local.ToString(local.Date == now.ToLocalTime().Date ? "HH:mm" : "ddd HH:mm", CultureInfo.CurrentCulture);
    }

    private void AppendCodexAndResetKey(StringBuilder sb, ClaudeLimits? limits)
    {
        sb.Append("|codex|").Append(_codexUsage?.HasSource).Append(_isLocalOnly());
        foreach (var s in _codexSessions)
            sb.Append('|').Append(s.Id).Append(s.Status).Append(s.Title).Append(s.Project).Append(s.Cwd);
        if (_codexUsage?.Latest is { } codex)
        {
            sb.Append('|').Append(codex.FiveHour).Append(codex.Week);
            foreach (var c in codex.Credits) sb.Append('|').Append(c);
        }
        if (limits != null)
            foreach (var grant in limits.Grants) sb.Append('|').Append(grant);
        foreach (var state in _resetStates.OrderBy(s => s.Key, StringComparer.Ordinal))
            sb.Append('|').Append(state.Key).Append(state.Value);
    }

    private sealed record ResetOption(string Id, string Name, DateTimeOffset? Expiry, bool Usable, string? Reason);
    private sealed record ResetState(DateTimeOffset Until, bool Running = false, string? Result = null);
    private readonly Dictionary<string, ResetState> _resetStates = new(StringComparer.Ordinal);
    private DispatcherTimer? _resetFeedbackTimer;

    private void AddResetRows(ClaudeLimits? limits)
    {
        var grants = limits?.Grants.Select(g => new ResetOption(g.Id, Loc.Get(g.Kind switch
        {
            ClaudeResetKind.Fable => "control.claude.resetFable",
            ClaudeResetKind.Full => "control.claude.resetFull",
            _ => "control.claude.reset5h"
        }), g.EndsAt, g.UsableNow, ResetUnavailableReason(g))).ToList();
        AddResetRow("claude", grants ?? new List<ResetOption>());
    }

    internal static string ResetUnavailableReason(ClaudeResetGrant grant) => "control.claude." + (grant.UseRequiresLimit
        ? "resetOnlyAtLimit"
        : grant.UnavailableReason switch
        {
            "not_at_limit" or "requires_limit" => "resetOnlyAtLimit",
            "cooldown" or "cooldown_active" => "resetCooldown",
            "not_started" or "not_yet_started" => "resetNotStarted",
            "expired" => "resetExpired",
            _ => "resetUnavailable"
        });

    private TextBlock QuietText(string text, Brush brush) => new()
    {
        Text = text, FontFamily = ControlFont, FontSize = 11, FontWeight = FontWeights.SemiBold,
        Foreground = brush, VerticalAlignment = VerticalAlignment.Center
    };

    private async void RedeemReset(string provider, string id)
    {
        if (_disposed || !IsVisible || Page != ControlCenterPage.Claude || _isLocalOnly()) return;
        bool usable = provider == "codex"
            ? _codexUsage?.Latest?.Credits.Any(c => c.Id == id) == true
            : _usage?.Latest?.Grants.Any(g => g.Id == id && g.UsableNow) == true;
        if (!usable) return;
        string key = provider + ":" + id;
        if (_resetStates.Any(s => s.Key.StartsWith(provider + ":", StringComparison.Ordinal) && s.Value.Running)) return;
        if (_resetStates.TryGetValue(key, out var current) && current.Result == "resetDone") return;
        if (!_resetStates.TryGetValue(key, out var state) || state.Result != null || state.Until <= DateTimeOffset.UtcNow)
        {
            _resetStates[key] = new ResetState(DateTimeOffset.UtcNow.AddSeconds(4));
            StartResetFeedback();
            BuildClaudeSection();
            return;
        }
        _resetStates[key] = new ResetState(DateTimeOffset.MaxValue, Running: true);
        BuildClaudeSection();
        string result = "resetFailed";
        try
        {
            if (provider == "codex" && _codexUsage != null)
                result = await Task.Run(() => _codexUsage.RedeemAsync(id));
            else if (_usage != null)
            {
                var claimed = await Task.Run(() => _usage.ClaimAsync(id));
                result = claimed.Success ? "resetDone" : claimed.Reason switch
                {
                    "auth" => "resetAuth", "retry" => "resetRetry", "unavailable" => "resetUnavailable", _ => "resetFailed"
                };
            }
        }
        catch (Exception ex) { RuntimeLog.Warn("LIMIT-RESET", $"Reset failed: {ex.Message}"); }
        if (_disposed) return;
        _resetStates[key] = new ResetState(DateTimeOffset.UtcNow.AddSeconds(result == "resetDone" ? 1.1 : 5), Result: result);
        StartResetFeedback();
        BuildClaudeSection();
    }

    private void StartResetFeedback()
    {
        if (!IsVisible || _disposed || Page != ControlCenterPage.Claude) return;
        if (_resetFeedbackTimer == null)
        {
            _resetFeedbackTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
            _resetFeedbackTimer.Tick += (_, _) =>
            {
                var expired = _resetStates.Where(s => !s.Value.Running && s.Value.Until <= DateTimeOffset.UtcNow).Select(s => s.Key).ToList();
                foreach (string key in expired) _resetStates.Remove(key);
                if (expired.Count > 0) BuildClaudeSection();
                if (!_resetStates.Any(s => !s.Value.Running)) _resetFeedbackTimer.Stop();
            };
        }
        _resetFeedbackTimer.Start();
    }

    private void StopResetFeedback()
    {
        _resetFeedbackTimer?.Stop();
        foreach (string key in _resetStates.Where(s => !s.Value.Running).Select(s => s.Key).ToList()) _resetStates.Remove(key);
        foreach (var row in _resetRows.Values) row.Stop();
    }
}
