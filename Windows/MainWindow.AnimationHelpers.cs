using System;
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
    #region Hover Animations

    private const double NotchHoverScaleX = 1.08;

    private bool _compactMarqueeRefreshQueued;

    private void AnimateNotchHover(bool isHovered)
    {
        if (_shellDragActive) return;
        if (_isExpanded || _isAnimating || _isGreetingActive || _isVolumeIndicatorActive || _spotlightMorphSessionActive || _spotlightMorphOwnsNotchVisibility) return;

        if (!isHovered && _isGestureActive) return;

        if (_isAppNotificationBannerVisible)
        {
            AnimateAppNotificationHover(isHovered);
            return;
        }

        Motion.To(HoverGlow, OpacityProperty, isHovered ? 0.35 : 0, Motion.Press);
        double targetScale = AnimationConfig.ReduceMotion ? 1 : isHovered ? NotchHoverScaleX : 1;
        int generation = BeginSpringHoverMotion();
        Motion.To(NotchShadowScale, ScaleTransform.ScaleXProperty, targetScale, Motion.Press);
        Motion.To(NotchScale, ScaleTransform.ScaleXProperty, targetScale, Motion.Press,
            done: () => EndSpringHoverMotion(generation));
    }

    private int BeginSpringHoverMotion()
    {
        int generation = ++_glassHoverGen;
        _glassHoverMotion = !AnimationConfig.ReduceMotion;
        UpdateGlassMotionState();
        return generation;
    }

    private void EndSpringHoverMotion(int generation)
    {
        if (generation != _glassHoverGen) return;
        _glassHoverMotion = false;
        UpdateGlassMotionState();
    }

    private void AnimateThumbnailHover(bool isHovered)
    {
        if (_isExpanded || _isAnimating || _isGreetingActive || _isVolumeIndicatorActive) return;

        if (!isHovered && _isGestureActive) return;

        if (_isAppNotificationBannerVisible)
        {
            AnimateAppNotificationHover(isHovered);
            return;
        }

        ResetAnimationThumbnailOverlay();

        if (!_isThumbnailSwitchActive)
        {
            ResetCompactThumbnailNextLayer();
        }

        bool islandMode = _settings.EnableDynamicIslandMode;
        double thumbScale;
        double notchWidth;
        double notchHeight;

        if (isHovered)
        {
            thumbScale = islandMode ? 1.28 : 1.5;
            notchWidth = _collapsedWidth + (islandMode ? 24 : 32);
            notchHeight = _collapsedHeight + (islandMode ? 22 : 36);
        }
        else
        {
            thumbScale = 1.0;
            notchWidth = _collapsedWidth;
            notchHeight = _collapsedHeight;
        }
        double infoOpacity = isHovered ? 1 : 0;

        int generation = BeginSpringHoverMotion();
        Motion.To(NotchBorder, WidthProperty, notchWidth, Motion.Press,
            done: () => EndSpringHoverMotion(generation));
        Motion.To(NotchBorder, HeightProperty, notchHeight, Motion.Press);

        if (isHovered)
        {
            ApplyCompactTitleContainerWidth(notchWidth);
        }

        CompactThumbnailBorder.RenderTransformOrigin = new Point(0, 0);

        Motion.To(CompactThumbnailScale, ScaleTransform.ScaleXProperty, thumbScale, Motion.Press);
        Motion.To(CompactThumbnailScale, ScaleTransform.ScaleYProperty, thumbScale, Motion.Press);

        if (isHovered)
        {
            CompactHoverInfo.Visibility = Visibility.Visible;

            CompactHoverInfo.UpdateLayout();
            UpdateCompactMarquee();

            QueueCompactMarqueeRefresh();
        }

        Motion.To(CompactHoverInfo, OpacityProperty, infoOpacity, Motion.Press, done: () =>
        {
            if (isHovered || _isCompactThumbnailHovered || CompactHoverInfo.Opacity >= 0.1) return;
            CompactHoverInfo.Visibility = Visibility.Collapsed;
            CompactHoverInfo.OpacityMask = null;
        });

        double radius;
        if (isHovered)
        {
            radius = islandMode ? notchHeight / 2.0 : 24;
        }
        else
        {
            radius = _cornerRadiusCollapsed;
        }
        AnimateCornerRadius(radius, Motion.Press);

        double thumbRadius = isHovered && islandMode ? 8 : 6;
        double startThumbRadius = CompactThumbnailBorder.CornerRadius.TopLeft;
        if (Math.Abs(thumbRadius - startThumbRadius) > 0.1)
        {
            Motion.To(this, CurrentCompactThumbnailRadiusProperty, thumbRadius, Motion.Press);
        }
    }

    private bool _compactMarqueeStopsWhenHidden;

    private void UpdateCompactMarquee()
    {
        if (_currentMediaInfo == null) return;
        if (!_compactMarqueeStopsWhenHidden)
        {
            _compactMarqueeStopsWhenHidden = true;
            // The marquee loops forever. Left running on hidden text it still repaints the whole notch window
            // every frame (a third of a core here) until the next hover, so it stops whenever the title hides.
            CompactHoverInfo.IsVisibleChanged += (_, _) =>
            {
                if (CompactHoverInfo.IsVisible) return;
                CompactTitleMarqueeTranslate.BeginAnimation(TranslateTransform.XProperty, null);
                CompactTitleMarqueeTranslate.X = 0;
            };
        }

        if (!AmbientAnimationsAllowed || !CompactHoverInfo.IsVisible)
        {
            CompactTitleMarqueeTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            CompactTitleMarqueeTranslate.X = 0;
            return;
        }
        CompactTitleMarquee.Text = _currentMediaInfo.CurrentTrack;
        CompactTitleMarqueeTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        CompactTitleMarquee.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        double textWidth = CompactTitleMarquee.DesiredSize.Width;

        double containerWidth = GetCompactTitleContainerWidth();
        if (containerWidth <= 0)
        {
            CompactHoverInfo.OpacityMask = null;
            CompactTitleMarqueeTranslate.X = 0;
            QueueCompactMarqueeRefresh();
            return;
        }

        const double marqueeTriggerOverflow = 10.0;

        if (textWidth > containerWidth + marqueeTriggerOverflow && containerWidth > 0)
        {

            CompactHoverInfo.OpacityMask = CompactMarqueeFadeBrush;
            MarqueeController.StartMarqueeAnimation(CompactTitleMarqueeTranslate, textWidth - containerWidth + 12);
        }
        else
        {

            CompactHoverInfo.OpacityMask = null;

            CompactTitleMarqueeTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            CompactTitleMarqueeTranslate.X = Math.Max(0, (containerWidth - textWidth) / 2);
        }
    }

    private void QueueCompactMarqueeRefresh()
    {
        if (_compactMarqueeRefreshQueued) return;
        _compactMarqueeRefreshQueued = true;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            _compactMarqueeRefreshQueued = false;
            if (!_isExpanded && !_isAnimating && _isMusicCompactMode && CompactHoverInfo.Visibility == Visibility.Visible)
            {
                UpdateCompactMarquee();
            }
        }), DispatcherPriority.Loaded);
    }

    private double GetCompactTitleContainerWidth()
    {
        double explicitWidth = CompactTitleScrollContainer.Width;
        if (double.IsFinite(explicitWidth) && explicitWidth > 0)
        {
            return explicitWidth;
        }

        double width = CompactTitleScrollContainer.ActualWidth;
        if (double.IsFinite(width) && width > 0)
        {
            return width;
        }

        try
        {
            CompactHoverInfo.UpdateLayout();
            width = CompactTitleScrollContainer.ActualWidth;
            if (double.IsFinite(width) && width > 0)
            {
                return width;
            }
        }
        catch
        {
            return 0;
        }

        return 0;
    }

    private void ApplyCompactTitleContainerWidth(double notchWidth)
    {
        if (!double.IsFinite(notchWidth) || notchWidth <= 0)
        {
            notchWidth = NotchBorder.ActualWidth > 0 ? NotchBorder.ActualWidth : _collapsedWidth;
        }

        double horizontalInset = MusicCompactContent.Margin.Left + MusicCompactContent.Margin.Right;
        double titleWidth = Math.Max(0, notchWidth - horizontalInset);
        CompactTitleScrollContainer.Width = titleWidth;
    }

    #endregion

    #region Animation Helpers

    private static void FadeSwitch(FrameworkElement from, FrameworkElement to)
    {

        Motion.To(from, OpacityProperty, 0, Motion.Exit, done: () =>
        {
            if (from.Opacity < 0.05) from.Visibility = Visibility.Collapsed;
        });
        bool entering = to.Visibility != Visibility.Visible;
        to.Visibility = Visibility.Visible;
        Motion.To(to, OpacityProperty, 1, Motion.Tile, from: entering ? 0 : null);
    }

    private static void AnimateIconSwitch(Canvas fromIcon, Canvas toIcon, TimeSpan duration, EasingFunctionBase easing) => Controls.ButtonFeedback.AnimateIconSwitch(fromIcon, toIcon, duration, easing);

    private static void PlayButtonPressAnimation(Border button) => Controls.ButtonFeedback.PlayButtonPressAnimation(button);

    private void PlayGentleButtonPressAnimation(Border button) => Controls.ButtonFeedback.PlayButtonPressAnimation(button, 0.90);

    private void PlayNextSkipAnimation()
    {
        PlayNextSkipAnimation(MediaPlayback.NextArrow0, MediaPlayback.NextArrow1);
    }

    private static void PlayNextSkipAnimation(System.Windows.Shapes.Path arrow0, System.Windows.Shapes.Path arrow1) => Controls.ButtonFeedback.PlayNextSkipAnimation(arrow0, arrow1);

    private void PlayPrevSkipAnimation()
    {
        PlayPrevSkipAnimation(MediaPlayback.PrevArrow0, MediaPlayback.PrevArrow2);
    }

    private static void PlayPrevSkipAnimation(System.Windows.Shapes.Path arrow0, System.Windows.Shapes.Path arrow2) => Controls.ButtonFeedback.PlayPrevSkipAnimation(arrow0, arrow2);

    private void PlayAppearAnimation()
    {
        bool showGreeting = _settings.EnableHelloGreeting;

        if (showGreeting)
        {
            _isGreetingActive = true;
            _isAnimating = true;
            NotchLiveContent.Visibility = Visibility.Hidden;
            PrepareGreetingPathsIfNeeded();
        }

        NotchBorder.Opacity = 0;

        var opacityAnim = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(400)),
            EasingFunction = _easeQuadOut
        };

        if (showGreeting)
        {
            opacityAnim.Completed += (s, e) =>
            {
                PlayGreetingAnimation();
            };
        }
        else
        {
            opacityAnim.Completed += (s, e) =>
            {
                StartStartupHold(TimeSpan.FromSeconds(3));
                SchedulePostAppearInitialization();
            };
        }

        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(opacityAnim, VNotch.Services.AnimationConfig.TargetFps);
        NotchBorder.BeginAnimation(OpacityProperty, opacityAnim);
    }

    public static readonly DependencyProperty CurrentCornerRadiusProperty =
        DependencyProperty.Register("CurrentCornerRadius", typeof(double), typeof(MainWindow),
            new PropertyMetadata(0.0, OnCurrentCornerRadiusChanged));

    public static readonly DependencyProperty CurrentThumbnailAnimationRadiusProperty =
        DependencyProperty.Register("CurrentThumbnailAnimationRadius", typeof(double), typeof(MainWindow),
            new PropertyMetadata(6.0, OnCurrentThumbnailAnimationRadiusChanged));

    public static readonly DependencyProperty CurrentCompactThumbnailRadiusProperty =
        DependencyProperty.Register("CurrentCompactThumbnailRadius", typeof(double), typeof(MainWindow),
            new PropertyMetadata(6.0, OnCurrentCompactThumbnailRadiusChanged));

    public double CurrentCornerRadius
    {
        get => (double)GetValue(CurrentCornerRadiusProperty);
        set => SetValue(CurrentCornerRadiusProperty, value);
    }

    public double CurrentThumbnailAnimationRadius
    {
        get => (double)GetValue(CurrentThumbnailAnimationRadiusProperty);
        set => SetValue(CurrentThumbnailAnimationRadiusProperty, value);
    }

    public double CurrentCompactThumbnailRadius
    {
        get => (double)GetValue(CurrentCompactThumbnailRadiusProperty);
        set => SetValue(CurrentCompactThumbnailRadiusProperty, value);
    }

    private static void OnCurrentCornerRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MainWindow window)
        {
            double radius = (double)e.NewValue;
            var cr = window.MakeNotchCornerRadius(radius);
            window.SyncShellCornerRadius(cr);
            window._glassMaterialPresenter.SyncCornerRadius(cr);
            window.UpdateNotchClip();
        }
    }

    private void InitializeShellCornerRadiusSync()
    {
        foreach (var layer in new[] { InnerClipBorder, NotchBackground, NotchBorderShadow, HoverGlow })
            layer.IsVisibleChanged += (_, _) =>
            {
                if (layer.IsVisible) layer.CornerRadius = NotchBorder.CornerRadius;
            };
    }

    private void SyncShellCornerRadius(CornerRadius radius)
    {
        NotchBorder.CornerRadius = radius;
        SyncVisibleRadius(InnerClipBorder, radius);
        SyncVisibleRadius(NotchBackground, radius);
        // removed MediaBackground CornerRadius sync
        // removed MediaBackground2 CornerRadius sync
        SyncVisibleRadius(NotchBorderShadow, radius);
        SyncVisibleRadius(HoverGlow, radius);
    }

    private static void SyncVisibleRadius(Border layer, CornerRadius radius)
    {
        if (layer.Visibility != Visibility.Collapsed && layer.CornerRadius != radius)
            layer.CornerRadius = radius;
    }

    private CornerRadius MakeNotchCornerRadius(double radius)
    {
        return _settings.EnableDynamicIslandMode
            ? new CornerRadius(radius, radius, radius, radius)
            : new CornerRadius(0, 0, radius, radius);
    }

    private bool _isModeTransitioning;
    private double _mtNotchW, _mtNotchH, _mtNotchBottomR;
    private double _mtIslandW, _mtIslandH, _mtIslandR;

    public static readonly DependencyProperty ModeTransitionTProperty =
        DependencyProperty.Register("ModeTransitionT", typeof(double), typeof(MainWindow),
            new PropertyMetadata(0.0, OnModeTransitionTChanged));

    public double ModeTransitionT
    {
        get => (double)GetValue(ModeTransitionTProperty);
        set => SetValue(ModeTransitionTProperty, value);
    }

    private static void OnModeTransitionTChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not MainWindow w) return;
        double t = (double)e.NewValue;

        double width = w._mtNotchW + (w._mtIslandW - w._mtNotchW) * t;
        double height = w._mtNotchH + (w._mtIslandH - w._mtNotchH) * t;
        // A spring may overshoot zero; WPF corner radii cannot be negative.
        double topRadius = Math.Max(0, w._mtIslandR * t);
        double bottomRadius = Math.Max(0, w._mtNotchBottomR + (w._mtIslandR - w._mtNotchBottomR) * t);
        double marginTop = DynamicIslandTopMargin * t;
        double earOpacity = 1.0 - t;

        w.ApplyModeTransitionFrame(width, height, topRadius, bottomRadius, marginTop, earOpacity);
    }

    private int _modeTransitionGeneration;

    private void AnimateModeTransition(int fps)
    {
        int gen = ++_modeTransitionGeneration;
        bool toIsland = _settings.EnableDynamicIslandMode;

        _mtNotchW = _settings.Width;
        _mtNotchH = _settings.Height;
        _mtNotchBottomR = _settings.CornerRadius;

        _mtIslandH = _settings.DynamicIslandHeight;
        _mtIslandW = _settings.DynamicIslandWidth;
        _mtIslandR = Math.Max(0, _mtIslandH / 2.0);

        bool retargeting = _isModeTransitioning;
        _isModeTransitioning = true;
        UpdateGlassMotionState();

        bool showEars = !toIsland && !IsLiquidGlassEnabled;
        var earVisStart = showEars ? Visibility.Visible : Visibility.Collapsed;
        double earOpacityStart = showEars ? 1.0 : 0.0;
        if (LeftEar != null) { LeftEar.Visibility = earVisStart; LeftEar.Opacity = earOpacityStart; }
        if (RightEar != null) { RightEar.Visibility = earVisStart; RightEar.Opacity = earOpacityStart; }
        if (LeftShadowEar != null) { LeftShadowEar.Visibility = earVisStart; LeftShadowEar.Opacity = earOpacityStart; }
        if (RightShadowEar != null) { RightShadowEar.Visibility = earVisStart; RightShadowEar.Opacity = earOpacityStart; }

        NotchBorder.BeginAnimation(WidthProperty, null);
        NotchBorder.BeginAnimation(HeightProperty, null);
        this.BeginAnimation(CurrentCornerRadiusProperty, null);

        double to = toIsland ? 1 : 0;
        Motion.To(this, ModeTransitionTProperty, to, Motion.Glide,
            from: retargeting ? null : toIsland ? 0 : 1, fps: fps, done: () =>
        {
            if (gen != _modeTransitionGeneration) return;
            Motion.Stop(this, ModeTransitionTProperty, to);
            FinalizeModeTransition(toIsland);
        });
    }

    private void ApplyModeTransitionFrame(double width, double height, double topRadius, double bottomRadius, double marginTop, double earOpacity)
    {
        NotchBorder.Width = width;
        NotchBorder.Height = height;

        var cr = new CornerRadius(topRadius, topRadius, bottomRadius, bottomRadius);
        SyncShellCornerRadius(cr);
        _glassMaterialPresenter.SyncCornerRadius(cr);

        if (NotchContainer != null)
        {
            var m = NotchContainer.Margin;
            if (Math.Abs(m.Top - marginTop) > 0.001)
            {
                NotchContainer.Margin = new Thickness(m.Left, marginTop, m.Right, m.Bottom);
            }
        }

        double effectiveEarOpacity = IsLiquidGlassEnabled ? 0.0 : earOpacity;
        if (LeftEar != null) LeftEar.Opacity = effectiveEarOpacity;
        if (RightEar != null) RightEar.Opacity = effectiveEarOpacity;
        if (LeftShadowEar != null) LeftShadowEar.Opacity = effectiveEarOpacity;
        if (RightShadowEar != null) RightShadowEar.Opacity = effectiveEarOpacity;

        UpdateNotchClip();
    }

    private void FinalizeModeTransition(bool toIsland)
    {
        _isModeTransitioning = false;
        UpdateGlassMotionState();

        NotchBorder.Width = _collapsedWidth;
        NotchBorder.Height = _collapsedHeight;

        var cr = MakeNotchCornerRadius(_cornerRadiusCollapsed);
        NotchBorder.CornerRadius = cr;
        InnerClipBorder.CornerRadius = cr;
        NotchBackground.CornerRadius = cr;
        // removed MediaBackground CornerRadius
        // removed MediaBackground2 CornerRadius
        NotchBorderShadow.CornerRadius = cr;
        HoverGlow.CornerRadius = cr;
        _glassMaterialPresenter.SyncCornerRadius(cr);
        CurrentCornerRadius = _cornerRadiusCollapsed;

        if (NotchContainer != null)
        {
            var m = NotchContainer.Margin;
            NotchContainer.Margin = new Thickness(m.Left, toIsland ? DynamicIslandTopMargin : 0, m.Right, m.Bottom);
        }

        UpdateEarVisibility();

        UpdateNotchClip();
        UpdateMediaBackgroundFootprint();
    }

    private double GetCollapsedWidth()
    {
        double width = _settings.EnableDynamicIslandMode
            ? _settings.DynamicIslandWidth
            : _settings.Width;

        // With nothing live the notch rests slim, showing the time and the weather.
        return IsIdleShape ? Math.Min(width, IdleCompactWidth) : width;
    }

    private double GetCollapsedHeight()
    {
        return _settings.EnableDynamicIslandMode
            ? _settings.DynamicIslandHeight
            : _settings.Height;
    }

    private double GetCollapsedCornerRadius()
    {
        return _settings.EnableDynamicIslandMode
            ? Math.Max(0, _collapsedHeight / 2.0)
            : _settings.CornerRadius;
    }

    private static void OnCurrentThumbnailAnimationRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not MainWindow window)
        {
            return;
        }

        double radius = (double)e.NewValue;
        var cornerRadius = new CornerRadius(radius);

        if (window.AnimationThumbnailBorder != null)
        {
            window.AnimationThumbnailBorder.CornerRadius = cornerRadius;
        }

        if (window.AnimationThumbnailRim != null)
        {
            window.AnimationThumbnailRim.CornerRadius = cornerRadius;
        }

        if (window.AnimationThumbnailClip != null)
        {
            window.AnimationThumbnailClip.RadiusX = radius;
            window.AnimationThumbnailClip.RadiusY = radius;
        }
    }

    private static void OnCurrentCompactThumbnailRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not MainWindow window) return;

        double radius = (double)e.NewValue;
        var cornerRadius = new CornerRadius(radius);
        window.CompactThumbnailBorder.CornerRadius = cornerRadius;
        if (window.CompactThumbnailRim != null)
        {
            window.CompactThumbnailRim.CornerRadius = cornerRadius;
        }
        if (window.CompactThumbnailClip != null)
        {
            window.CompactThumbnailClip.RadiusX = radius;
            window.CompactThumbnailClip.RadiusY = radius;
        }
    }

    private void AnimateCornerRadius(double targetRadius, TimeSpan duration) => AnimateCornerRadius(targetRadius, Motion.Glide);

    private void AnimateCornerRadius(double targetRadius, SpringMotion? spring)
    {
        if (spring is { } motion) Motion.To(this, CurrentCornerRadiusProperty, targetRadius, motion);
        else Motion.Stop(this, CurrentCornerRadiusProperty, targetRadius);
    }

    private void AnimateThumbnailAnimationRadius(double fromRadius, double toRadius, Duration duration, IEasingFunction easing, TimeSpan? beginTime = null) =>
        Motion.To(this, CurrentThumbnailAnimationRadiusProperty, toRadius, Motion.Glide, delay: beginTime);

    public void PlayTrackChangeBounce()
    {
        if (_isExpanded || _isAnimating) return;
        double restX = !_isGreetingActive && !_isVolumeIndicatorActive && NotchWrapper.IsMouseOver ? NotchHoverScaleX : 1;
        Motion.To(NotchScale, ScaleTransform.ScaleXProperty, restX, Motion.Pop, velocity: 2.8);
        Motion.To(NotchScale, ScaleTransform.ScaleYProperty, 1, Motion.Pop, velocity: -1.8);
        Motion.To(NotchShadowScale, ScaleTransform.ScaleXProperty, restX, Motion.Pop, velocity: 2.8);
        Motion.To(NotchShadowScale, ScaleTransform.ScaleYProperty, 1, Motion.Pop, velocity: -1.8);
    }

    #endregion

    #region Status Bar Animation

    private int _statusBarRevealVersion;
    private bool _statusBarRevealing;

    private void AnimateStatusBarReveal(bool show)
    {
        int version = ++_statusBarRevealVersion;
        _statusBarRevealing = show;
        bool animate = !_shellAttachment.IsAttached && !VNotch.Services.AnimationConfig.ReduceMotion && VNotch.Services.AnimationConfig.ClientAreaAnimation;
        bool chromeVisible = NavIconsPanel.Visibility == Visibility.Visible && NavIconsPanel.Opacity > 0.01;
        var targetView = _navigationVisualTarget ?? _transitionCoordinator.CurrentView;
        bool showBattery = show && _settings.ShowBatteryIndicator;
        bool showBackground = show && targetView is VNotch.Models.NotchView.Secondary
            or VNotch.Models.NotchView.Timer or VNotch.Models.NotchView.AudioMixer
            or VNotch.Models.NotchView.Claude or VNotch.Models.NotchView.Pi
            or VNotch.Models.NotchView.School or VNotch.Models.NotchView.Roblox;
        double restY = _settings.EnableDynamicIslandMode ? 5 : 0;
        double settingsY = targetView == VNotch.Models.NotchView.Secondary ? 0 : restY;
        var duration = new Duration(TimeSpan.FromMilliseconds(show ? 340 : 220));
        var settingsDelay = show ? TimeSpan.FromMilliseconds(56) : TimeSpan.Zero;

        SettingsButton.IsHitTestVisible = show;
        SettingsButton.Focusable = show;
        BatterySection.IsHitTestVisible = showBattery;
        NavIconsBackground.IsHitTestVisible = showBackground;
        NavIconsPanel.IsHitTestVisible = show;
        if (show)
        {
            bool movedSettingsHost = PlaceSharedSettingsButton(targetView);
            BatterySection.Visibility = showBattery ? Visibility.Visible : Visibility.Collapsed;
            NavIconsPanel.Visibility = Visibility.Visible;
            NavIconsBackground.Visibility = Visibility.Visible;
            SettingsButton.Visibility = Visibility.Visible;
            UpdateNavIconsActiveState(animate);
            // The chrome belongs to the shell. A tab switch must not replay its
            // entrance or expose the base values underneath a previous clock.
            if (!chromeVisible)
            {
                SettingsScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                SettingsScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                SettingsRotate.BeginAnimation(RotateTransform.AngleProperty, null);
                SettingsScale.ScaleX = SettingsScale.ScaleY = Motion.TileFromScale;
                SettingsRotate.Angle = 20;
            }
            else if (movedSettingsHost)
            {
                // Reparenting into the tray changes layout coordinates. Reveal in
                // the destination toolbar rather than displaying a teleport frame.
                SettingsButton.BeginAnimation(OpacityProperty, null);
                SettingsButton.Opacity = 0;
            }
        }

        RetargetChromeValue(BatterySection, OpacityProperty, showBattery ? 1 : 0, duration, animate);
        RetargetChromeValue(BatteryTranslate, TranslateTransform.YProperty, showBattery ? restY : -6, duration, animate);
        RetargetChromeValue(NavIconsPanel, OpacityProperty, show ? 1 : 0, duration, animate, () =>
        {
            if (version == _statusBarRevealVersion && !show) NavIconsPanel.Visibility = Visibility.Collapsed;
        });
        RetargetChromeValue(NavIconsTranslate, TranslateTransform.YProperty, show ? restY : -6, duration, animate);
        RetargetChromeValue(NavIconsBackground, OpacityProperty, showBackground ? 1 : 0,
            new Duration(TimeSpan.FromMilliseconds(show ? 340 : 120)), animate, () =>
            {
                if (version == _statusBarRevealVersion && !showBackground) NavIconsBackground.Visibility = Visibility.Collapsed;
            });
        RetargetChromeValue(SettingsTranslate, TranslateTransform.YProperty, show ? settingsY : -6, duration, animate,
            easing: _easeBackOut022, beginTime: settingsDelay);
        var settingsScaleDuration = new Duration(TimeSpan.FromMilliseconds(show ? 460 : 200));
        RetargetChromeValue(SettingsScale, ScaleTransform.ScaleXProperty, show ? 1 : 0.86, settingsScaleDuration, animate,
            easing: _easeBackOut022, beginTime: settingsDelay);
        RetargetChromeValue(SettingsScale, ScaleTransform.ScaleYProperty, show ? 1 : 0.86, settingsScaleDuration, animate,
            easing: _easeBackOut022, beginTime: settingsDelay);
        RetargetChromeValue(SettingsRotate, RotateTransform.AngleProperty, show ? 45 : 20,
            new Duration(TimeSpan.FromMilliseconds(show ? 520 : 200)), animate, beginTime: settingsDelay);
        RetargetChromeValue(SettingsButton, OpacityProperty, show ? 1 : 0, duration, animate, () =>
        {
            if (version == _statusBarRevealVersion && !show) HideSharedSettingsButton();
        }, beginTime: settingsDelay);

        if (UpdateNotificationButton == null) return;
        bool showUpdate = show && _isUpdateAvailable;
        UpdateNotificationButton.IsHitTestVisible = showUpdate;
        if (showUpdate)
        {
            bool alreadyVisible = UpdateNotificationButton.Visibility == Visibility.Visible && UpdateNotificationButton.Opacity > 0.01;
            UpdateNotificationButton.Visibility = Visibility.Visible;
            UpdateNotificationButton.Cursor = System.Windows.Input.Cursors.Hand;
            if (!alreadyVisible)
            {
                if (UpdateIconBrush != null)
                {
                    UpdateIconBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
                    UpdateIconBrush.Color = Color.FromRgb(48, 209, 88);
                }
                StartUpdatePulseAnimation();
            }
        }
        else StopUpdatePulseAnimation();
        var updateDelay = show ? TimeSpan.FromMilliseconds(30) : TimeSpan.Zero;
        RetargetChromeValue(UpdateNotificationTranslate, TranslateTransform.YProperty, showUpdate ? restY : -4, duration, animate,
            beginTime: updateDelay);
        RetargetChromeValue(UpdateNotificationButton, OpacityProperty, showUpdate ? 1 : 0, duration, animate,
            beginTime: updateDelay);
    }

    private void RetargetChromeValue(IAnimatable target, DependencyProperty property,
        double to, Duration duration, bool animate, Action? completed = null,
        IEasingFunction? easing = null, TimeSpan? beginTime = null)
    {
        var element = (DependencyObject)target;
        if (!animate)
        {
            Motion.Stop(element, property, to);
            completed?.Invoke();
            return;
        }
        Motion.To(element, property, to, _statusBarRevealing ? Motion.Tile : Motion.Exit,
            delay: _statusBarRevealing ? beginTime : null, done: () =>
            {
                // Other shell paths clear clocks; keep the settled state as the base value too.
                Motion.Stop(element, property, to);
                completed?.Invoke();
            });
    }

    #endregion

    #region Settings Absorb Animation
    public void PlaySettingsEjectAnimation()
    {
        if (_isExpanded || _isAnimating) return;
        SpringSettingsFeedback(900, 180, 3);
    }

    public void PlaySettingsAbsorbAnimation()
    {
        if (_isExpanded || _isAnimating) return;
        SpringSettingsFeedback(1200, 220, 2.5);
    }

    private void SpringSettingsFeedback(double widthVelocity, double heightVelocity, double scaleVelocity)
    {
        Motion.To(NotchBorder, WidthProperty, _collapsedWidth, Motion.Pop, velocity: widthVelocity);
        Motion.To(NotchBorder, HeightProperty, _collapsedHeight, Motion.Pop, velocity: heightVelocity);
        Motion.To(NotchScale, ScaleTransform.ScaleYProperty, 1, Motion.Pop, velocity: scaleVelocity);
        Motion.To(NotchShadowScale, ScaleTransform.ScaleYProperty, 1, Motion.Pop, velocity: scaleVelocity);
        AnimateCornerRadius(_cornerRadiusCollapsed, Motion.Pop);
    }

    #endregion

    #region Apple-Style Collapse Fade Out

    private void AnimateExpandedContentFadeOut()
    {
        if (!_isExpanded || ExpandedContent.Visibility != Visibility.Visible) return;

        int fps = VNotch.Services.AnimationConfig.TargetFps;

        var baseDuration = new Duration(TimeSpan.FromMilliseconds(180));
        var easing = _easeQuadIn;

        var wave1Delay = TimeSpan.Zero;

        if (MediaPlayback.PlayPauseButton != null && MediaPlayback.PlayPauseButton.Visibility == Visibility.Visible)
        {
            double currentOpacity = MediaPlayback.PlayPauseButton.Opacity;
            if (currentOpacity > 0.01)
            {
                MediaPlayback.PlayPauseButton.BeginAnimation(OpacityProperty, null);
                var fadeAnim = MakeAnim(currentOpacity, 0, baseDuration, easing, wave1Delay);
                Timeline.SetDesiredFrameRate(fadeAnim, fps);
                MediaPlayback.PlayPauseButton.BeginAnimation(OpacityProperty, fadeAnim);
            }
        }

        if (MediaPlayback.PrevButton != null && MediaPlayback.PrevButton.Visibility == Visibility.Visible)
        {
            double currentOpacity = MediaPlayback.PrevButton.Opacity;
            if (currentOpacity > 0.01)
            {
                MediaPlayback.PrevButton.BeginAnimation(OpacityProperty, null);
                var fadeAnim = MakeAnim(currentOpacity, 0, baseDuration, easing, wave1Delay);
                Timeline.SetDesiredFrameRate(fadeAnim, fps);
                MediaPlayback.PrevButton.BeginAnimation(OpacityProperty, fadeAnim);
            }
        }

        if (MediaPlayback.NextButton != null && MediaPlayback.NextButton.Visibility == Visibility.Visible)
        {
            double currentOpacity = MediaPlayback.NextButton.Opacity;
            if (currentOpacity > 0.01)
            {
                MediaPlayback.NextButton.BeginAnimation(OpacityProperty, null);
                var fadeAnim = MakeAnim(currentOpacity, 0, baseDuration, easing, wave1Delay);
                Timeline.SetDesiredFrameRate(fadeAnim, fps);
                MediaPlayback.NextButton.BeginAnimation(OpacityProperty, fadeAnim);
            }
        }

        var wave2Delay = TimeSpan.FromMilliseconds(20);

        if (MediaPlayback.ProgressBarContainer != null && MediaPlayback.ProgressBarContainer.Visibility == Visibility.Visible)
        {
            double currentOpacity = MediaPlayback.ProgressBarContainer.Opacity;
            if (currentOpacity > 0.01)
            {
                MediaPlayback.ProgressBarContainer.BeginAnimation(OpacityProperty, null);
                var fadeAnim = MakeAnim(currentOpacity, 0, baseDuration, easing, wave2Delay);
                Timeline.SetDesiredFrameRate(fadeAnim, fps);
                MediaPlayback.ProgressBarContainer.BeginAnimation(OpacityProperty, fadeAnim);
            }
        }

        if (MediaPlayback.RemainingTimeText != null && MediaPlayback.RemainingTimeText.Visibility == Visibility.Visible)
        {
            double currentOpacity = MediaPlayback.RemainingTimeText.Opacity;
            if (currentOpacity > 0.01)
            {
                MediaPlayback.RemainingTimeText.BeginAnimation(OpacityProperty, null);
                var fadeAnim = MakeAnim(currentOpacity, 0, baseDuration, easing, wave2Delay);
                Timeline.SetDesiredFrameRate(fadeAnim, fps);
                MediaPlayback.RemainingTimeText.BeginAnimation(OpacityProperty, fadeAnim);
            }
        }

        var wave3Delay = TimeSpan.FromMilliseconds(40);

        if (CalendarWidget != null && CalendarWidget.Visibility == Visibility.Visible)
        {
            double currentOpacity = CalendarWidget.Opacity;
            if (currentOpacity > 0.01)
            {
                CalendarWidget.BeginAnimation(OpacityProperty, null);
                var fadeAnim = MakeAnim(currentOpacity, 0, baseDuration, easing, wave3Delay);
                Timeline.SetDesiredFrameRate(fadeAnim, fps);
                CalendarWidget.BeginAnimation(OpacityProperty, fadeAnim);
            }
        }

        if (ShouldShowMediaBlurBackground && LyricsBlurBackground != null && LyricsBlurBackground.Visibility == Visibility.Visible)
        {
            double currentOpacity = LyricsBlurBackground.Opacity;
            if (currentOpacity > 0.01)
            {
                var wave4Delay = TimeSpan.FromMilliseconds(30);
                LyricsBlurBackground.BeginAnimation(OpacityProperty, null);
                var blurFadeAnim = MakeAnim(currentOpacity, 0, new Duration(TimeSpan.FromMilliseconds(150)), easing, wave4Delay);
                Timeline.SetDesiredFrameRate(blurFadeAnim, fps);
                LyricsBlurBackground.BeginAnimation(OpacityProperty, blurFadeAnim);
            }
        }

        if (MusicViz != null && MusicViz.Visibility == Visibility.Visible)
        {
            double currentOpacity = MusicViz.Opacity;
            if (currentOpacity > 0.01)
            {
                MusicViz.BeginAnimation(OpacityProperty, null);
                var fadeAnim = MakeAnim(currentOpacity, 0, new Duration(TimeSpan.FromMilliseconds(140)), easing, TimeSpan.FromMilliseconds(15));
                Timeline.SetDesiredFrameRate(fadeAnim, fps);
                MusicViz.BeginAnimation(OpacityProperty, fadeAnim);
            }
        }
    }

    private void RestoreExpandedContentOpacity()
    {
        if (MediaPlayback.PlayPauseButton != null)
        {
            MediaPlayback.PlayPauseButton.BeginAnimation(OpacityProperty, null);
            if (MediaPlayback.PlayPauseButton.Visibility == Visibility.Visible)
                MediaPlayback.PlayPauseButton.Opacity = 1.0;
        }

        if (MediaPlayback.PrevButton != null)
        {
            MediaPlayback.PrevButton.BeginAnimation(OpacityProperty, null);
            if (MediaPlayback.PrevButton.Visibility == Visibility.Visible)
                MediaPlayback.PrevButton.Opacity = 1.0;
        }

        if (MediaPlayback.NextButton != null)
        {
            MediaPlayback.NextButton.BeginAnimation(OpacityProperty, null);
            if (MediaPlayback.NextButton.Visibility == Visibility.Visible)
                MediaPlayback.NextButton.Opacity = 1.0;
        }

        if (MediaPlayback.ProgressBarContainer != null)
        {
            MediaPlayback.ProgressBarContainer.BeginAnimation(OpacityProperty, null);
            if (MediaPlayback.ProgressBarContainer.Visibility == Visibility.Visible)
                MediaPlayback.ProgressBarContainer.Opacity = 1.0;
        }

        if (MediaPlayback.RemainingTimeText != null)
        {
            MediaPlayback.RemainingTimeText.BeginAnimation(OpacityProperty, null);
            if (MediaPlayback.RemainingTimeText.Visibility == Visibility.Visible)
                MediaPlayback.RemainingTimeText.Opacity = 1.0;
        }

        if (MediaRightColumn != null)
            MediaRightColumn.Visibility = IsNoWidgetMode && !_isLyricsActive ? Visibility.Collapsed : Visibility.Visible;
        if (CalendarWidget != null)
        {
            if (_isLyricsActive)
            {
                // Lyrics are active — ensure calendar is suppressed and lyrics widget is visible
                CalendarWidget.BeginAnimation(OpacityProperty, null);
                CalendarWidget.Visibility = Visibility.Collapsed;
                CalendarWidget.Opacity = 0;
                if (LyricsWidget != null)
                {
                    LyricsWidget.BeginAnimation(OpacityProperty, null);
                    LyricsWidget.Visibility = Visibility.Visible;
                    LyricsWidget.Opacity = 1.0;

                    if (_currentLyricIndex < 0 && !_isLyricsSearchVisible && LyricsPlaceholderPanel != null && LyricsPlaceholderPanel.Visibility != Visibility.Visible)
                    {
                        string placeholderTitle = _currentMediaInfo?.CurrentTrack ?? "";
                        string placeholderArtist = _currentMediaInfo?.CurrentArtist ?? "";
                        ShowLyricsPlaceholder(placeholderTitle, placeholderArtist, _lyricsProvider);
                    }
                }
            }
            else
            {
                CalendarWidget.BeginAnimation(OpacityProperty, null);
                if (IsNoWidgetMode) CalendarWidget.Visibility = Visibility.Collapsed;
                if (CalendarWidget.Visibility == Visibility.Visible)
                    CalendarWidget.Opacity = 1.0;
            }
        }

        if (MusicViz != null)
        {
            MusicViz.BeginAnimation(OpacityProperty, null);
            if (MusicViz.Visibility == Visibility.Visible)
                MusicViz.Opacity = 1.0;
        }

        if (GreetingSection != null)
        {
            GreetingSection.BeginAnimation(OpacityProperty, null);
            if (_isLyricsActive)
            {
                GreetingSection.Visibility = Visibility.Collapsed;
                GreetingSection.Opacity = 0;
            }
            else
            {
                bool showGreeting = ShouldShowGreetingSection;
                GreetingSection.Visibility = showGreeting ? Visibility.Visible : Visibility.Collapsed;
                if (showGreeting)
                    GreetingSection.Opacity = 1.0;
            }
        }
    }


    private void RestoreTimerContentOpacity()
    {
        if (CountdownWidget.CountdownDisplay != null)
        {
            CountdownWidget.CountdownDisplay.BeginAnimation(OpacityProperty, null);
            CountdownWidget.CountdownDisplay.Opacity = 1.0;
        }

        if (CountdownWidget.CountdownProgressFill != null)
        {
            CountdownWidget.CountdownProgressFill.BeginAnimation(OpacityProperty, null);
            CountdownWidget.CountdownProgressFill.Opacity = 1.0;
        }

        if (CountdownWidget.CountdownDisplayPanel != null)
        {
            CountdownWidget.CountdownDisplayPanel.BeginAnimation(OpacityProperty, null);
            CountdownWidget.CountdownDisplayPanel.Opacity = 1.0;
        }

        if (CountdownWidget.CountdownStartBtn != null)
        {
            CountdownWidget.CountdownStartBtn.BeginAnimation(OpacityProperty, null);
            CountdownWidget.CountdownStartBtn.Opacity = 1.0;
        }

        if (CountdownWidget.CountdownResetBtn != null)
        {
            CountdownWidget.CountdownResetBtn.BeginAnimation(OpacityProperty, null);
            CountdownWidget.CountdownResetBtn.Opacity = 1.0;
        }

        if (CountdownWidget.CountdownStepperCapsule != null)
        {
            CountdownWidget.CountdownStepperCapsule.BeginAnimation(OpacityProperty, null);
            // The stepper stays dimmed while a countdown is running.
            CountdownWidget.CountdownStepperCapsule.Opacity = _isCountdownRunning ? 0.4 : 1.0;
        }
    }

    #endregion
}
