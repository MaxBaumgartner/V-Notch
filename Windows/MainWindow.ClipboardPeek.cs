using System;
using System.Windows;
using System.Windows.Media;
using VNotch.Controllers;
using VNotch.Presenters;
using VNotch.Services;

namespace VNotch;

public partial class MainWindow
{
    #region Clipboard Peek

    private bool _isClipboardPeekActive = false;
    private int _clipboardPeekToken = 0;
    private UiScheduledTimer? _clipboardRevertTimer;
    private CompactSurfaceAttachment _clipboardCopiedAttachment = null!;

    private void PlayClipboardPeek()
    {
        if (!_isExpanded && !_isAnimating && AmbientAnimationsAllowed && NotchBorder.IsVisible)
            foreach (var scale in new[] { NotchScale, NotchShadowScale })
            {
                Motion.To(scale, ScaleTransform.ScaleXProperty, 1, Motion.Press, velocity: Motion.VelocityOf(scale, ScaleTransform.ScaleXProperty) + 1);
                Motion.To(scale, ScaleTransform.ScaleYProperty, 1, Motion.Press, velocity: Motion.VelocityOf(scale, ScaleTransform.ScaleYProperty) - 0.8);
            }
        if (_isMusicCompactMode && !_isExpanded) ShowClipboardCopiedState();
        else if (!_isExpanded && !_isAnimating) ShowCopiedPill();
    }
    private void ShowClipboardCopiedState()
    {
        if (_isAnimating || _isExpanded || _isHiddenByFullscreen || !_isMusicCompactMode ||
            !TryAcquireCompactSlot(CompactPillSlot.Clipboard, out int token))
            return;

        _clipboardPeekToken = token;
        _isClipboardPeekActive = true;

        SuppressPrivacyDot();

        ClipboardCopiedText.Text = Loc.Get("clipboard.copied").TrimStart('✓', ' ');

        CancelThumbnailSwitchAnimations();

        if (CompactThumbnailBorder.IsVisible && AmbientAnimationsAllowed)
        {
            Motion.To(CompactThumbnailScale, ScaleTransform.ScaleXProperty, Motion.ExitToScale, Motion.Exit);
            Motion.To(CompactThumbnailScale, ScaleTransform.ScaleYProperty, Motion.ExitToScale, Motion.Exit);
            Motion.To(CompactThumbnailBorder, OpacityProperty, 0, Motion.Exit, done: () =>
            {
                if (_isClipboardPeekActive && !IsCompactSlotStale(token))
                    CompactThumbnailBorder.Visibility = Visibility.Collapsed;
            });
        }
        else CompactThumbnailBorder.Visibility = Visibility.Collapsed;

        Motion.Stop(MusicViz, OpacityProperty, 1);
        MusicViz.Visibility = Visibility.Visible;
        MusicViz.SetCopiedFeedback(true);

        Motion.Stop(CompactHoverInfo, OpacityProperty, 0);
        CompactHoverInfo.Visibility = Visibility.Collapsed;

        HideCompactSurface(ClipboardCheckIcon);

        bool entering = ClipboardCopiedText.Visibility != Visibility.Visible;
        ClipboardCopiedText.Visibility = Visibility.Visible;
        if (ClipboardCopiedText.IsVisible && AmbientAnimationsAllowed)
        {
            Motion.To(ClipboardCopiedText, OpacityProperty, 1, Motion.Tile, from: entering ? 0 : null);
            Motion.To(ClipboardCopiedTranslate, TranslateTransform.XProperty, 0, Motion.Tile, from: entering ? -8 : null);
            _notchShellPresenter?.CancelCurrentAnimation();
            _clipboardCopiedAttachment.MoveTo(_collapsedWidth + 28, _collapsedHeight, Motion.Pop, shown: true);
        }
        else
        {
            Motion.Stop(ClipboardCopiedText, OpacityProperty, 1);
            Motion.Stop(ClipboardCopiedTranslate, TranslateTransform.XProperty, 0);
        }

        StopClipboardRevertTimer();

        int revertToken = _clipboardPeekToken;
        var revertTimer = new UiScheduledTimer
        {
            Interval = TimeSpan.FromMilliseconds(1600)
        };
        _clipboardRevertTimer = revertTimer;
        revertTimer.Tick += (s, e) =>
        {
            revertTimer.Stop();
            revertTimer.Dispose();
            if (!ReferenceEquals(_clipboardRevertTimer, revertTimer) ||
                revertToken != _clipboardPeekToken)
            {
                return;
            }

            _clipboardRevertTimer = null;
            RevertClipboardCopiedState();
        };
        revertTimer.Start();
    }
    private void RevertClipboardCopiedState(bool followGesture = false)
    {
        int token = _clipboardPeekToken;
        if (IsCompactSlotStale(token)) return;
        StopClipboardRevertTimer();
        MusicViz.SetCopiedFeedback(false);
        void Finish()
        {
            if (IsCompactSlotStale(token)) return;
            CancelClipboardPeekImmediate();
            RestorePrivacyDotVisibility();
        }
        if (!ClipboardCopiedText.IsVisible || !AmbientAnimationsAllowed)
        {
            if (!_isAnimating && !_isExpanded && !_isMusicExpanded)
            {
                Motion.Stop(NotchBorder, WidthProperty, _collapsedWidth);
                Motion.Stop(NotchBorder, HeightProperty, _collapsedHeight);
            }
            if (MusicCompactContent.IsVisible) ResetCompactThumbnailRestingState();
            Finish();
            return;
        }
        Motion.To(ClipboardCopiedText, OpacityProperty, 0, Motion.Exit);
        if (!followGesture) Motion.To(ClipboardCopiedTranslate, TranslateTransform.XProperty, -8, Motion.Exit);

        CompactThumbnailBorder.Visibility = Visibility.Visible;
        Motion.To(CompactThumbnailBorder, OpacityProperty, 1, Motion.Tile);
        Motion.To(CompactThumbnailScale, ScaleTransform.ScaleXProperty, 1, Motion.TileScale);
        Motion.To(CompactThumbnailScale, ScaleTransform.ScaleYProperty, 1, Motion.TileScale);

        // Keep the underlying music visible while the label leaves and the notch returns to its resting width.
        _clipboardCopiedAttachment.MoveTo(_collapsedWidth, _collapsedHeight, Motion.PopBack, shown: true, done: Finish);
    }

