using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.ClaudeCode;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

public partial class MainWindow
{
    #region "Claude waits": decide in the closed notch

    // When Claude Code asks (a tool to allow, or a question), the closed notch shows the session's pill and keeps
    // it until it is settled, here or in the terminal. Swiped right it allows, swiped left it denies: the pill
    // follows the finger and names the outcome once far enough. A question is pulled down to answer it in the
    // Claude tab, where its options are. A tap opens that tab too. A plan to read still opens the tab by itself.
    private const double AskSwipeDistance = 64;
    private const double AskPullDistance = 34;
    private static readonly Brush AskAllowBrush = FrozenBrush(0xA6, 0xC9, 0xA0);
    private static readonly Brush AskDenyBrush = FrozenBrush(0xD9, 0x8A, 0x84);

    private enum AskGesture { None, Pressed, Swipe, Pull }

    private ClaudePermissionRequest? _askRequest;
    private string _askStatus = "";
    private Brush? _askStatusBrush;
    private AskGesture _askGesture;
    private Point _askGestureStart;

    private bool IsAskPillShown => _askRequest != null && _isClaudeNotificationVisible;

    /// <summary>The request the pill takes: the newest one waiting, unless it is a plan to read.</summary>
    private ClaudePermissionRequest? NewestAskForPill() =>
        _claudeTracker?.GetPending().Where(r => r.Plan == null).OrderByDescending(r => r.SinceUtc).FirstOrDefault();

    private bool TryShowClaudeAskPill(ClaudePermissionRequest request)
    {
        if (_cleanedUp || _isExpanded || _isAnimating || _isGreetingActive || _isMusicExpanded || !CanShowClaudeAttention) return false;
        string name = _claudeTracker?.GetSessions().FirstOrDefault(s => s.Id == request.SessionId)?.Name ?? "Claude";
        string status = DescribeAsk(request);
        ShowNotchPill(name, status, null, isClaude: true, tap: OpenClaudeTabFromPill);
        if (!_isClaudeNotificationVisible) return false;
        // Stays until it is settled.
        _claudeNotificationTimer?.Stop();
        _askRequest = request;
        _askStatus = status;
        _askStatusBrush ??= ClaudeNotificationStatus.Foreground;
        return true;
    }

    private static string DescribeAsk(ClaudePermissionRequest request)
    {
        if (request.Questions is { Count: > 0 } questions) return questions[0].Text;
        return request.Detail is { Length: > 0 } detail ? $"{request.Tool} · {detail}" : request.Tool;
    }

    private bool OpenClaudeTabFromPill()
    {
        _transitionCoordinator.RequestView(NotchView.Claude, "ClaudeAskPill");
        return true;
    }

    /// <summary>Settled elsewhere (terminal, the tab): the pill goes, or moves on to the next one waiting.</summary>
    private void SyncClaudeAskPill()
    {
        if (_askRequest == null || _claudeTracker == null) return;
        if (_claudeTracker.GetPending().Any(r => r.Id == _askRequest.Id)) return;
        _askRequest = null;
        if (!_isClaudeNotificationVisible || _askGesture != AskGesture.None) return;
        if (NewestAskForPill() is { } next && TryShowClaudeAskPill(next)) return;
        AnimateClaudeNotificationOut();
    }

    private bool TryBeginAskGesture(MouseButtonEventArgs e)
    {
        if (!IsAskPillShown || _isExpanded || _isAnimating || e.ChangedButton != MouseButton.Left) return false;
        _askGesture = AskGesture.Pressed;
        _askGestureStart = e.GetPosition(this);
        NotchWrapper.CaptureMouse();
        return true;
    }

