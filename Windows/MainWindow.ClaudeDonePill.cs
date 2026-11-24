using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using VNotch.Controllers;
using VNotch.Services;
using VNotch.Services.ClaudeCode;

namespace VNotch;

public partial class MainWindow
{
    #region Claude done pill

    // A finished Claude turn stays in the closed notch for half a minute, unless it is tapped (that goes to the
    // session), pushed away with a right-click, the session gets a new prompt or you look at the session yourself.
    // It never shows while you are already in that session. Other alerts may pass it; it comes back afterwards.
    private static readonly TimeSpan ClaudeDoneReturnCheck = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ClaudeDoneLifetime = TimeSpan.FromSeconds(30);

    private sealed class ClaudeDoneNote
    {
        public ClaudeDoneNote(string sessionId, string name, string? title, IntPtr window)
        {
            SessionId = sessionId;
            Name = name;
            Title = title;
            Window = window;
        }

        public string SessionId { get; }
        public string Name { get; }
        public string? Title { get; }
        // The session's terminal or app window, zero when it has none.
        public IntPtr Window { get; }
        public DateTime Until { get; } = DateTime.UtcNow + ClaudeDoneLifetime;
    }

    // Oldest first; the newest is the one on screen.
    private readonly List<ClaudeDoneNote> _claudeDone = new();
    private DispatcherTimer? _claudeDoneReturnTimer;
    // The session of the done pill on screen; null while the pill shows something else or nothing.
    private string? _claudeDoneShownId;

    private bool IsClaudeDonePillShown => _isClaudeNotificationVisible && _claudeDoneShownId != null;

    private void RememberClaudeDone(ClaudeSession session)
    {
        string id = session.Id, name = session.Name;
        string? title = session.Title;
        // Finding the window reads Claude Code's session files and attaches to its console: off the UI thread.
        Task.Run(() => ClaudeSessionWindow.FindWindow(id)).ContinueWith(found => Dispatcher.BeginInvoke(() =>
        {
            if (_cleanedUp) return;
            var note = new ClaudeDoneNote(id, name, title, found.IsCompletedSuccessfully ? found.Result : IntPtr.Zero);
            _claudeDone.RemoveAll(n => n.SessionId == id);
            if (ClaudeSessionWindow.IsInFront(note.Window, note.Title))
            {
                HideClaudeDonePillIfGone();
                return;
            }
            _claudeDone.Add(note);
            ShowClaudeDonePill();
            WatchForClaudeDoneReturn();
        }), TaskScheduler.Default);
    }

    private void ShowClaudeDonePill()
    {
        DateTime now = DateTime.UtcNow;
        _claudeDone.RemoveAll(n => n.Until <= now);
        if (_claudeDone.Count == 0) return;
        var note = _claudeDone[^1];
        ShowNotchPill(note.Name, Loc.Get("control.claude.done"), appIcon: null, isClaude: true,
            tap: () => ClaudeSessionWindow.TryActivate(note.SessionId, note.Title), doneSessionId: note.SessionId,
            duration: note.Until - now);
    }

    /// <summary>Turns that ran out of time, or whose session you are looking at, are gone; the pill leaves with them.</summary>
    private void DropClaudeDoneSeen()
    {
        DateTime now = DateTime.UtcNow;
        if (_claudeDone.RemoveAll(n => n.Until <= now || ClaudeSessionWindow.IsInFront(n.Window, n.Title)) > 0)
            HideClaudeDonePillIfGone();
    }

    private void HideClaudeDonePillIfGone()
    {
        if (!IsClaudeDonePillShown || _claudeDone.Exists(n => n.SessionId == _claudeDoneShownId)) return;
        _claudeNotificationTimer?.Stop();
        AnimateClaudeNotificationOut();
    }

    private void ForgetClaudeDone(string sessionId) => _claudeDone.RemoveAll(n => n.SessionId == sessionId);

    /// <summary>
    /// While a finished turn waits, looks once a second whether it ran out or you went to its session, and whether the closed notch is free again (another alert
    /// ended, the notch closed, the fullscreen app left) and brings the pill back.
    /// </summary>
    private void WatchForClaudeDoneReturn()
    {
        if (_claudeDoneReturnTimer == null)
        {
            _claudeDoneReturnTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = ClaudeDoneReturnCheck };
            _claudeDoneReturnTimer.Tick += (_, _) =>
            {
                if (!_cleanedUp) DropClaudeDoneSeen();
                if (_claudeDone.Count == 0 || _cleanedUp)
                {
                    _claudeDoneReturnTimer.Stop();
                    return;
                }
                if (!IsCodexJobPillShown && (_isClaudeNotificationVisible || _compactPillArbiter.ActiveSlot != CompactPillSlot.None)) return;
                ShowClaudeDonePill();
            };
        }
        if (!_claudeDoneReturnTimer.IsEnabled) _claudeDoneReturnTimer.Start();
    }

    /// <summary>A session that got a new prompt, waits for you again or has ended no longer counts as done.</summary>
    private void PruneClaudeDone()
    {
        if (_claudeDone.Count == 0 || _claudeTracker == null) return;
        var sessions = _claudeTracker.GetSessions();
        int removed = _claudeDone.RemoveAll(n =>
            sessions.Find(s => s.Id == n.SessionId) is not { Status: ClaudeSessionStatus.Done });
        if (removed > 0) HideClaudeDonePillIfGone();
    }

    /// <summary>Another alert wants the closed notch: the done pill steps aside and returns once it is free.</summary>
    private void StepClaudePillAside()
    {
        int token = _claudeNotificationToken;
        KeepCompactDeparture(ClaudeNotification);
        CancelClaudeNotificationImmediate();
        _compactPillArbiter.Release(token);
        RestorePrivacyDotVisibility();
    }

    /// <summary>A right-click pushes the done pill away without going to the session.</summary>
    private void NotchWrapper_DismissClaudeDone(object sender, MouseButtonEventArgs e)
    {
        if (!IsClaudeDonePillShown || IsSpeakerDragSwitch) return;
        e.Handled = true;
        ForgetClaudeDone(_claudeDoneShownId!);
        _claudeNotificationTimer?.Stop();
        AnimateClaudeNotificationOut();
    }

    #endregion
}
