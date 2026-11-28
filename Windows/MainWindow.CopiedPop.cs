using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.ClaudeCode;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

public partial class MainWindow
{
    #region "Copied" pop: drag it out, or send it to Claude or the phone

    // Copying shows a short pop in the closed notch: "Copied · what it is" (with music the visualizer's own
    // "Copied"). While it is up, the notch takes two gestures. Pulled to the right it follows the finger and,
    // let go past the line, goes to the Claude session that was active last, attached to a new message there.
    // Pulled down or to the left it becomes a real drag: the copied thing can be dropped into any app.
    // Pulled up it sends text to the phone. A plain tap still opens the notch.
    private static readonly TimeSpan CopiedPillDuration = TimeSpan.FromMilliseconds(2200);
    private const string CopiedGlyph = "";
    private const double CopiedGestureSlop = 6;
    private const double CopiedToClaudeDistance = 64;
    // The pointer can only travel to the top of the screen from this small pill.
    private const double CopiedToPhoneDistance = 16;
    private const int CopiedPreviewChars = 28;

    private enum CopiedGesture { None, Pressed, ToClaude, ToPhone, DragOut }

    private bool _isCopiedPillShown;
    private CopiedGesture _copiedGesture;
    private Point _copiedGestureStart;
    private string _copiedPillStatus = "";
    private int _phoneSendToken;
    internal Func<string, Task<bool>>? PhoneSender { get; set; }

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    private bool IsCopiedPopShown => _isCopiedPillShown || _isClipboardPeekActive;

    /// <summary>Without music the copy gets the notification pill; with music the visualizer shows it.</summary>
    private void ShowCopiedPill()
    {
        _phoneSendToken++;
        string status = DescribeClipboard();
        if (!ShowNotchPill(Loc.Get("clipboard.copied").TrimStart('✓', ' '), status, null, isClaude: false, tap: null,
            glyph: CopiedGlyph, duration: CopiedPillDuration)) return;
        _isCopiedPillShown = true;
        _copiedPillStatus = status;
    }

