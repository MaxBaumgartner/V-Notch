using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using VNotch.Services;
using VNotch.Services.ClaudeCode;

namespace VNotch.Controls;

/// <summary>The Claude accounts face and session controls below it.</summary>
public partial class ControlCenterView
{
    #region Colours

    private static Color Hex(int rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    // The mood per state: the main colour and the two neighbours the backdrop shows with it.
    private static readonly (Color Main, Color Warmer, Color Cooler) ClaudeQuiet = (Hex(0xA89A90), Hex(0x8E7E74), Hex(0x7F93A8));
    private static readonly (Color Main, Color Warmer, Color Cooler) ClaudeAtWork = (Hex(0xD49A82), Hex(0xC98A70), Hex(0xA19BD2));
    private static readonly (Color Main, Color Warmer, Color Cooler) ClaudeWaits = (Hex(0xD6B07E), Hex(0xD2957E), Hex(0xBFB48A));
    private static readonly (Color Main, Color Warmer, Color Cooler) ClaudeNearLimit = (Hex(0xD2857E), Hex(0xC98AB0), Hex(0xD2AE7C));

    // A session's own colour: amber while it waits, sage once done, grey when idle; at work it wears the mood.
    private static readonly Color WaitingAmber = Hex(0xD6B07E);
    private static readonly Color DoneSage = Hex(0x9BC7BC);
    private static readonly Color IdleGrey = Hex(0x8C8C96);
    private static readonly Brush WaitingDot = FrozenOf(WaitingAmber);

    private static readonly Color CardRest = Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF);
    private static readonly Color CardLifted = Color.FromArgb(0x17, 0xFF, 0xFF, 0xFF);
    private static readonly Brush HeroName = Frozen("#CCFFFFFF");
    private static readonly Brush HeroSoft = Frozen("#99FFFFFF");
    private static readonly Brush HeroTime = Frozen("#E6FFFFFF");
    private static readonly Brush ChipText = Frozen("#D9FFFFFF");
    private static readonly Brush ChipSoft = Frozen("#73FFFFFF");
    private static readonly Brush LimitLabel = Frozen("#BFFFFFFF");
    private static readonly Brush LimitValue = Frozen("#E6FFFFFF");
    private static readonly Brush LimitTrack = Frozen("#1FFFFFFF");

    /// <summary>From this share of a plan limit on, the tab turns rosé.</summary>
    internal const double NearLimitPercent = 85;

    private const string ChipsTag = "chips";


