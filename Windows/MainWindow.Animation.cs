using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using VNotch.Controls;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

public partial class MainWindow
{

    #region Notch Expand/Collapse
    private enum LastExpandedView { Primary, Secondary, Timer, Audio, Camera, Claude, Pi, School, Roblox }
    private LastExpandedView _lastExpandedViewBeforeCollapse = LastExpandedView.Primary;

    private const double ExpandedContentHorizontalInset = 16;
    private const double ExpandedContentVerticalInset = 10;

    private double ExpandedContentRestY => _settings.EnableDynamicIslandMode ? 8.5 : 4;

    private int _mediaWidthAnimationVersion;
    private bool _isMediaWidthAnimating;

    private void CancelMediaWidthAnimation()
    {
        ++_mediaWidthAnimationVersion;
        if (_isMediaWidthAnimating)
        {
            _isMediaWidthAnimating = false;
            UpdateGlassMotionState();
        }
        if (ExpandedContent != null) Motion.Stop(ExpandedContent, WidthProperty);
    }

    private void SyncExpandedMediaWidth(bool animate = true)
    {
        double targetWidth = _shellState.UpdateExpandedWidth(IsNoWidgetMode, _isLyricsActive, _isMusicExpanded);
        double targetContentWidth = targetWidth - ExpandedContentHorizontalInset;
        if (ExpandedContent == null || NotchBorder == null) return;

        bool inMediaView = _isExpanded &&
            !_isTimerView && !_isSecondaryView && !_isAudioView && !_isCameraView &&
            !_isMusicExpanded &&
            _transitionCoordinator.Snapshot.TargetView == VNotch.Models.NotchView.Media;

        if (!inMediaView)
        {
            CancelMediaWidthAnimation();
            ExpandedContent.Width = targetContentWidth;
            return;
        }

        if (_transitionCoordinator.IsTransitionActive || _isAnimating)
        {
            return;
        }

        double currentWidth = NotchBorder.ActualWidth > 0 ? NotchBorder.ActualWidth : NotchBorder.Width;
        if (!double.IsFinite(currentWidth) || currentWidth <= 0) currentWidth = targetWidth;

        double currentContentWidth = ExpandedContent.ActualWidth > 0 ? ExpandedContent.ActualWidth : ExpandedContent.Width;
        if (!double.IsFinite(currentContentWidth) || currentContentWidth <= 0)
            currentContentWidth = currentWidth - ExpandedContentHorizontalInset;

        int version = ++_mediaWidthAnimationVersion;
        if (!animate || AnimationConfig.ReduceMotion ||
            (Math.Abs(currentWidth - targetWidth) <= 0.5 && Math.Abs(currentContentWidth - targetContentWidth) <= 0.5))
        {
            Motion.Stop(NotchBorder, WidthProperty, targetWidth);
            Motion.Stop(ExpandedContent, WidthProperty, targetContentWidth);
            if (_isMediaWidthAnimating)
            {
                _isMediaWidthAnimating = false;
                UpdateGlassMotionState();
            }
            UpdateProgressSectionLayout();
            MediaPlayback?.RefreshMediaMarquee();
            return;
        }

        _isMediaWidthAnimating = true;
        UpdateGlassMotionState();
        if (!double.IsFinite(ExpandedContent.Width)) ExpandedContent.Width = currentContentWidth;
        Motion.To(ExpandedContent, WidthProperty, targetContentWidth, Motion.Glide);
        Motion.To(NotchBorder, WidthProperty, targetWidth, Motion.Glide, done: () =>
        {
            if (version != _mediaWidthAnimationVersion) return;
            Motion.Stop(NotchBorder, WidthProperty, targetWidth);
            Motion.Stop(ExpandedContent, WidthProperty, targetContentWidth);
            _isMediaWidthAnimating = false;
            UpdateGlassMotionState();
            UpdateProgressSectionLayout();
            MediaPlayback?.RefreshMediaMarquee();
        });
    }


    private void RestoreExpandedContentRestLayout()
    {
        if (ExpandedContent == null) return;

        ExpandedContent.BeginAnimation(WidthProperty, null);
        ExpandedContent.BeginAnimation(HeightProperty, null);
        ExpandedContent.HorizontalAlignment = HorizontalAlignment.Center;
        ExpandedContent.VerticalAlignment = VerticalAlignment.Top;
        ExpandedContent.UseLayoutRounding = false;
        CancelMediaWidthAnimation();
        _shellState.UpdateExpandedWidth(IsNoWidgetMode, _isLyricsActive, _isMusicExpanded);
        if (MediaRightColumn != null)
            MediaRightColumn.Visibility = IsNoWidgetMode && !_isLyricsActive ? Visibility.Collapsed : Visibility.Visible;
        ExpandedContent.Width = _expandedWidth - ExpandedContentHorizontalInset;
        ExpandedContent.Height = _expandedHeight - ExpandedContentVerticalInset;

        double restY = ExpandedContentRestY;
        ExpandedContent.RenderTransform = restY != 0 ? new TranslateTransform(0, restY) : null;
    }

    private void PrepareExpandedContentLayoutForReveal()
    {
        if (ExpandedContent == null) return;

        UpdateProgressSectionLayout();
        MediaPlayback.RefreshMediaMarquee();
    }

    private double GetCurrentExpandedContentTranslationY()
    {
        if (ExpandedContent == null) return 0;
        var transform = ExpandedContent.RenderTransform;
        if (transform == null || transform == Transform.Identity) return 0;

        if (transform is TranslateTransform tt)
        {
            return tt.Y;
        }

        if (transform is TransformGroup tg)
        {
            foreach (var child in tg.Children)
            {
                if (child is TranslateTransform ctt)
                {
                    return ctt.Y;
                }
            }
        }

        return 0;
    }