    /// <summary>A short look at what was copied; nothing of it when the source marked it private (passwords).</summary>
    internal static string DescribeClipboard()
    {
        try
        {
            var data = Clipboard.GetDataObject();
            if (data == null) return "";
            if (Controllers.ClipboardHistoryController.ShouldIgnoreClipboardData(data)) return "";
            if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files)
                return files.Length == 1 ? System.IO.Path.GetFileName(files[0]) : Loc.Get("clipboard.fileCount", files.Length);
            if (data.GetDataPresent(DataFormats.Bitmap)) return Loc.Get("clipboard.picture");
            if (data.GetData(DataFormats.UnicodeText) is string text)
            {
                text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                return text.Length > CopiedPreviewChars ? text[..(CopiedPreviewChars - 1)].TrimEnd() + "…" : text;
            }
        }
        catch (Exception ex) when (ex is COMException or ExternalException or OutOfMemoryException) { }
        return "";
    }

    private bool TryBeginCopiedGesture(MouseButtonEventArgs e)
    {
        if (!IsCopiedPopShown || _isExpanded || _isAnimating || e.ChangedButton != MouseButton.Left) return false;
        _phoneSendToken++;
        _copiedGesture = CopiedGesture.Pressed;
        _copiedGestureStart = e.GetPosition(this);
        PauseCopiedPop();
        NotchWrapper.CaptureMouse();
        return true;
    }

    /// <summary>True while the copied pop owns the mouse; the notch's own gestures stay out.</summary>
    private bool HandleCopiedGestureMove(MouseEventArgs e)
    {
        if (_copiedGesture == CopiedGesture.None) return false;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndCopiedGesture(commit: false);
            return true;
        }
        Vector moved = e.GetPosition(this) - _copiedGestureStart;
        if (_copiedGesture == CopiedGesture.Pressed && moved.Length > CopiedGestureSlop)
        {
            if (moved.X > Math.Abs(moved.Y) && LatestClaudeSession() != null) _copiedGesture = CopiedGesture.ToClaude;
            else if (moved.Y < 0 && Math.Abs(moved.Y) >= Math.Abs(moved.X)) _copiedGesture = CopiedGesture.ToPhone;
            else
            {
                _copiedGesture = CopiedGesture.DragOut;
                StartCopiedDragOut();
                return true;
            }
        }
        if (_copiedGesture == CopiedGesture.ToClaude)
        {
            // Follows the finger with some give, and says what letting go will do once it is far enough.
            double x = Math.Max(0, moved.X);
            double follow = x < CopiedToClaudeDistance ? x * 0.6 : CopiedToClaudeDistance * 0.6 + (x - CopiedToClaudeDistance) * 0.2;
            SetCopiedPopOffset(follow);
            bool armed = x >= CopiedToClaudeDistance;
            if (_isCopiedPillShown)
                CrossFadeText(ClaudeNotificationStatus, armed ? Loc.Get("clipboard.toClaude") : _copiedPillStatus);
        }
        if (_copiedGesture == CopiedGesture.ToPhone && CanSendToPhone())
        {
            bool armed = -moved.Y >= CopiedToPhoneDistance;
            CrossFadeText(_isCopiedPillShown ? ClaudeNotificationStatus : ClipboardCopiedText,
                armed ? Loc.Get("clipboard.toPhone") : CopiedOriginalLabel());
        }
        return true;
    }

    private bool HandleCopiedGestureUp(MouseButtonEventArgs e)
    {
        if (_copiedGesture == CopiedGesture.None) return false;
        Vector moved = e.GetPosition(this) - _copiedGestureStart;
        var gesture = _copiedGesture;
        bool toClaude = gesture == CopiedGesture.ToClaude && moved.X >= CopiedToClaudeDistance;
        if (gesture == CopiedGesture.ToPhone && -moved.Y >= CopiedToPhoneDistance && CanSendToPhone())
        {
            _copiedGesture = CopiedGesture.None;
            if (NotchWrapper.IsMouseCaptured) NotchWrapper.ReleaseMouseCapture();
            SendClipboardToPhoneAsync().SafeFireAndForget("CLIPBOARD-PHONE");
            return true;
        }
        EndCopiedGesture(commit: toClaude);
        if (gesture == CopiedGesture.Pressed)
        {
            // Just a tap: what a tap on the notch always does.
            ToggleNotchFromClick(e.ClickCount);
        }
        return true;
    }

    private void EndCopiedGesture(bool commit)
    {
        _copiedGesture = CopiedGesture.None;
        if (NotchWrapper.IsMouseCaptured) NotchWrapper.ReleaseMouseCapture();
        if (commit && SendClipboardToClaude())
        {
            PlayCopiedPopLeaving();
            return;
        }
        if (IsCopiedPopShown) CrossFadeText(_isCopiedPillShown ? ClaudeNotificationStatus : ClipboardCopiedText, CopiedOriginalLabel());
        SpringCopiedPopBack();
        ResumeCopiedPop();
    }

    private bool CanSendToPhone() => PhoneSender != null ||
        (_settings != null && !_settings.EnableLocalOnlyMode && _piClient?.IsConfigured == true);

    private string CopiedOriginalLabel() => _isCopiedPillShown ? _copiedPillStatus : Loc.Get("clipboard.copied").TrimStart('✓', ' ');

    /// <summary>The copied text goes to the phone; private or non-text clips stay here.</summary>
    private async Task SendClipboardToPhoneAsync()
    {
        if (!CanSendToPhone()) { ResumeCopiedPop(); return; }
        string text;
        uint sequence;
        try
        {
            sequence = GetClipboardSequenceNumber();
            var data = Clipboard.GetDataObject();
            if (data == null || Controllers.ClipboardHistoryController.ShouldIgnoreClipboardData(data) ||
                data.GetDataPresent(DataFormats.FileDrop) || data.GetDataPresent(DataFormats.Bitmap) ||
                data.GetData(DataFormats.UnicodeText) is not string value || string.IsNullOrWhiteSpace(value))
            {
                CrossFadeText(_isCopiedPillShown ? ClaudeNotificationStatus : ClipboardCopiedText, CopiedOriginalLabel());
                ResumeCopiedPop();
                return;
            }
            text = value;
        }
        catch (Exception ex) when (ex is COMException or ExternalException or OutOfMemoryException)
        {
            ResumeCopiedPop();
            return;
        }
        int token = ++_phoneSendToken;
        bool sent;
        try { sent = await (PhoneSender?.Invoke(text) ?? _piClient!.SendToPhoneAsync(text)); }
        catch (Exception ex) { RuntimeLog.Warn("CLIPBOARD-PHONE", $"Send failed: {ex.Message}"); sent = false; }
        if (_cleanedUp || token != _phoneSendToken || !IsCopiedPopShown) return;
        if (sequence != GetClipboardSequenceNumber()) { ResumeCopiedPop(); return; }
        CrossFadeText(_isCopiedPillShown ? ClaudeNotificationStatus : ClipboardCopiedText,
            Loc.Get(sent ? "clipboard.sentToPhone" : "clipboard.notSent"));
        ResumeCopiedPop();
    }

    /// <summary>A real OLE drag of what the clipboard holds; Windows runs it until the drop.</summary>
    private void StartCopiedDragOut()
    {
        if (NotchWrapper.IsMouseCaptured) NotchWrapper.ReleaseMouseCapture();
        DragDropEffects result = DragDropEffects.None;
        try
        {
            if (Clipboard.GetDataObject() is { } data)
                result = DragDrop.DoDragDrop(NotchWrapper, data, DragDropEffects.Copy);
        }
        catch (Exception ex) when (ex is COMException or ExternalException or InvalidOperationException)
        {
            RuntimeLog.Warn("CLIPBOARD", $"Drag out failed: {ex.Message}");
        }
        _copiedGesture = CopiedGesture.None;
        if (result != DragDropEffects.None)
        {
            PlayCopiedPopLeaving();
            return;
        }
        SpringCopiedPopBack();
        ResumeCopiedPop();
    }

    private ClaudeSession? LatestClaudeSession() =>
        _claudeTracker?.GetSessions().OrderByDescending(s => s.LastEventUtc).FirstOrDefault();

    private bool SendClipboardToClaude()
    {
        if (LatestClaudeSession() is not { } session) return false;
        if (ClaudeAttachments.FromClipboard() is not { } attachment) return false;
        ClaudeCenter.AttachScreenshot(session.Id, attachment);
        _transitionCoordinator.RequestView(NotchView.Claude, "CopiedToClaude");
        return true;
    }

    private void PauseCopiedPop()
    {
        if (_isCopiedPillShown) _claudeNotificationTimer?.Stop();
        if (_isClipboardPeekActive) _clipboardRevertTimer?.Stop();
    }

    private void ResumeCopiedPop()
    {
        if (_isCopiedPillShown && _claudeNotificationTimer != null)
        {
            _claudeNotificationTimer.Interval = CopiedPillDuration;
            _claudeNotificationTimer.Start();
        }
        if (_isClipboardPeekActive && _clipboardRevertTimer != null) _clipboardRevertTimer.Start();
    }

    private TranslateTransform CopiedPopTransform => _isCopiedPillShown ? ClaudeNotificationTranslate : ClipboardCopiedTranslate;

    private void SetCopiedPopOffset(double x)
    {
        var transform = CopiedPopTransform;
        Motion.Stop(transform, TranslateTransform.XProperty, x);
    }

    private void SpringCopiedPopBack()
    {
        Motion.To(CopiedPopTransform, TranslateTransform.XProperty, 0, Motion.Press);
    }

    /// <summary>Handed over: the pop carries on in the direction it went and the pill closes.</summary>
    private void PlayCopiedPopLeaving()
    {
        var transform = CopiedPopTransform;
        Motion.To(transform, TranslateTransform.XProperty, transform.X + 36, Motion.Exit);
        if (_isCopiedPillShown) AnimateClaudeNotificationOut();
        else if (_isClipboardPeekActive) RevertClipboardCopiedState(followGesture: true);
    }

    #endregion
}