    private void CancelClipboardPeekImmediate()
    {
        if (!_isClipboardPeekActive) return;

        StopClipboardRevertTimer();
        MusicViz.SetCopiedFeedback(false);
        _isClipboardPeekActive = false;
        int token = _clipboardPeekToken;
        _clipboardPeekToken = 0;
        ++_phoneSendToken;
        if (_copiedGesture != CopiedGesture.None)
        {
            _copiedGesture = CopiedGesture.None;
            if (NotchWrapper.IsMouseCaptured) NotchWrapper.ReleaseMouseCapture();
        }
        _clipboardCopiedAttachment.Detach(true);

        Motion.Stop(CompactThumbnailScale, ScaleTransform.ScaleXProperty, 1);
        Motion.Stop(CompactThumbnailScale, ScaleTransform.ScaleYProperty, 1);
        Motion.Stop(CompactThumbnailBorder, OpacityProperty, 1);

        Motion.Stop(ClipboardCheckIcon, OpacityProperty, 0);
        Motion.Stop(ClipboardCheckScale, ScaleTransform.ScaleXProperty, 1);
        Motion.Stop(ClipboardCheckScale, ScaleTransform.ScaleYProperty, 1);
        ClipboardCheckIcon.Visibility = Visibility.Collapsed;

        Motion.Stop(ClipboardCopiedText, OpacityProperty, 0);
        Motion.Stop(ClipboardCopiedTranslate, TranslateTransform.XProperty, 0);
        ClipboardCopiedText.Visibility = Visibility.Collapsed;
        _compactPillArbiter.Release(token);
    }

    private void RefreshClipboardPeekMotionVisibility()
    {
        if (!_isClipboardPeekActive || (AmbientAnimationsAllowed && MusicCompactContent.IsVisible)) return;
        if (!_isAnimating && _compactPillArbiter.IsActive(CompactPillSlot.Clipboard))
        {
            Motion.Stop(NotchBorder, WidthProperty, _collapsedWidth);
            Motion.Stop(NotchBorder, HeightProperty, _collapsedHeight);
        }
        CancelClipboardPeekImmediate();
        ResetCompactThumbnailRestingState();
    }

    private void StopClipboardRevertTimer()
    {
        var timer = _clipboardRevertTimer;
        _clipboardRevertTimer = null;
        timer?.Stop();
        timer?.Dispose();
    }

    #endregion
}
