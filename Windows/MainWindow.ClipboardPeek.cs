using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

public partial class MainWindow
{
    #region Clipboard Peek

    private bool _isClipboardPeekActive = false;
    private int _clipboardPeekToken = 0;
    private UiScheduledTimer? _clipboardRevertTimer;

    private void PlayClipboardPeek()
    {
        if (!_isExpanded && !_isAnimating && !AnimationConfig.ReduceMotion)
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
        if (!TryAcquireCompactSlot(VNotch.Controllers.CompactPillSlot.Clipboard, out int token))
            return;

        _clipboardPeekToken = token;
        _isClipboardPeekActive = true;

        SuppressPrivacyDot();

        ClipboardCopiedText.Text = Loc.Get("clipboard.copied").TrimStart('✓', ' ');

        CancelThumbnailSwitchAnimations();

        Motion.To(CompactThumbnailScale, ScaleTransform.ScaleXProperty, Motion.ExitToScale, Motion.Exit);
        Motion.To(CompactThumbnailScale, ScaleTransform.ScaleYProperty, Motion.ExitToScale, Motion.Exit);
        Motion.To(CompactThumbnailBorder, OpacityProperty, 0, Motion.Exit, done: () =>
        {
            if (_isClipboardPeekActive && !IsCompactSlotStale(token))
                CompactThumbnailBorder.Visibility = Visibility.Collapsed;
        });

        MusicViz.BeginAnimation(OpacityProperty, null);
        MusicViz.Opacity = 1;
        MusicViz.Visibility = Visibility.Visible;
        MusicViz.SetCopiedFeedback(true);

        CompactHoverInfo.BeginAnimation(OpacityProperty, null);
        CompactHoverInfo.Opacity = 0;
        CompactHoverInfo.Visibility = Visibility.Collapsed;

        HideCompactSurface(ClipboardCheckIcon);

        bool entering = ClipboardCopiedText.Visibility != Visibility.Visible;
        ClipboardCopiedText.Visibility = Visibility.Visible;
        Motion.To(ClipboardCopiedText, OpacityProperty, 1, Motion.Tile, from: entering ? 0 : null, delay: Motion.Stagger(1));
        Motion.To(ClipboardCopiedTranslate, TranslateTransform.XProperty, 0, Motion.Tile, from: entering ? -8 : null, delay: Motion.Stagger(1));

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
    private void RevertClipboardCopiedState()
    {
        int token = _clipboardPeekToken;
        if (IsCompactSlotStale(token)) return;
        _isClipboardPeekActive = false;
        _compactPillArbiter.Release(token);
        _clipboardPeekToken = 0;

        RestorePrivacyDotVisibility();

        MusicViz.SetCopiedFeedback(false);
        HideCompactSurface(ClipboardCheckIcon);

        Motion.To(ClipboardCopiedText, OpacityProperty, 0, Motion.Exit, done: () =>
        {
            if (!_isClipboardPeekActive)
                ClipboardCopiedText.Visibility = Visibility.Collapsed;
        });
        Motion.To(ClipboardCopiedTranslate, TranslateTransform.XProperty, -8, Motion.Exit);

        CompactThumbnailBorder.Visibility = Visibility.Visible;
        Motion.To(CompactThumbnailBorder, OpacityProperty, 1, Motion.Tile);
        Motion.To(CompactThumbnailScale, ScaleTransform.ScaleXProperty, 1, Motion.TileScale);
        Motion.To(CompactThumbnailScale, ScaleTransform.ScaleYProperty, 1, Motion.TileScale);

        ShowMusicVisualizer(duration: _dur100);
    }

    private void CancelClipboardPeekImmediate()
    {
        if (!_isClipboardPeekActive) return;

        StopClipboardRevertTimer();
        MusicViz.SetCopiedFeedback(false);
        _isClipboardPeekActive = false;
        _clipboardPeekToken = 0;

        ClipboardCheckIcon.BeginAnimation(OpacityProperty, null);
        ClipboardCheckScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ClipboardCheckScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        ClipboardCheckIcon.Opacity = 0;
        ClipboardCheckIcon.Visibility = Visibility.Collapsed;

        Motion.Stop(ClipboardCopiedText, OpacityProperty, 0);
        Motion.Stop(ClipboardCopiedTranslate, TranslateTransform.XProperty, 0);
        ClipboardCopiedText.Visibility = Visibility.Collapsed;
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