    private bool TryComputeThumbnailExpandTarget(out (double X, double Y) target)
    {
        target = default;
        try
        {
            if (ThumbnailBorder == null || InnerClipBorder == null) return false;
            if (!ThumbnailBorder.IsLoaded || !InnerClipBorder.IsLoaded) return false;
            if (ThumbnailBorder.ActualWidth <= 0 || ThumbnailBorder.ActualHeight <= 0) return false;
            if (InnerClipBorder.ActualWidth <= 0 || InnerClipBorder.ActualHeight <= 0) return false;

            var thumbPos = ThumbnailBorder.TransformToAncestor(InnerClipBorder).Transform(new Point(0, 0));

            ConfigureAnimationThumbnailRestSlot();

            var overlayMargin = AnimationThumbnailBorder.Margin;
            double targetX = thumbPos.X - overlayMargin.Left;

            double currentTranslationY = GetCurrentExpandedContentTranslationY();
            double contentTargetY = ExpandedContentRestY;
            double targetY = (thumbPos.Y - currentTranslationY) + contentTargetY - overlayMargin.Top;

            if (double.IsNaN(targetX) || double.IsInfinity(targetX) ||
                double.IsNaN(targetY) || double.IsInfinity(targetY))
            {
                return false;
            }

            if (Math.Abs(targetX) > 2000 || Math.Abs(targetY) > 2000) return false;

            target = (targetX, targetY);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void ConfigureAnimationThumbnailRestSlot()
    {
        double left = _settings.EnableDynamicIslandMode ? 12 : 8;
        double top = 4;

        if (_settings.EnableDynamicIslandMode)
        {
            const double compactThumbSize = 22;
            top = Math.Max(0, (GetCollapsedHeight() - compactThumbSize) / 2.0);
        }

        AnimationThumbnailBorder.Margin = new Thickness(left, top, 0, 0);
    }

    private Size GetExpandedThumbnailAnimationSize()
    {
        double width = ThumbnailBorder?.ActualWidth > 0 ? ThumbnailBorder.ActualWidth : 102;
        double height = ThumbnailBorder?.ActualHeight > 0 ? ThumbnailBorder.ActualHeight : 102;
        return new Size(width, height);
    }

    private bool TryComputeCompactThumbnailRestOffset(out (double X, double Y) offset)
    {
        offset = default;
        try
        {
            ConfigureAnimationThumbnailRestSlot();

            if (CompactThumbnailBorder == null || InnerClipBorder == null) return false;
            if (!CompactThumbnailBorder.IsLoaded || !InnerClipBorder.IsLoaded) return false;
            if (CompactThumbnailBorder.ActualWidth <= 0 || CompactThumbnailBorder.ActualHeight <= 0) return false;
            if (InnerClipBorder.ActualWidth <= 0 || InnerClipBorder.ActualHeight <= 0) return false;

            Point compactPosition = CompactThumbnailBorder
                .TransformToAncestor(InnerClipBorder)
                .Transform(new Point(0, 0));
            Thickness overlayMargin = AnimationThumbnailBorder.Margin;
            double offsetX = compactPosition.X - overlayMargin.Left;
            double offsetY = compactPosition.Y - overlayMargin.Top;

            if (!double.IsFinite(offsetX) || !double.IsFinite(offsetY)) return false;
            if (Math.Abs(offsetX) > 2000 || Math.Abs(offsetY) > 2000) return false;

            offset = (offsetX, offsetY);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private (double X, double Y)? _cachedCompactThumbnailOffset;
    private bool _cachedCompactIslandMode;
    private double _cachedCompactHeight;
    private double _cachedCompactDpi;

    private void CacheCompactThumbnailRestOffset(object? sender, EventArgs e)
    {
        if (_isAnimating || _isExpanded || _isMusicExpanded ||
            MusicCompactContent.Visibility == Visibility.Collapsed ||
            !MusicCompactContent.IsArrangeValid || !CompactThumbnailBorder.IsArrangeValid) return;
        if (TryComputeCompactThumbnailRestOffset(out var offset))
        {
            _cachedCompactThumbnailOffset = offset;
            _cachedCompactIslandMode = _settings.EnableDynamicIslandMode;
            _cachedCompactHeight = GetCollapsedHeight();
            _cachedCompactDpi = GetGlassDpiScale();
        }
    }

    private (double X, double Y) MeasureCompactThumbnailRestOffset()
    {
        if (_cachedCompactThumbnailOffset.HasValue &&
            _cachedCompactIslandMode == _settings.EnableDynamicIslandMode &&
            Math.Abs(_cachedCompactHeight - GetCollapsedHeight()) < 1e-6 &&
            Math.Abs(_cachedCompactDpi - GetGlassDpiScale()) < 1e-6)
            return _cachedCompactThumbnailOffset.Value;
        return MusicCompactContent.Visibility != Visibility.Collapsed && MusicCompactContent.IsArrangeValid &&
            TryComputeCompactThumbnailRestOffset(out var measuredOffset) ? measuredOffset : (0, 0);
    }

    private bool _isThumbnailExpandAnimating;
    private bool _isThumbnailCollapseAnimating;
    private Action? _pendingThumbnailHandoff;
    private long _thumbnailOverlayVersion;

    private void CancelMediaThumbnailTransition()
    {


        ResetAnimationThumbnailOverlay();
        ThumbnailBorder.BeginAnimation(OpacityProperty, null);
        ThumbnailBorder.Opacity = 1;
        CompactThumbnailBorder.BeginAnimation(OpacityProperty, null);
        CompactThumbnailBorder.Opacity = 1;
    }

    private void ResetAnimationThumbnailOverlay(bool clearSource = true)
    {
        ++_thumbnailOverlayVersion;
        _isThumbnailExpandAnimating = false;
        _isThumbnailCollapseAnimating = false;
        _pendingThumbnailHandoff = null;
        AnimationThumbnailBorder.BeginAnimation(OpacityProperty, null);
        Motion.Stop(AnimationThumbnailBorder, WidthProperty, 22);
        Motion.Stop(AnimationThumbnailBorder, HeightProperty, 22);
        AnimationThumbnailBorder.BeginAnimation(Border.BorderThicknessProperty, null);
        Motion.Stop(AnimationThumbnailTranslate, TranslateTransform.XProperty, 0);
        Motion.Stop(AnimationThumbnailTranslate, TranslateTransform.YProperty, 0);
        AnimationThumbnailClip.BeginAnimation(RectangleGeometry.RectProperty, null);
        Motion.StopClipSize(AnimationThumbnailClip, 22, 22);
        this.BeginAnimation(CurrentThumbnailAnimationRadiusProperty, null);
        CurrentThumbnailAnimationRadius = 6;

        AnimationThumbnailBorder.Visibility = Visibility.Collapsed;
        AnimationThumbnailBorder.Opacity = 0;
        AnimationThumbnailBorder.Width = 22;
        AnimationThumbnailBorder.Height = 22;
        AnimationThumbnailBorder.BorderThickness = new Thickness(0);
        AnimationThumbnailBorder.CornerRadius = new CornerRadius(6);
        if (AnimationThumbnailRim != null)
        {
            AnimationThumbnailRim.CornerRadius = new CornerRadius(6);
            AnimationThumbnailRim.BorderThickness = IsLiquidGlassEnabled ? new Thickness(0.5) : new Thickness(0);
        }
        AnimationThumbnailClip.Rect = new Rect(0, 0, 22, 22);
        AnimationThumbnailClip.RadiusX = 6;
        AnimationThumbnailClip.RadiusY = 6;
        AnimationThumbnailTranslate.X = 0;
        AnimationThumbnailTranslate.Y = 0;

        if (AnimationThumbnailBorder.Effect is DropShadowEffect animShadow)
        {
            animShadow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, null);
            animShadow.BeginAnimation(DropShadowEffect.OpacityProperty, null);
            animShadow.BeginAnimation(DropShadowEffect.ShadowDepthProperty, null);
            animShadow.BlurRadius = 8;
            animShadow.Opacity = 0.5;
            animShadow.ShadowDepth = 1.2;
        }

        if (clearSource)
        {
            AnimationThumbnailImage.Source = null;
        }
    }

    private void HandoffAnimationThumbnailToExpanded()
    {
        SynchronizeMediaPinForTransition();
        if (ThumbnailBorder != null)
        {
            if (!_showingEmptyThumbnail && CompactThumbnail.Source != null && ThumbnailImage != null)
            {
                ThumbnailImage.Source = CompactThumbnail.Source;
                ThumbnailImage.Visibility = Visibility.Visible;
                ThumbnailFallback.Visibility = Visibility.Collapsed;
            }
            else if (ThumbnailImage != null && ThumbnailImage.Source == null)
            {
                ThumbnailImage.Visibility = Visibility.Collapsed;
                ThumbnailFallback.Visibility = Visibility.Visible;
            }
            ThumbnailBorder.BeginAnimation(OpacityProperty, null);
            ThumbnailBorder.Opacity = 1;
            if (ThumbnailScale != null)
            {
                ThumbnailScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                ThumbnailScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                ThumbnailScale.ScaleX = 1;
                ThumbnailScale.ScaleY = 1;
            }
        }

        ResetAnimationThumbnailOverlay(clearSource: false);
    }

    private void HandoffAnimationThumbnailToCompact()
    {
        SynchronizeMediaPinForTransition();
        if (CompactThumbnailBorder != null)
        {
            if (ThumbnailImage.Source != null && CompactThumbnail != null)
            {
                CompactThumbnail.Source = ThumbnailImage.Source;
                CompactThumbnail.Visibility = Visibility.Visible;
            }
            ResetCompactThumbnailRestingState();
            CompactThumbnailBorder.BeginAnimation(OpacityProperty, null);
            CompactThumbnailBorder.Visibility = Visibility.Visible;
            CompactThumbnailBorder.Opacity = 1;
        }

        ResetAnimationThumbnailOverlay(clearSource: false);
    }

    private (double X, double Y)? _cachedThumbnailExpandTarget;
    private double _lastMeasuredTargetExpandedWidth;
    private double _lastMeasuredTargetExpandedHeight;
    private double _lastMeasuredTargetDpi;
    private bool _lastMeasuredTargetIslandMode;



    private void DismissStateBeforeExpand()
    {
        _hoverCollapseTimer.Stop();
        _suppressHoverCollapseUntilUtc = DateTime.UtcNow.AddMilliseconds(800);

        MediaPlayback.CancelRewindForNavigation();
        CancelAppNotificationBannerImmediate();

        if (IsCountdownCompletionVisualActive)
        {
            DismissCountdownCompletion();
        }

        if (_isVolumeIndicatorActive || _isVolumeIndicatorExiting)
        {
            DismissVolumeIndicatorImmediate(animateExit: true);
        }

        CancelChargingGlanceImmediate();
        if (_isBluetoothNotificationVisible)
        {
            _bluetoothController.MarkDismissed();
            BluetoothNotification.BeginAnimation(OpacityProperty, null);
            BluetoothNotification.Opacity = 0;
            BluetoothNotification.Visibility = Visibility.Collapsed;
            BluetoothDisconnectNotification.BeginAnimation(OpacityProperty, null);
            BluetoothDisconnectNotification.Opacity = 0;
            BluetoothDisconnectNotification.Visibility = Visibility.Collapsed;
            _isBluetoothNotificationVisible = false;
        }
        CancelClaudeNotificationImmediate();
        CancelAppPillImmediate();
        _compactPillArbiter.ForceClear();

        if (_isCompactThumbnailHovered)
        {
            _isCompactThumbnailHovered = false;
            _compactThumbnailHoverLeaveTimer.Stop();
        }
        CompactHoverInfo.BeginAnimation(OpacityProperty, null);
        CompactHoverInfo.Opacity = 0;
        CompactHoverInfo.Visibility = Visibility.Collapsed;
        if (!_isThumbnailExpandAnimating && !_isThumbnailCollapseAnimating) ResetAnimationThumbnailOverlay();
        this.BeginAnimation(CurrentCompactThumbnailRadiusProperty, null);
        CurrentCompactThumbnailRadius = 6;
        ResetCompactThumbnailRestingState();
    }

    private void ResetNotchScaleBounce()
    {
        Motion.To(NotchScale, ScaleTransform.ScaleXProperty, 1, Motion.Press);
        Motion.To(NotchScale, ScaleTransform.ScaleYProperty, 1, Motion.Press);
        Motion.To(NotchShadowScale, ScaleTransform.ScaleXProperty, 1, Motion.Press);
        Motion.To(NotchShadowScale, ScaleTransform.ScaleYProperty, 1, Motion.Press);
    }

    private void SetupExpandedContentBeforeAnimation()
    {
        if (!_shellAttachment.IsAttached && ExpandedContent.Visibility != Visibility.Visible)
            Motion.Stop(ShellContentTranslate(ExpandedContent), TranslateTransform.YProperty, ExpandedContentRestY);
        double opacity = ExpandedContent.Visibility == Visibility.Visible ? ExpandedContent.Opacity : 0;
        ExpandedContent.BeginAnimation(OpacityProperty, null);
        CollapsedContent.BeginAnimation(OpacityProperty, null);
        MusicCompactContent.BeginAnimation(OpacityProperty, null);
        if (!_isThumbnailExpandAnimating && !_isThumbnailCollapseAnimating) ResetAnimationThumbnailOverlay();
        MediaBackground.BeginAnimation(OpacityProperty, null);
        MediaBackground2.BeginAnimation(OpacityProperty, null);

        ExpandedContentBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
        CollapsedContentBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
        MusicCompactContentBlur.BeginAnimation(BlurEffect.RadiusProperty, null);

        MediaBackground.Opacity = 0;
        MediaBackground2.Opacity = 0;

        // Keep the outgoing view visible until the content presenter's fade ends.
        if (_notchContentPresenter == null)
        {
            SecondaryContent.BeginAnimation(OpacityProperty, null);
            SecondaryContent.Visibility = Visibility.Collapsed;
            TimerContent.Visibility = Visibility.Collapsed;
        }

        ExpandedContent.Opacity = opacity;
        ExpandedContent.Visibility = Visibility.Visible;

        if (LyricsBlurBackground != null)
        {
            _isLyricsBlurFadeInProgress = false;
            LyricsBlurBackground.BeginAnimation(OpacityProperty, null);
            LyricsBlurBackground.Opacity = 0;
            LyricsBlurBackground.Visibility = Visibility.Collapsed;
        }
        if (_isLyricsActive && LyricsCanvasBackground != null)
        {
            LyricsCanvasBackground.BeginAnimation(OpacityProperty, null);
            LyricsCanvasBackground.Opacity = 0;
        }
        CancelMediaWidthAnimation();
        _shellState.UpdateExpandedWidth(IsNoWidgetMode, _isLyricsActive, _isMusicExpanded);
        if (MediaRightColumn != null)
            MediaRightColumn.Visibility = IsNoWidgetMode && !_isLyricsActive ? Visibility.Collapsed : Visibility.Visible;
        ExpandedContent.Width = _expandedWidth - ExpandedContentHorizontalInset;
        ExpandedContent.Height = _expandedHeight - ExpandedContentVerticalInset;
        PrepareExpandedContentLayoutForReveal();
    }

    private (double X, double Y)? EnsureCachedThumbnailExpandTarget()
    {
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (_cachedThumbnailExpandTarget.HasValue &&
            _lastMeasuredTargetIslandMode == _settings.EnableDynamicIslandMode &&
            Math.Abs(_lastMeasuredTargetExpandedWidth - _expandedWidth) < 0.1 &&
            Math.Abs(_lastMeasuredTargetExpandedHeight - _expandedHeight) < 0.1 &&
            Math.Abs(_lastMeasuredTargetDpi - dpi) < 0.001)
        {
            return _cachedThumbnailExpandTarget;
        }

        // A cold cache needs only the media subtree's destination layout. Never
        // resize the shell or flush the window's pending Measure/Arrange queue.
        var destination = new Size(Math.Max(0, _expandedWidth - ExpandedContentHorizontalInset), Math.Max(0, _expandedHeight - ExpandedContentVerticalInset));
        ExpandedContent.Measure(destination);
        ExpandedContent.Arrange(new Rect(destination));
        if (ThumbnailBorder.ActualWidth > 0 && ThumbnailBorder.ActualHeight > 0)
        {
            ConfigureAnimationThumbnailRestSlot();
            var local = ThumbnailBorder.TransformToAncestor(ExpandedContent).Transform(new Point());
            _cachedThumbnailExpandTarget = (
                local.X + (_expandedWidth - ExpandedContent.ActualWidth) / 2 - AnimationThumbnailBorder.Margin.Left,
                local.Y + ExpandedContent.Margin.Top + ExpandedContentRestY - AnimationThumbnailBorder.Margin.Top);
            _lastMeasuredTargetExpandedWidth = _expandedWidth;
            _lastMeasuredTargetExpandedHeight = _expandedHeight;
            _lastMeasuredTargetDpi = dpi;
            _lastMeasuredTargetIslandMode = _settings.EnableDynamicIslandMode;
        }

        return _cachedThumbnailExpandTarget;
    }



    private void AnimateThumbnailExpandOverlay((double X, double Y) compactThumbnailRestOffset)
    {
        SynchronizeMediaPinForTransition();
        var target = EnsureCachedThumbnailExpandTarget();
        if (!target.HasValue)
        {
            ResetAnimationThumbnailOverlay();
            if (CompactThumbnailBorder != null) CompactThumbnailBorder.Opacity = 1;
            if (ThumbnailBorder != null) ThumbnailBorder.Opacity = 1;
            return;
        }

        bool continuing = AnimationThumbnailBorder.Visibility == Visibility.Visible;
        if (!continuing)
        {
            AnimationThumbnailBorder.Width = 22;
            AnimationThumbnailBorder.Height = 22;
            Motion.StopClipSize(AnimationThumbnailClip, 22, 22);
            AnimationThumbnailTranslate.X = compactThumbnailRestOffset.X;
            AnimationThumbnailTranslate.Y = compactThumbnailRestOffset.Y;
            Motion.Stop(this, CurrentThumbnailAnimationRadiusProperty, 6);
        }
        AnimationThumbnailImage.Source = _showingEmptyThumbnail ? null : CompactThumbnail.Source;
        AnimationThumbnailBorder.Visibility = Visibility.Visible;
        AnimationThumbnailBorder.Opacity = 1;
        AnimationThumbnailBorder.BorderThickness = new Thickness(0);
        var size = GetExpandedThumbnailAnimationSize();
        _isThumbnailExpandAnimating = true;
        _isThumbnailCollapseAnimating = false;
        _pendingThumbnailHandoff = null;
        long version = ++_thumbnailOverlayVersion;
        int pending = 4;
        void Finished()
        {
            if (version != _thumbnailOverlayVersion || --pending != 0) return;
            _isThumbnailExpandAnimating = false;
            var handoff = _pendingThumbnailHandoff;
            _pendingThumbnailHandoff = null;
            handoff?.Invoke();
        }
        Motion.To(AnimationThumbnailBorder, WidthProperty, size.Width, Motion.ShellOpenWidth, done: Finished);
        Motion.To(AnimationThumbnailBorder, HeightProperty, size.Height, Motion.ShellOpenHeight, done: Finished);
        Motion.To(AnimationThumbnailTranslate, TranslateTransform.XProperty, target.Value.X, Motion.ShellOpenWidth, done: Finished);
        Motion.To(AnimationThumbnailTranslate, TranslateTransform.YProperty, target.Value.Y, Motion.ShellOpenHeight, done: Finished);
        Motion.ClipSizeTo(AnimationThumbnailClip, size.Width, size.Height, Motion.ShellOpenWidth, Motion.ShellOpenHeight);
        Motion.To(this, CurrentThumbnailAnimationRadiusProperty, 14, Motion.ShellOpenHeight);
        if (IsLiquidGlassEnabled && AnimationThumbnailBorder.Effect is DropShadowEffect shadow)
            Motion.To(shadow, DropShadowEffect.OpacityProperty, 0, Motion.Exit);
        if (CompactThumbnailBorder != null)
        {
            CompactThumbnailBorder.Opacity = 0;
            CompactThumbnailBorder.Visibility = Visibility.Collapsed;
        }
        if (ThumbnailBorder != null) ThumbnailBorder.Opacity = 0;
    }

    private void AnimateLyricsBlurImageSwitch(ImageSource? newThumbnail)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => AnimateLyricsBlurImageSwitch(newThumbnail));
            return;
        }

        if (LyricsBlurImage == null || newThumbnail == null) return;
        newThumbnail = LyricsBackgroundBrightness.Apply(newThumbnail, _settings.BrightenDarkLyricsBackground);

        if (LyricsBlurImage.Source == null || !_isExpanded)
        {
            LyricsBlurImage.BeginAnimation(OpacityProperty, null);
            LyricsBlurImage.Source = newThumbnail;
            LyricsBlurImage.Opacity = 1.0;
            if (LyricsBlurImageNext != null)
            {
                LyricsBlurImageNext.BeginAnimation(OpacityProperty, null);
                LyricsBlurImageNext.Opacity = 0.0;
                LyricsBlurImageNext.Source = null;
            }
            return;
        }

        if (ReferenceEquals(LyricsBlurImage.Source, newThumbnail))
        {
            // Reversing a toggle mid-transition must also cancel the other layer.
            if (LyricsBlurImageNext != null)
            {
                LyricsBlurImageNext.BeginAnimation(OpacityProperty, null);
                LyricsBlurImageNext.Opacity = 0;
                LyricsBlurImageNext.Source = null;
            }
            return;
        }

        if (ReferenceEquals(LyricsBlurImageNext?.Source, newThumbnail))
        {
            return;
        }

        if (LyricsBlurImageNext != null)
        {
            LyricsBlurImageNext.BeginAnimation(OpacityProperty, null);
            if (LyricsBlurImageNext.Opacity > 0.5 && LyricsBlurImageNext.Source != null)
            {
                LyricsBlurImage.Source = LyricsBlurImageNext.Source;
            }
            LyricsBlurImageNext.Opacity = 0;
            LyricsBlurImageNext.Source = newThumbnail;
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(400))
            {
                EasingFunction = new ExponentialEase { Exponent = 4, EasingMode = EasingMode.EaseOut }
            };
            fadeIn.Completed += (s, e) =>
            {
                if (ReferenceEquals(LyricsBlurImageNext.Source, newThumbnail))
                {
                    LyricsBlurImage.Source = newThumbnail;
                    LyricsBlurImageNext.BeginAnimation(OpacityProperty, null);
                    LyricsBlurImageNext.Opacity = 0;
                    LyricsBlurImageNext.Source = null;
                }
            };
            Timeline.SetDesiredFrameRate(fadeIn, VNotch.Services.AnimationConfig.TargetFps);
            LyricsBlurImageNext.BeginAnimation(OpacityProperty, fadeIn);
        }
        else
        {
            LyricsBlurImage.Source = newThumbnail;
            LyricsBlurImage.Opacity = 1.0;
        }
    }

    private bool _isLyricsBlurFadeInProgress = false;

    private void HideLyricsBlurBackgroundImmediately()
    {
        _isLyricsBlurFadeInProgress = false;
        // A hidden view can reopen on the same track without another artwork update.
        bool releaseSources = _currentMediaInfo?.IsAnyMediaPlaying != true;
        if (LyricsBlurImage != null)
        {
            LyricsBlurImage.BeginAnimation(OpacityProperty, null);
            if (releaseSources) LyricsBlurImage.Source = null;
        }
        if (LyricsBlurImageNext != null)
        {
            LyricsBlurImageNext.BeginAnimation(OpacityProperty, null);
            LyricsBlurImageNext.Opacity = 0;
            if (releaseSources) LyricsBlurImageNext.Source = null;
        }
        if (LyricsBlurBackground == null) return;
        LyricsBlurBackground.BeginAnimation(OpacityProperty, null);
        LyricsBlurBackground.Opacity = 0;
        LyricsBlurBackground.Visibility = Visibility.Collapsed;
    }

    private bool CanShowLyricsBlurBackground => _settings.EnableBlurEffects && !IsLiquidGlassEnabled &&
        ShouldShowMediaBlurBackground && !_isSpotifyCanvasMediaOpen &&
        _isExpanded && !_isTimerView && !_isAudioView && !_isSecondaryView;

    private void FadeInLyricsBlurBackgroundIfActive(bool force = false)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => FadeInLyricsBlurBackgroundIfActive(force));
            return;
        }

        if (!CanShowLyricsBlurBackground)
        {
            HideLyricsBlurBackgroundImmediately();
            return;
        }
        if (LyricsBlurBackground == null) return;

        bool wasCollapsed = LyricsBlurBackground.Visibility != Visibility.Visible;

        if (LyricsBlurImage != null)
        {
            if ((wasCollapsed || LyricsBlurImage.Source == null) && _currentMediaInfo?.Thumbnail != null)
            {
                LyricsBlurImage.Source = LyricsBackgroundBrightness.Apply(_currentMediaInfo.Thumbnail, _settings.BrightenDarkLyricsBackground);
                if (wasCollapsed && LyricsBlurImageNext != null)
                {
                    LyricsBlurImageNext.BeginAnimation(OpacityProperty, null);
                    LyricsBlurImageNext.Opacity = 0.0;
                    LyricsBlurImageNext.Source = null;
                }
            }
            if (LyricsBlurImageNext == null || LyricsBlurImageNext.Opacity <= 0.01)
            {
                LyricsBlurImage.BeginAnimation(OpacityProperty, null);
                LyricsBlurImage.Opacity = 1.0;
            }
        }
        double currentOpacity = wasCollapsed ? 0 : LyricsBlurBackground.Opacity;

        if (!force && !wasCollapsed && (currentOpacity >= 0.52 || _isLyricsBlurFadeInProgress))
        {
            return;
        }

        _isLyricsBlurFadeInProgress = true;
        LyricsBlurBackground.BeginAnimation(OpacityProperty, null);
        LyricsBlurBackground.Opacity = currentOpacity;
        LyricsBlurBackground.Visibility = Visibility.Visible;

        var fadeIn = new DoubleAnimation(currentOpacity, 0.55, new Duration(TimeSpan.FromMilliseconds(400)))
        {
            EasingFunction = new ExponentialEase { Exponent = 4, EasingMode = EasingMode.EaseOut }
        };
        fadeIn.Completed += (s, e) =>
        {
            if (!CanShowLyricsBlurBackground)
            {
                HideLyricsBlurBackgroundImmediately();
                return;
            }
            _isLyricsBlurFadeInProgress = false;
            LyricsBlurBackground.BeginAnimation(OpacityProperty, null);
            LyricsBlurBackground.Opacity = 0.55;
        };
        Timeline.SetDesiredFrameRate(fadeIn, VNotch.Services.AnimationConfig.TargetFps);
        LyricsBlurBackground.BeginAnimation(OpacityProperty, fadeIn);
    }

    private void FinalizeCompactThumbnailStateAfterExpand(bool suppressCompactThumbnailMotion)
    {
        if (CompactThumbnailBorder != null && !_isClipboardPeekActive && !suppressCompactThumbnailMotion)
        {
            ResetCompactThumbnailRestingState();
            CompactThumbnailBorder.BeginAnimation(OpacityProperty, null);
            CompactThumbnailBorder.Visibility = Visibility.Visible;
            CompactThumbnailBorder.Opacity = 1;
            CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            CompactThumbnailScale.ScaleX = 1.0;
            CompactThumbnailScale.ScaleY = 1.0;

            CompactThumbnailOutScale.ScaleX = 1.0;
            CompactThumbnailOutScale.ScaleY = 1.0;
            CompactThumbnailOutBlur.Radius = 0.0;
            CompactThumbnail.Opacity = 1.0;
        }
        else if (suppressCompactThumbnailMotion)
        {
            SuppressCompactMediaChromeForCountdownCompletion();
        }
    }

    private void OnExpandCompleted(int generation, bool suppressCompactThumbnailMotion, VNotch.Models.NotchView effectiveTarget = VNotch.Models.NotchView.Media)
    {
        if (generation != _viewTransitionGeneration || generation != _transitionCoordinator.ActiveTransitionId) return;
        _isAnimating = false;
        NotchBorder.IsHitTestVisible = true;

        _isCameraView = effectiveTarget == VNotch.Models.NotchView.Camera;
        _isClaudeView = effectiveTarget == VNotch.Models.NotchView.Claude;
        _isPiView = effectiveTarget == VNotch.Models.NotchView.Pi;
        _isSchoolView = effectiveTarget == VNotch.Models.NotchView.School;
        _isRobloxView = effectiveTarget == VNotch.Models.NotchView.Roblox;

        if (effectiveTarget == VNotch.Models.NotchView.Timer)
        {
            _isTimerView = true;
            _isSecondaryView = false;
            _isAudioView = false;
            VNotch.Presenters.NotchContentTransitionPresenter.ClearTransformAndEffects(TimerContent);
            ApplyClockViewWindowSize();
        }
        else if (effectiveTarget == VNotch.Models.NotchView.Secondary)
        {
            _isSecondaryView = true;
            _isTimerView = false;
            _isAudioView = false;
            VNotch.Presenters.NotchContentTransitionPresenter.ClearTransformAndEffects(SecondaryContent);
            ResizeHostWindowHeight(ClipboardTrayHeight);
        }
        else if (effectiveTarget == VNotch.Models.NotchView.AudioMixer)
        {
            _isAudioView = true;
            _isTimerView = false;
            _isSecondaryView = false;
            VNotch.Presenters.NotchContentTransitionPresenter.ClearTransformAndEffects(AudioContent);
            if (AudioScrollViewer != null)
            {
                VNotch.Presenters.NotchContentTransitionPresenter.ClearTransformAndEffects(AudioScrollViewer);
                AudioScrollViewer.BeginAnimation(OpacityProperty, null);
                AudioScrollViewer.Visibility = Visibility.Visible;
                AudioScrollViewer.Opacity = 1;
            }
            if (!AudioContent.ApplyPendingSnapshot())
                SettleAudioNotchToFit();
            StartAudioPoll();
            AudioContent.Refresh(SettleAudioNotchToFit);
            ResizeHostWindowHeight(_audioViewHeight > 0 ? _audioViewHeight : _audioViewMaxHeight);
        }
        else if (effectiveTarget == VNotch.Models.NotchView.Camera)
        {
            ResizeHostWindowHeight(CameraViewHeight);
        }
        else if (effectiveTarget == VNotch.Models.NotchView.Claude)
        {
            _isTimerView = false;
            _isSecondaryView = false;
            _isAudioView = false;
            VNotch.Presenters.NotchContentTransitionPresenter.ClearTransformAndEffects(ClaudeContent);
            ResizeHostWindowHeight(_claudeViewHeight);
            // Sessions may have come or gone while it opened.
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, SettleClaudeNotchToFit);
        }
        else if (effectiveTarget == VNotch.Models.NotchView.Pi)
        {
            _isTimerView = false;
            _isSecondaryView = false;
            _isAudioView = false;
            VNotch.Presenters.NotchContentTransitionPresenter.ClearTransformAndEffects(PiContent);
            ResizeHostWindowHeight(_homeViewHeight);
            // The dashboard may have answered while it opened.
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, SettleHomeNotchToFit);
        }
        else if (effectiveTarget == VNotch.Models.NotchView.School)
        {
            _isTimerView = false;
            _isSecondaryView = false;
            _isAudioView = false;
            VNotch.Presenters.NotchContentTransitionPresenter.ClearTransformAndEffects(SchoolContent);
            ResizeHostWindowHeight(_schoolViewHeight);
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, SettleSchoolNotchToFit);
        }
        else if (effectiveTarget == VNotch.Models.NotchView.Roblox)
        {
            _isTimerView = false;
            _isSecondaryView = false;
            _isAudioView = false;
            VNotch.Presenters.NotchContentTransitionPresenter.ClearTransformAndEffects(RobloxContent);
            ResizeHostWindowHeight(_robloxViewHeight);
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, SettleRobloxNotchToFit);
        }
        else
        {
            _isTimerView = false;
            _isSecondaryView = false;
            _isAudioView = false;
            RestoreExpandedWindowSize();
        }

        if (_pendingCameraPreviewVisualTeardown)
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, FinalizePendingCameraPreviewTeardown);
        }

        _transitionCoordinator.CompleteTransition(generation);
        UpdateSpotifyCanvasPresentationContext();
        UpdateNavIconsActiveState();
        RunQueuedNavigation(generation);

        if (_focusNotchAfterExpand) FocusNotchForKeyboard();

        RestoreExpandedContentOpacity();

        if (effectiveTarget == VNotch.Models.NotchView.Media)
        {
            UpdateProgressTimerState();
            UpdateCalendarInfo();
            ShowMediaBackground();
            FadeInLyricsBlurBackgroundIfActive();
            FadeInSpotifyCanvasBackgroundIfReady();
            ResumeSpotifyCanvasLifecycle();

            if (_isLyricsActive)
            {
                UpdateLyricsDisplay();
            }
            else
            {
                CheckAndRetryYouTubeSubtitlesOnExpand();
            }

            StartProgressCatchUpAnimation();
            RenderProgressBar();
            SyncExpandedMediaWidth(animate: true);
            RenderProgressBar();
        }

        if (!_showingEmptyThumbnail && _pendingFlipThumbnail != null)
        {
            var thumb = _pendingFlipThumbnail;
            _pendingFlipThumbnail = null;
            ThumbnailImage.Source = thumb;
            CompactThumbnail.Source = thumb;
            ThumbnailImage.Visibility = Visibility.Visible;
            ThumbnailFallback.Visibility = Visibility.Collapsed;
        }

        CollapsedContentBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
        CollapsedContentBlur.Radius = 0;
        MusicCompactContentBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
        MusicCompactContentBlur.Radius = 0;

        if (effectiveTarget == VNotch.Models.NotchView.Media && _isMusicCompactMode && TryComputeThumbnailExpandTarget(out var updatedTarget))
        {
            _cachedThumbnailExpandTarget = updatedTarget;
            _lastMeasuredTargetExpandedWidth = _expandedWidth;
            _lastMeasuredTargetExpandedHeight = _expandedHeight;
            _lastMeasuredTargetDpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            _lastMeasuredTargetIslandMode = _settings.EnableDynamicIslandMode;
        }

        if (effectiveTarget == VNotch.Models.NotchView.Media && _isThumbnailExpandAnimating)
        {
            _pendingThumbnailHandoff = () =>
            {
                if (generation != _viewTransitionGeneration) return;
                HandoffAnimationThumbnailToExpanded();
                FinalizeCompactThumbnailStateAfterExpand(suppressCompactThumbnailMotion);
            };
        }
        else
        {
            HandoffAnimationThumbnailToExpanded();
            FinalizeCompactThumbnailStateAfterExpand(suppressCompactThumbnailMotion);
        }

        CollapsedContent.Visibility = Visibility.Collapsed;
        MusicCompactContent.Visibility = Visibility.Collapsed;
    }

    private void ExpandNotch(long? transitionId = null, VNotch.Models.NotchView? targetView = null,
        VNotch.Models.NotchView fromView = VNotch.Models.NotchView.Compact)
    {
        if (transitionId == null)
        {
            var target = targetView ?? DetermineTargetExpandedView();
            _transitionCoordinator.RequestView(target, "ExpandNotch");
            return;
        }

        if (_isGreetingActive)
        {
            _transitionCoordinator.CancelTransition(transitionId.Value, "GreetingActive");
            return;
        }
        // The orb goes back in before the notch grows over it.
        RetractBubbleForExpand();
        int generation = (int)transitionId;
        _viewTransitionGeneration = generation;
        _isAnimating = true;

        var effectiveTarget = targetView ?? VNotch.Models.NotchView.Media;
        bool switchingExpandedViews = fromView != VNotch.Models.NotchView.Compact &&
            !(_shellAttachment.IsAttached && effectiveTarget == fromView);
        if (fromView == VNotch.Models.NotchView.Timer && effectiveTarget != VNotch.Models.NotchView.Timer)
            CountdownWidget.CancelEditing();

        if (_isAudioView && effectiveTarget != VNotch.Models.NotchView.AudioMixer)
        {
            StopAudioPoll();
            AudioContent.ReleaseSessionCache();
        }
        if (effectiveTarget != VNotch.Models.NotchView.Media)
        {
            ClearGlassMediaTint();
            HideMediaBackgroundOverlay();
            SuspendSpotifyCanvasLifecycle();
            if (_isThumbnailExpandAnimating && fromView == VNotch.Models.NotchView.Media)
            {
                HandoffAnimationThumbnailToExpanded();
            }
            else
            {
                ResetAnimationThumbnailOverlay();
            }

            if (!switchingExpandedViews)
            {
                if (ThumbnailScale != null)
                {
                    ThumbnailScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                    ThumbnailScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                    ThumbnailScale.ScaleX = 1;
                    ThumbnailScale.ScaleY = 1;
                }
                ThumbnailBorder.BeginAnimation(OpacityProperty, null);
                ThumbnailBorder.Opacity = 0;
            }
        }


        bool suppressCompactThumbnailMotion = IsCountdownCompletionVisualActive;
        if (suppressCompactThumbnailMotion)
        {
            SuppressCompactMediaChromeForCountdownCompletion();
        }
        CancelThumbnailSwitchForExpand();

        // Read effective animated values before detaching clocks. ActualWidth may
        // still describe the previous layout pass when an animation reverses.
        double currentWidth = _collapsedWidth;
        if (double.IsFinite(NotchBorder.Width) && NotchBorder.Width > 0)
        {
            currentWidth = NotchBorder.Width;
        }
        else if (NotchBorder.ActualWidth > 0)
        {
            currentWidth = NotchBorder.ActualWidth;
        }

        double currentHeight = _collapsedHeight;
        if (double.IsFinite(NotchBorder.Height) && NotchBorder.Height > 0)
        {
            currentHeight = NotchBorder.Height;
        }
        else if (NotchBorder.ActualHeight > 0)
        {
            currentHeight = NotchBorder.ActualHeight;
        }
        // Any closed-notch width animation still in flight (a pill sizing itself) is void from here on.
        ++_compactWidthAnimationVersion;

        DismissStateBeforeExpand();

        EnsureTopmost();

        ResetNotchScaleBounce();
        CancelMediaWidthAnimation();
        _shellState.UpdateExpandedWidth(IsNoWidgetMode, _isLyricsActive, _isMusicExpanded);

        double targetWidth = _expandedWidth;
        double targetHeight = _expandedHeight;

        if (effectiveTarget == VNotch.Models.NotchView.Timer)
        {
            if (!switchingExpandedViews) VNotch.Presenters.NotchContentTransitionPresenter.ClearTransformAndEffects(TimerContent);
            targetWidth = _clockViewWidth;
            targetHeight = MeasureSystemViewHeight();
            if (targetHeight > currentHeight)
                ApplyClockViewWindowSize();
            PrepareClockViewContentSize();
            RefreshClockView();
            UpdateTimerNavIconsState();
        }
        else if (effectiveTarget == VNotch.Models.NotchView.Secondary)
        {
            if (!switchingExpandedViews) VNotch.Presenters.NotchContentTransitionPresenter.ClearTransformAndEffects(SecondaryContent);
            targetWidth = ClipboardTrayWidth;
            targetHeight = ClipboardTrayHeight;
            SecondaryContent.Width = targetWidth - SecondaryContent.Margin.Left - SecondaryContent.Margin.Right;
            // Lay out the tray at its destination size. The shell's rounded clip
            // reveals it while expanding, without remeasuring every card each frame.
            SecondaryContent.Height = targetHeight - SecondaryContent.Margin.Top - SecondaryContent.Margin.Bottom;
            SecondaryContent.VerticalAlignment = VerticalAlignment.Top;
            if (targetHeight > currentHeight)
                ResizeHostWindowHeight(targetHeight);
            UpdateNavIconsActiveState();
            EnableKeyboardInput();
        }
        else if (effectiveTarget == VNotch.Models.NotchView.Camera)
        {
            targetWidth = CameraViewWidth;
            targetHeight = CameraViewHeight;
            CameraContent.Width = targetWidth - CameraContent.Margin.Left - CameraContent.Margin.Right;
            if (targetHeight > currentHeight)
                ResizeHostWindowHeight(targetHeight);
            UpdateNavIconsActiveState();
        }
        else if (effectiveTarget == VNotch.Models.NotchView.Claude)
        {
            if (!switchingExpandedViews) VNotch.Presenters.NotchContentTransitionPresenter.ClearTransformAndEffects(ClaudeContent);
            targetWidth = ClaudeViewWidth;
            targetHeight = _claudeViewHeight = MeasureClaudeViewHeight();
            // Laid out at its final size; the shell's clip reveals it while expanding.
            ClaudeContent.Width = targetWidth - ClaudeContent.Margin.Left - ClaudeContent.Margin.Right;
            ClaudeContent.Height = targetHeight - ClaudeContent.Margin.Top - ClaudeContent.Margin.Bottom;
            if (targetHeight > currentHeight)
                ResizeHostWindowHeight(targetHeight);
            UpdateNavIconsActiveState();
        }
        else if (effectiveTarget == VNotch.Models.NotchView.Pi)
        {
            if (!switchingExpandedViews) VNotch.Presenters.NotchContentTransitionPresenter.ClearTransformAndEffects(PiContent);
            targetWidth = HomeViewWidth;
            targetHeight = MeasureHomeViewHeight();
            // Laid out at its final size; the shell's clip reveals it while expanding.
            PiContent.Width = targetWidth - PiContent.Margin.Left - PiContent.Margin.Right;
            PiContent.Height = targetHeight - PiContent.Margin.Top - PiContent.Margin.Bottom;
            if (targetHeight > currentHeight)
                ResizeHostWindowHeight(targetHeight);
            UpdateNavIconsActiveState();
        }
        else if (effectiveTarget == VNotch.Models.NotchView.School)
        {
            if (!switchingExpandedViews) VNotch.Presenters.NotchContentTransitionPresenter.ClearTransformAndEffects(SchoolContent);
            targetWidth = SchoolViewWidth;
            targetHeight = MeasureSchoolViewHeight();
            SchoolContent.Width = targetWidth - SchoolContent.Margin.Left - SchoolContent.Margin.Right;
            SchoolContent.Height = targetHeight - SchoolContent.Margin.Top - SchoolContent.Margin.Bottom;
            if (targetHeight > currentHeight)
                ResizeHostWindowHeight(targetHeight);
            UpdateNavIconsActiveState();
        }
        else if (effectiveTarget == VNotch.Models.NotchView.Roblox)
        {
            if (!switchingExpandedViews) VNotch.Presenters.NotchContentTransitionPresenter.ClearTransformAndEffects(RobloxContent);
            targetWidth = RobloxViewWidth;
            targetHeight = MeasureRobloxViewHeight();
            RobloxContent.Width = targetWidth - RobloxContent.Margin.Left - RobloxContent.Margin.Right;
            RobloxContent.Height = targetHeight - RobloxContent.Margin.Top - RobloxContent.Margin.Bottom;
            if (targetHeight > currentHeight)
                ResizeHostWindowHeight(targetHeight);
            UpdateNavIconsActiveState();
        }
        else if (effectiveTarget == VNotch.Models.NotchView.AudioMixer)
        {
            if (!switchingExpandedViews) VNotch.Presenters.NotchContentTransitionPresenter.ClearTransformAndEffects(AudioContent);
            if (AudioScrollViewer != null)
            {
                VNotch.Presenters.NotchContentTransitionPresenter.ClearTransformAndEffects(AudioScrollViewer);
                AudioScrollViewer.BeginAnimation(OpacityProperty, null);
                AudioScrollViewer.Visibility = Visibility.Visible;
                AudioScrollViewer.Opacity = 1;
            }
            AudioContent.PrepareForEntrance();

            targetWidth = _audioViewWidth;
            targetHeight = _audioViewHeight > 0 ? _audioViewHeight : _audioViewMaxHeight;
            if (targetHeight > currentHeight)
                ResizeHostWindowHeight(targetHeight);
            UpdateNavIconsActiveState();
        }
        else
        {
            if (targetHeight > currentHeight)
                RestoreExpandedWindowSize();
            SetupExpandedContentBeforeAnimation();
        }

        _shellOpenWidth = targetWidth;
        _shellOpenHeight = targetHeight;
        if (!switchingExpandedViews) _shellAttachment.Attach(targetWidth, targetHeight);
        AnimateStatusBarReveal(true);

        NotchBorder.IsHitTestVisible = true;
        if (!switchingExpandedViews) ArmEarlyHitTest(generation);
        int animFps = VNotch.Services.AnimationConfig.TargetFps;


        var fadeInAnim = MakeAnim(0d, 1d, _dur400, _easePowerOut3);


        var motion = new VNotch.Models.TransitionMotionConfig(
            Duration: _dur500,
            Easing: _easeAppleOut,
            TargetFps: animFps,
            ReduceMotion: AnimationConfig.ReduceMotion,
            Spring: switchingExpandedViews ? Motion.Shift : Motion.ShellOpenWidth
        );

        var orderedTabs = GetActiveTabSequence();
        int navigationDirection = orderedTabs.IndexOf(effectiveTarget) >= orderedTabs.IndexOf(fromView) ? 1 : -1;
        var plan = new VNotch.Models.TransitionPlan(
            SessionId: generation,
            FromView: switchingExpandedViews ? fromView : VNotch.Models.NotchView.Compact,
            TargetView: effectiveTarget,
            TargetShape: _isMusicCompactMode ? VNotch.Controllers.NotchShapeState.MusicExpanding : VNotch.Controllers.NotchShapeState.Expanding,
            TargetWidth: targetWidth,
            TargetHeight: targetHeight,
            TargetCornerRadius: _cornerRadiusExpanded,
            Motion: motion,
            NavigationDirection: navigationDirection,
            FromHeight: currentHeight
        );

        int pendingPresenters = _notchContentPresenter != null ? 2 : 1;
        void PartCompleted()
        {
            if (--pendingPresenters == 0)
                OnExpandCompleted(generation, suppressCompactThumbnailMotion, effectiveTarget);
        }
        if (effectiveTarget == VNotch.Models.NotchView.Media)
            ShellContentTranslate(ExpandedContent);

        ApplyShellReleaseVelocity();
        if (_notchShellPresenter != null)
        {
            _notchShellPresenter!.AnimateShell(plan, result =>
            {
                if (result.Status == VNotch.Models.TransitionExecutionStatus.Completed)
                {
                    PartCompleted();
                }
            });
        }
        else
        {
            Motion.To(NotchBorder, WidthProperty, targetWidth,
                switchingExpandedViews ? Motion.Shift : Motion.ShellOpenWidth, fps: animFps);
            Motion.To(NotchBorder, HeightProperty, targetHeight,
                switchingExpandedViews ? Motion.Shift : Motion.ShellOpenHeight, fps: animFps, done: PartCompleted);
            AnimateCornerRadius(_cornerRadiusExpanded, switchingExpandedViews ? Motion.Shift : Motion.ShellOpenHeight);
        }



        // Opacity/transform clocks keep the transition on the compositor without
        // changing the blur kernel and intermediate surface on every tick.
        CollapsedContentBlur.Radius = 0;
        MusicCompactContentBlur.Radius = 0;

        if (_notchContentPresenter != null)
        {
            var contentPlan = plan;
            _notchContentPresenter.TransitionContent(contentPlan, result =>
            {
                if (result.Status == VNotch.Models.TransitionExecutionStatus.Completed) PartCompleted();
            });
        }
        else
        {
            if (effectiveTarget == VNotch.Models.NotchView.Timer && TimerContent != null)
            {
                VNotch.Presenters.NotchContentTransitionPresenter.ClearTransformAndEffects(TimerContent);
                ExpandedContent.Visibility = Visibility.Collapsed;
                TimerContent.Visibility = Visibility.Visible;
                TimerContent.BeginAnimation(OpacityProperty, fadeInAnim);
                TimerContent.Opacity = 1;
            }
            else if (effectiveTarget == VNotch.Models.NotchView.Secondary && SecondaryContent != null)
            {
                VNotch.Presenters.NotchContentTransitionPresenter.ClearTransformAndEffects(SecondaryContent);
                ExpandedContent.Visibility = Visibility.Collapsed;
                SecondaryContent.Visibility = Visibility.Visible;
                SecondaryContent.BeginAnimation(OpacityProperty, fadeInAnim);
                SecondaryContent.Opacity = 1;
            }
            else if (effectiveTarget == VNotch.Models.NotchView.AudioMixer && AudioContent != null)
            {
                VNotch.Presenters.NotchContentTransitionPresenter.ClearTransformAndEffects(AudioContent);
                if (AudioScrollViewer != null)
                {
                    VNotch.Presenters.NotchContentTransitionPresenter.ClearTransformAndEffects(AudioScrollViewer);
                    AudioScrollViewer.BeginAnimation(OpacityProperty, null);
                    AudioScrollViewer.Visibility = Visibility.Visible;
                    AudioScrollViewer.Opacity = 1;
                }
                ExpandedContent.Visibility = Visibility.Collapsed;
                AudioContent.Visibility = Visibility.Visible;
                AudioContent.BeginAnimation(OpacityProperty, fadeInAnim);
                AudioContent.Opacity = 1;
            }
            else
            {
                VNotch.Presenters.NotchContentTransitionPresenter.ClearTransformAndEffects(ExpandedContent);
                ExpandedContent.BeginAnimation(OpacityProperty, fadeInAnim);
                ExpandedContent.Opacity = 1;
            }
        }

        // The shell and page transition already move the whole view; do not replay tile entrances.
        if (effectiveTarget == VNotch.Models.NotchView.Secondary) ClipboardTrayView.BeginEntrance(animate: false);
        ResetClockViewUnfoldIn();
        AudioContent?.ResetRowAnimations();
        ResetCameraContentReveal();
        ResetMediaContentReveal();
        Motion.To(HoverGlow, OpacityProperty, .15, Motion.Press);
    }

    private int _mediaContentRevealGeneration;
    private int _cameraContentRevealGeneration;

    private void PlayThumbnailViewHideAnimation()
    {
        if (ThumbnailBorder == null) return;
        ++_mediaContentRevealGeneration;
        Motion.To(ThumbnailBorder, OpacityProperty, 0, Motion.Exit);
        if (ThumbnailScale != null)
        {
            Motion.To(ThumbnailScale, ScaleTransform.ScaleXProperty, Motion.ExitToScale, Motion.Exit);
            Motion.To(ThumbnailScale, ScaleTransform.ScaleYProperty, Motion.ExitToScale, Motion.Exit);
        }
    }

    private void ResetMediaContentReveal()
    {
        _mediaContentRevealGeneration++;

        if (ThumbnailBorder != null && !_isThumbnailExpandAnimating)
        {
            ThumbnailBorder.BeginAnimation(OpacityProperty, null);
            ThumbnailBorder.Opacity = 1;
        }
        if (ThumbnailScale != null)
        {
            ThumbnailScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ThumbnailScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            ThumbnailScale.ScaleX = 1;
            ThumbnailScale.ScaleY = 1;
        }

        if (MediaPlayback != null)
        {
            MediaPlayback.BeginAnimation(OpacityProperty, null);
            MediaPlayback.Opacity = 1;
            if (MediaPlayback.RenderTransform is TranslateTransform playbackTranslate)
            {
                playbackTranslate.BeginAnimation(TranslateTransform.YProperty, null);
                playbackTranslate.X = 0;
                playbackTranslate.Y = 0;
            }
            else
            {
                MediaPlayback.RenderTransform = null;
            }
        }

        if (MediaRightColumn != null)
        {
            MediaRightColumn.BeginAnimation(OpacityProperty, null);
            MediaRightColumn.Opacity = 1;
            if (MediaRightColumn.RenderTransform is TransformGroup rightGroup &&
                rightGroup.Children.Count == 2 &&
                rightGroup.Children[0] is ScaleTransform rightScale &&
                rightGroup.Children[1] is TranslateTransform rightTranslate)
            {
                rightScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                rightScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                rightTranslate.BeginAnimation(TranslateTransform.YProperty, null);
                rightScale.ScaleX = 1;
                rightScale.ScaleY = 1;
                rightTranslate.X = 0;
                rightTranslate.Y = 0;
            }
            else
            {
                MediaRightColumn.RenderTransform = null;
            }
        }
    }

    private void PlayMediaContentReveal(TimeSpan baseDelay, bool animateThumbnail = true)
    {
        if (AnimationConfig.ReduceMotion)
        {
            ResetMediaContentReveal();
            return;
        }
        ++_mediaContentRevealGeneration;
        if (animateThumbnail && ThumbnailBorder != null)
        {
            SynchronizeMediaPinForTransition();
            if (!_showingEmptyThumbnail && CompactThumbnail?.Source != null && ThumbnailImage != null)
            {
                ThumbnailImage.Source = CompactThumbnail.Source;
                ThumbnailImage.Visibility = Visibility.Visible;
                ThumbnailFallback.Visibility = Visibility.Collapsed;
            }
            RevealShellTile(ThumbnailBorder, ThumbnailScale, null, 0);
        }
        if (MediaPlayback != null)
        {
            var translate = ShellContentTranslate(MediaPlayback);
            RevealShellTile(MediaPlayback, null, translate, 1);
        }
        if (MediaRightColumn != null && MediaRightColumn.Visibility == Visibility.Visible)
        {
            var translate = ShellContentTranslate(MediaRightColumn);
            var scale = ShellContentScale(MediaRightColumn);
            RevealShellTile(MediaRightColumn, scale, translate, 2);
        }
    }

    private static void RevealShellTile(FrameworkElement element, ScaleTransform? scale, TranslateTransform? translate, int index)
    {
        bool continuing = DependencyPropertyHelper.GetValueSource(element, OpacityProperty).IsAnimated;
        var delay = continuing ? TimeSpan.Zero : Motion.Stagger(index);
        Motion.To(element, OpacityProperty, 1, Motion.Tile, from: continuing ? null : 0, delay: delay);
        if (scale != null)
        {
            Motion.To(scale, ScaleTransform.ScaleXProperty, 1, Motion.TileScale, from: continuing ? null : Motion.TileFromScale, delay: delay);
            Motion.To(scale, ScaleTransform.ScaleYProperty, 1, Motion.TileScale, from: continuing ? null : Motion.TileFromScale, delay: delay);
            element.RenderTransformOrigin = new Point(.5, .5);
        }
        if (translate != null)
            Motion.To(translate, TranslateTransform.YProperty, 0, Motion.Tile, from: continuing ? null : 10, delay: delay);
    }

    private static TranslateTransform ShellContentTranslate(FrameworkElement element)
    {
        var existing = FindShellTranslate(element.RenderTransform);
        if (existing != null) return existing;
        var translate = new TranslateTransform();
        var group = new TransformGroup();
        group.Children.Add(element.RenderTransform ?? Transform.Identity);
        group.Children.Add(translate);
        element.RenderTransform = group;
        return translate;
    }

    private static TranslateTransform? FindShellTranslate(Transform? transform)
    {
        if (transform is TranslateTransform translate && !translate.IsFrozen) return translate;
        if (transform is TransformGroup group)
            foreach (var child in group.Children)
                if (FindShellTranslate(child) is { } found) return found;
        return null;
    }

    private static ScaleTransform ShellContentScale(FrameworkElement element)
    {
        if (element.RenderTransform is TransformGroup existing)
            foreach (var child in existing.Children)
                if (child is ScaleTransform scale && !scale.IsFrozen) return scale;
        var result = new ScaleTransform();
        var group = new TransformGroup();
        group.Children.Add(result);
        group.Children.Add(element.RenderTransform ?? Transform.Identity);
        element.RenderTransform = group;
        return result;
    }

    private void ResetCameraContentReveal()
    {
        _cameraContentRevealGeneration++;
        if (CameraSection != null)
        {
            CameraSection.BeginAnimation(OpacityProperty, null);
            CameraSection.Opacity = 1;
        }
        if (CameraSectionScale != null)
        {
            CameraSectionScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            CameraSectionScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            CameraSectionScale.ScaleX = 1;
            CameraSectionScale.ScaleY = 1;
        }
    }

    private void PlayCameraContentReveal(TimeSpan baseDelay)
    {
        if (CameraSection == null || CameraSectionScale == null) return;
        ++_cameraContentRevealGeneration;
        RevealShellTile(CameraSection, CameraSectionScale, null, 0);
    }

    private LastExpandedView DetermineCurrentExpandedView()
    {
        if (_isCameraView) return LastExpandedView.Camera;
        if (_isClaudeView) return LastExpandedView.Claude;
        if (_isPiView) return LastExpandedView.Pi;
        if (_isSchoolView) return LastExpandedView.School;
        if (_isRobloxView) return LastExpandedView.Roblox;
        if (_isAudioView) return LastExpandedView.Audio;
        if (_isTimerView) return LastExpandedView.Timer;
        if (_isSecondaryView) return LastExpandedView.Secondary;
        return LastExpandedView.Primary;
    }

    private VNotch.Models.NotchView DetermineTargetExpandedView()
    {
        // Tapping a Claude alert, or the closed notch while Claude works, opens what it is about.
        if (_isClaudeNotificationVisible && _isClaudePill) return VNotch.Models.NotchView.Claude;
        if (_claudeLiveSession != null && ClaudeLive.IsVisible) return VNotch.Models.NotchView.Claude;

        if (_settings.ReopenLastViewOnExpand)
        {
            return _lastExpandedViewBeforeCollapse switch
            {
                LastExpandedView.Timer => VNotch.Models.NotchView.Timer,
                LastExpandedView.Secondary => VNotch.Models.NotchView.Secondary,
                LastExpandedView.Audio => VNotch.Models.NotchView.AudioMixer,
                LastExpandedView.Camera when !_settings.HideCamera => VNotch.Models.NotchView.Camera,
                LastExpandedView.Claude => VNotch.Models.NotchView.Claude,
                LastExpandedView.Pi => VNotch.Models.NotchView.Pi,
                LastExpandedView.School => VNotch.Models.NotchView.School,
                LastExpandedView.Roblox => VNotch.Models.NotchView.Roblox,
                _ => VNotch.Models.NotchView.Media
            };
        }
        return VNotch.Models.NotchView.Media;
    }

    private void AnimateAuxiliaryViewCollapse(FrameworkElement content, int generation)
    {
        if (content.Visibility != Visibility.Visible) return;

        // Capture the rendered state so closing during a view transition does not jump.
        double opacity = content.Opacity;
        var transform = content.RenderTransform?.Value ?? Matrix.Identity;
        double blurRadius = (content.Effect as BlurEffect)?.Radius ?? 0;
        content.BeginAnimation(OpacityProperty, null);
        content.Opacity = opacity;

        var scale = new ScaleTransform(transform.M11, transform.M22);
        var translate = new TranslateTransform(transform.OffsetX, transform.OffsetY);
        var group = new TransformGroup();
        group.Children.Add(scale);
        group.Children.Add(translate);
        content.RenderTransform = group;
        content.RenderTransformOrigin = new Point(0.5, 0);

        int fps = AnimationConfig.TargetFps;
        var fade = MakeAnim(opacity, 0, _dur200, _easeQuadOut);
        Timeline.SetDesiredFrameRate(fade, fps);

        fade.Completed += (_, _) =>
        {
            if (generation != _viewTransitionGeneration) return;
            VNotch.Presenters.NotchContentTransitionPresenter.ResetElementVisualState(content);
        };

        Motion.To(scale, ScaleTransform.ScaleXProperty, Motion.ExitToScale, Motion.Exit, fps: fps);
        Motion.To(scale, ScaleTransform.ScaleYProperty, Motion.ExitToScale, Motion.Exit, fps: fps);
        Motion.To(translate, TranslateTransform.YProperty, -16, Motion.Exit, fps: fps);
        if (_settings.EnableBlurEffects)
        {
            var blur = new BlurEffect { Radius = blurRadius, RenderingBias = RenderingBias.Performance };
            content.Effect = blur;
            var blurOut = MakeAnim(blurRadius, 10, _dur200, _easeQuadOut);
            Timeline.SetDesiredFrameRate(blurOut, fps);
            blur.BeginAnimation(BlurEffect.RadiusProperty, blurOut);
        }
        else
        {
            content.Effect = null;
        }
        content.BeginAnimation(OpacityProperty, fade);
    }


    private void AnimateThumbnailCollapseOverlay()
    {
        _mediaContentRevealGeneration++;
        SynchronizeMediaPinForTransition();
        bool continuing = AnimationThumbnailBorder.Visibility == Visibility.Visible;
        if (!continuing && TryComputeThumbnailExpandTarget(out var measuredTarget))
            _cachedThumbnailExpandTarget = measuredTarget;
        if (!_cachedThumbnailExpandTarget.HasValue) return;

        if (!continuing)
        {
            var start = _cachedThumbnailExpandTarget.Value;
            var size = GetExpandedThumbnailAnimationSize();
            AnimationThumbnailBorder.Width = size.Width;
            AnimationThumbnailBorder.Height = size.Height;
            Motion.StopClipSize(AnimationThumbnailClip, size.Width, size.Height);
            AnimationThumbnailTranslate.X = start.X;
            AnimationThumbnailTranslate.Y = start.Y;
            Motion.Stop(this, CurrentThumbnailAnimationRadiusProperty, 14);
        }
        AnimationThumbnailImage.Source = ThumbnailImage.Source ?? CompactThumbnail.Source;
        AnimationThumbnailBorder.Visibility = Visibility.Visible;
        AnimationThumbnailBorder.Opacity = 1;
        AnimationThumbnailBorder.BorderThickness = new Thickness(0);
        if (CompactThumbnailBorder != null)
        {
            CompactThumbnail.Source = AnimationThumbnailImage.Source;
            CompactThumbnail.Visibility = Visibility.Visible;
            CompactThumbnail.Opacity = 1;
            CompactThumbnailBorder.Opacity = 0;
            CompactThumbnailBorder.Visibility = Visibility.Hidden;
        }
        if (ThumbnailBorder != null) Motion.Stop(ThumbnailBorder, OpacityProperty, 0);

        var rest = MeasureCompactThumbnailRestOffset();
        _isThumbnailCollapseAnimating = true;
        _isThumbnailExpandAnimating = false;
        _pendingThumbnailHandoff = null;
        long version = ++_thumbnailOverlayVersion;
        int pending = 4;
        void Finished()
        {
            if (version != _thumbnailOverlayVersion || --pending != 0) return;
            _isThumbnailCollapseAnimating = false;
            var handoff = _pendingThumbnailHandoff;
            _pendingThumbnailHandoff = null;
            handoff?.Invoke();
        }
        Motion.To(AnimationThumbnailBorder, WidthProperty, 22, Motion.ShellCloseWidth, done: Finished);
        Motion.To(AnimationThumbnailBorder, HeightProperty, 22, Motion.ShellCloseHeight, done: Finished);
        Motion.To(AnimationThumbnailTranslate, TranslateTransform.XProperty, rest.X, Motion.ShellCloseWidth, done: Finished);
        Motion.To(AnimationThumbnailTranslate, TranslateTransform.YProperty, rest.Y, Motion.ShellCloseHeight, done: Finished);
        Motion.ClipSizeTo(AnimationThumbnailClip, 22, 22, Motion.ShellCloseWidth, Motion.ShellCloseHeight);
        Motion.To(this, CurrentThumbnailAnimationRadiusProperty, 6, Motion.ShellCloseHeight);
        if (IsLiquidGlassEnabled && AnimationThumbnailBorder.Effect is DropShadowEffect shadow)
            Motion.To(shadow, DropShadowEffect.OpacityProperty, .5, Motion.Tile);
    }

    private void DismissCollapseNotificationsAndOverlays()
    {
        CancelChargingGlanceImmediate();
        if (_isBluetoothNotificationVisible)
        {
            _bluetoothController.MarkDismissed();
            BluetoothNotification.BeginAnimation(OpacityProperty, null);
            BluetoothNotification.Opacity = 0;
            BluetoothNotification.Visibility = Visibility.Collapsed;
            BluetoothDisconnectNotification.BeginAnimation(OpacityProperty, null);
            BluetoothDisconnectNotification.Opacity = 0;
            BluetoothDisconnectNotification.Visibility = Visibility.Collapsed;
            _isBluetoothNotificationVisible = false;
        }
        CancelClaudeNotificationImmediate();
        CancelAppPillImmediate();
        _compactPillArbiter.ForceClear();

        NavIconsPanel.BeginAnimation(OpacityProperty, null);
        NavIconsPanel.Opacity = 0;
        NavIconsPanel.Visibility = Visibility.Collapsed;
        NavIconsBackground.BeginAnimation(OpacityProperty, null);
        NavIconsBackground.Opacity = 0;
        NavIconsBackground.Visibility = Visibility.Collapsed;
    }

    private void FinalizeCompactModeAfterCollapse(FrameworkElement contentToShow, bool suppressCompactThumbnailMotion, bool deferCompactThumbnailReveal = false)
    {
        if (!_isMusicCompactMode)
        {
            ResetAnimationThumbnailOverlay();
            return;
        }

        contentToShow.Opacity = 1;
        contentToShow.BeginAnimation(OpacityProperty, null);
        contentToShow.RenderTransform = null;

        if (!deferCompactThumbnailReveal && !_isThumbnailCollapseAnimating)
        {
            ResetCompactThumbnailRestingState();
        }
        CompactHoverInfo.BeginAnimation(OpacityProperty, null);
        CompactHoverInfo.Opacity = 0;
        CompactHoverInfo.Visibility = Visibility.Collapsed;

        bool allowThumbnailHandoff = !_isClipboardPeekActive && !_isVolumeIndicatorActive && !suppressCompactThumbnailMotion && !deferCompactThumbnailReveal;
        if (allowThumbnailHandoff)
        {
            if (_isThumbnailCollapseAnimating)
            {
                int generation = _viewTransitionGeneration;
                _pendingThumbnailHandoff = () =>
                {
                    if (generation != _viewTransitionGeneration || _isExpanded) return;
                    HandoffAnimationThumbnailToCompact();
                };
            }
            else
            {
                HandoffAnimationThumbnailToCompact();
            }
        }
        else
        {
            ResetAnimationThumbnailOverlay();
        }
    }

    private void FinalizeCompactThumbnailAndVisualizerAfterCollapse(bool suppressCompactThumbnailMotion, bool deferCompactThumbnailReveal = false)
    {
        bool allowCompactChrome = !_isClipboardPeekActive && !_isVolumeIndicatorActive && !suppressCompactThumbnailMotion;

        if (CompactThumbnailBorder != null)
        {
            if (allowCompactChrome && !deferCompactThumbnailReveal && !_isThumbnailCollapseAnimating)
            {
                ResetCompactThumbnailRestingState();
            CompactThumbnailBorder.BeginAnimation(OpacityProperty, null);
                CompactThumbnailBorder.Visibility = Visibility.Visible;
                CompactThumbnailBorder.Opacity = 1;
            }
            else if (suppressCompactThumbnailMotion)
            {
                SuppressCompactMediaChromeForCountdownCompletion();
            }
        }

        if (ThumbnailBorder != null) ThumbnailBorder.Opacity = 1;

        if (_isMusicCompactMode && _currentMediaInfo?.IsPlaying == true && allowCompactChrome)
        {
            ShowMusicVisualizer(animate: false);
        }

        MusicCompactContent.InvalidateArrange();
        MusicCompactContent.UpdateLayout();

        if (_pendingFlipThumbnail != null && !suppressCompactThumbnailMotion)
        {
            var thumb = _pendingFlipThumbnail;
            _pendingFlipThumbnail = null;
            AnimateThumbnailSwitchOnly(thumb, force: true);
        }
        else if (suppressCompactThumbnailMotion)
        {
            _pendingFlipThumbnail = null;
        }
    }

    private void OnCollapseCompleted(
        int generation,
        bool wasAudio,
        FrameworkElement contentToShow,
        bool suppressCompactThumbnailMotion,
        bool deferCompactThumbnailReveal = false)
    {
        if (generation != _viewTransitionGeneration || generation != _transitionCoordinator.ActiveTransitionId) return;
        _isAnimating = false;
        NotchBorder.IsHitTestVisible = true;
        _transitionCoordinator.CompleteTransition(generation);
        UpdateSpotifyCanvasPresentationContext();
        Motion.Stop(NotchBorder, WidthProperty, _collapsedWidth);
        Motion.Stop(NotchBorder, HeightProperty, _collapsedHeight);

        RestoreExpandedWindowSize();
        FinalizePendingCameraPreviewTeardown();
        if (wasAudio)
        {
            RestorePrivacyDotVisibility();
        }

        contentToShow.RenderTransform = null;

        DismissCollapseNotificationsAndOverlays();

        ResetContentTransformAndEffectsAfterCollapse();

        if (!_isExpanded && _currentMediaInfo?.IsAnyMediaPlaying != true)
        {
            ClearBlurredBackgroundSources();
            HideLyricsBlurBackgroundImmediately();
        }
        if (VNotch.Controls.ActiveBlur.GetBlur(ExpandedContent) == null)
        {
            ExpandedContent.Effect = null;
        }

        FinalizeCompactModeAfterCollapse(contentToShow, suppressCompactThumbnailMotion, deferCompactThumbnailReveal);
        FinalizeCompactThumbnailAndVisualizerAfterCollapse(suppressCompactThumbnailMotion, deferCompactThumbnailReveal);
        // Closed again: the orb beside it can bud back out.
        UpdateActivityBubble();

        if (_settings.StayBehindWindows && _isDesktopEdgePromoted && !IsDesktopNotchInteractionActive())
        {
            ScheduleDesktopLayerDemotion();
        }

        if (_currentMediaInfo?.IsAnyMediaPlaying != true)
        {
            Task.Run(() => _mediaService.ArtworkService.TrimMemory(compactWorkingSet: true)).SafeFireAndForget("COLLAPSE-TRIM");
        }
    }

    private void ResetContentTransformAndEffectsAfterCollapse()
    {
        VNotch.Presenters.NotchContentTransitionPresenter.ResetElementVisualState(ExpandedContent);
        VNotch.Presenters.NotchContentTransitionPresenter.ResetElementVisualState(TimerContent);
        VNotch.Presenters.NotchContentTransitionPresenter.ResetElementVisualState(AudioContent);
        if (AudioScrollViewer != null)
        {
            VNotch.Presenters.NotchContentTransitionPresenter.ResetElementVisualState(AudioScrollViewer);
        }
        VNotch.Presenters.NotchContentTransitionPresenter.ResetElementVisualState(SecondaryContent);
        VNotch.Presenters.NotchContentTransitionPresenter.ResetElementVisualState(CameraContent);
        VNotch.Presenters.NotchContentTransitionPresenter.ResetElementVisualState(ClaudeContent);
        VNotch.Presenters.NotchContentTransitionPresenter.ResetElementVisualState(PiContent);
        VNotch.Presenters.NotchContentTransitionPresenter.ResetElementVisualState(SchoolContent);
        VNotch.Presenters.NotchContentTransitionPresenter.ResetElementVisualState(RobloxContent);

        ResetMediaContentReveal();
        ResetClockViewUnfoldIn();
        ResetCameraContentReveal();
        AudioContent?.ResetRowAnimations();

        ExpandedContentBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
        ExpandedContentBlur.Radius = 0;
        CollapsedContentBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
        CollapsedContentBlur.Radius = 0;
        MusicCompactContentBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
        MusicCompactContentBlur.Radius = 0;

        _isTimerView = false;
        _isSecondaryView = false;
        _isAudioView = false;
        _isCameraView = false;
        _isClaudeView = false;
        _isPiView = false;
        _isSchoolView = false;
        _isRobloxView = false;
    }

    private void PrepareStateBeforeCollapse()
    {

        System.Windows.Input.Keyboard.ClearFocus();


        _lastExpandedViewBeforeCollapse = DetermineCurrentExpandedView();

        if (IsCameraPreviewLifecycleActive)
        {
            StopCameraPreviewForViewTransition();
        }
        _isAnimating = true;
        SuspendSpotifyCanvasLifecycle();
        if (IsCountdownCompletionVisualActive)
        {
            SuppressCompactMediaChromeForCountdownCompletion();
        }
        CancelThumbnailSwitchAnimations();

        MediaPlayback.CancelRewindForNavigation();
        CancelAppNotificationBannerImmediate();

        EnsureTopmost();

        // The attached shell layer owns the exit.
        // Chrome stays attached until the closed shell settles.

    }

    private void OnExpandedContentFadeOutCompleted(int generation, bool wasSecondary, bool wasTimer)
    {
        if (generation != _viewTransitionGeneration) return;
        ExpandedContent.BeginAnimation(OpacityProperty, null);
        ExpandedContent.Opacity = 0;
        ExpandedContent.Visibility = Visibility.Collapsed;
        ExpandedContent.RenderTransform = null;

        if (wasSecondary) return;

        SecondaryContent.BeginAnimation(OpacityProperty, null);
        SecondaryContent.Opacity = 0;
        SecondaryContent.Visibility = Visibility.Collapsed;
        SecondaryContent.RenderTransform = null;
        if (!wasTimer)
        {
            TimerContent.Visibility = Visibility.Collapsed;
            TimerContent.Opacity = 0;
        }
    }

    private void ResetContentBlurAndOverlaysBeforeCollapse()
    {
        ExpandedContent.BeginAnimation(OpacityProperty, null);
        MusicCompactContent.BeginAnimation(OpacityProperty, null);
        CancelMediaWidthAnimation();
        ResetCalendarScroll();
        ResetCalendarHoverFocusVisualState();
        CollapsedContent.BeginAnimation(OpacityProperty, null);
        if (!_isThumbnailExpandAnimating && !_isThumbnailCollapseAnimating) ResetAnimationThumbnailOverlay();

        ExpandedContentBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
        CollapsedContentBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
        MusicCompactContentBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
    }

    private void CollapseNotch(long? transitionId = null, VNotch.Models.NotchView? fromView = null)
    {
        if (transitionId == null)
        {
            _transitionCoordinator.RequestCollapse("CollapseNotch");
            return;
        }

        // Debug-lock permission was checked when the coordinator accepted this
        // request. Debug view selection temporarily unlocks only that request.
        if (_isGreetingActive)
        {
            _transitionCoordinator.CancelTransition(transitionId.Value, "GreetingActive");
            return;
        }
        int generation = (int)transitionId;
        _viewTransitionGeneration = generation;
        _isAnimating = true;

        PrepareStateBeforeCollapse();
        bool wasAudio = _isAudioView;
        if (wasAudio)
        {
            StopAudioPoll();
            AudioContent.ReleaseSessionCache();
        }
        FrameworkElement compact = _isMusicCompactMode ? MusicCompactContent : CollapsedContent;
        FrameworkElement hidden = _isMusicCompactMode ? CollapsedContent : MusicCompactContent;
        hidden.Visibility = Visibility.Collapsed;
        compact.Visibility = Visibility.Visible;
        bool suppressThumbnail = IsCountdownCompletionVisualActive;
        CancelMediaThumbnailTransition();
        ResetContentBlurAndOverlaysBeforeCollapse();
        Motion.To(MediaBackground, OpacityProperty, 0, Motion.Exit);
        Motion.To(MediaBackground2, OpacityProperty, 0, Motion.Exit);
        NotchBorder.IsHitTestVisible = true;
        var plan = new VNotch.Models.TransitionPlan(generation,
            fromView ?? _transitionCoordinator.CurrentView, VNotch.Models.NotchView.Compact,
            VNotch.Controllers.NotchShapeState.Collapsing, _collapsedWidth, _collapsedHeight,
            _cornerRadiusCollapsed, new(_dur500, _easeExpOut6, AnimationConfig.TargetFps,
                AnimationConfig.ReduceMotion, Motion.ShellCloseWidth));
        ApplyShellReleaseVelocity();
        _notchShellPresenter!.AnimateShell(plan, result =>
        {
            if (result.Status != VNotch.Models.TransitionExecutionStatus.Completed) return;
            _notchContentPresenter!.SnapToView(VNotch.Models.NotchView.Compact);
            AnimateStatusBarReveal(false);
            OnCollapseCompleted(generation, wasAudio, compact, suppressThumbnail);
        });
        Motion.To(HoverGlow, OpacityProperty, 0, Motion.Exit);
    }

    #endregion

}
