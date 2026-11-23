using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VNotch.Controllers;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.ClaudeCode;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

public partial class MainWindow
{
    #region Control Center and Claude tabs

    private static readonly TimeSpan ClaudeSweepInterval = TimeSpan.FromSeconds(15);
    private const double ClaudeViewWidth = Controls.ControlCenterView.ClaudeFaceWidth;
    private const double ClaudeViewMinHeight = 120;
    private const double ClaudeViewMaxHeight = CenterViewMaxHeight;

    private ClaudeSessionTracker? _claudeTracker;
    private ClaudeHookServer? _claudeHookServer;
    private ClaudeUsageClient? _claudeUsage;
    private DispatcherTimer? _claudeSweepTimer;
    private bool _claudeTabAutoOpened;
    private bool _isClaudeView;
    private bool _isClaudeWaiting;
    private DispatcherTimer? _claudeAttentionRetry;
    private static readonly TimeSpan ClaudeAttentionRetryInterval = TimeSpan.FromSeconds(1);
    private double _claudeViewHeight = ClaudeViewMaxHeight;

    private void InitializeControlCenter()
    {
        _claudeTracker = new ClaudeSessionTracker();
        _claudeHookServer = new ClaudeHookServer(_claudeTracker);
        _claudeUsage = new ClaudeUsageClient();
        _claudeHookServer.Start();

        // Settings load after this runs, and anything that pumps messages meanwhile can let the views build:
        // until then they are plain-skinned and offline.
        ControlCenter.Attach(() => _settings != null && IsLiquidGlassEnabled);
        ControlCenter.ContentHeightChanged += OnSystemContentHeightChanged;
        ControlCenter.AttachBackdrop(SystemBackdrop);
        ClaudeCenter.AttachClaude(_claudeTracker, _claudeHookServer, _claudeUsage,
            () => _settings?.EnableLocalOnlyMode ?? true, () => _settings != null && IsLiquidGlassEnabled);
        ClaudeCenter.AttachBackdrop(ClaudeBackdrop);
        ClaudeCenter.AttentionRequested += OpenClaudeTabForAttention;
        ClaudeCenter.ContentHeightChanged += OnClaudeContentHeightChanged;
        ClaudeCenter.AllowanceDataChanged += CheckAllowanceAlerts;
        ClaudeNotification.MouseLeave += (_, _) => ResumeAllowancePill();
        ClaudeCenter.SessionOpened += OnClaudeSessionOpened;
        ClaudeCenter.KeyboardInputRequested += OnClaudeKeyboardInputRequested;
        ClaudeCenter.ScreenshotRequested += OnClaudeScreenshotRequested;
        _claudeTracker.Changed += OnClaudeSessionsChanged;
        _claudeTracker.TurnFinished += OnClaudeTurnFinished;
        _claudeTracker.PermissionRequested += OnClaudePermissionRequestedSound;
        NotchWrapper.PreviewMouseRightButtonUp += NotchWrapper_DismissClaudeDone;
        ClaudeCenter.AttachSoundSwitch(() => _settings?.PlayClaudeSounds ?? false, SetClaudeSounds);
        ClaudeCenter.AttachNotchSwitch(() => _settings?.ShowClaudeInNotch ?? true, SetClaudeInNotch);

        _claudeSweepTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = ClaudeSweepInterval };
        _claudeSweepTimer.Tick += (_, _) =>
        {
            _claudeTracker?.Sweep();
            CheckClaudeLimitResets();
            CheckAllowanceAlerts();
        };
        _claudeSweepTimer.Start();
        // Asking Windows about notification access is a cross-apartment call that pumps messages: after startup.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, InitializeAppNotifications);
        InitializePi();
        InitializeWeather();
        InitializeMicMuted();
        InitializeDownloads();
        InitializeLevelHud();
        InitializeKeepAwake();
        SchedulePrefetch();
    }

    private void DisposeControlCenter()
    {
        _claudeSweepTimer?.Stop();
        _claudeNotificationTimer?.Stop();
        _claudeAttentionRetry?.Stop();
        DisposeAppNotifications();
        DisposePi();
        DisposeWeather();
        DisposeMicMuted();
        DisposeDownloads();
        DisposeLevelHud();
        DisposeKeepAwake();
        ClaudeCenter.AttentionRequested -= OpenClaudeTabForAttention;
        ClaudeCenter.ContentHeightChanged -= OnClaudeContentHeightChanged;
        ClaudeCenter.AllowanceDataChanged -= CheckAllowanceAlerts;
        ClaudeCenter.SessionOpened -= OnClaudeSessionOpened;
        ClaudeCenter.KeyboardInputRequested -= OnClaudeKeyboardInputRequested;
        ClaudeCenter.ScreenshotRequested -= OnClaudeScreenshotRequested;
        DisposeClaudeShots();
        if (_claudeTracker != null)
        {
            _claudeTracker.Changed -= OnClaudeSessionsChanged;
            _claudeTracker.TurnFinished -= OnClaudeTurnFinished;
            _claudeTracker.PermissionRequested -= OnClaudePermissionRequestedSound;
        }
        ControlCenter.Dispose();
        ControlCenter.ContentHeightChanged -= OnSystemContentHeightChanged;
        ClaudeCenter.Dispose();
        // Hands every held request back to Claude Code's own dialog.
        _claudeHookServer?.Dispose();
        _claudeUsage?.Dispose();
    }

    private void ClaudeIconButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!_isClaudeView) NavigateToNotchView(NotchView.Claude);
    }

    /// <summary>Nothing from Claude shows over a fullscreen app or while the notch is hidden.</summary>
    private bool CanShowClaudeAttention =>
        IsEffectivelyNotchVisible &&
        !FullscreenDetector.IsForegroundWindowFullscreen(Win32Interop.GetForegroundWindow(), _hwnd);

    /// <summary>
    /// A session waits for an approval or an answer: open the Claude tab so it can be given here. While the
    /// notch is busy (greeting, another tab open, a fullscreen app) it tries again until nothing waits.
    /// </summary>
    private void OpenClaudeTabForAttention()
    {
        if (_cleanedUp || _isClaudeView || _settings?.ShowClaudeInNotch == false) return;
        // A tool to allow or a question goes to the pill in the closed notch; a plan to read opens the tab.
        if (NewestAskForPill() is { } ask && (IsAskPillShown || TryShowClaudeAskPill(ask)))
        {
            _claudeAttentionRetry?.Stop();
            return;
        }
        if (_allowancePill || _isAnimating || _isGreetingActive || _isExpanded || _isMusicExpanded || !CanShowClaudeAttention)
        {
            _claudeAttentionRetry ??= CreateClaudeAttentionRetry();
            _claudeAttentionRetry.Start();
            return;
        }
        _claudeAttentionRetry?.Stop();
        _claudeTabAutoOpened = true;
        _transitionCoordinator.RequestView(NotchView.Claude, "ClaudeAttention");
    }

    private DispatcherTimer CreateClaudeAttentionRetry()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = ClaudeAttentionRetryInterval };
        timer.Tick += (_, _) =>
        {
            if (_isClaudeView || !_isClaudeWaiting)
            {
                timer.Stop();
                return;
            }
            OpenClaudeTabForAttention();
        };
        return timer;
    }

    /// <summary>
    /// Keeps the tab-bar badge in step with the waiting requests, and closes the tab again once the request
    /// it opened for was answered, here or in the terminal.
    /// </summary>
    private void OnClaudeSessionsChanged() => Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
    {
        if (_claudeTracker == null || _cleanedUp) return;
        UpdateClaudeLive();
        PruneClaudeDone();
        SyncClaudeAskPill();
        bool waiting = _claudeTracker.GetPending().Count > 0;
        if (waiting != _isClaudeWaiting)
        {
            _isClaudeWaiting = waiting;
            ShowClaudeWaitingBadge(waiting);
            UpdateNavIconsActiveState();
        }
        if (waiting || !_claudeTabAutoOpened) return;
        _claudeTabAutoOpened = false;
        if (_isClaudeView && !_isAnimating && !NotchBorder.IsMouseOver) CollapseNotch();
    });

    private void ShowClaudeWaitingBadge(bool show)
    {
        bool entering = ClaudeWaitingBadge.Visibility != Visibility.Visible;
        if (show) ClaudeWaitingBadge.Visibility = Visibility.Visible;
        Motion.To(ClaudeWaitingBadgeScale, ScaleTransform.ScaleXProperty, show ? 1 : Motion.ExitToScale,
            show ? Motion.TileScale : Motion.Exit, from: show && entering ? Motion.TileFromScale : null);
        Motion.To(ClaudeWaitingBadgeScale, ScaleTransform.ScaleYProperty, show ? 1 : Motion.ExitToScale,
            show ? Motion.TileScale : Motion.Exit, from: show && entering ? Motion.TileFromScale : null);
        Motion.To(ClaudeWaitingBadge, OpacityProperty, show ? 1 : 0, show ? Motion.Tile : Motion.Exit,
            from: show && entering ? 0 : null, done: () =>
            {
                if (!show) ClaudeWaitingBadge.Visibility = Visibility.Collapsed;
            });
    }

    /// <summary>Typing a message to a session needs the keys; otherwise the notch never takes focus.</summary>
    private void OnClaudeKeyboardInputRequested(bool enabled)
    {
        if (enabled) EnableKeyboardInput();
        else if (!_isSecondaryView) DisableKeyboardInput();
    }

    /// <summary>The session's terminal or app is in front now; the notch gets out of its way.</summary>
    private void OnClaudeSessionOpened()
    {
        _claudeTabAutoOpened = false;
        if (_isClaudeView && !_isAnimating) CollapseNotch();
    }

    /// <summary>The notch height that fits the Claude page, between a single card and a scrolling list.</summary>
    private double MeasureClaudeViewHeight() =>
        FitHeight(ClaudeCenter, ClaudeContent, ClaudeViewWidth, ClaudeViewMinHeight, ClaudeViewMaxHeight);

    private void OnClaudeContentHeightChanged() =>
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, SettleClaudeNotchToFit);

    /// <summary>Grows or shrinks the open tab when sessions or requests come and go.</summary>
    private void SettleClaudeNotchToFit()
    {
        if (!_isClaudeView || _isAnimating || _cleanedUp) return;
        _claudeViewHeight = MeasureClaudeViewHeight();
        SettleNotchToContent(ClaudeContent, _claudeViewHeight, () => _isClaudeView);
    }

    #endregion

    #region Claude notification (compact pill)

    private static readonly TimeSpan ClaudeNotificationDuration = TimeSpan.FromSeconds(4);
    // App and Pi notifications carry a message to read, so they stay longer than Claude's short status pills.
    private static readonly TimeSpan AppNotificationDuration = TimeSpan.FromSeconds(10);

    private bool _isClaudeNotificationVisible;
    private int _claudeNotificationToken;
    private DispatcherTimer? _claudeNotificationTimer;

    // What a tap on the pill does instead of opening the notch: bring up the session or the app it is about.
    private Func<bool>? _notificationTap;
    // A Claude pill opens the Claude tab when there is no window to bring up; an app's pill opens the notch.
    private bool _isClaudePill;

    private void OnClaudePermissionRequestedSound(ClaudePermissionRequest request) =>
        Dispatcher.BeginInvoke(() => PlayClaudeSound(ClaudeSound.Waiting));

    private void PlayClaudeSound(ClaudeSound sound)
    {
        if (_cleanedUp || _settings?.PlayClaudeSounds != true || !CanShowClaudeAttention) return;
        ClaudeSounds.Play(sound);
    }

    private void SetClaudeSounds(bool on)
    {
        _settings.PlayClaudeSounds = on;
        _settingsService.SaveAsync(_settings).SafeFireAndForget("SETTINGS-SAVE");
    }

    /// <summary>Claude Code in the closed notch on or off; the sessions leave (or come back to) the notch at once.</summary>
    private void SetClaudeInNotch(bool on)
    {
        _settings.ShowClaudeInNotch = on;
        _settingsService.SaveAsync(_settings).SafeFireAndForget("SETTINGS-SAVE");
        if (!on) _claudeAttentionRetry?.Stop();
        UpdateClaudeLive();
    }

    private void OnClaudeTurnFinished(ClaudeSession session) => Dispatcher.BeginInvoke(() =>
    {
        PlayClaudeSound(ClaudeSound.Done);
        // Switched off for the closed notch (the eye in the Claude tab): the sound stays, no pill.
        if (_settings?.ShowClaudeInNotch == false) return;
        RememberClaudeDone(session);
    });

    /// <summary>A tap on the pill goes straight to the session's terminal or the app, not into the notch.</summary>
    private bool TryHandleNotificationTap()
    {
        if (!_isClaudeNotificationVisible) return false;
        // Tapping a finished turn counts as seen, even when its window cannot come up and the notch opens instead.
        if (_claudeDoneShownId is { } done) ForgetClaudeDone(done);
        IntPtr foregroundBefore = OverlayWindowController.GetForegroundWindowHandle();
        if (_notificationTap is not { } tap || !tap()) return false;
        PlayNotificationDrop(foregroundBefore);
        _claudeNotificationTimer?.Stop();
        AnimateClaudeNotificationOut();
        return true;
    }

    /// <summary>
    /// "name · status" in the closed notch, like a Bluetooth device connecting: Claude's star for a finished
    /// turn, the app's logo for a Windows notification. Never over a fullscreen app.
    /// </summary>
    private const string NotificationBellGlyph = "";

    /// <summary>Shows the pill; false when the notch was busy or another pill held the slot, so callers can try again.</summary>
    private bool ShowNotchPill(string title, string status, ImageSource? appIcon, bool isClaude, Func<bool>? tap,
        string? doneSessionId = null, string? glyph = null, TimeSpan? duration = null, Brush? accent = null)
    {
        if (_allowancePill || _cleanedUp || _isGreetingActive || _isAnimating || _isExpanded || _isMusicExpanded || !CanShowClaudeAttention) return false;

        _isClaudePeek = false;
        _isCopiedPillShown = false;
        _askRequest = null;
        bool replacing = _isClaudeNotificationVisible &&
            (ClaudeNotificationTitle.Text != title || ClaudeNotificationStatus.Text != status ||
             NotificationAppIcon.Source != appIcon || _isClaudePill != isClaude || NotificationBellIcon.Text != (glyph ?? NotificationBellGlyph));
        if (replacing) KeepCompactDeparture(ClaudeNotification);
        ClaudeNotificationTitle.Text = title;
        ClaudeNotificationTitle.Foreground = accent ?? UiPalette.PrimaryBrush;
        NotificationBellIcon.Foreground = accent ?? UiPalette.PrimaryBrush;
        ClaudeNotificationStatus.Text = status;
        NotificationClaudeIcon.Visibility = isClaude ? Visibility.Visible : Visibility.Collapsed;
        NotificationAppIcon.Source = appIcon;
        NotificationAppIcon.Visibility = !isClaude && appIcon != null ? Visibility.Visible : Visibility.Collapsed;
        NotificationBellIcon.Text = glyph ?? NotificationBellGlyph;
        NotificationBellIcon.Visibility = !isClaude && appIcon == null ? Visibility.Visible : Visibility.Collapsed;
        _notificationTap = tap;
        _isClaudePill = isClaude;
        if (!_isClaudeNotificationVisible)
        {
            if (!TryAcquireCompactSlot(CompactPillSlot.Claude, out int token)) return false;
            _claudeNotificationToken = token;
            _isClaudeNotificationVisible = true;
            AnimateClaudeNotificationIn();
        }
        else if (replacing) AnimateClaudeNotificationIn();
        _claudeDoneShownId = doneSessionId;
        FitNotchToPill();

        _claudeNotificationTimer ??= CreateClaudeNotificationTimer();
        _claudeNotificationTimer.Stop();
        _claudeNotificationTimer.Interval = duration ?? (isClaude ? ClaudeNotificationDuration : AppNotificationDuration);
        _claudeNotificationTimer.Start();
        return true;
    }

    private const double PillMaxWidth = 460;
    private VNotch.Presenters.CompactSurfaceAttachment? _claudePillAttachment;

    /// <summary>Widens the closed notch until name and status read in full; it narrows again with the pill.</summary>
    private void FitNotchToPill()
    {
        NotchPillRow.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var margin = ClaudeNotification.Margin;
        double wanted = Math.Ceiling(NotchPillRow.DesiredSize.Width + margin.Left + margin.Right + 28);
        double target = Math.Clamp(wanted, _collapsedWidth + 28, Math.Max(PillMaxWidth, _collapsedWidth + 28));
        _notchShellPresenter?.CancelCurrentAnimation();
        _claudePillAttachment ??= new(NotchBorder, ClaudeNotification, () => new Size(_collapsedWidth, _collapsedHeight));
        _claudePillAttachment.MoveTo(target, _collapsedHeight, Motion.Pop, shown: true);
    }

    private DispatcherTimer CreateClaudeNotificationTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = ClaudeNotificationDuration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_allowancePill && ClaudeNotification.IsMouseOver)
            {
                _allowancePillPaused = true;
                return;
            }
            AnimateClaudeNotificationOut();
        };
        return timer;
    }

    private void AnimateClaudeNotificationIn()
    {
        SuppressPrivacyDot();

        HideCompactSurface(CollapsedContent);
        if (MusicCompactContent.Visibility == Visibility.Visible)
            HideCompactSurface(MusicCompactContent);

        ClaudeNotification.Visibility = Visibility.Visible;
        Motion.Stop(ClaudeNotification, OpacityProperty, 1);
        Motion.Stop(ClaudeNotificationTranslate, TranslateTransform.YProperty, 0);

        Motion.To(ClaudeNotificationIconScale, ScaleTransform.ScaleXProperty, 1, Motion.TileScale, from: Motion.TileFromScale);
        Motion.To(ClaudeNotificationIconScale, ScaleTransform.ScaleYProperty, 1, Motion.TileScale, from: Motion.TileFromScale);
    }

    private void AnimateClaudeNotificationOut()
    {
        if (!_isClaudeNotificationVisible) return;
        int token = _claudeNotificationToken;
        _isClaudePeek = false;
        _isCopiedPillShown = false;
        _askRequest = null;
        _claudeDoneShownId = null;
        _claudeNotificationTimer?.Stop();
        _notchShellPresenter?.CancelCurrentAnimation();
        _claudePillAttachment ??= new(NotchBorder, ClaudeNotification, () => new Size(_collapsedWidth, _collapsedHeight));
        _claudePillAttachment.MoveTo(_collapsedWidth, _collapsedHeight, Motion.PopBack, shown: false, done: () =>
        {
            if (token != _claudeNotificationToken || IsCompactSlotStale(token)) return;

            ClaudeNotification.Visibility = Visibility.Collapsed;
            _isClaudeNotificationVisible = false;
            if (_allowancePill) SetAllowancePill(null);
            _compactPillArbiter.Release(token);
            RestoreCompactMediaPresentation();
            _claudeNotificationToken = 0;
            RestorePrivacyDotVisibility();

            FrameworkElement content = _isMusicCompactMode && _currentMediaInfo != null ? MusicCompactContent : CollapsedContent;
            content.Visibility = Visibility.Visible;
            Motion.To(content, OpacityProperty, 1, Motion.Tile);
            // A limit warning that waited for this pill can come next, without waiting for the 15 s sweep.
            Dispatcher.BeginInvoke(DispatcherPriority.Background, CheckAllowanceAlerts);
        });
    }

    /// <summary>The notch opens or another alert takes the pill: drop it without an exit animation.</summary>
    private void CancelClaudeNotificationImmediate()
    {
        _claudeNotificationTimer?.Stop();
        _claudeDoneShownId = null;
        _isCopiedPillShown = false;
        _askRequest = null;
        if (_allowancePill) SetAllowancePill(null);
        if (!_isClaudeNotificationVisible) return;
        _isClaudeNotificationVisible = false;
        _claudeNotificationToken = 0;
        _claudePillAttachment?.Detach(false);
        Motion.Stop(ClaudeNotification, OpacityProperty, 0);
        Motion.Stop(ClaudeNotificationTranslate, TranslateTransform.YProperty, 0);
        Motion.Stop(ClaudeNotificationIconScale, ScaleTransform.ScaleXProperty, 1);
        Motion.Stop(ClaudeNotificationIconScale, ScaleTransform.ScaleYProperty, 1);
        ClaudeNotification.Visibility = Visibility.Collapsed;
    }

    #endregion
}
