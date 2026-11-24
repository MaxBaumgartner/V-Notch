using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using VNotch.Services;
using VNotch.Services.ClaudeCode;

namespace VNotch.Controls;

public partial class ControlCenterView
{
    internal const double ClaudeFaceWidth = 628;
    private const double ClaudeFrontHeight = 180;
    private const double AccountHeaderHeight = 43;
    private const double NextHeight = 18;
    private static readonly FontFamily AccountFont = new("Segoe UI Variable Text, Segoe UI");
    private static readonly Brush AccountWhite = Frozen("#F5F5F7");
    private static readonly Brush AccountLabel = Frozen("#94FFFFFF");
    private static readonly Brush AccountTile = Frozen("#0FFFFFFF");
    private static readonly Brush AccountLine = Frozen("#14FFFFFF");
    private static readonly Brush AccountTrack = Frozen("#21FFFFFF");
    private static readonly Brush AccountClay = Frozen("#D49A82");
    private static readonly Brush AccountMist = Frozen("#7FA6CC");
    private static readonly Brush AccountSand = Frozen("#D2AE7C");
    private static readonly Brush AccountRose = Frozen("#D2857E");
    private static readonly Brush AccountSage = Frozen("#9DBDB1");
    private static readonly Brush AccountNeutral = Frozen("#8C8C96");
    private static readonly CultureInfo AccountEnglish = CultureInfo.GetCultureInfo("en-US");
    private static readonly CultureInfo AccountGerman = CultureInfo.GetCultureInfo("de-DE");
    private static CultureInfo AccountCulture => Loc.CurrentLanguage == "de" ? AccountGerman : AccountEnglish;

    private Grid? _claudeFace;
    private Grid _accountFront = null!, _accountBack = null!, _accountRows = null!;
    private TextBlock _accountTitle = null!, _accountStatus = null!, _accountElapsed = null!, _accountCount = null!;
    private FrameworkElement _accountStar = null!, _accountBackStar = null!;
    private Border _accountStatusDot = null!, _nextAccount = null!, _accountNotice = null!, _accountNoticeMark = null!;
    private TextBlock _accountNoticeText = null!;
    private Button _accountExpand = null!, _accountClose = null!;
    private readonly TranslateTransform _nextAccountShift = new();
    private readonly Dictionary<CodexAccount, AccountRow> _accountRowBySlot = new();
    private AccountRow _claudeAccountRow = null!;
    private AccountMeter[] _claudeFrontMeters = [];
    private DispatcherTimer? _accountNoticeTimer;
    private bool _accountsOpen;
    private DateTime? _accountTurnStarted;
    private string? _inlineClaudeGrant;

    private bool AnimateAccounts => IsVisible && !AnimationConfig.ReduceMotion;

    private static TextBlock AccountText(string text, double size, Brush? brush = null, int weight = 400) => new()
    {
        Text = text, FontFamily = AccountFont, FontSize = size, Foreground = brush ?? AccountWhite,
        FontWeight = FontWeight.FromOpenTypeWeight(weight), VerticalAlignment = VerticalAlignment.Center
    };

    private static void PlaceAccount(Grid host, FrameworkElement child, double left, double top,
        double? width = null, double? height = null)
    {
        child.Margin = new Thickness(left, top, 0, 0);
        child.HorizontalAlignment = HorizontalAlignment.Left;
        child.VerticalAlignment = VerticalAlignment.Top;
        if (width is { } w) child.Width = w;
        if (height is { } h) child.Height = h;
        host.Children.Add(child);
    }

    private static FrameworkElement AccountStar(double size)
    {
        // Keep the path's original 20-unit viewport, including its transparent perimeter.
        var viewport = new Canvas { Width = 20, Height = 20 };
        viewport.Children.Add(new Path { Data = ClaudeGlyph.Data, Fill = AccountClay });
        return new Viewbox { Width = size, Height = size, Child = viewport };
    }

    private static FrameworkElement AccountIcon(string data, Brush? fill = null)
    {
        var path = new Path
        {
            Data = Geometry.Parse(data), Fill = fill, Stroke = fill == null ? AccountWhite : null,
            StrokeThickness = 1.45, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round
        };
        var viewport = new Canvas { Width = 15, Height = 15, Children = { path } };
        return new Viewbox { Width = 14, Height = 14, Child = viewport, VerticalAlignment = VerticalAlignment.Center };
    }