    private static Brush FrozenOf(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    #endregion

    #region State

    // The hero's state colour (its word, tick and accent button), kept across rebuilds so a change fades.
    private readonly ClaudeStatusTint _heroTint = new(IdleGrey);
    private SolidColorBrush _heroInk => _heroTint.Brush;
    private string? _heroSessionId;
    // The colour of work (terracotta, rosé near a limit): a working session keeps it even while the tab is amber.
    private Color _workColor = ClaudeAtWork.Main;
    // A chip tapped to bring its session up; it holds until that session goes or another starts to wait.
    private string? _focusedSessionId;
    private readonly HashSet<string> _waitingSeen = new(StringComparer.Ordinal);
    private TextBlock? _heroTick;

    // Request cards on the page by id, so an answer can fold its card away before it goes.
    private readonly Dictionary<string, FrameworkElement> _requestCards = new(StringComparer.Ordinal);

    private Color? _claudeMoodMain;
    private readonly Dictionary<(string Session, string Part), TextBlock> _claudeTexts = new();
    private readonly List<(TextBlock Text, string Value, bool Animate, bool Optional)> _claudeTextUpdates = new();
    private readonly Dictionary<string, ClaudeStatusTint> _claudeDots = new(StringComparer.Ordinal);
    private Dictionary<string, Border> _claudeSessionRows = new(StringComparer.Ordinal);
    private Dictionary<string, ClaudeSession> _claudeShownSessions = new(StringComparer.Ordinal);
    private string? _claudeShownHeroId;
    private readonly List<(Border Row, bool Chip, int Index)> _claudeSessionExits = new();
    private bool _claudeWasShown;

    #endregion

    #region Building

    private void BuildClaudeSection() => UpdateClaudeSection(entering: false);

    private void UpdateClaudeSection(bool entering)
    {
        _accent = _mood;
        if (_disposed || _claude == null || (!IsVisible && _claudeWasShown)) return;
        if (_settlingClaudeRequests.Count > 0 || _swipingClaudePermission != null) return;
        var sessions = _claude.GetSessions();
        var pending = _claude.GetPending();
        var limits = _usage?.Latest;
        TrackFocus(sessions);
        var order = FocusOrder(sessions, _composingSessionId ?? _focusedSessionId);
        var lead = order.Count > 0 ? order[0] : null;
        RefreshClaudeFace(lead, limits);
        // A rebuild would take the text box from under the typing; catch up once the message is sent.
        if (_composerBox?.IsKeyboardFocusWithin == true)
        {
            _rebuildAfterComposing = true;
            if (FitClaudeAccountFace()) ContentHeightChanged?.Invoke();
            return;
        }
        bool listening = _hookServer?.IsListening == true;
        string key = ClaudeKey(order, pending, limits, listening);
        if (key == _claudeKey)
        {
            if (IsVisible && !_claudeWasShown) RememberClaudeSessions(sessions);
            UpdateClaudeClock();
            UpdateTokenClock(lead);
            if (FitClaudeAccountFace()) ContentHeightChanged?.Invoke();
            return;
        }
        _claudeKey = key;

        ApplyClaudeMood(ClaudeMoodFor(sessions, limits));
        _workColor = NearLimit(limits) ? ClaudeNearLimit.Main : ClaudeAtWork.Main;

        _elapsedLabels.Clear();
        _firstRequestCard = null;
        _requestCards.Clear();
        _heroTick = null;
        _tokenRow = null;
        foreach (var seen in new List<string>(_tokensSeen.Keys))
            if (!sessions.Exists(s => s.Id == seen)) _tokensSeen.Remove(seen);
        ForgetAnsweredQuestions(pending);
        var previousRows = _claudeSessionRows;
        _claudeSessionRows = new(StringComparer.Ordinal);
        var activeIds = sessions.Select(session => session.Id).ToHashSet(StringComparer.Ordinal);
        var leaving = previousRows.Where(pair => !activeIds.Contains(pair.Key)).Select(pair =>
        {
            var parent = (Panel)pair.Value.Parent;
            return (Row: pair.Value, Chip: parent is WrapPanel, Index: parent.Children.IndexOf(pair.Value));
        }).ToList();
        foreach (var exit in leaving)
            ((Panel)exit.Row.Parent).Children.Remove(exit.Row);
        _rebuildingResetRows = true;
        try
        {
            foreach (var exit in _claudeSessionExits)
                ((Panel)exit.Row.Parent).Children.Remove(exit.Row);
            while (_claudeSection.Children.Count > 1) _claudeSection.Children.RemoveAt(_claudeSection.Children.Count - 1);

            if (!_claudeInstalled) _claudeSection.Children.Add(SetupCard(listening));
            // Hooks in a project's own settings report here too, without our entry in the user settings.
            if ((_claudeInstalled || sessions.Count > 0) && !listening)
                _claudeSection.Children.Add(HintCard("", Loc.Get("control.claude.portBusy", ClaudeHookServer.DefaultPort)));
            if (lead != null && (lead.Cwd.Length > 0 || pending.Count > 0 || _composingSessionId != null ||
                !string.IsNullOrEmpty(lead.Activity ?? lead.Prompt ?? lead.Summary)))
                _claudeSection.Children.Add(_claudeSessionRows[lead.Id] = Hero(lead, pending));
            else _heroSessionId = null;

            bool animateRows = _claudeWasShown && IsVisible && !AnimationConfig.ReduceMotion;
            // Rows removed while the tab was away are already offscreen.
            if (!animateRows || entering) leaving.Clear();
            bool exitingChips = _claudeSessionExits.Concat(leaving).Any(exit => exit.Chip);
            var chips = order.Count > 1 || _codexSessions.Count > 0 || exitingChips ? Chips(order) : null;
            if (chips != null) _claudeSection.Children.Add(chips);
            if (_accountsOpen) AddResetRows(limits, _inlineClaudeGrant);
            foreach (var detail in _claudeSection.Children.OfType<FrameworkElement>().Skip(1))
            {
                var margin = detail.Margin;
                detail.Margin = new Thickness(margin.Left + 20, margin.Top, margin.Right + 20, margin.Bottom);
            }
            foreach (var exit in _claudeSessionExits.Concat(leaving))
            {
                Panel panel = exit.Chip ? chips! : _claudeSection;
                panel.Children.Insert(Math.Min(exit.Index, panel.Children.Count), exit.Row);
            }
            foreach (var exit in leaving)
            {
                _claudeSessionExits.Add(exit);
                FoldClaudeCard(exit.Row, () => RemoveClaudeSessionExit(exit));
            }
            if (animateRows)
                foreach (var (id, row) in _claudeSessionRows)
                    if (!previousRows.ContainsKey(id)) GlideIn(row);
        }
        finally { _rebuildingResetRows = false; }
        foreach (var (text, value, animate, optional) in _claudeTextUpdates)
            ApplyClaudeText(text, value, animate, optional);
        _claudeTextUpdates.Clear();
        FitClaudeAccountFace();
        foreach (var pair in _claudeTexts.ToList())
        {
            if ((!pair.Value.IsVisible || !activeIds.Contains(pair.Key.Session)) && DetailTexts.TryGetValue(pair.Value, out var state))
                SettleDetailText(pair.Value, state);
            if (!activeIds.Contains(pair.Key.Session)) _claudeTexts.Remove(pair.Key);
        }
        foreach (var id in _claudeDots.Keys.Where(id => !activeIds.Contains(id)).ToArray())
            if (_claudeDots.Remove(id, out var dot)) dot.Stop();

        if (IsVisible)
        {
            if (lead != null && _claudeShownSessions.TryGetValue(lead.Id, out var previous) &&
                (previous.Status != lead.Status || previous.FinishedUtc != lead.FinishedUtc))
                PopTick();
            RememberClaudeSessions(sessions);
            UpdateTokenClock(lead);
            RefreshTokens();
        }
        UpdateClaudeClock();
        ContentHeightChanged?.Invoke();
    }

    private void RememberClaudeSessions(List<ClaudeSession> sessions)
    {
        _claudeShownSessions = sessions.ToDictionary(session => session.Id, StringComparer.Ordinal);
        _claudeShownHeroId = _heroSessionId;
        _claudeWasShown = true;
    }

    private readonly Dictionary<TextBlock, (string Value, bool Optional)> _deferredClaudeText = new();

    private void ApplyClaudeText(TextBlock text, string value, bool animate, bool optional = false)
    {
        if (animate && IsVisible && !AnimationConfig.ReduceMotion && !text.IsVisible &&
            DetailTexts.TryGetValue(text, out var state) && state.Target != value)
        {
            bool queued = _deferredClaudeText.ContainsKey(text);
            _deferredClaudeText[text] = (value, optional);
            if (!queued) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (_deferredClaudeText.Remove(text, out var next) && !_disposed && IsVisible)
                    SetDetailText(text, next.Value, rollUp: true, collapseEmpty: next.Optional);
            }));
            return;
        }
        _deferredClaudeText.Remove(text);
        SetDetailText(text, value, rollUp: true, animate: animate, collapseEmpty: optional);
    }

    private string ClaudeKey(List<ClaudeSession> order, List<ClaudePermissionRequest> pending, ClaudeLimits? limits, bool listening)
    {
        var sb = new StringBuilder();
        sb.Append(_claudeInstalled).Append(listening).Append(_soundsOn?.Invoke()).Append(_claudeInNotch?.Invoke()).Append(_composingSessionId).Append('|');
        if (limits != null)
        {
            sb.Append(limits.FiveHour?.UsedPercent).Append(limits.FiveHour?.ResetsAt?.ToUnixTimeSeconds() / 60)
              .Append(limits.Week?.UsedPercent).Append(limits.Week?.ResetsAt?.ToUnixTimeSeconds() / 60).Append('|');
        }
        foreach (var s in order)
            sb.Append(s.Id).Append(s.Status).Append(s.Title).Append(s.Activity).Append(s.Prompt).Append(s.Summary).Append(s.Cwd)
              .Append(s.TurnStartedUtc?.Ticks).Append(s.FinishedUtc?.Ticks).Append(';');
        foreach (var p in pending) sb.Append(p.Id).Append(';');
        AppendCodexAndResetKey(sb, limits);
        return sb.ToString();
    }

    /// <summary>
    /// The order the tab shows sessions in: <paramref name="pinned"/> (tapped, or getting a message) first, then
    /// those waiting for you and those at work (longest first, like the closed notch), then the finished ones
    /// (latest first) and the idle ones.
    /// </summary>
    internal static List<ClaudeSession> FocusOrder(IEnumerable<ClaudeSession> sessions, string? pinned)
    {
        var order = sessions
            .OrderBy(s => s.Status switch
            {
                ClaudeSessionStatus.Waiting => 0,
                ClaudeSessionStatus.Working => 1,
                ClaudeSessionStatus.Done => 2,
                _ => 3
            })
            .ThenBy(s => s.Status is ClaudeSessionStatus.Waiting or ClaudeSessionStatus.Working ? s.TurnStartedUtc ?? DateTime.MaxValue : DateTime.MaxValue)
            .ThenByDescending(s => s.Status == ClaudeSessionStatus.Done ? s.FinishedUtc ?? s.LastEventUtc : s.LastEventUtc)
            .ThenBy(s => s.Id, StringComparer.Ordinal)
            .ToList();
        int at = pinned == null ? -1 : order.FindIndex(s => s.Id == pinned);
        if (at > 0)
        {
            var first = order[at];
            order.RemoveAt(at);
            order.Insert(0, first);
        }
        return order;
    }

    /// <summary>The tab's mood: amber while anything waits, rosé near a limit, terracotta at work, warm grey otherwise.</summary>
    internal static (Color Main, Color Warmer, Color Cooler) ClaudeMoodFor(IReadOnlyCollection<ClaudeSession> sessions, ClaudeLimits? limits)
    {
        bool waiting = false, working = false;
        foreach (var s in sessions)
        {
            waiting |= s.Status == ClaudeSessionStatus.Waiting;
            working |= s.Status == ClaudeSessionStatus.Working;
        }
        if (waiting) return ClaudeWaits;
        if (NearLimit(limits)) return ClaudeNearLimit;
        return working ? ClaudeAtWork : ClaudeQuiet;
    }

    internal static bool NearLimit(ClaudeLimits? limits) =>
        limits != null && Math.Max(limits.FiveHour?.UsedPercent ?? 0, limits.Week?.UsedPercent ?? 0) >= NearLimitPercent;

    /// <summary>A hand-picked session keeps the stage until it is gone or another session starts to wait.</summary>
    private void TrackFocus(List<ClaudeSession> sessions)
    {
        bool keep = false;
        var waiting = new List<string>();
        foreach (var s in sessions)
        {
            if (s.Id == _focusedSessionId) keep = true;
            if (s.Status == ClaudeSessionStatus.Waiting) waiting.Add(s.Id);
        }
        foreach (var id in waiting)
            if (!_waitingSeen.Contains(id) && id != _focusedSessionId) keep = false;
        if (!keep) _focusedSessionId = null;
        _waitingSeen.Clear();
        _waitingSeen.UnionWith(waiting);
    }

    private void FocusSession(string sessionId)
    {
        _focusedSessionId = sessionId;
        _claudeKey = "";
        BuildClaudeSection();
    }

    private void ApplyClaudeMood((Color Main, Color Warmer, Color Cooler) mood)
    {
        if (_claudeMoodMain == mood.Main) return;
        _claudeMoodMain = mood.Main;
        SetMoodColors(mood.Main, mood.Warmer, mood.Cooler);
    }

    private Color StatusColor(ClaudeSessionStatus status) => status switch
    {
        ClaudeSessionStatus.Waiting => WaitingAmber,
        ClaudeSessionStatus.Working => _workColor,
        ClaudeSessionStatus.Done => DoneSage,
        _ => IdleGrey
    };

    private Brush SessionDot(ClaudeSession session, ClaudeSession? previous)
    {
        Color color = StatusColor(session.Status);
        if (!_claudeDots.TryGetValue(session.Id, out var tint))
            _claudeDots[session.Id] = tint = new ClaudeStatusTint(previous == null ? color : StatusColor(previous.Status));
        else if (previous != null)
            tint.Set(StatusColor(previous.Status), animate: false);
        tint.Set(color, IsVisible);
        return tint.Brush;
    }

    private sealed class ClaudeStatusTint
    {
        private readonly WeatherTween _mix = new();
        private Color _from, _target;
        public SolidColorBrush Brush { get; }

        public ClaudeStatusTint(Color color)
        {
            Brush = new SolidColorBrush(color);
            _from = _target = color;
            _mix.Step = progress => Brush.Color = Mix(_from, _target, progress);
        }

        public void Set(Color color, bool animate)
        {
            if (animate && color == _target) return;
            _from = Brush.Color;
            _target = color;
            if (!animate || AnimationConfig.ReduceMotion || _from == color) Stop();
            else
            {
                _mix.Stop(0);
                Motion.To(_mix, WeatherTween.ValueProperty, 1, Motion.StatusRollIn, done: Stop);
            }
        }

        public void Stop()
        {
            _mix.Stop(1);
            if (Brush.Color != _target) Brush.Color = _target;
        }
    }

    private static string StateWord(ClaudeSession session) => session.Status switch
    {
        ClaudeSessionStatus.Waiting => Loc.Get("control.claude.waiting"),
        ClaudeSessionStatus.Working => Loc.Get("control.claude.working"),
        ClaudeSessionStatus.Done => Loc.Get("control.claude.done"),
        _ => Loc.Get("control.claude.idle")
    };

    /// <summary>What it does now, or what it was asked, or what it answered.</summary>
    private static string? HeroLine(ClaudeSession session) => session.Status switch
    {
        ClaudeSessionStatus.Working => session.Activity is { Length: > 0 } activity ? activity : Loc.Get("control.claude.thinking"),
        ClaudeSessionStatus.Done => session.Summary is { Length: > 0 } summary ? summary : session.Prompt,
        _ => session.Prompt is { Length: > 0 } prompt ? prompt : session.Summary
    };

    private static string ChipLine(ClaudeSession session) => session.Status switch
    {
        ClaudeSessionStatus.Waiting => Loc.Get("control.claude.waiting"),
        ClaudeSessionStatus.Working => session.Activity is { Length: > 0 } activity ? activity : Loc.Get("control.claude.thinking"),
        ClaudeSessionStatus.Done => Loc.Get("control.claude.done"),
        _ => Loc.Get("control.claude.idle")
    };

    #endregion

    #region Hero

    private Path Star(double size) => new()
    {
        Data = ClaudeGlyph.Data,
        Fill = _mood,
        Width = size,
        Height = size,
        Stretch = Stretch.Uniform,
        VerticalAlignment = VerticalAlignment.Center
    };

    /// <summary>
    /// The session that matters most, big: its name, its state in large type, what it does, how long the
    /// turn runs, and its requests with big buttons. A click brings its terminal tab or app to the front.
    /// </summary>
    private Border Hero(ClaudeSession lead, List<ClaudePermissionRequest> pending)
    {
        bool sameLead = lead.Id == _heroSessionId;
        var previous = sameLead ? null : _claudeShownSessions.GetValueOrDefault(lead.Id);
        _heroSessionId = lead.Id;
        if (previous != null) _heroTint.Set(StatusColor(previous.Status), animate: false);
        _heroTint.Set(StatusColor(lead.Status), (sameLead || previous != null) && IsVisible);
        bool waiting = lead.Status == ClaudeSessionStatus.Waiting;

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int i = 0; i < 3; i++) top.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var who = WhoRow(lead.Name, lead.Title is { Length: > 0 } && lead.Project != lead.Title ? lead.Project : null, lead.Cwd);
        top.Children.Add(who);

        var state = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 7, 0, 4) };
        state.Children.Add(ClaudeText(lead.Id, "state", new TextBlock
        {
            Text = StateWord(lead),
            FontFamily = ControlFont,
            FontSize = 19,
            FontWeight = FontWeights.Bold,
            Foreground = _heroInk,
            VerticalAlignment = VerticalAlignment.Center
        }, before: previous == null ? null : StateWord(previous)));
        if (lead.Status == ClaudeSessionStatus.Done)
        {
            // A small tick springs up next to "Done" the first time it shows.
            var tick = new TextBlock
            {
                Text = "",
                FontFamily = SegoeSymbolFont,
                FontSize = 13,
                Foreground = _heroInk,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(7, 2, 0, 0)
            };
            _heroTick = tick;
            state.Children.Add(tick);
        }
        Grid.SetRow(state, 1);
        top.Children.Add(state);

        string line = HeroLine(lead) ?? "";
        string? previousLine = previous == null ? null : HeroLine(previous) ?? "";
        if (line.Length > 0 || previousLine is { Length: > 0 } || _claudeTexts.ContainsKey((lead.Id, "activity")))
        {
            var doing = ClaudeText(lead.Id, "activity", new TextBlock
            {
                Text = line,
                FontFamily = ControlFont,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = HeroSoft,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            }, optional: true, before: previousLine);
            Grid.SetRow(doing, 2);
            top.Children.Add(doing);
        }

        if (HeroTimeLabel(lead, previous) is { } time)
        {
            Grid.SetColumn(time, 1);
            Grid.SetRowSpan(time, 2);
            top.Children.Add(time);
        }

        // Shown on hover with the open arrow; a waiting session wants its request answered first.
        var hoverParts = new List<FrameworkElement>(4);
        var side = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        if (!waiting && _composingSessionId != lead.Id)
        {
            string replyTo = lead.Id;
            hoverParts.Add(IconButton("", Loc.Get("control.claude.reply"), () => StartComposing(replyTo)));
            hoverParts.Add(IconButton("", Loc.Get("control.claude.pasteClipboard"), () => AttachClipboard(replyTo)));
            hoverParts.Add(IconButton("", Loc.Get("control.claude.screenshot"), () => ScreenshotRequested?.Invoke(replyTo)));
            for (int i = 0; i < hoverParts.Count; i++)
            {
                hoverParts[i].Width = hoverParts[i].Height = 24;
                hoverParts[i].Margin = new Thickness(i == 0 ? 0 : 5, -4, 0, -4);
            }
        }
        hoverParts.Add(new TextBlock
        {
            Text = "",
            FontFamily = SegoeSymbolFont,
            FontSize = 12,
            Foreground = VNotch.Services.UiPalette.PrimaryBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        });
        foreach (var part in hoverParts)
        {
            part.Opacity = 0;
            part.IsVisibleChanged += (_, _) =>
            {
                if (part.IsVisible) return;
                Motion.Stop(part, OpacityProperty, 0);
                Motion.Stop(WeatherMotion.Transform<TranslateTransform>(part), TranslateTransform.XProperty, 0);
            };
            side.Children.Add(part);
        }
        Grid.SetColumn(side, 1);
        Grid.SetRow(side, 2);
        top.Children.Add(side);

        var content = new StackPanel { Margin = new Thickness(18, 16, 18, 16) };
        content.Children.Add(top);
        content.Children.Add(TokenStats(lead));

        _accent = _heroInk;
        StackPanel? requests = null;
        foreach (var request in pending)
        {
            if (request.SessionId != lead.Id) continue;
            var requestCard = request.Questions != null ? QuestionCard(request, request.Questions)
                : request.Plan != null ? PlanCard(request, request.Plan)
                : PermissionCard(request);
            _firstRequestCard ??= requestCard;
            _requestCards[request.Id] = requestCard;
            requests ??= new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
            requests.Children.Add(requestCard);
        }
        _accent = _mood;
        if (_composingSessionId == lead.Id)
        {
            requests ??= new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
            requests.Children.Add(Composer(lead.Id));
        }
        if (requests != null) content.Children.Add(requests);

        var fill = new SolidColorBrush(CardRest);
        var hero = new Border
        {
            CornerRadius = new CornerRadius(18),
            Background = fill,
            Margin = new Thickness(0, 0, 0, 14),
            Cursor = Cursors.Hand,
            Child = content
        };
        HoverLift(hero, fill, show =>
        {
            foreach (var part in hoverParts)
            {
                WeatherMotion.Glide(part, OpacityProperty, show ? 1 : 0, show ? 140 : 200);
                Motion.To(WeatherMotion.Transform<TranslateTransform>(part), TranslateTransform.XProperty,
                    show ? 2 : 0, Motion.Press);
            }
        });
        System.Windows.Automation.AutomationProperties.SetName(hero, lead.Name);
        string sessionId = lead.Id;
        string? title = lead.Title;
        hero.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            OpenSession(hero, sessionId, title);
        };
        return hero;
    }

    private TextBlock ClaudeText(string session, string part, TextBlock next, bool animate = true, bool optional = false,
        string? before = null)
    {
        string value = next.Text;
        if (!_claudeTexts.TryGetValue((session, part), out var text))
        {
            _claudeTexts[(session, part)] = text = next;
            SetDetailText(text, before ?? value, rollUp: true, animate: false, collapseEmpty: optional);
            if (DetailTexts.TryGetValue(text, out var state)) state.Reparenting = () => _rebuildingResetRows;
        }
        else
        {
            if (text.Parent is Panel previous) previous.Children.Remove(text);
            if (before != null) SetDetailText(text, before, rollUp: true, animate: false, collapseEmpty: optional);
            text.Foreground = next.Foreground;
        }
        _claudeTextUpdates.Add((text, value, animate, optional));
        return text;
    }

    /// <summary>The star and the session's name; the folder follows a name Claude Code gave it.</summary>
    private FrameworkElement WhoRow(string name, string? project, string? tooltip)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(Star(12));
        var text = new TextBlock
        {
            FontFamily = ControlFont,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = HeroSoft,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(7, 0, 12, 0)
        };
        text.Inlines.Add(new Run(name) { Foreground = HeroName });
        if (project is { Length: > 0 }) text.Inlines.Add(new Run("  ·  " + project));
        if (tooltip is { Length: > 0 }) text.ToolTip = tooltip;
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        return row;
    }

    /// <summary>The turn's time in large light digits: ticking while it runs, how long it took once done.</summary>
    private static string? HeroTimeText(ClaudeSession session, DateTime now)
    {
        if (session.TurnStartedUtc is { } started && session.Status is ClaudeSessionStatus.Working or ClaudeSessionStatus.Waiting)
            return FormatElapsed(now - started);
        if (session.Status == ClaudeSessionStatus.Done && session.FinishedUtc is { } finished && session.TurnStartedUtc is { } turnStart)
            return FormatElapsed(finished - turnStart);
        return null;
    }

    private TextBlock? HeroTimeLabel(ClaudeSession session, ClaudeSession? previousSession)
    {
        DateTime now = DateTime.UtcNow;
        string? text = HeroTimeText(session, now);
        string? before = previousSession == null ? null : HeroTimeText(previousSession, now) ?? "";
        _claudeTexts.TryGetValue((session.Id, "time"), out var previous);
        if (text == null && previous == null && before is not { Length: > 0 }) return null;
        var label = new TextBlock
        {
            Text = text ?? "",
            FontFamily = ControlFont,
            FontSize = 20,
            FontWeight = FontWeights.Light,
            Foreground = HeroTime,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(12, -2, 0, 0)
        };
        Typography.SetNumeralAlignment(label, FontNumeralAlignment.Tabular);
        bool running = session.Status is ClaudeSessionStatus.Working or ClaudeSessionStatus.Waiting;
        var timeKey = (session.TurnStartedUtc, running ? null : session.FinishedUtc);
        label = ClaudeText(session.Id, "time", label, animate: !running || !Equals(previous?.Tag, timeKey),
            optional: true, before: before);
        label.Tag = timeKey;
        if (session.Status is ClaudeSessionStatus.Working or ClaudeSessionStatus.Waiting && session.TurnStartedUtc is { } from)
            _elapsedLabels.Add((label, from));
        return label;
    }

    /// <summary>The tick next to "Done" springs up with a little twist, once per finished turn.</summary>
    private void PopTick()
    {
        if (_heroTick is not { } tick || AnimationConfig.ReduceMotion) return;
        var scale = new ScaleTransform(0, 0);
        var turn = new RotateTransform(-30);
        tick.RenderTransformOrigin = new Point(0.5, 0.5);
        tick.RenderTransform = new TransformGroup { Children = { scale, turn } };
        tick.Opacity = 0;
        Motion.To(tick, OpacityProperty, 1, Motion.Exit, from: 0);
        Motion.To(scale, ScaleTransform.ScaleXProperty, 1, Motion.TileScale, from: Motion.TileFromScale);
        Motion.To(scale, ScaleTransform.ScaleYProperty, 1, Motion.TileScale, from: Motion.TileFromScale);
        Motion.To(turn, RotateTransform.AngleProperty, 0, Motion.Press, from: -30);
        tick.IsVisibleChanged += (_, _) => { if (!tick.IsVisible) StopClaudeTick(tick); };
    }

    private static void StopClaudeTick(TextBlock? tick)
    {
        if (tick?.RenderTransform is not TransformGroup { Children: [ScaleTransform scale, RotateTransform turn] }) return;
        Motion.Stop(tick, OpacityProperty, 1);
        Motion.Stop(scale, ScaleTransform.ScaleXProperty, 1);
        Motion.Stop(scale, ScaleTransform.ScaleYProperty, 1);
        Motion.Stop(turn, RotateTransform.AngleProperty, 0);
    }

    #endregion

    #region Chips

    private WrapPanel Chips(List<ClaudeSession> order)
    {
        var wrap = new WrapPanel { Margin = new Thickness(0, 0, 0, 10), Tag = ChipsTag };
        foreach (var session in order.Skip(1))
        {
            var previous = session.Id == _claudeShownHeroId ? _claudeShownSessions.GetValueOrDefault(session.Id) : null;
            wrap.Children.Add(_claudeSessionRows[session.Id] = Chip(session, previous));
        }
        foreach (var session in _codexSessions) wrap.Children.Add(CodexChip(session));
        return wrap;
    }

    /// <summary>Another session as a small pill: its dot, name and state. A tap brings it up into the hero.</summary>
    private Border Chip(ClaudeSession session, ClaudeSession? previous)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(ChipDot(SessionDot(session, previous)));
        row.Children.Add(ClaudeText(session.Id, "chip-name", new TextBlock
        {
            Text = session.Name,
            FontFamily = ControlFont,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = ChipText,
            MaxWidth = 220,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(7, 0, 0, 0)
        }, animate: false));
        row.Children.Add(ClaudeText(session.Id, "chip-state", new TextBlock
        {
            Text = ChipLine(session),
            FontFamily = ControlFont,
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = session.Status == ClaudeSessionStatus.Waiting ? WaitingDot : ChipSoft,
            MaxWidth = 200,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(7, 0, 0, 0)
        }, before: previous == null ? null : ChipLine(previous)));
        var fill = new SolidColorBrush(CardRest);
        var chip = new Border
        {
            CornerRadius = new CornerRadius(14),
            Background = fill,
            Padding = new Thickness(10, 7, 13, 7),
            Margin = new Thickness(0, 0, 8, 8),
            Cursor = Cursors.Hand,
            Child = row
        };
        if (session.Cwd.Length > 0) chip.ToolTip = session.Cwd;
        HoverLift(chip, fill, null);
        System.Windows.Automation.AutomationProperties.SetName(chip, session.Name);
        string id = session.Id;
        chip.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            FocusSession(id);
        };
        return chip;
    }

    private static FrameworkElement ChipDot(Brush brush) => new Grid
    {
        Width = 12, Height = 12, VerticalAlignment = VerticalAlignment.Center,
        Children =
        {
            new Ellipse
            {
                Width = 6, Height = 6, Fill = brush,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            }
        }
    };

    #endregion

    #region Motion

    /// <summary>The scale and shift every card of this tab moves with: for gliding in, lifting and the shake.</summary>
    private static (ScaleTransform Scale, TranslateTransform Shift) LiftParts(FrameworkElement element)
    {
        if (element.RenderTransform is TransformGroup group && group.Children.Count == 2 &&
            group.Children[0] is ScaleTransform s && group.Children[1] is TranslateTransform t)
            return (s, t);
        var scale = new ScaleTransform(1, 1);
        var shift = new TranslateTransform();
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.RenderTransform = new TransformGroup { Children = { scale, shift } };
        return (scale, shift);
    }

    /// <summary>A card rises a little under the pointer and brightens, and settles back with a spring.</summary>
    private static void HoverLift(Border card, SolidColorBrush fill, Action<bool>? onHover)
    {
        var (scale, shift) = LiftParts(card);
        Squish(card);
        card.MouseEnter += (_, _) =>
        {
            if (!card.IsVisible || !card.IsHitTestVisible) return;
            WeatherMotion.Tint(fill, CardLifted, 140);
            Motion.To(shift, TranslateTransform.YProperty, -1.5, Motion.Press);
            onHover?.Invoke(true);
        };
        card.MouseLeave += (_, _) =>
        {
            if (!card.IsVisible || !card.IsHitTestVisible) return;
            WeatherMotion.Tint(fill, CardRest, 160);
            Motion.To(shift, TranslateTransform.YProperty, 0, Motion.Press);
            onHover?.Invoke(false);
        };
        card.IsVisibleChanged += (_, _) =>
        {
            if (card.IsVisible || !card.IsHitTestVisible) return;
            SetOrTint(fill, CardRest, 0);
            Motion.Stop(card, OpacityProperty, 1);
            Motion.Stop(scale, ScaleTransform.ScaleXProperty, 1);
            Motion.Stop(scale, ScaleTransform.ScaleYProperty, 1);
            Motion.Stop(shift, TranslateTransform.XProperty, 0);
            Motion.Stop(shift, TranslateTransform.YProperty, 0);
        };
    }

    private static void GlideIn(FrameworkElement element)
    {
        Motion.To(element, OpacityProperty, 1, Motion.Exit, from: 0);
        Motion.To(LiftParts(element).Shift, TranslateTransform.YProperty, 0, Motion.Tile, from: -8);
    }

    private void RemoveClaudeSessionExit((Border Row, bool Chip, int Index) exit)
    {
        _claudeSessionExits.Remove(exit);
        if (exit.Row.Parent is Panel parent)
        {
            parent.Children.Remove(exit.Row);
            if (exit.Chip && parent.Children.Count == 0) _claudeSection.Children.Remove(parent);
        }
        if (IsVisible && !_disposed) ContentHeightChanged?.Invoke();
    }

    private void StopClaudeDetailsMotion()
    {
        if (_claudeFace == null) return;
        if (_deferredClaudeText.Count != 0) _claudeKey = "";
        _deferredClaudeText.Clear();
        _swipingClaudePermission?.Stop();
        foreach (var exit in _claudeSessionExits.ToArray())
        {
            StopClaudeFold(exit.Row);
            RemoveClaudeSessionExit(exit);
        }
        foreach (var card in _settlingClaudeRequests)
        {
            StopClaudeFold(card);
            if (card.Parent is Panel parent) parent.Children.Remove(card);
        }
        _settlingClaudeRequests.Clear();
        foreach (var dot in _claudeDots.Values) dot.Stop();
        _heroTint.Stop();
        _faceStatusTint.Stop();
        foreach (var text in _claudeTexts.Values.Append(_accountStatus))
            if (DetailTexts.TryGetValue(text, out var state)) SettleDetailText(text, state);
        StopClaudeTick(_heroTick);
    }

    private static void FoldClaudeCard(FrameworkElement card, Action done)
    {
        card.IsHitTestVisible = false;
        var squash = new ScaleTransform(1, 1);
        card.LayoutTransform = squash;
        Motion.To(card, OpacityProperty, 0, Motion.Exit);
        Motion.To(squash, ScaleTransform.ScaleYProperty, 0, Motion.Exit, done: done);
    }

    private static void StopClaudeFold(FrameworkElement card)
    {
        Motion.Stop(card, OpacityProperty, 0);
        if (card.RenderTransform is TranslateTransform shift)
            Motion.Stop(shift, TranslateTransform.XProperty);
        if (card.LayoutTransform is ScaleTransform squash)
            Motion.Stop(squash, ScaleTransform.ScaleYProperty, 0);
    }

    private readonly HashSet<FrameworkElement> _settlingClaudeRequests = new();

    private bool Settle(string requestId, Func<bool> answer, int swipeDirection = 0)
    {
        if (!answer()) return false;
        if (!IsVisible || AnimationConfig.ReduceMotion || !_requestCards.Remove(requestId, out var card)) return true;
        _settlingClaudeRequests.Add(card);
        card.IsHitTestVisible = false;
        void Finish()
        {
            _settlingClaudeRequests.Remove(card);
            if (card.Parent is Panel parent) parent.Children.Remove(card);
            if (IsVisible) BuildClaudeSection();
        }
        if (swipeDirection != 0 && card.RenderTransform is TranslateTransform shift)
        {
            Motion.To(shift, TranslateTransform.XProperty, shift.X + swipeDirection * 12, Motion.StatusRollOut);
            Motion.To(card, OpacityProperty, 0, Motion.StatusRollOut, done: () => FoldClaudeCard(card, Finish));
        }
        else FoldClaudeCard(card, Finish);
        return true;
    }

    #endregion
}
