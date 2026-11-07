using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using VNotch.Services;
using VNotch.Services.ClaudeCode;

namespace VNotch.Controls;

/// <summary>
/// The Claude tab in its "focus" layout. The session that matters most right now sits big at the top: one
/// that waits for you, else the one at work. It shows its state, what it does and a large running time, and
/// a request with big buttons. The other sessions are small chips under it (a tap brings one up), and the plan
/// limits are a slim strip at the bottom. One muted mood colour says what Claude is at: warm grey when quiet,
/// a soft Claude terracotta at work, amber when it waits, rosé when a limit is nearly used up. The backdrop
/// drifts in it like the system tab's. Everything that moves runs only while the tab is on screen, and the
/// moving parts outlive a rebuild, so a redraw does not restart them.
/// </summary>
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
    private static readonly Brush DoneDot = FrozenOf(DoneSage);
    private static readonly Brush IdleDot = FrozenOf(IdleGrey);

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

    private const int ClaudeLoopFps = 30;
    private const string ChipsTag = "chips";

    private static readonly IEasingFunction BarSpring = FreezeEase(new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut });
    private static readonly IEasingFunction TickSpring = FreezeEase(new BackEase { Amplitude = 0.9, EasingMode = EasingMode.EaseOut });

    private static Brush FrozenOf(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    #endregion

    #region State

    // The hero's state colour (its word, tick and accent button), kept across rebuilds so a change fades.
    private readonly SolidColorBrush _heroInk = new(IdleGrey);
    private string? _heroSessionId;
    // The colour of work (terracotta, rosé near a limit): a working session keeps it even while the tab is amber.
    private Color _workColor = ClaudeAtWork.Main;
    private readonly SolidColorBrush _workDot = new(ClaudeAtWork.Main);
    // A chip tapped to bring its session up; it holds until that session goes or another starts to wait.
    private string? _focusedSessionId;
    private readonly HashSet<string> _waitingSeen = new(StringComparer.Ordinal);
    private TextBlock? _heroTick;
    private string? _heroTickKey;
    private string? _tickShownFor;

    // Request cards on the page by id, so an answer can fold its card away before it goes.
    private readonly Dictionary<string, FrameworkElement> _requestCards = new(StringComparer.Ordinal);

    // The moving parts, shared by every rebuild of the hero.
    private readonly RotateTransform _starSpin = new();
    private readonly ScaleTransform _starPulse = new(1, 1);
    private TransformGroup? _starMotion;
    private readonly TranslateTransform _shimmerShift = new(-1, 0);
    private LinearGradientBrush? _shimmer;
    private readonly TranslateTransform _sweepShift = new(-1, 0);
    private readonly GradientStop _sweepCore = new(ClaudeAtWork.Main, 0.5);
    private LinearGradientBrush? _sweep;
    private readonly SolidColorBrush _breathEdge = new(Color.FromArgb(0x8C, 0xD6, 0xB0, 0x7E));
    private RadialGradientBrush? _breathGlow;
    private readonly LoopSet _spinLoop = new();
    private readonly LoopSet _pulseLoop = new();
    private readonly LoopSet _shimmerLoop = new();
    private readonly LoopSet _sweepLoop = new();
    private readonly LoopSet _breathLoop = new();
    private bool _wantSpin, _wantPulse, _wantShimmer, _wantSweep, _wantBreath;

    // The limit bars: a darker shade at the start, the mood at the tip.
    private readonly GradientStop _claudeBarBase = new(Dim(ClaudeQuiet.Main, 0.62), 0);
    private readonly GradientStop _claudeBarTip = new(ClaudeQuiet.Main, 1);
    private LinearGradientBrush? _claudeBar;
    private Color? _claudeMoodMain;
    private sealed record LimitBar(string Key, double Used, ScaleTransform Grow, WeatherTween Number);
    private readonly List<LimitBar> _limitBars = new();
    private readonly Dictionary<string, double> _limitShown = new(StringComparer.Ordinal);

    private TransformGroup StarMotion => _starMotion ??= new TransformGroup { Children = { _starPulse, _starSpin } };

    // "Liest MainWindow.xaml…" glints like Claude Code's own spinner text.
    private LinearGradientBrush Shimmer => _shimmer ??= new LinearGradientBrush
    {
        StartPoint = new Point(0, 0.5),
        EndPoint = new Point(1, 0.5),
        RelativeTransform = _shimmerShift,
        GradientStops =
        {
            new GradientStop(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF), 0),
            new GradientStop(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF), 0.38),
            new GradientStop(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF), 0.5),
            new GradientStop(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF), 0.62),
            new GradientStop(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF), 1)
        }
    };

    // A streak of light that runs along the bottom of the hero while it works.
    private LinearGradientBrush Sweep => _sweep ??= new LinearGradientBrush
    {
        StartPoint = new Point(0, 0.5),
        EndPoint = new Point(1, 0.5),
        RelativeTransform = _sweepShift,
        GradientStops =
        {
            new GradientStop(Colors.Transparent, 0),
            new GradientStop(Colors.Transparent, 0.325),
            _sweepCore,
            new GradientStop(Colors.Transparent, 0.675),
            new GradientStop(Colors.Transparent, 1)
        }
    };

    // A waiting hero glows softly from its top edge.
    private RadialGradientBrush BreathGlow => _breathGlow ??= new RadialGradientBrush
    {
        Center = new Point(0.5, 0),
        GradientOrigin = new Point(0.5, 0),
        RadiusX = 0.85,
        RadiusY = 1.3,
        GradientStops =
        {
            new GradientStop(Color.FromArgb(0x2E, 0xD6, 0xB0, 0x7E), 0),
            new GradientStop(Color.FromArgb(0x00, 0xD6, 0xB0, 0x7E), 1)
        }
    };

    private LinearGradientBrush ClaudeBar => _claudeBar ??= new LinearGradientBrush
    {
        StartPoint = new Point(0, 0),
        EndPoint = new Point(1, 0),
        GradientStops = { _claudeBarBase, _claudeBarTip }
    };

    #endregion

    #region Building

    private void BuildClaudeSection()
    {
        _accent = _mood;
        if (_disposed || _claude == null) return;
        // A rebuild would take the text box from under the typing; catch up once the message is sent.
        if (_composerBox?.IsKeyboardFocusWithin == true)
        {
            _rebuildAfterComposing = true;
            return;
        }
        var sessions = _claude.GetSessions();
        var pending = _claude.GetPending();
        var limits = _usage?.Latest;
        bool listening = _hookServer?.IsListening == true;

        TrackFocus(sessions);
        var order = FocusOrder(sessions, _composingSessionId ?? _focusedSessionId);
        string key = ClaudeKey(order, pending, limits, listening);
        if (key == _claudeKey) return;
        _claudeKey = key;

        ApplyClaudeMood(ClaudeMoodFor(sessions, limits));
        var work = NearLimit(limits) ? ClaudeNearLimit.Main : ClaudeAtWork.Main;
        if (work != _workColor)
        {
            _workColor = work;
            SetOrTint(_workDot, work, IsVisible ? MoodFadeMs : 0);
            Fade(_sweepCore, work, IsVisible ? MoodFadeMs : 0);
        }
        var lead = order.Count > 0 ? order[0] : null;
        bool anyWaiting = sessions.Any(s => s.Status == ClaudeSessionStatus.Waiting);
        _wantPulse = anyWaiting;
        _wantSpin = !anyWaiting && sessions.Any(s => s.Status == ClaudeSessionStatus.Working);
        _wantShimmer = _wantSweep = lead?.Status == ClaudeSessionStatus.Working;
        _wantBreath = lead?.Status == ClaudeSessionStatus.Waiting;

        _elapsedLabels.Clear();
        _firstRequestCard = null;
        _requestCards.Clear();
        _limitBars.Clear();
        _heroTick = null;
        _tokenRow = null;
        foreach (var seen in new List<string>(_tokensSeen.Keys))
            if (!sessions.Exists(s => s.Id == seen)) _tokensSeen.Remove(seen);
        ForgetAnsweredQuestions(pending);
        StopRipples();
        _ripples.Clear();
        _claudeSection.Children.Clear();

        if (!_claudeInstalled) _claudeSection.Children.Add(SetupCard(listening));
        // Hooks in a project's own settings report here too, without our entry in the user settings.
        if ((_claudeInstalled || sessions.Count > 0) && !listening)
            _claudeSection.Children.Add(HintCard("", Loc.Get("control.claude.portBusy", ClaudeHookServer.DefaultPort)));
        if (lead != null) _claudeSection.Children.Add(Hero(lead, pending));
        else if (_claudeInstalled) _claudeSection.Children.Add(EmptyHero());
        if (order.Count > 1) _claudeSection.Children.Add(Chips(order));
        _claudeSection.Children.Add(LimitStrip(limits));

        RevealNewCards(_cardScratch);
        _cardScratch.Clear();
        if (IsVisible)
        {
            StartRipples();
            PopTick(250);
            RefreshTokens();
        }
        ApplyClaudeLoops();
        ContentHeightChanged?.Invoke();
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
        double ms = _claudeMoodMain == null || !IsVisible ? 0 : MoodFadeMs;
        _claudeMoodMain = mood.Main;
        SetMoodColors(mood.Main, mood.Warmer, mood.Cooler);
        Fade(_claudeBarTip, mood.Main, ms);
        Fade(_claudeBarBase, Dim(mood.Main, 0.62), ms);
    }

    private Color StatusColor(ClaudeSessionStatus status) => status switch
    {
        ClaudeSessionStatus.Waiting => WaitingAmber,
        ClaudeSessionStatus.Working => _workColor,
        ClaudeSessionStatus.Done => DoneSage,
        _ => IdleGrey
    };

    private Brush DotBrush(ClaudeSessionStatus status) => status switch
    {
        ClaudeSessionStatus.Waiting => WaitingDot,
        ClaudeSessionStatus.Working => _workDot,
        ClaudeSessionStatus.Done => DoneDot,
        _ => IdleDot
    };

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
        VerticalAlignment = VerticalAlignment.Center,
        RenderTransformOrigin = new Point(0.5, 0.5),
        RenderTransform = StarMotion
    };

    /// <summary>
    /// The session that matters most, big: its name, its state in large type, what it does, how long the
    /// turn runs, and its requests with big buttons. A click brings its terminal tab or app to the front.
    /// </summary>
    private FrameworkElement Hero(ClaudeSession lead, List<ClaudePermissionRequest> pending)
    {
        bool sameLead = lead.Id == _heroSessionId;
        _heroSessionId = lead.Id;
        SetOrTint(_heroInk, StatusColor(lead.Status), sameLead && IsVisible ? 900 : 0);
        bool working = lead.Status == ClaudeSessionStatus.Working;
        bool waiting = lead.Status == ClaudeSessionStatus.Waiting;

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int i = 0; i < 3; i++) top.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        top.Children.Add(WhoRow(lead.Name, lead.Title is { Length: > 0 } && lead.Project != lead.Title ? lead.Project : null, lead.Cwd));

        var state = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 7, 0, 4) };
        state.Children.Add(new TextBlock
        {
            Text = StateWord(lead),
            FontFamily = ControlFont,
            FontSize = 19,
            FontWeight = FontWeights.Bold,
            Foreground = _heroInk,
            VerticalAlignment = VerticalAlignment.Center
        });
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
            _heroTickKey = lead.Id + (lead.FinishedUtc?.Ticks ?? 0);
            state.Children.Add(tick);
        }
        Grid.SetRow(state, 1);
        top.Children.Add(state);

        if (HeroLine(lead) is { Length: > 0 } line)
        {
            var doing = new TextBlock
            {
                Text = line,
                FontFamily = ControlFont,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = working && !AnimationConfig.ReduceMotion ? Shimmer : HeroSoft,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };
            Grid.SetRow(doing, 2);
            top.Children.Add(doing);
        }

        if (HeroTimeLabel(lead) is { } time)
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
            _cardScratch.Add(("request:" + request.Id, requestCard));
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

        var layers = new Grid();
        if (waiting)
        {
            layers.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(18),
                BorderThickness = new Thickness(1.2),
                BorderBrush = _breathEdge,
                Background = BreathGlow,
                IsHitTestVisible = false
            });
        }
        layers.Children.Add(content);
        if (working && !AnimationConfig.ReduceMotion)
        {
            layers.Children.Add(new Border
            {
                Height = 2,
                CornerRadius = new CornerRadius(1),
                Background = Sweep,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(18, 0, 18, 0),
                IsHitTestVisible = false
            });
        }

        var fill = new SolidColorBrush(CardRest);
        var hero = new Border
        {
            CornerRadius = new CornerRadius(18),
            Background = fill,
            Margin = new Thickness(0, 0, 0, 14),
            Cursor = Cursors.Hand,
            Child = layers
        };
        HoverLift(hero, fill, show =>
        {
            foreach (var part in hoverParts) WeatherMotion.Glide(part, OpacityProperty, show ? 1 : 0, show ? 140 : 200);
        });
        System.Windows.Automation.AutomationProperties.SetName(hero, lead.Name);
        _cardScratch.Add(("hero:" + lead.Id, hero));
        string sessionId = lead.Id;
        string? title = lead.Title;
        hero.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            OpenSession(hero, sessionId, title);
        };
        return hero;
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
    private TextBlock? HeroTimeLabel(ClaudeSession session)
    {
        string? text = null;
        if (session.TurnStartedUtc is { } started && session.Status is ClaudeSessionStatus.Working or ClaudeSessionStatus.Waiting)
            text = FormatElapsed(DateTime.UtcNow - started);
        else if (session.Status == ClaudeSessionStatus.Done && session.FinishedUtc is { } finished && session.TurnStartedUtc is { } turnStart)
            text = FormatElapsed(finished - turnStart);
        if (text == null) return null;
        var label = new TextBlock
        {
            Text = text,
            FontFamily = ControlFont,
            FontSize = 20,
            FontWeight = FontWeights.Light,
            Foreground = HeroTime,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(12, -2, 0, 0)
        };
        Typography.SetNumeralAlignment(label, FontNumeralAlignment.Tabular);
        if (session.Status is ClaudeSessionStatus.Working or ClaudeSessionStatus.Waiting && session.TurnStartedUtc is { } from)
            _elapsedLabels.Add((label, from));
        return label;
    }

    /// <summary>No session yet: the star and a calm line where the hero would be.</summary>
    private FrameworkElement EmptyHero()
    {
        _heroSessionId = null;
        SetOrTint(_heroInk, IdleGrey, 0);
        var stack = new StackPanel { Margin = new Thickness(18, 16, 18, 17) };
        stack.Children.Add(WhoRow("Claude Code", null, null));
        stack.Children.Add(new TextBlock
        {
            Text = Loc.Get("control.claude.noSessions"),
            FontFamily = ControlFont,
            FontSize = 17,
            FontWeight = FontWeights.Bold,
            Foreground = _moodInk,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 6, 0, 0)
        });
        var hero = new Border
        {
            CornerRadius = new CornerRadius(18),
            Background = new SolidColorBrush(CardRest),
            Margin = new Thickness(0, 0, 0, 14),
            Child = stack
        };
        _cardScratch.Add(("hero:none", hero));
        return hero;
    }

    /// <summary>The tick next to "Done" springs up with a little twist, once per finished turn.</summary>
    private void PopTick(double delayMs)
    {
        if (_heroTick is not { } tick || _tickShownFor == _heroTickKey) return;
        _tickShownFor = _heroTickKey;
        if (AnimationConfig.ReduceMotion) return;
        var scale = new ScaleTransform(0, 0);
        var turn = new RotateTransform(-30);
        tick.RenderTransformOrigin = new Point(0.5, 0.5);
        tick.RenderTransform = new TransformGroup { Children = { scale, turn } };
        tick.Opacity = 0;
        tick.BeginAnimation(OpacityProperty, WeatherMotion.To(0, 1, 220, WeatherMotion.Out, delayMs));
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, WeatherMotion.To(0, 1, 600, TickSpring, delayMs));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, WeatherMotion.To(0, 1, 600, TickSpring, delayMs));
        turn.BeginAnimation(RotateTransform.AngleProperty, WeatherMotion.To(-30, 0, 600, TickSpring, delayMs));
    }

    #endregion

    #region Chips and limits

    private FrameworkElement Chips(List<ClaudeSession> order)
    {
        var wrap = new WrapPanel { Margin = new Thickness(0, 0, 0, 10), Tag = ChipsTag };
        for (int i = 1; i < order.Count; i++) wrap.Children.Add(Chip(order[i]));
        return wrap;
    }

    /// <summary>Another session as a small pill: its dot, name and state. A tap brings it up into the hero.</summary>
    private Border Chip(ClaudeSession session)
    {
        bool live = session.Status is ClaudeSessionStatus.Working or ClaudeSessionStatus.Waiting;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(ChipDot(DotBrush(session.Status), live));
        row.Children.Add(new TextBlock
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
        });
        row.Children.Add(new TextBlock
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
        });
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
        _cardScratch.Add(("chip:" + session.Id, chip));
        string id = session.Id;
        chip.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            FocusSession(id);
        };
        return chip;
    }

    /// <summary>A dot in the session's colour; while it works or waits a ring ripples out of it.</summary>
    private FrameworkElement ChipDot(Brush brush, bool live)
    {
        var dot = new Grid { Width = 12, Height = 12, VerticalAlignment = VerticalAlignment.Center };
        if (live)
        {
            var ring = new Ellipse
            {
                Fill = brush,
                Opacity = 0.22,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(1, 1)
            };
            _ripples.Add(ring);
            dot.Children.Add(ring);
        }
        dot.Children.Add(new Ellipse
        {
            Width = 6,
            Height = 6,
            Fill = brush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        return dot;
    }

    /// <summary>The 5-hour and weekly limits as two slim bars, with the sound switch at the end.</summary>
    private FrameworkElement LimitStrip(ClaudeLimits? limits)
    {
        var grid = new Grid { Margin = new Thickness(4, 4, 4, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (limits?.FiveHour is { } fiveHour) grid.Children.Add(MiniLimit("5h", Loc.Get("control.claude.limit5h"), fiveHour));
        if (limits?.Week is { } week)
        {
            var mini = MiniLimit("week", Loc.Get("control.claude.limitWeek"), week);
            Grid.SetColumn(mini, 2);
            grid.Children.Add(mini);
        }

        var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
        // Whether sessions show in the closed notch at all (see ControlCenterView.ClaudeNotch.cs).
        if (ClaudeNotchButton() is { } eye) tools.Children.Add(eye);
        if (_soundsOn != null)
        {
            // Sounds when a turn ends or a session waits: the speaker shows and flips the setting.
            bool on = _soundsOn();
            var sound = IconButton(on ? "" : "", Loc.Get(on ? "control.claude.soundsOn" : "control.claude.soundsOff"), () =>
            {
                _setSounds?.Invoke(!_soundsOn());
                _claudeKey = "";
                BuildClaudeSection();
            });
            sound.Width = sound.Height = 24;
            sound.Margin = new Thickness(0);
            sound.Opacity = on ? 1 : 0.6;
            tools.Children.Add(sound);
        }
        if (_claudeInstalled)
        {
            var remove = ColumnLabel(Loc.Get("control.claude.remove"));
            remove.FontSize = 11.5;
            remove.Margin = new Thickness(12, 0, 0, 0);
            MakeLink(remove, RemoveClaudeHooks);
            tools.Children.Add(remove);
        }
        Grid.SetColumn(tools, 3);
        grid.Children.Add(tools);
        _cardScratch.Add(("strip", grid));
        return grid;
    }

    private FrameworkElement MiniLimit(string key, string label, ClaudeLimitWindow window)
    {
        double used = Math.Clamp(window.UsedPercent, 0, 100);
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(new TextBlock
        {
            Text = label,
            FontFamily = ControlFont,
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = LimitLabel,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        var percent = new TextBlock
        {
            FontFamily = ControlFont,
            FontSize = 11.5,
            FontWeight = FontWeights.Bold,
            Foreground = used >= NearLimitPercent ? _moodInk : LimitValue,
            Margin = new Thickness(8, 0, 0, 0)
        };
        Typography.SetNumeralAlignment(percent, FontNumeralAlignment.Tabular);
        Grid.SetColumn(percent, 1);
        top.Children.Add(percent);

        var grow = new ScaleTransform(used / 100, 1);
        var fill = new Border { Background = ClaudeBar, CornerRadius = new CornerRadius(1.5), RenderTransformOrigin = new Point(0, 0.5), RenderTransform = grow };
        var bar = new Grid
        {
            Height = 3,
            Margin = new Thickness(0, 7, 0, 6),
            Children = { new Border { Background = LimitTrack, CornerRadius = new CornerRadius(1.5) }, fill }
        };

        var stack = new StackPanel();
        stack.Children.Add(top);
        stack.Children.Add(bar);
        if (window.ResetsAt is { } resets)
        {
            stack.Children.Add(new TextBlock
            {
                Text = Loc.Get("control.claude.resetsIn", FormatDuration(resets - DateTimeOffset.UtcNow)),
                FontFamily = ControlFont,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = ChipSoft,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }

        var limitBar = new LimitBar(key, used, grow, new WeatherTween { Step = v => percent.Text = $"{Math.Round(v)} %" });
        _limitBars.Add(limitBar);
        // A changed value moves from where the bar was; the first one seen with the tab open fills from empty.
        PlayLimit(limitBar, _limitShown.TryGetValue(key, out double was) ? was : 0, 0);
        return stack;
    }

    /// <summary>Fills a bar with a spring and rolls its number along; without the tab on screen it just sets them.</summary>
    private void PlayLimit(LimitBar bar, double from, double delayMs)
    {
        _limitShown[bar.Key] = bar.Used;
        bool animate = IsVisible && !AnimationConfig.ReduceMotion && Math.Abs(from - bar.Used) > 0.05;
        if (!animate)
        {
            bar.Grow.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            bar.Grow.ScaleX = bar.Used / 100;
            bar.Number.Play(bar.Used, bar.Used, 0, WeatherMotion.OutQuint);
            return;
        }
        bar.Grow.BeginAnimation(ScaleTransform.ScaleXProperty, WeatherMotion.To(from / 100, bar.Used / 100, 950, BarSpring, delayMs));
        bar.Number.Play(from, bar.Used, 900, WeatherMotion.Out, delayMs);
    }

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
        var (_, shift) = LiftParts(card);
        card.MouseEnter += (_, _) =>
        {
            WeatherMotion.Tint(fill, CardLifted, 140);
            if (!AnimationConfig.ReduceMotion) shift.BeginAnimation(TranslateTransform.YProperty, WeatherMotion.To(shift.Y, -1.5, 380, KnobSpring));
            onHover?.Invoke(true);
        };
        card.MouseLeave += (_, _) =>
        {
            WeatherMotion.Tint(fill, CardRest, 220);
            if (!AnimationConfig.ReduceMotion) shift.BeginAnimation(TranslateTransform.YProperty, WeatherMotion.To(shift.Y, 0, 420, KnobSpring));
            onHover?.Invoke(false);
        };
    }

    /// <summary>Glides in from a little above with a soft overshoot, the way new sessions and requests arrive.</summary>
    private static void GlideIn(FrameworkElement element, double delayMs)
    {
        if (AnimationConfig.ReduceMotion) return;
        var (scale, shift) = LiftParts(element);
        element.Opacity = 0;
        var fade = WeatherMotion.To(0, 1, 320, WeatherMotion.Out, delayMs);
        fade.Completed += (_, _) =>
        {
            element.BeginAnimation(OpacityProperty, null);
            element.Opacity = 1;
        };
        element.BeginAnimation(OpacityProperty, fade);
        shift.BeginAnimation(TranslateTransform.YProperty, WeatherMotion.To(-8, 0, 560, WeatherMotion.OutBack, delayMs));
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, WeatherMotion.To(0.98, 1, 560, WeatherMotion.OutBack, delayMs));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, WeatherMotion.To(0.98, 1, 560, WeatherMotion.OutBack, delayMs));
    }

    /// <summary>When the tab opens: the hero, then the chips, then the strip glide in; the bars fill from empty.</summary>
    private void RevealClaude(TimeSpan baseDelay)
    {
        double start = baseDelay.TotalMilliseconds;
        int index = 0;
        foreach (UIElement child in _claudeSection.Children)
        {
            if (child is not FrameworkElement element || element.Visibility != Visibility.Visible) continue;
            if (element is WrapPanel { Tag: ChipsTag } chips)
            {
                foreach (UIElement chip in chips.Children)
                    if (chip is FrameworkElement c) GlideIn(c, start + Math.Min(index++, 6) * 70);
                continue;
            }
            GlideIn(element, start + Math.Min(index++, 6) * 70);
        }
        foreach (var bar in _limitBars) PlayLimit(bar, 0, start + 160);
        RollTokensIn();
        PopTick(start + 280);
    }

    /// <summary>
    /// An answer folds its request card away before it goes to Claude Code, so the card does not just vanish.
    /// A timer makes sure the answer goes out even if the fold never finishes.
    /// </summary>
    private void Settle(string requestId, Action answer)
    {
        if (!_requestCards.Remove(requestId, out var card) || !IsVisible || AnimationConfig.ReduceMotion)
        {
            answer();
            return;
        }
        bool sent = false;
        void Send()
        {
            if (sent) return;
            sent = true;
            answer();
        }
        card.IsHitTestVisible = false;
        var squash = new ScaleTransform(1, 1);
        card.LayoutTransform = squash;
        card.BeginAnimation(OpacityProperty, WeatherMotion.To(1, 0, 170, WeatherMotion.Out));
        var fold = WeatherMotion.To(1, 0, 240, WeatherMotion.Sine);
        fold.Completed += (_, _) => Send();
        squash.BeginAnimation(ScaleTransform.ScaleYProperty, fold);
        var guard = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        guard.Tick += (_, _) =>
        {
            guard.Stop();
            Send();
        };
        guard.Start();
    }

    /// <summary>Starts what the current state needs and stops the rest; only while the tab is on screen.</summary>
    private void ApplyClaudeLoops()
    {
        bool on = IsVisible && !AnimationConfig.ReduceMotion && Page == ControlCenterPage.Claude;
        Loop(_spinLoop, on && _wantSpin, () =>
            _spinLoop.Run(_starSpin, RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromSeconds(7)) { RepeatBehavior = RepeatBehavior.Forever }, ClaudeLoopFps));
        Loop(_pulseLoop, on && _wantPulse, () =>
        {
            var pulse = new DoubleAnimation(0.9, 1.12, TimeSpan.FromMilliseconds(900))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = WeatherMotion.Sine
            };
            _pulseLoop.Run(_starPulse, ScaleTransform.ScaleXProperty, pulse, ClaudeLoopFps);
            _pulseLoop.Run(_starPulse, ScaleTransform.ScaleYProperty, pulse, ClaudeLoopFps);
        });
        Loop(_shimmerLoop, on && _wantShimmer, () =>
            _shimmerLoop.Run(_shimmerShift, TranslateTransform.XProperty,
                new DoubleAnimation(-0.7, 0.7, TimeSpan.FromMilliseconds(2400)) { RepeatBehavior = RepeatBehavior.Forever }, ClaudeLoopFps));
        Loop(_sweepLoop, on && _wantSweep, () =>
            _sweepLoop.Run(_sweepShift, TranslateTransform.XProperty,
                new DoubleAnimation(-0.675, 0.675, TimeSpan.FromMilliseconds(1800))
                {
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = WeatherMotion.Sine
                }, ClaudeLoopFps));
        Loop(_breathLoop, on && _wantBreath, () =>
        {
            var breathe = new DoubleAnimation(0.25, 1, TimeSpan.FromMilliseconds(1300))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = WeatherMotion.Sine
            };
            _breathLoop.Run(_breathEdge, Brush.OpacityProperty, breathe, 24);
            _breathLoop.Run(BreathGlow, Brush.OpacityProperty, breathe, 24);
        });
    }

    private static void Loop(LoopSet loop, bool want, Action start)
    {
        if (want && loop.IsEmpty) start();
        else if (!want && !loop.IsEmpty) loop.StopAll();
    }

    private void StopClaudeLoops()
    {
        _spinLoop.StopAll();
        _pulseLoop.StopAll();
        _shimmerLoop.StopAll();
        _sweepLoop.StopAll();
        _breathLoop.StopAll();
    }

    #endregion
}
