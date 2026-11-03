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
    // As wide as the controls tab next to it; as tall as its cards, up to where the list scrolls.
    private const double ClaudeViewWidth = 720;
    private const double ClaudeViewMinHeight = 120;
    private const double ClaudeViewMaxHeight = 420;

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
    private int _claudeNotchHeightGeneration;

    private void InitializeControlCenter()
    {
        _claudeTracker = new ClaudeSessionTracker();
        _claudeHookServer = new ClaudeHookServer(_claudeTracker);
        _claudeUsage = new ClaudeUsageClient();
        _claudeHookServer.Start();

        // Settings load after this runs, and anything that pumps messages meanwhile can let the views build:
        // until then they are plain-skinned and offline.
        ControlCenter.Attach(() => _settings != null && IsLiquidGlassEnabled);
        // The mixer owns switching outputs (write queue, master volume, media volume cache).
        ControlCenter.OutputDeviceSelected += AudioContent.SelectDefaultOutputDevice;
        ClaudeCenter.AttachClaude(_claudeTracker, _claudeHookServer, _claudeUsage,
            () => _settings?.EnableLocalOnlyMode ?? true, () => _settings != null && IsLiquidGlassEnabled);
        ClaudeCenter.AttentionRequested += OpenClaudeTabForAttention;
        ClaudeCenter.ContentHeightChanged += OnClaudeContentHeightChanged;
        ClaudeCenter.SessionOpened += OnClaudeSessionOpened;
        ClaudeCenter.KeyboardInputRequested += OnClaudeKeyboardInputRequested;
        ClaudeCenter.ScreenshotRequested += OnClaudeScreenshotRequested;
        _claudeTracker.Changed += OnClaudeSessionsChanged;
        _claudeTracker.TurnFinished += OnClaudeTurnFinished;
        _claudeTracker.PermissionRequested += OnClaudePermissionRequestedSound;
        ClaudeCenter.AttachSoundSwitch(() => _settings?.PlayClaudeSounds ?? false, SetClaudeSounds);

        _claudeSweepTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = ClaudeSweepInterval };
        _claudeSweepTimer.Tick += (_, _) => _claudeTracker?.Sweep();
        _claudeSweepTimer.Start();
        // Asking Windows about notification access is a cross-apartment call that pumps messages: after startup.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, InitializeAppNotifications);
        InitializePi();
        InitializeWeather();
        InitializeLegion();
        InitializeMicMuted();
    }

    private void DisposeControlCenter()
    {
        _claudeSweepTimer?.Stop();
        _claudeNotificationTimer?.Stop();
        _claudeAttentionRetry?.Stop();
        DisposeAppNotifications();
        DisposePi();
        DisposeWeather();
        DisposeLegion();
        DisposeMicMuted();
        ControlCenter.OutputDeviceSelected -= AudioContent.SelectDefaultOutputDevice;
        ClaudeCenter.AttentionRequested -= OpenClaudeTabForAttention;
        ClaudeCenter.ContentHeightChanged -= OnClaudeContentHeightChanged;
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
        if (_cleanedUp || _isClaudeView) return;
        if (_isAnimating || _isGreetingActive || _isExpanded || _isMusicExpanded || !CanShowClaudeAttention)
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
        ClaudeWaitingBadgeScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ClaudeWaitingBadgeScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        ClaudeWaitingBadge.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show || AnimationConfig.ReduceMotion) return;
        var pop = MakeAnim(0d, 1d, _dur400, _easeSpring);
        ClaudeWaitingBadgeScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        ClaudeWaitingBadgeScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
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

    /// <summary>The notch height that fits the Claude page, between a header-only page and a scrolling list.</summary>
    private double MeasureClaudeViewHeight()
    {
        var margin = ClaudeContent.Margin;
        double content = ClaudeCenter.MeasureContentHeight(ClaudeViewWidth - margin.Left - margin.Right);
        return Math.Clamp(Math.Ceiling(content + margin.Top + margin.Bottom), ClaudeViewMinHeight, ClaudeViewMaxHeight);
    }

    private void OnClaudeContentHeightChanged() =>
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, SettleClaudeNotchToFit);

    /// <summary>Grows or shrinks the open tab when sessions or requests come and go, like the mixer with its devices.</summary>
    private void SettleClaudeNotchToFit()
    {
        if (!_isClaudeView || _isAnimating || _cleanedUp) return;
        double target = MeasureClaudeViewHeight();
        double current = NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight : target;
        double chrome = ClaudeContent.Margin.Top + ClaudeContent.Margin.Bottom;
        _claudeViewHeight = target;
        if (Math.Abs(current - target) < 0.5) return;

        int generation = ++_claudeNotchHeightGeneration;
        bool growing = target > current;
        if (growing) ResizeHostWindowHeight(target);

        double currentContent = ClaudeContent.ActualHeight > 0 ? ClaudeContent.ActualHeight : Math.Max(0, current - chrome);
        NotchBorder.BeginAnimation(HeightProperty, null);
        ClaudeContent.BeginAnimation(HeightProperty, null);
        NotchBorder.Height = current;
        ClaudeContent.Height = currentContent;
        _isAnimating = true;

        var duration = new Duration(TimeSpan.FromMilliseconds(320));
        var notchAnim = MakeAnim(current, target, duration, _easeExpOut6);
        var contentAnim = MakeAnim(currentContent, Math.Max(0, target - chrome), duration, _easeExpOut6);
        Timeline.SetDesiredFrameRate(notchAnim, AnimationConfig.TargetFps);
        Timeline.SetDesiredFrameRate(contentAnim, AnimationConfig.TargetFps);
        notchAnim.Completed += (_, _) =>
        {
            if (generation != _claudeNotchHeightGeneration) return;
            _isAnimating = false;
            if (!_isClaudeView) return;
            NotchBorder.BeginAnimation(HeightProperty, null);
            NotchBorder.Height = target;
            ClaudeContent.BeginAnimation(HeightProperty, null);
            ClaudeContent.Height = Math.Max(0, target - chrome);
            if (!growing) ResizeHostWindowHeight(target);
        };
        NotchBorder.BeginAnimation(HeightProperty, notchAnim, HandoffBehavior.SnapshotAndReplace);
        ClaudeContent.BeginAnimation(HeightProperty, contentAnim, HandoffBehavior.SnapshotAndReplace);
    }

    #endregion

    #region Claude notification (compact pill)

    private static readonly TimeSpan ClaudeNotificationDuration = TimeSpan.FromSeconds(4);

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

    private void OnClaudeTurnFinished(ClaudeSession session) => Dispatcher.BeginInvoke(() =>
    {
        PlayClaudeSound(ClaudeSound.Done);
        ShowNotchPill(session.Name, Loc.Get("control.claude.done"), appIcon: null, isClaude: true,
            tap: () => ClaudeSessionWindow.TryActivate(session.Id, session.Title));
    });

    /// <summary>A tap on the pill goes straight to the session's terminal or the app, not into the notch.</summary>
    private bool TryHandleNotificationTap()
    {
        if (!_isClaudeNotificationVisible || _notificationTap is not { } tap || !tap()) return false;
        _claudeNotificationTimer?.Stop();
        AnimateClaudeNotificationOut();
        return true;
    }

    /// <summary>
    /// "name · status" in the closed notch, like a Bluetooth device connecting: Claude's star for a finished
    /// turn, the app's logo for a Windows notification. Never over a fullscreen app.
    /// </summary>
    private void ShowNotchPill(string title, string status, ImageSource? appIcon, bool isClaude, Func<bool>? tap)
    {
        if (_cleanedUp || _isGreetingActive || _isAnimating || _isExpanded || _isMusicExpanded || !CanShowClaudeAttention) return;

        _isClaudePeek = false;
        ClaudeNotificationTitle.Text = title;
        ClaudeNotificationStatus.Text = status;
        NotificationClaudeIcon.Visibility = isClaude ? Visibility.Visible : Visibility.Collapsed;
        NotificationAppIcon.Source = appIcon;
        NotificationAppIcon.Visibility = !isClaude && appIcon != null ? Visibility.Visible : Visibility.Collapsed;
        NotificationBellIcon.Visibility = !isClaude && appIcon == null ? Visibility.Visible : Visibility.Collapsed;
        _notificationTap = tap;
        _isClaudePill = isClaude;
        if (!_isClaudeNotificationVisible)
        {
            if (!TryAcquireCompactSlot(CompactPillSlot.Claude, out int token)) return;
            _claudeNotificationToken = token;
            _isClaudeNotificationVisible = true;
            AnimateClaudeNotificationIn();
        }
        FitNotchToPill();

        _claudeNotificationTimer ??= CreateClaudeNotificationTimer();
        _claudeNotificationTimer.Stop();
        _claudeNotificationTimer.Interval = ClaudeNotificationDuration;
        _claudeNotificationTimer.Start();
    }

    private const double PillMaxWidth = 460;

    /// <summary>Widens the closed notch until name and status read in full; it narrows again with the pill.</summary>
    private void FitNotchToPill()
    {
        NotchPillRow.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var margin = ClaudeNotification.Margin;
        double wanted = Math.Ceiling(NotchPillRow.DesiredSize.Width + margin.Left + margin.Right + 28);
        double target = Math.Clamp(wanted, _collapsedWidth, PillMaxWidth);
        double current = NotchBorder.ActualWidth > 0 ? NotchBorder.ActualWidth : _collapsedWidth;
        if (Math.Abs(target - current) < 1) return;
        AnimateCompactWidth(target, TimeSpan.FromMilliseconds(450), _easeSoftSpring, _claudeNotificationToken);
    }

    private DispatcherTimer CreateClaudeNotificationTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = ClaudeNotificationDuration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            AnimateClaudeNotificationOut();
        };
        return timer;
    }

    private void AnimateClaudeNotificationIn()
    {
        SuppressPrivacyDot();
        PlayBluetoothBounce();

        CollapsedContent.BeginAnimation(OpacityProperty, null);
        CollapsedContent.Opacity = 0;
        CollapsedContent.Visibility = Visibility.Collapsed;
        if (MusicCompactContent.Visibility == Visibility.Visible)
        {
            MusicCompactContent.BeginAnimation(OpacityProperty, null);
            MusicCompactContent.Opacity = 0;
            MusicCompactContent.Visibility = Visibility.Collapsed;
        }

        ClaudeNotification.Visibility = Visibility.Visible;
        ClaudeNotification.Opacity = 0;
        ClaudeNotification.BeginAnimation(OpacityProperty, MakeCompactNotificationEntrance(0, 1));
        ClaudeNotificationTranslate.BeginAnimation(
            TranslateTransform.YProperty, MakeCompactNotificationEntrance(6, 0), HandoffBehavior.SnapshotAndReplace);

        var iconScale = MakeAnim(0.8d, 1d, _dur400, _easeSpring, TimeSpan.FromMilliseconds(150));
        ClaudeNotificationIconScale.BeginAnimation(ScaleTransform.ScaleXProperty, iconScale);
        ClaudeNotificationIconScale.BeginAnimation(ScaleTransform.ScaleYProperty, iconScale);
    }

    private void AnimateClaudeNotificationOut()
    {
        if (!_isClaudeNotificationVisible) return;
        int token = _claudeNotificationToken;
        _isClaudePeek = false;
        if (NotchBorder.ActualWidth > _collapsedWidth + 0.5)
            AnimateCompactWidth(_collapsedWidth, TimeSpan.FromMilliseconds(400), _easeExpOut6, token);

        var fadeOut = MakeCompactNotificationExit(1, 0);
        fadeOut.Completed += (_, _) =>
        {
            if (token != _claudeNotificationToken || IsCompactSlotStale(token)) return;

            ClaudeNotification.Visibility = Visibility.Collapsed;
            _isClaudeNotificationVisible = false;
            _compactPillArbiter.Release(token);
            RestoreCompactMediaPresentation();
            _claudeNotificationToken = 0;
            RestorePrivacyDotVisibility();

            FrameworkElement content = _isMusicCompactMode && _currentMediaInfo != null ? MusicCompactContent : CollapsedContent;
            content.Visibility = Visibility.Visible;
            content.BeginAnimation(OpacityProperty, MakeCompactContentRestore());
        };
        ClaudeNotification.BeginAnimation(OpacityProperty, fadeOut);
        ClaudeNotificationTranslate.BeginAnimation(
            TranslateTransform.YProperty, MakeCompactNotificationExit(0, -4), HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>The notch opens or another alert takes the pill: drop it without an exit animation.</summary>
    private void CancelClaudeNotificationImmediate()
    {
        _claudeNotificationTimer?.Stop();
        if (!_isClaudeNotificationVisible) return;
        _isClaudeNotificationVisible = false;
        _claudeNotificationToken = 0;
        ClaudeNotification.BeginAnimation(OpacityProperty, null);
        ClaudeNotificationTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        ClaudeNotification.Opacity = 0;
        ClaudeNotification.Visibility = Visibility.Collapsed;
    }

    #endregion
}
