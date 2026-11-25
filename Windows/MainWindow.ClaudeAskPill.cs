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
    // follows the finger and names the outcome once far enough. Pulling a question down unfolds its options.
    // A tap opens the Claude tab. A plan to read still opens the tab by itself.
    private const double AskSwipeDistance = 64;
    private const double AskPullDistance = 34;
    private static readonly Brush AskAllowBrush = FrozenBrush(0xA6, 0xC9, 0xA0);
    private static readonly Brush AskDenyBrush = FrozenBrush(0xD9, 0x8A, 0x84);

    private enum AskGesture { None, Pressed, Swipe, Pull, SheetPressed, SheetSwipe }

    private ClaudePermissionRequest? _askRequest;
    private string _askStatus = "";
    private AskGesture _askGesture;
    private Point _askGestureStart;
    private DispatcherTimer? _askSettledTimer;

    private bool IsAskPillShown => _askRequest != null && _isClaudeNotificationVisible;

    /// <summary>The request the pill takes: the newest one waiting, unless it is a plan to read.</summary>
    private ClaudePermissionRequest? NewestAskForPill() =>
        _claudeTracker?.GetPending().Where(r => r.Plan == null).OrderByDescending(r => r.SinceUtc).FirstOrDefault();

    private bool TryShowClaudeAskPill(ClaudePermissionRequest request)
    {
        if (_cleanedUp || _isExpanded || _isAnimating || _isGreetingActive || _isMusicExpanded || !CanShowClaudeAttention) return false;
        string name = _claudeTracker?.GetSessions().FirstOrDefault(s => s.Id == request.SessionId)?.Name ?? "Claude";
        string status = DescribeAsk(request);
        if (!ShowNotchPill(name, status, null, isClaude: true, tap: OpenClaudeTabFromPill)) return false;
        // Stays until it is settled.
        _claudeNotificationTimer?.Stop();
        _askRequest = request;
        _askStatus = status;
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
        if ((_askRequest == null && _askSheetRequest == null) || _claudeTracker == null) return;
        var pending = _claudeTracker.GetPending();
        if (_askSheetRequest != null && !pending.Contains(_askSheetRequest))
        {
            _askSheetRequest = null;
            _askSheetAnswers = null;
        }
        if (_askRequest == null || pending.Contains(_askRequest)) return;
        if (_askGesture != AskGesture.None) EndAskGesture();
        _askRequest = null;
        FoldAskSheet(AdvanceAskPill);
    }

    private void AdvanceAskPill()
    {
        if (_askRequest != null || !_isClaudeNotificationVisible) return;
        if (NewestAskForPill() is { } next && TryShowClaudeAskPill(next)) return;
        AnimateClaudeNotificationOut();
    }

    private bool TryBeginAskGesture(MouseButtonEventArgs e)
    {
        if ((!IsAskPillShown && _askSheet == null) || _isExpanded || _isAnimating || e.ChangedButton != MouseButton.Left) return false;
        _askGesture = _askSheet == null ? AskGesture.Pressed : AskGesture.SheetPressed;
        _askGestureStart = e.GetPosition(this);
        NotchWrapper.CaptureMouse();
        return true;
    }

    private bool HandleAskGestureMove(MouseEventArgs e)
    {
        if (_askGesture == AskGesture.None) return false;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndAskGesture();
            return true;
        }
        MoveAskGesture(e.GetPosition(this) - _askGestureStart);
        return true;
    }

    private void MoveAskGesture(Vector moved)
    {
        if (_askGesture == AskGesture.SheetPressed && moved.Length > CopiedGestureSlop)
            _askGesture = AskGesture.SheetSwipe;
        if (_askGesture == AskGesture.Pressed && moved.Length > CopiedGestureSlop)
        {
            bool question = _askRequest?.Questions is { Count: > 0 };
            if (question && moved.Y > Math.Abs(moved.X)) _askGesture = AskGesture.Pull;
            else if (!question && Math.Abs(moved.X) > Math.Abs(moved.Y)) _askGesture = AskGesture.Swipe;
            else
            {
                EndAskGesture();
                return;
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
            ClaudeNotificationStatus.Foreground = !armed ? DefaultPillStatus : x > 0 ? AskAllowBrush : AskDenyBrush;
        }
        else if (_askGesture == AskGesture.Pull)
        {
            double y = Math.Max(0, moved.Y);
            SetAskOffset(0, Math.Min(y * 0.35, 12));
            CrossFadeText(ClaudeNotificationStatus, y >= AskPullDistance ? Loc.Get("claude.pill.answer") : _askStatus);
        }
    }

    private bool HandleAskGestureUp(MouseButtonEventArgs e)
    {
        if (_askGesture == AskGesture.None) return false;
        CompleteAskGesture(e.GetPosition(this) - _askGestureStart, e.ClickCount);
        return true;
    }

    private void CompleteAskGesture(Vector moved, int clickCount)
    {
        var gesture = _askGesture;
        var request = _askRequest;
        EndAskGesture();

        if (gesture is AskGesture.Pressed or AskGesture.SheetPressed)
        {
            ToggleNotchFromClick(clickCount);
            return;
        }
        if (gesture == AskGesture.SheetSwipe && IsAskSheetPullUp(moved)) FoldAskSheet();
        if (request == null || _claudeTracker == null) return;
        if (gesture == AskGesture.Swipe && Math.Abs(moved.X) >= AskSwipeDistance)
        {
            bool allow = moved.X > 0;
            if (_claudeTracker.Decide(request.Id, allow ? ClaudeDecision.Allow : ClaudeDecision.Deny))
                PlayAskSettled(allow);
        }
        else if (gesture == AskGesture.Pull && moved.Y >= AskPullDistance) OpenAskSheet(request);
    }

    private void EndAskGesture()
    {
        _askGesture = AskGesture.None;
        if (NotchWrapper.IsMouseCaptured) NotchWrapper.ReleaseMouseCapture();
        if (_askSheet == null)
        {
            CrossFadeText(ClaudeNotificationStatus, _askStatus);
            ClaudeNotificationStatus.Foreground = DefaultPillStatus;
            SpringAskBack();
        }
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
        LingerAskSettled();
    }

    private void LingerAskSettled()
    {
        _askSettledTimer ??= CreateAskSettledTimer();
        _askSettledTimer.Start();
    }

    private DispatcherTimer CreateAskSettledTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(900) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            AdvanceAskPill();
        };
        return timer;
    }

    #endregion
}
