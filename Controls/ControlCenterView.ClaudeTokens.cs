using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using VNotch.Services;
using VNotch.Services.ClaudeCode;

namespace VNotch.Controls;

/// <summary>
/// What the session in the big card used, under its activity line: the tokens of the current task and of the
/// whole chat, how full its context is, and the task's share of the five-hour limit. The counts come from the
/// session's transcript, read every few seconds while the tab is open, and roll to each new value.
/// </summary>
public partial class ControlCenterView
{
    private static readonly TimeSpan TokenRefreshInterval = TimeSpan.FromSeconds(3);
    private static readonly Brush TokenDivider = Frozen("#14FFFFFF");

    private ClaudeTokenLedger? _ledger;
    private readonly Dictionary<string, ClaudeSessionTokens> _tokensSeen = new(StringComparer.Ordinal);
    private DispatcherTimer? _tokenTimer;
    private bool _tokenReading;
    private TokenRow? _tokenRow;

    private sealed class TokenRow
    {
        public required string SessionId;
        public required FrameworkElement Host;
        public required RollingNumber Task, Chat, Context;
        public required TextBlock Share;
        public bool Filled;
    }

    /// <summary>Four small figures under a hairline: task, chat, context, share of the five-hour limit.</summary>
    private FrameworkElement TokenStats(ClaudeSession lead)
    {
        var grid = new Grid();
        for (int i = 0; i < 4; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var task = TokenFigure(grid, 0, Loc.Get("control.claude.tokensTask"));
        var chat = TokenFigure(grid, 1, Loc.Get("control.claude.tokensChat"));
        var context = TokenFigure(grid, 2, Loc.Get("control.claude.tokensContext"));
        var share = TokenFigure(grid, 3, Loc.Get("control.claude.tokensLimit"));

        var stack = new StackPanel { Background = Brushes.Transparent };
        stack.Children.Add(new Border { Height = 1, Background = TokenDivider, Margin = new Thickness(0, 14, 0, 11) });
        stack.Children.Add(grid);

        var row = new TokenRow
        {
            SessionId = lead.Id,
            Host = stack,
            Task = new RollingNumber(v => task.Text = FormatTokens(v)),
            Chat = new RollingNumber(v => chat.Text = FormatTokens(v)),
            Context = new RollingNumber(v => context.Text = FormatTokens(v)),
            Share = share
        };
        _tokenRow = row;
        if (_tokensSeen.TryGetValue(lead.Id, out var seen)) ShowTokens(row, seen, fromZero: false);
        return stack;
    }

    private TextBlock TokenFigure(Grid grid, int column, string label)
    {
        var value = new TextBlock
        {
            Text = "–",
            FontFamily = ControlFont,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = HeroTime,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Typography.SetNumeralAlignment(value, FontNumeralAlignment.Tabular);
        var cell = new StackPanel { Margin = new Thickness(column == 0 ? 0 : 12, 0, 0, 0) };
        cell.Children.Add(new TextBlock
        {
            Text = label,
            FontFamily = ControlFont,
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = ChipSoft,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 0, 3)
        });
        cell.Children.Add(value);
        Grid.SetColumn(cell, column);
        grid.Children.Add(cell);
        return value;
    }

    private void ShowTokens(TokenRow row, ClaudeSessionTokens tokens, bool fromZero)
    {
        if (fromZero && !AnimationConfig.ReduceMotion)
        {
            row.Task.To(0, 0);
            row.Chat.To(0, 0);
            row.Context.To(0, 0);
        }
        row.Task.To(tokens.Task.Total, 900);
        row.Chat.To(tokens.Chat.Total, 900);
        row.Context.To(tokens.Context, 900);
        row.Share.Text = tokens.TaskLimitPercent is { } percent ? FormatShare(percent) : "–";
        var chat = tokens.Chat;
        row.Host.ToolTip = Loc.Get("control.claude.tokensBreakdown",
            FormatTokens(chat.Input), FormatTokens(chat.CacheWrite), FormatTokens(chat.CacheRead), FormatTokens(chat.Output));
        row.Filled = true;
    }

    /// <summary>"812", "48,1k", "266k", "2,4M", in the reader's own decimal sign.</summary>
    internal static string FormatTokens(double count)
    {
        var culture = CultureInfo.CurrentCulture;
        count = Math.Max(0, count);
        if (count < 1_000) return Math.Round(count).ToString("0", culture);
        if (count < 100_000) return (count / 1_000).ToString("0.#", culture) + "k";
        if (count < 1_000_000) return (count / 1_000).ToString("0", culture) + "k";
        if (count < 100_000_000) return (count / 1_000_000).ToString("0.#", culture) + "M";
        return (count / 1_000_000).ToString("0", culture) + "M";
    }

    /// <summary>"≈ 2,4 %": an estimate, so it says so; tiny shares do not pretend to a precise zero.</summary>
    internal static string FormatShare(double percent)
    {
        var culture = CultureInfo.CurrentCulture;
        if (percent < 0.1) return "< " + 0.1.ToString("0.0", culture) + " %";
        return "≈ " + percent.ToString(percent < 10 ? "0.0" : "0", culture) + " %";
    }

    private void StartTokenClock()
    {
        if (_claude == null) return;
        if (_tokenTimer == null)
        {
            _tokenTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TokenRefreshInterval };
            _tokenTimer.Tick += (_, _) => RefreshTokens();
        }
        _tokenTimer.Start();
        RefreshTokens();
    }

    private void StopTokenClock() => _tokenTimer?.Stop();

    /// <summary>Reads the big card's session off the UI thread and rolls its figures to what it finds.</summary>
    private async void RefreshTokens()
    {
        if (_tokenRow is not { } row || _tokenReading || _claude == null || _disposed) return;
        ClaudeSession? session = null;
        foreach (var s in _claude.GetSessions())
            if (s.Id == row.SessionId) session = s;
        if (session?.TranscriptPath is not { Length: > 0 } path) return;
        var turnStart = session.TurnStartedUtc;
        var fiveHour = _usage?.Latest?.FiveHour;
        var ledger = _ledger ??= new ClaudeTokenLedger();
        _tokenReading = true;
        try
        {
            var tokens = await Task.Run(() => ledger.Read(path, turnStart, fiveHour, DateTime.UtcNow));
            if (tokens == null || _disposed) return;
            _tokensSeen[row.SessionId] = tokens;
            // The card may have been rebuilt meanwhile: fill whichever row shows this session now.
            if (_tokenRow is { } now && now.SessionId == row.SessionId)
                ShowTokens(now, tokens, fromZero: !now.Filled && IsVisible);
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("CLAUDE", $"Token count failed: {ex.Message}");
        }
        finally
        {
            _tokenReading = false;
        }
    }

    /// <summary>When the tab opens the figures roll up from zero, like the limit bars fill.</summary>
    private void RollTokensIn()
    {
        if (_tokenRow is { } row && _tokensSeen.TryGetValue(row.SessionId, out var tokens)) ShowTokens(row, tokens, fromZero: true);
    }
}