    private bool HandleAskGestureMove(MouseEventArgs e)
    {
        if (_askGesture == AskGesture.None) return false;
        if (e.LeftButton != MouseButtonState.Pressed || _askRequest == null)
        {
            EndAskGesture();
            return true;
        }
        Vector moved = e.GetPosition(this) - _askGestureStart;
        if (_askGesture == AskGesture.Pressed && moved.Length > CopiedGestureSlop)
        {
            bool question = _askRequest.Questions is { Count: > 0 };
            if (question && moved.Y > Math.Abs(moved.X)) _askGesture = AskGesture.Pull;
            else if (!question && Math.Abs(moved.X) > Math.Abs(moved.Y)) _askGesture = AskGesture.Swipe;
            else
            {
                EndAskGesture();
                return true;
            }
        }

        if (_askGesture == AskGesture.Swipe)
        {
            double x = moved.X;
            double reach = Math.Abs(x);
            double follow = reach < AskSwipeDistance ? reach * 0.6 : AskSwipeDistance * 0.6 + (reach - AskSwipeDistance) * 0.2;
            SetAskOffset(Math.Sign(x) * follow, 0);
            bool armed = reach >= AskSwipeDistance;
            CrossFadeText(ClaudeNotificationStatus, !armed ? _askStatus : Loc.Get(x > 0 ? "control.claude.allow" : "control.claude.deny"));
            ClaudeNotificationStatus.Foreground = !armed ? _askStatusBrush : x > 0 ? AskAllowBrush : AskDenyBrush;
        }
        else if (_askGesture == AskGesture.Pull)
        {
            double y = Math.Max(0, moved.Y);
            SetAskOffset(0, Math.Min(y * 0.35, 12));
            CrossFadeText(ClaudeNotificationStatus, y >= AskPullDistance ? Loc.Get("claude.pill.answer") : _askStatus);
        }
        return true;
    }

    private bool HandleAskGestureUp(MouseButtonEventArgs e)
    {
        if (_askGesture == AskGesture.None) return false;
        Vector moved = e.GetPosition(this) - _askGestureStart;
        var gesture = _askGesture;
        var request = _askRequest;
        EndAskGesture();
        if (request == null || _claudeTracker == null) return true;

        if (gesture == AskGesture.Pressed)
        {
            ToggleNotchFromClick(e.ClickCount);
            return true;
        }
        if (gesture == AskGesture.Swipe && Math.Abs(moved.X) >= AskSwipeDistance)
        {
            bool allow = moved.X > 0;
            if (_claudeTracker.Decide(request.Id, allow ? ClaudeDecision.Allow : ClaudeDecision.Deny))
                PlayAskSettled(allow);
            return true;
        }
        if (gesture == AskGesture.Pull && moved.Y >= AskPullDistance)
        {
            OpenClaudeTabFromPill();
            return true;
        }
        SpringAskBack();
        return true;
    }

    private void EndAskGesture()
    {
        _askGesture = AskGesture.None;
        if (NotchWrapper.IsMouseCaptured) NotchWrapper.ReleaseMouseCapture();
        CrossFadeText(ClaudeNotificationStatus, _askStatus);
        if (_askStatusBrush != null) ClaudeNotificationStatus.Foreground = _askStatusBrush;
        SpringAskBack();
    }

    private void SetAskOffset(double x, double y)
    {
        Motion.Stop(ClaudeNotificationTranslate, TranslateTransform.XProperty, x);
        Motion.Stop(ClaudeNotificationTranslate, TranslateTransform.YProperty, y);
    }

    private void SpringAskBack()
    {
        Motion.To(ClaudeNotificationTranslate, TranslateTransform.XProperty, 0, Motion.Press);
        Motion.To(ClaudeNotificationTranslate, TranslateTransform.YProperty, 0, Motion.Press);
    }

    /// <summary>Decided: the pill names it in the answer's colour, carries on a little that way, then goes.</summary>
    private void PlayAskSettled(bool allowed)
    {
        _askRequest = null;
        CrossFadeText(ClaudeNotificationStatus, Loc.Get(allowed ? "claude.pill.allowed" : "claude.pill.denied"));
        ClaudeNotificationStatus.Foreground = allowed ? AskAllowBrush : AskDenyBrush;
        // A small directional impulse settles at home and carries an interrupted swipe's speed.
        Motion.To(ClaudeNotificationTranslate, TranslateTransform.XProperty, 0, Motion.Press,
            velocity: Motion.VelocityOf(ClaudeNotificationTranslate, TranslateTransform.XProperty) + (allowed ? 1 : -1) * 220);
        // Read it, then the next one waiting or the pill goes.
        var linger = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(900) };
        linger.Tick += (_, _) =>
        {
            linger.Stop();
            if (_askStatusBrush != null) ClaudeNotificationStatus.Foreground = _askStatusBrush;
            if (_askRequest != null || !_isClaudeNotificationVisible) return;
            if (NewestAskForPill() is { } next && TryShowClaudeAskPill(next)) return;
            AnimateClaudeNotificationOut();
        };
        linger.Start();
    }

    #endregion
}