    private static Button AccountButton(FrameworkElement content, double width, double height, double radius)
    {
        var chrome = new FrameworkElementFactory(typeof(Border), "Chrome");
        chrome.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        chrome.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(BorderBrushProperty));
        chrome.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        // Never past half the shorter side: WPF would draw an oval where the design (CSS) has a capsule.
        chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(Math.Min(radius, Math.Min(width, height) / 2)));
        chrome.SetValue(ClipToBoundsProperty, true);
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        chrome.AppendChild(presenter);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = chrome };
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, Frozen("#1FFFFFFF"), "Chrome"));
        hover.Setters.Add(new Setter(Border.BorderBrushProperty, Frozen("#2EFFFFFF"), "Chrome"));
        var disabled = new Trigger { Property = IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(Border.BackgroundProperty, Brushes.Transparent, "Chrome"));
        disabled.Setters.Add(new Setter(Border.BorderBrushProperty, Brushes.Transparent, "Chrome"));
        var focus = new Trigger { Property = IsKeyboardFocusedProperty, Value = true };
        focus.Setters.Add(new Setter(Border.BorderBrushProperty, Frozen("#80FFFFFF"), "Chrome"));
        template.Triggers.Add(hover);
        template.Triggers.Add(disabled);
        template.Triggers.Add(focus);
        return new Button
        {
            Content = content, Width = width, Height = height, Template = template,
            Cursor = Cursors.Hand, Background = AccountTile, BorderBrush = AccountLine,
            FocusVisualStyle = null
        };
    }

    private void BuildClaudeFace()
    {
        _claudeFace = new Grid { Height = ClaudeFrontHeight, Tag = "claude-face" };
        _accountFront = new Grid { Tag = "claude-summary" };
        _accountBack = new Grid { Tag = "claude-accounts", Visibility = Visibility.Collapsed };
        _claudeFace.Children.Add(_accountFront);
        _claudeFace.Children.Add(_accountBack);

        _accountStar = AccountStar(44);
        PlaceAccount(_accountFront, _accountStar, 24, 23.4);
        _accountTitle = AccountText("", 24, weight: 640);
        _accountTitle.TextTrimming = TextTrimming.CharacterEllipsis;
        _accountTitle.MaxWidth = 420;
        _accountTitle.RenderTransform = new ScaleTransform(.94, 1);
        PlaceAccount(_accountFront, _accountTitle, 80, 20, height: 27);
        _accountStatusDot = new Border { Width = 5, Height = 5, CornerRadius = new CornerRadius(2.5), Background = AccountClay };
        PlaceAccount(_accountFront, _accountStatusDot, 80, 58.5);
        _accountStatus = AccountText("", 12, AccountLabel);
        _accountStatus.TextTrimming = TextTrimming.CharacterEllipsis;
        _accountStatus.MaxWidth = 390;
        PlaceAccount(_accountFront, _accountStatus, 91, 52, height: 19);
        _accountElapsed = AccountText("", 19, weight: 550);
        Typography.SetNumeralAlignment(_accountElapsed, FontNumeralAlignment.Tabular);
        _accountElapsed.Margin = new Thickness(0, 32, 24, 0);
        _accountElapsed.HorizontalAlignment = HorizontalAlignment.Right;
        _accountElapsed.VerticalAlignment = VerticalAlignment.Top;
        _accountElapsed.Height = 24;
        _accountElapsed.RenderTransformOrigin = new Point(1, 0);
        _accountElapsed.RenderTransform = new ScaleTransform(.95, 1);
        _accountFront.Children.Add(_accountElapsed);
        PlaceAccount(_accountFront, new Border { Background = AccountLine }, 24, 85.9, 580, 1);
        _claudeFrontMeters = [new AccountMeter(false), new AccountMeter(false)];
        PlaceAccount(_accountFront, _claudeFrontMeters[0].Host, 24, 100.9, 243.5, 55);
        PlaceAccount(_accountFront, _claudeFrontMeters[1].Host, 295.5, 100.9, 243.5, 55);

        _accountCount = AccountText("", 12, weight: 600);
        var toggleContent = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        var layers = AccountIcon("M2,6 l5,-3 5,3 -5,3 Z M2,9 l5,3 5,-3");
        layers.Margin = new Thickness(0, 0, 6, 0);
        toggleContent.Children.Add(layers);
        toggleContent.Children.Add(_accountCount);
        _accountExpand = AccountButton(toggleContent, 57, 31, 20);
        _accountExpand.Click += (_, _) => SetAccountsOpen(true);
        PlaceAccount(_accountFront, _accountExpand, 547, 116);

        _accountBackStar = AccountStar(20);
        PlaceAccount(_accountBack, _accountBackStar, 17, 14);
        var head = AccountText("", 16, weight: 630);
        head.RenderTransform = new ScaleTransform(.96, 1);
        head.Tag = "plan-limits-title";
        PlaceAccount(_accountBack, head, 44, 12, height: 24);
        PlaceAccount(_accountBack, AccountText("", 11.5, AccountLabel), 205, 15.4, height: 17.3);
        PlaceAccount(_accountBack, AccountText("", 11.5, AccountLabel), 366.5, 15.4, height: 17.3);
        var up = AccountIcon("M3,9 l4,-4 4,4");
        up.HorizontalAlignment = HorizontalAlignment.Center;
        _accountClose = AccountButton(up, 32, 26, 20);
        _accountClose.Click += (_, _) => SetAccountsOpen(false);
        PlaceAccount(_accountBack, _accountClose, 579, 11);
        _accountRows = new Grid { Margin = new Thickness(17, AccountHeaderHeight, 17, 6) };
        _accountBack.Children.Add(_accountRows);
        _claudeAccountRow = new AccountRow();
        _accountRows.Children.Add(_claudeAccountRow.Host);
        BuildCodexJobList();

        var next = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        next.Children.Add(new Path
        {
            Data = Geometry.Parse("M0,0 L4,3 L0,6 Z"), Fill = AccountMist, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 3, 0)
        });
        next.Children.Add(AccountText("Next", 10, weight: 600));
        _nextAccount = new Border
        {
            Height = NextHeight, CornerRadius = new CornerRadius(9), Background = AccountTile, BorderBrush = AccountLine,
            BorderThickness = new Thickness(1), Padding = new Thickness(6, 0, 6, 0), Child = next,
            IsHitTestVisible = false, Visibility = Visibility.Collapsed, RenderTransform = _nextAccountShift
        };
        PlaceAccount(_accountRows, _nextAccount, 130, 0);
        Panel.SetZIndex(_nextAccount, 1);

        var noticeContent = new StackPanel { Orientation = Orientation.Horizontal };
        _accountNoticeMark = new Border
        {
            Width = 5, Height = 5, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        _accountNoticeText = AccountText("", 12, weight: 550);
        noticeContent.Children.Add(_accountNoticeMark);
        noticeContent.Children.Add(_accountNoticeText);
        _accountNotice = new Border
        {
            Background = AccountTile, BorderBrush = AccountLine, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12), Padding = new Thickness(7, 3, 7, 3),
            Child = noticeContent, Visibility = Visibility.Collapsed
        };
        AutomationProperties.SetLiveSetting(_accountNotice, AutomationLiveSetting.Polite);
        _accountNotice.MouseLeave += (_, _) =>
        {
            if (_accountNoticeTimer?.IsEnabled == false && _accountNotice.IsVisible) StartAccountNoticeTimer();
        };
        _claudeFace.Children.Add(_accountNotice);
        _claudeSection.Children.Insert(0, _claudeFace);
    }

    private void RefreshClaudeFace(ClaudeSession? session, ClaudeLimits? limits)
    {
        if (_claudeFace == null) BuildClaudeFace();
        string hourLabel = Say("5-Stunden-Nutzung", "5-hour usage");
        string weekLabel = Say("Wochennutzung", "Weekly usage");
        _claudeFrontMeters[0].Label.Text = hourLabel;
        _claudeFrontMeters[1].Label.Text = weekLabel;
        ((TextBlock)_accountBack.Children[1]).Text = Say("Tariflimits", "Plan limits");
        ((TextBlock)_accountBack.Children[2]).Text = hourLabel;
        ((TextBlock)_accountBack.Children[3]).Text = weekLabel;
        AutomationProperties.SetName(_accountExpand, Say("Alle Tariflimits anzeigen", "Show all plan limits"));
        AutomationProperties.SetName(_accountClose, Say("Nur Claude anzeigen", "Show Claude only"));

        _accountTitle.Text = session?.Name ?? "Claude";
        _accountStatus.Text = session?.Status == ClaudeSessionStatus.Working
            ? Say("Claude arbeitet", "Claude is working") : session == null ? Loc.Get("control.claude.idle") : StateWord(session);
        _accountStatusDot.Background = session?.Status switch
        {
            ClaudeSessionStatus.Working => AccountClay, ClaudeSessionStatus.Waiting => AccountSand,
            ClaudeSessionStatus.Done => AccountSage, _ => AccountNeutral
        };
        _accountTurnStarted = session?.Status is ClaudeSessionStatus.Working or ClaudeSessionStatus.Waiting ? session.TurnStartedUtc : null;
        _accountCount.Text = $"+{_codexAccounts.Count}";
        if (_inlineClaudeGrant == null || !_resetStates.ContainsKey("claude:" + _inlineClaudeGrant))
            _inlineClaudeGrant = limits?.Grants.FirstOrDefault(item => item.Kind != ClaudeResetKind.Fable)?.Id;
        _accountExpand.Visibility = _codexAccounts.Count > 0 || limits?.Grants.Count > 0 || _resetStates.Count > 0
            ? Visibility.Visible : Visibility.Collapsed;
        if (_accountsOpen && _accountExpand.Visibility == Visibility.Collapsed) SetAccountsOpen(false, animate: false);
        PaintAccountRows(limits, DateTimeOffset.Now);
        UpdateAccountElapsed();
        PaintClaudeNotchButton();
        GatherClaudeLightSoon();
    }

    private void PaintAccountRows(ClaudeLimits? limits, DateTimeOffset now)
    {
        double rowHeight = AccountRowHeight;
        _claudeAccountRow.Place(0, rowHeight);
        _claudeAccountRow.SetIdentity("Claude", limits?.Plan, AccountClay);
        SetMeters(_claudeAccountRow, "claude", limits?.FiveHour, limits?.Week, AccountClay, now);
        var grant = limits?.Grants.FirstOrDefault(item => item.Id == _inlineClaudeGrant);
        SetAccountCredit(_claudeAccountRow, "claude", _inlineClaudeGrant, grant?.ResetsLeft ?? 0,
            grant?.UsableNow == true, "Claude", grant == null ? null : Loc.Get(ResetUnavailableReason(grant)));

        for (int i = 0; i < _codexAccounts.Count; i++)
        {
            var account = _codexAccounts[i];
            if (!_accountRowBySlot.TryGetValue(account, out var row))
            {
                _accountRowBySlot[account] = row = new AccountRow();
                _accountRows.Children.Add(row.Host);
            }
            var blocked = account.BlockedUntil(now);
            double max = Math.Max(account.FiveHour?.UsedPercent ?? -1, account.Week?.UsedPercent ?? -1);
            Brush light = blocked != null ? AccountRose : max >= 80 ? AccountRose : max >= 50 ? AccountSand :
                max >= 0 ? AccountSage : AccountNeutral;
            row.Place((i + 1) * rowHeight, rowHeight);
            row.SetIdentity(account.DisplayName, account.Plan, light);
            SetMeters(row, AccountProvider(account), account.FiveHour, account.Week, AccountMist, now, blocked);
            var credits = account.Client.Latest?.Credits.Where(c => c.ExpiresAt == null || c.ExpiresAt > now).ToArray() ?? [];
            string provider = AccountProvider(account);
            string? id = _resetStates.Keys.FirstOrDefault(key => key.StartsWith(provider + ":", StringComparison.Ordinal))?
                [(provider.Length + 1)..] ?? credits.FirstOrDefault()?.Id;
            SetAccountCredit(row, provider, id, credits.Length, credits.Any(credit => credit.Id == id), account.DisplayName, account: account);
        }

        PaintNextAccount(now, rowHeight);
    }

    private double AccountRowHeight => Math.Min(54, Math.Floor(250d / (_codexAccounts.Count + 1)));

    private bool FitClaudeAccountFace()
    {
        double height = ClaudeFrontHeight;
        if (_accountsOpen)
        {
            double rowHeight = AccountRowHeight;
            double accountsBottom = AccountHeaderHeight + (_codexAccounts.Count + 1) * rowHeight;
            height = accountsBottom + PaintCodexJobList(DateTimeOffset.UtcNow, accountsBottom, rowHeight) + 6;
        }
        if (_claudeFace!.Height == height) return false;
        _claudeFace.Height = height;
        return true;
    }

    private void SetMeters(AccountRow row, string key, ClaudeLimitWindow? hour, ClaudeLimitWindow? week, Brush color,
        DateTimeOffset now, DateTimeOffset? blocked = null)
    {
        var meters = key == "claude" && !_accountsOpen ? _claudeFrontMeters : row.Meters;
        if (key != "claude" && !_accountsOpen) return;
        SetMeter(meters[0], key + ":5h", hour, color);
        SetMeter(meters[1], key + ":week", week, color);
        // Keep both layouts ready for the gesture without counting the hidden layout as another observation.
        var hidden = key == "claude" ? (_accountsOpen ? _claudeFrontMeters : row.Meters) : [];
        if (hidden.Length != 0)
        {
            hidden[0].Update(hour, color, false);
            hidden[1].Update(week, color, false);
        }
        meters[0].SetClock(now, weekly: false, blocked);
        meters[1].SetClock(now, weekly: true);
    }

    private void SetMeter(AccountMeter meter, string key, ClaudeLimitWindow? value, Brush fill)
    {
        if (!IsVisible) return;
        bool changed = Seen.Changed("accounts:" + key, value?.UsedPercent.ToString("R", CultureInfo.InvariantCulture) ?? "");
        meter.Update(value, fill, changed && AnimateAccounts);
    }

    private void PaintNextAccount(DateTimeOffset now, double rowHeight)
    {
        var next = CodexAccount.Next(_codexAccounts, now);
        _nextAccount.Visibility = next != null ? Visibility.Visible : Visibility.Collapsed;
        if (next == null)
        {
            if (_accountsOpen) Seen.Forget("accounts:next");
            Motion.Stop(_nextAccountShift, TranslateTransform.YProperty);
            return;
        }
        if (!IsVisible) return;
        double y = (_codexAccounts.IndexOf(next) + 1) * rowHeight + (rowHeight - NextHeight) / 2;
        string identity = AccountProvider(next) + ":" + y.ToString(CultureInfo.InvariantCulture);
        bool changed = _accountsOpen && Seen.Changed("accounts:next", identity);
        var caption = (TextBlock)((StackPanel)_nextAccount.Child).Children[1];
        caption.Text = Say("Nächster", "Next");
        AutomationProperties.SetName(_nextAccount, Say("Nächster Worker verwendet ", "Next worker uses ") + next.DisplayName);
        if (changed && AnimateAccounts && Math.Abs(y - _nextAccountShift.Y) > .1)
            Motion.To(_nextAccountShift, TranslateTransform.YProperty, y, Motion.Press);
        else if (!Motion.IsRunning(_nextAccountShift, TranslateTransform.YProperty) || !AnimateAccounts || !_accountsOpen)
            Motion.Stop(_nextAccountShift, TranslateTransform.YProperty, y);
    }

    private void UpdateAccountElapsed()
    {
        if (_claudeFace == null || !IsVisible) return;
        if (_accountsOpen)
        {
            foreach (var row in _codexJobRows.Values) row.UpdateClock(DateTimeOffset.UtcNow);
            return;
        }
        if (_accountTurnStarted is { } started)
        {
            var elapsed = DateTime.UtcNow - started;
            if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
            _accountElapsed.Text = elapsed.TotalHours >= 1 ? FormatElapsed(elapsed) : elapsed.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
        }
        else _accountElapsed.Text = "";
    }

    private static string AccountProvider(CodexAccount slot) => "codex-" + slot.Slot.Name;

    private void ForgetAccountRow(CodexAccount account)
    {
        string prefix = AccountProvider(account) + ":";
        Seen.ForgetAll("accounts:" + prefix);
        foreach (string key in _resetStates.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
            _resetStates.Remove(key);
        if (!_accountRowBySlot.Remove(account, out var row)) return;
        row.Stop();
        _accountRows.Children.Remove(row.Host);
    }

    internal bool TryFoldClaudeAccounts()
    {
        if (!_accountsOpen) return false;
        SetAccountsOpen(false);
        return true;
    }

    private void SetAccountsOpen(bool open, bool animate = true)
    {
        if (_accountsOpen == open || _claudeFace == null) return;
        bool move = animate && AnimateAccounts;
        var from = _accountsOpen ? _claudeAccountRow.Meters : _claudeFrontMeters;
        var to = open ? _claudeAccountRow.Meters : _claudeFrontMeters;
        var previous = move ? from.Select(m => m.BoundsIn(_claudeFace)).ToArray() : [];
        _accountsOpen = open;
        if (!open) StopCodexJobMotion();
        DisarmAccountCredits();
        if (!open)
            foreach (var row in _resetRows.Values) row.Stop();
        _accountFront.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
        _accountBack.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        foreach (var meter in from) meter.Stop();
        var target = open ? _accountBack : _accountFront;
        var source = open ? _accountFront : _accountBack;
        double opacity = source.Opacity;
        Motion.Stop(source, OpacityProperty, 1);
        _claudeKey = "";
        BuildClaudeSection();
        PositionAccountNotice();
        if (move)
        {
            _claudeFace.UpdateLayout();
            var spring = open ? Motion.Exit : Motion.MorphClose;
            Motion.To(target, OpacityProperty, 1, Motion.PressDown, from: opacity < .99 ? opacity : .22);
            for (int i = 0; i < to.Length; i++) to[i].MorphFrom(previous[i], _claudeFace, spring);
        }
        else Motion.Stop(target, OpacityProperty, 1);
        if (animate && IsVisible)
        {
            if (open)
            {
                KeyboardInputRequested?.Invoke(true);
                _accountClose.Focus();
            }
            else
            {
                _accountExpand.Focus();
                if (_composingSessionId == null) KeyboardInputRequested?.Invoke(false);
            }
        }
    }

    private void DisarmAccountCredits()
    {
        foreach (var row in _accountRowBySlot.Values.Append(_claudeAccountRow))
        {
            if (row?.CreditKey is { } key && _resetStates.TryGetValue(key, out var state) && !state.Running && state.Result == null)
                _resetStates.Remove(key);
            row?.StopCountdown();
        }
    }

    private void SetAccountCredit(AccountRow row, string provider, string? id, int count, bool canUse,
        string name, string? reason = null, CodexAccount? account = null)
    {
        string? key = id == null ? null : provider + ":" + id;
        if (row.CreditKey != key)
        {
            if (row.CreditKey is { } old && _resetStates.TryGetValue(old, out var oldState) && !oldState.Running && oldState.Result == null)
                _resetStates.Remove(old);
            row.CreditKey = key;
            row.Redeem = id == null ? null : () => RedeemReset(provider, id, account, inline: true);
        }
        ResetState? state = key == null ? null : _resetStates.GetValueOrDefault(key);
        bool confirm = state?.Until > DateTimeOffset.UtcNow && !state.Running && state.Result == null;
        string label = state?.Running == true ? "…" : state?.Result == "resetDone" ? "✓" :
            state?.Result != null ? Say("Fehler", "Failed") : confirm ? Say("Bestätigen", "Confirm") :
            count > 0 ? count + (account == null ? Say(" Grant", " grant") : Say(" Guthaben", " credit")) : "–";
        row.CreditLabel.Text = label;
        row.CreditLabel.Foreground = canUse || state != null ? AccountWhite : AccountLabel;
        row.Credit.IsEnabled = canUse && !_isLocalOnly() && state?.Running != true && state?.Result != "resetDone";
        string action = confirm ? Say("Reset bestätigen", "Confirm reset") : Say("Reset einlösen", "Redeem reset");
        string? result = state?.Result == null ? null : Loc.Get("control.claude." + state.Result);
        AutomationProperties.SetName(row.Credit, name + " · " + (result ?? action));
        row.Credit.ToolTip = result ?? (canUse ? name + " · " + action : reason);
        row.SetCountdown(confirm && _accountsOpen && IsVisible ? state!.Until : null, AnimateAccounts);
    }

    private void StopClaudeFaceMotion()
    {
        if (_claudeFace == null) return;
        Motion.Stop(_accountFront, OpacityProperty, 1);
        Motion.Stop(_accountBack, OpacityProperty, 1);
        Motion.Stop(_nextAccountShift, TranslateTransform.YProperty);
        foreach (var meter in _claudeFrontMeters) meter.Stop();
        _claudeAccountRow.Stop();
        foreach (var row in _accountRowBySlot.Values) row.Stop();
        StopCodexJobMotion();
    }

    private void GatherClaudeLightSoon() => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, GatherClaudeLight);

    private void GatherClaudeLight()
    {
        if (_disposed || _backdrop == null || _claudeFace?.IsVisible != true || !_backdrop.IsVisible) return;
        FrameworkElement anchor = _accountNotice.IsVisible ? _accountNoticeMark : _accountsOpen ? _accountBackStar : _accountStar;
        var center = anchor.TranslatePoint(new Point(anchor.ActualWidth / 2, anchor.ActualHeight / 2), _backdrop);
        var color = _accountNotice.IsVisible ? ((SolidColorBrush)_accountNoticeMark.Background).Color : ClaudeAtWork.Main;
        _backdrop.SetMood(color, color, color, 0);
        _backdrop.Gather(center, verticalScale: 1.2);
        _backdrop.Intensity = _accountNotice.IsVisible ? .36 : _accountsOpen ? .41 : .44;
    }

    internal bool TryShowAllowanceNotice(string text, int threshold)
    {
        if (_claudeFace?.IsVisible != true || _accountNotice.IsVisible) return false;
        _accountNoticeText.Text = text;
        _accountNoticeMark.Background = threshold <= 20 ? AccountRose : AccountSand;
        _accountNotice.Visibility = Visibility.Visible;
        AutomationProperties.SetName(_accountNotice, text);
        if (UIElementAutomationPeer.FromElement(_accountNotice) is { } peer)
            peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        PositionAccountNotice();
        GatherClaudeLightSoon();
        StartAccountNoticeTimer();
        return true;
    }

    private void PositionAccountNotice()
    {
        _accountNotice.HorizontalAlignment = HorizontalAlignment.Left;
        _accountNotice.VerticalAlignment = VerticalAlignment.Top;
        _accountNotice.Margin = _accountsOpen ? new Thickness(17, 11, 0, 0) : new Thickness(24, 157, 0, 0);
        _accountNotice.Padding = _accountsOpen ? new Thickness(7, 3, 7, 3) : new Thickness(7, 1, 7, 1);
        _accountBackStar.Visibility = _accountNotice.IsVisible ? Visibility.Hidden : Visibility.Visible;
        _accountBack.Children[1].Visibility = _accountBackStar.Visibility;
        bool notice = _accountNotice.IsVisible && _accountsOpen;
        foreach (var heading in _accountBack.Children.OfType<TextBlock>().Skip(1))
            heading.Visibility = notice ? Visibility.Hidden : Visibility.Visible;
    }

    private void StartAccountNoticeTimer()
    {
        if (_accountNoticeTimer == null)
        {
            _accountNoticeTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = AllowanceAlerts.DisplayTime };
            _accountNoticeTimer.Tick += (_, _) =>
            {
                _accountNoticeTimer.Stop();
                if (_accountNotice.IsMouseOver) return;
                HideAccountNotice();
                AllowanceDataChanged?.Invoke();
            };
        }
        _accountNoticeTimer.Start();
    }

    private void HideAccountNotice()
    {
        _accountNoticeTimer?.Stop();
        if (_accountNotice == null) return;
        _accountNotice.Visibility = Visibility.Collapsed;
        PositionAccountNotice();
        GatherClaudeLightSoon();
    }

    private sealed class AccountRow
    {
        public Grid Host { get; } = new() { VerticalAlignment = VerticalAlignment.Top, Tag = "account-row" };
        public AccountMeter[] Meters { get; } = [new(true), new(true)];
        public TextBlock CreditLabel { get; } = AccountText("–", 11, AccountLabel);
        public Button Credit { get; }
        public string? CreditKey { get; set; }
        public Func<Task>? Redeem { get; set; }
        private readonly TextBlock _name = AccountText("", 14, weight: 570);
        private readonly TextBlock _plan = AccountText("", 12, AccountLabel);
        private readonly Border _lamp = new() { Width = 6, Height = 6, CornerRadius = new CornerRadius(3) };
        private readonly DropShadowEffect _light = new() { BlurRadius = 7, ShadowDepth = 0, Opacity = .22 };
        private readonly Border _remaining;
        private readonly ScaleTransform _countdown = new(1, 1);
        private DateTimeOffset? _deadline;

        public AccountRow()
        {
            var rule = new Border { Background = AccountLine, Height = 1, VerticalAlignment = VerticalAlignment.Top };
            Host.Children.Add(rule);
            _lamp.Effect = _light;
            _lamp.VerticalAlignment = VerticalAlignment.Center;
            _name.Margin = new Thickness(8, 0, 0, 0);
            _name.MaxWidth = 108;
            _name.TextTrimming = TextTrimming.CharacterEllipsis;
            _plan.Margin = new Thickness(4, 0, 0, 0);
            Host.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { _lamp, _name, _plan }
            });
            PlaceAccount(Host, Meters[0].Host, 188, 0, 148.5);
            PlaceAccount(Host, Meters[1].Host, 349.5, 0, 148.5);
            foreach (var meter in Meters) meter.Host.VerticalAlignment = VerticalAlignment.Center;
            _remaining = new Border
            {
                Background = AccountSand, Height = 2, VerticalAlignment = VerticalAlignment.Bottom,
                RenderTransform = _countdown, Visibility = Visibility.Collapsed
            };
            CreditLabel.HorizontalAlignment = HorizontalAlignment.Center;
            Credit = AccountButton(new Grid { Children = { CreditLabel, _remaining } }, 73, 26, 9);
            Credit.HorizontalAlignment = HorizontalAlignment.Right;
            Credit.Click += async (_, _) => { if (Redeem is { } redeem) await redeem(); };
            ToolTipService.SetShowOnDisabled(Credit, true);
            Host.Children.Add(Credit);
        }

        public void Place(double y, double height)
        {
            Host.Margin = new Thickness(0, y, 0, 0);
            Host.Height = height;
        }

        public void SetIdentity(string name, string? plan, Brush lamp)
        {
            _lamp.Background = lamp;
            _light.Color = ((SolidColorBrush)lamp).Color;
            _name.Text = name;
            _plan.Text = plan == null ? "" : "· " + plan;
            _plan.MaxWidth = Math.Max(0, 109 - new FormattedText(name, AccountCulture, FlowDirection.LeftToRight,
                new Typeface(AccountFont, FontStyles.Normal, _name.FontWeight, FontStretches.Normal), 14, AccountWhite, 1).Width);
            _plan.TextTrimming = TextTrimming.CharacterEllipsis;
        }

        public void SetCountdown(DateTimeOffset? until, bool animate)
        {
            if (until == _deadline) return;
            StopCountdown();
            if (until == null) return;
            _deadline = until;
            _remaining.Visibility = Visibility.Visible;
            if (animate)
            {
                var remaining = until.Value - DateTimeOffset.UtcNow;
                if (remaining > TimeSpan.Zero)
                    _countdown.BeginAnimation(ScaleTransform.ScaleXProperty,
                        new DoubleAnimation(remaining.TotalSeconds / 3, 0, remaining));
            }
        }

        public void StopCountdown()
        {
            Motion.Stop(_countdown, ScaleTransform.ScaleXProperty, 1);
            _remaining.Visibility = Visibility.Collapsed;
            _deadline = null;
        }

        public void Stop()
        {
            StopCountdown();
            foreach (var meter in Meters) meter.Stop();
        }
    }

    private sealed class AccountMeter
    {
        public Grid Host { get; } = new();
        public TextBlock Label { get; } = AccountText("", 12, AccountLabel);
        public ScaleTransform Grow { get; } = new(0, 1);
        public WeatherTween Number { get; } = new();
        private readonly TextBlock _clock, _readout;
        private readonly Run _digits = new("–"), _unit;
        private readonly Border _fill;
        private readonly ScaleTransform _scale = new(1, 1);
        private readonly TranslateTransform _shift = new();
        private ClaudeLimitWindow? _window;

        public AccountMeter(bool compact)
        {
            _clock = AccountText("", compact ? 11 : 12, AccountLabel);
            _readout = AccountText("", compact ? 13 : 25, weight: compact ? 560 : 530);
            _readout.TextAlignment = TextAlignment.Right;
            _readout.HorizontalAlignment = HorizontalAlignment.Right;
            _readout.VerticalAlignment = VerticalAlignment.Top;
            _readout.Margin = new Thickness(0, compact ? 0 : -3, 0, 0);
            Typography.SetNumeralAlignment(_readout, FontNumeralAlignment.Tabular);
            _unit = new Run { FontSize = compact ? 10 : 15.5 };
            _readout.Inlines.Add(_digits);
            _readout.Inlines.Add(_unit);
            Number.Step = value => _digits.Text = value.ToString("0", CultureInfo.CurrentCulture);

            double radius = compact ? 1.5 : 2;
            _fill = new Border { CornerRadius = new CornerRadius(radius), RenderTransform = Grow };
            var track = new Grid
            {
                Height = compact ? 3 : 4, VerticalAlignment = VerticalAlignment.Top,
                Margin = compact ? new Thickness(0, 6.3, 46, 0) : new Thickness(0, 32, 0, 0),
                Children = { new Border { Background = AccountTrack, CornerRadius = new CornerRadius(radius) }, _fill }
            };
            if (!compact) PlaceAccount(Host, Label, 0, 9);
            _clock.VerticalAlignment = VerticalAlignment.Top;
            _clock.HorizontalAlignment = HorizontalAlignment.Left;
            _clock.Margin = new Thickness(0, compact ? 14 : 40, 0, 0);
            if (compact) Host.Height = 29.4;
            Host.Children.Add(track);
            Host.Children.Add(_readout);
            Host.Children.Add(_clock);
            Host.RenderTransform = new TransformGroup { Children = { _scale, _shift } };
        }

        public void Update(ClaudeLimitWindow? window, Brush fill, bool animate)
        {
            _fill.Background = fill;
            double? previous = _window?.UsedPercent;
            _window = window;
            if (previous == window?.UsedPercent) return;
            double used = window?.UsedPercent ?? 0;
            _unit.Text = window == null ? "" : "\u200A%";
            if (animate && previous.HasValue && window != null && Math.Abs(previous.Value - used) > .01)
            {
                Motion.To(Grow, ScaleTransform.ScaleXProperty, used / 100, Motion.Glide);
                Number.To(used);
            }
            else StopValue();
        }

        public void SetClock(DateTimeOffset now, bool weekly, DateTimeOffset? blocked = null)
        {
            _clock.Foreground = blocked != null ? AccountRose : AccountLabel;
            if (blocked is { } until)
                _clock.Text = Say("Voll bis ", "Full until ") + until.ToLocalTime().ToString("HH:mm", AccountCulture);
            else if (_window?.ResetsAt is { } reset && reset > now)
                _clock.Text = weekly ? reset.ToLocalTime().ToString("ddd HH:mm", AccountCulture) :
                    Loc.Get("control.claude.resetsIn", AccountDuration(reset - now));
            else _clock.Text = _window == null ? Say("Nicht verfügbar", "Unavailable") : Say("Kein aktiver Zeitraum", "No active window");
        }

        private static string AccountDuration(TimeSpan left)
        {
            int minutes = Math.Max(1, (int)Math.Ceiling(left.TotalMinutes));
            return minutes >= 60 ? $"{minutes / 60} h {minutes % 60:00}" : $"{minutes} min";
        }

        public Rect BoundsIn(Grid parent) => Host.TransformToAncestor(parent).TransformBounds(new Rect(Host.RenderSize));

        public void MorphFrom(Rect previous, Grid parent, SpringMotion spring)
        {
            StopMorph();
            var target = BoundsIn(parent);
            if (previous.IsEmpty || target.Width <= 0 || previous.Width <= 0) return;
            Motion.To(_scale, ScaleTransform.ScaleXProperty, 1, spring, from: previous.Width / target.Width);
            Motion.To(_shift, TranslateTransform.XProperty, 0, spring, from: previous.X - target.X);
            Motion.To(_shift, TranslateTransform.YProperty, 0, spring, from: previous.Y - target.Y);
        }

        public void StopMorph()
        {
            Motion.Stop(_scale, ScaleTransform.ScaleXProperty, 1);
            Motion.Stop(_shift, TranslateTransform.XProperty, 0);
            Motion.Stop(_shift, TranslateTransform.YProperty, 0);
        }

        private void StopValue()
        {
            double value = _window?.UsedPercent ?? 0;
            Motion.Stop(Grow, ScaleTransform.ScaleXProperty, value / 100);
            Number.Stop(value);
            _digits.Text = _window == null ? "–" : value.ToString("0", CultureInfo.CurrentCulture);
        }

        public void Stop()
        {
            StopMorph();
            StopValue();
        }
    }
}
