using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
    private string _codexAccountsDirectory = CodexAccounts.DirectoryPath;
    private readonly List<CodexAccount> _codexAccounts = new();
    private FileSystemWatcher? _codexAccountsWatcher;
    private IReadOnlyDictionary<string, CodexBudget>? _codexBudgets;
    internal IReadOnlyList<CodexAccount> AccountSlots => _codexAccounts;
    internal event Action? AllowanceDataChanged;

    private void WatchCodexAccounts()
    {
        if (_codexAccountsWatcher != null || !Directory.Exists(_codexAccountsDirectory)) return;
        try
        {
            var watcher = new FileSystemWatcher(_codexAccountsDirectory, "*.json");
            watcher.Changed += OnCodexAccountFileChanged;
            watcher.Created += OnCodexAccountFileChanged;
            watcher.Deleted += OnCodexAccountFileChanged;
            watcher.Renamed += OnCodexAccountFileChanged;
            watcher.EnableRaisingEvents = true;
            _codexAccountsWatcher = watcher;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RuntimeLog.Warn("CODEX", $"Account watch unavailable: {ex.GetType().Name}");
        }
    }

    private void OnCodexAccountFileChanged(object sender, FileSystemEventArgs e)
    {
        if (e.Name is CodexAccounts.SlotsFile or CodexAccounts.BudgetFile ||
            e is RenamedEventArgs { OldName: CodexAccounts.SlotsFile or CodexAccounts.BudgetFile })
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => RefreshCodexUsage()));
    }

    private void SyncCodexAccounts()
    {
        WatchCodexAccounts();
        if (CodexAccounts.ReadSlots(_codexAccountsDirectory) is { } slots)
        {
            var removed = _codexAccounts.Where(account => !slots.Contains(account.Slot)).ToArray();
            var current = slots.Select(slot => _codexAccounts.Find(account => account.Slot == slot) ??
                new CodexAccount(slot, new CodexUsageClient(codexDirectory: slot.Home, useRolloutFallback: false))).ToArray();
            _codexAccounts.Clear();
            _codexAccounts.AddRange(current);
            foreach (var account in removed)
            {
                ForgetAccountRow(account);
                account.Dispose();
            }
        }
        _codexBudgets = CodexAccounts.ReadBudget(_codexAccountsDirectory) ?? _codexBudgets;
        foreach (var account in _codexAccounts)
            account.Budget = _codexBudgets?.GetValueOrDefault(account.Slot.Name);
    }

    private void DisposeCodexAccounts()
    {
        _codexAccountsWatcher?.Dispose();
        foreach (var account in _codexAccounts) account.Dispose();
        _codexAccounts.Clear();
    }

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
        if (_disposed || Page != ControlCenterPage.Claude) return;
        SyncCodexAccounts();
        BuildClaudeSection();
        AllowanceDataChanged?.Invoke();
        bool allowNetwork = !_isLocalOnly();
        if (!allowNetwork) return;
        try
        {
            var reads = _codexAccounts.Select(account => Task.Run(() => account.Client.RefreshAsync(UsageViewMinAge))).ToArray();
            if ((await Task.WhenAll(reads)).Any(usage => usage != null) && !_disposed)
            {
                BuildClaudeSection();
                AllowanceDataChanged?.Invoke();
            }
        }
        catch (ObjectDisposedException) { } // A slot was removed while its read was queued.
        catch (Exception ex) { RuntimeLog.Warn("CODEX", $"Usage refresh failed: {ex.GetType().Name}"); }
    }

    private Border CodexChip(CodexSession session)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(ChipDot(CodexAccent));
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
        return chip;
    }

    internal static string FormatResetTime(DateTimeOffset resets, DateTimeOffset now)
    {
        var local = resets.ToLocalTime();
        return local.ToString(local.Date == now.ToLocalTime().Date ? "HH:mm" : "ddd HH:mm", CultureInfo.CurrentCulture);
    }

    private void AppendCodexAndResetKey(StringBuilder sb, ClaudeLimits? limits)
    {
        sb.Append("|codex|").Append(_isLocalOnly());
        foreach (var s in _codexSessions)
            sb.Append('|').Append(s.Id).Append(s.Status).Append(s.Title).Append(s.Project).Append(s.Cwd);
        if (limits != null)
            foreach (var grant in limits.Grants) sb.Append('|').Append(grant);
        foreach (var state in _resetStates.OrderBy(s => s.Key, StringComparer.Ordinal))
            sb.Append('|').Append(state.Key).Append(state.Value);
    }

    private sealed record ResetOption(string Id, string Name, DateTimeOffset? Expiry, bool Usable, string? Reason);
    private sealed record ResetState(DateTimeOffset Until, bool Running = false, string? Result = null, string? AccountId = null);
    private readonly Dictionary<string, ResetState> _resetStates = new(StringComparer.Ordinal);
    private DispatcherTimer? _resetFeedbackTimer;

    private void AddResetRows(ClaudeLimits? limits, string? inlineGrant = null)
    {
        var grants = limits?.Grants.Where(g => g.Id != inlineGrant).Select(g => new ResetOption(g.Id, Loc.Get(g.Kind switch
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

    private async Task RedeemReset(string provider, string id, CodexAccount? account = null, bool inline = false)
    {
        if (_disposed || !IsVisible || Page != ControlCenterPage.Claude || _isLocalOnly()) return;
        if (inline && !_accountsOpen) return;
        var credit = account?.Client.Latest?.Credits.FirstOrDefault(c => c.Id == id &&
            (c.ExpiresAt == null || c.ExpiresAt > DateTimeOffset.UtcNow));
        bool usable = account != null
            ? _codexAccounts.Contains(account) && credit != null
            : provider == "claude" && _usage?.Latest?.Grants.Any(g => g.Id == id && g.UsableNow) == true;
        if (!usable) return;
        string key = provider + ":" + id;
        if (_resetStates.Any(s => s.Key.StartsWith(provider + ":", StringComparison.Ordinal) && s.Value.Running)) return;
        if (_resetStates.TryGetValue(key, out var current) && current.Result == "resetDone") return;
        if (!_resetStates.TryGetValue(key, out var state) || state.Result != null || state.Until <= DateTimeOffset.UtcNow ||
            state.AccountId != credit?.AccountId)
        {
            if (inline) DisarmAccountCredits();
            _resetStates[key] = new ResetState(DateTimeOffset.UtcNow.AddSeconds(inline ? 3 : 4), AccountId: credit?.AccountId);
            StartResetFeedback();
            BuildClaudeSection();
            return;
        }
        _resetStates[key] = new ResetState(DateTimeOffset.MaxValue, Running: true);
        BuildClaudeSection();
        string result = "resetFailed";
        try
        {
            if (account != null)
                result = await Task.Run(() => account.Client.RedeemAsync(id, expectedAccountId: credit?.AccountId));
            else if (_usage != null)
            {
                var claimed = await Task.Run(() => _usage.ClaimAsync(id));
                result = claimed.Success ? "resetDone" : claimed.Reason switch
                {
                    "auth" => "resetAuth", "retry" => "resetRetry", "unavailable" => "resetUnavailable", _ => "resetFailed"
                };
            }
        }
        catch (Exception ex) { RuntimeLog.Warn("LIMIT-RESET", $"Reset failed: {ex.GetType().Name}"); }
        if (_disposed || (account != null && !_codexAccounts.Contains(account))) return;
        _resetStates[key] = new ResetState(DateTimeOffset.UtcNow.AddSeconds(result == "resetDone" ? 1.1 : 5), Result: result);
        StartResetFeedback();
        BuildClaudeSection();
        AllowanceDataChanged?.Invoke();
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
