using System;
using System.Diagnostics;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using VNotch.Controllers;
using VNotch.Presenters;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;
using static VNotch.Services.Win32Interop;

namespace VNotch;

public partial class MainWindow
{

    private bool _isTimerView
    {
        get;
        set;
    }


    private double CountdownCompleteViewWidth => _collapsedWidth;
    private double CountdownCompleteViewHeight => _collapsedHeight;

    private CountdownController CountdownController => CountdownWidget.Controller;
    private TimeSpan _countdownDuration => _viewModel.Timer.Duration;
    private bool _isCountdownRunning => CountdownController.IsRunning;

    #region Timer View Navigation

    private void TimerIconButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_isAudioView && !_isAnimating)
        {
            SwitchFromAudioToTimerView();
            return;
        }
        if (!_isTimerView && !_isAnimating)
        {
            if (_isSecondaryView)
            {
                StopCameraPreviewForViewExit();
            }
            SwitchToTimerView();
        }
    }

    private void SwitchToTimerView(long? transitionId = null)
    {
        if (transitionId == null)
        {
            _transitionCoordinator.RequestView(VNotch.Models.NotchView.Timer, "SwitchToTimerView");
            return;
        }

        int generation = (int)transitionId;
        _viewTransitionGeneration = generation;
        _isTimerView = true;
        _isSecondaryView = false;
        _isAudioView = false;
        _isAnimating = true;
        SuspendSpotifyCanvasLifecycle();

        HideMediaBackgroundOverlay();
        if (LyricsBlurBackground != null && LyricsBlurBackground.Visibility == Visibility.Visible)
        {
            LyricsBlurBackground.BeginAnimation(OpacityProperty, null);
            LyricsBlurBackground.Opacity = 0;
            LyricsBlurBackground.Visibility = Visibility.Collapsed;
        }

        UpdateTimerNavIconsState();
        NavIconsPanel.Visibility = Visibility.Visible;
        NavIconsPanel.Opacity = 1;

        NavIconsBackground.Visibility = Visibility.Visible;
        Motion.To(NavIconsBackground, OpacityProperty, 1, Motion.Tile, from: 0, delay: Motion.Stagger(1));

        NotchBorder.IsHitTestVisible = false;

        ApplyClockViewWindowSize();
        PrepareClockViewContentSize();
        RefreshClockView();

        var durOut = new Duration(TimeSpan.FromMilliseconds(190));
        var durIn = new Duration(TimeSpan.FromMilliseconds(440));
        var inDelay = TimeSpan.FromMilliseconds(40);
        int fps = VNotch.Services.AnimationConfig.TargetFps;

        var primaryTranslate = new TranslateTransform(0, ExpandedContentRestY);
        ExpandedContent.RenderTransform = primaryTranslate;

        var fadeOut = MakeAnim(1, 0, durOut, _easeQuadInOut);

        bool useContentBlur = _settings.EnableBlurEffects && !IsLiquidGlassEnabled;
        BlurEffect? expandedBlur = null;
        DoubleAnimation? blurOutAnim = null;
        if (useContentBlur)
        {
            expandedBlur = ExpandedContent.Effect as BlurEffect ?? new BlurEffect { Radius = 0, RenderingBias = RenderingBias.Performance };
            ExpandedContent.Effect = expandedBlur;
            blurOutAnim = MakeAnim(0, 6, durOut, _easeAppleIn);
        }

        fadeOut.Completed += (s, ev) =>
        {
            if (generation != _viewTransitionGeneration) return;
            ExpandedContent.Visibility = Visibility.Collapsed;
            ExpandedContent.RenderTransform = null;
            ExpandedContent.Effect = null;
            if (expandedBlur != null) expandedBlur.Radius = 0;
        };

        ExpandedContent.BeginAnimation(OpacityProperty, fadeOut);
        Motion.To(primaryTranslate, TranslateTransform.YProperty, ExpandedContentRestY - 10, Motion.Exit);
        if (expandedBlur != null && blurOutAnim != null)
            expandedBlur.BeginAnimation(BlurEffect.RadiusProperty, blurOutAnim);

        AnimateClockViewNotchResize(
            NotchBorder.ActualWidth > 0 ? NotchBorder.ActualWidth : _expandedWidth,
            NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight : _expandedHeight,
            _clockViewWidth, _clockViewHeight, durIn, inDelay, generation: generation);

        PlayTimerViewEntrance(durIn, inDelay, generation);

        CountdownWidget.RefreshProgress();
    }

    // control bar rises from below — three staggered layers.
    private void PlayTimerViewEntrance(Duration durIn, TimeSpan inDelay, int generation)
    {
        bool entering = TimerContent.Visibility != Visibility.Visible;
        TimerContent.Visibility = Visibility.Visible;
        var timerTranslate = TimerContent.RenderTransform as TranslateTransform ?? new TranslateTransform();
        TimerContent.RenderTransform = timerTranslate;
        Motion.To(TimerContent, OpacityProperty, 1, Motion.Tile, from: entering ? 0 : null, delay: inDelay);
        Motion.To(timerTranslate, TranslateTransform.YProperty, 0, Motion.Tile, from: entering ? -22 : null, delay: inDelay, done: () =>
        {
            if (generation != _viewTransitionGeneration) return;
            _isAnimating = false;
            NotchBorder.IsHitTestVisible = true;
            if (!_isTimerView) return;
            RestoreTimerContentOpacity();
            _transitionCoordinator.CompleteTransition(generation);
        });
        TimerContent.InvalidateMeasure();
        TimerContent.InvalidateArrange();
        PlayClockViewUnfoldIn(inDelay);
    }

    private int _clockViewUnfoldGeneration;

    private void ResetClockViewUnfoldIn()
    {
        _clockViewUnfoldGeneration++;
        if (ClockViewHeader != null)
        {
            ClockViewHeader.BeginAnimation(OpacityProperty, null);
            ClockViewHeader.Opacity = 1;
            ClockViewHeader.RenderTransform = null;
        }
        FrameworkElement[] clockWidgets = [ClockViewClock, ClockViewDigitalClock, ClockViewWordClock];
        foreach (var w in clockWidgets)
        {
            if (w != null) w.RenderTransform = null;
        }
        if (CountdownWidget?.TimerControlBar != null)
        {
            CountdownWidget.TimerControlBar.BeginAnimation(OpacityProperty, null);
            CountdownWidget.TimerControlBar.Opacity = 1;
            CountdownWidget.TimerControlBar.RenderTransform = null;
        }
        if (WhiteNoiseWidget != null)
        {
            WhiteNoiseWidget.BeginAnimation(OpacityProperty, null);
            WhiteNoiseWidget.Opacity = 1;
            WhiteNoiseWidget.RenderTransform = null;
        }
    }

    private void PlayClockViewUnfoldIn(TimeSpan baseDelay)
    {
        ControlCenter.RevealRows(baseDelay);
        ++_clockViewUnfoldGeneration;
        if (AnimationConfig.ReduceMotion)
        {
            ResetClockViewUnfoldIn();
            return;
        }
        var headerTranslate = ClockViewHeader.RenderTransform as TranslateTransform ?? new TranslateTransform(0, -14);
        ClockViewHeader.RenderTransform = headerTranslate;
        Motion.To(ClockViewHeader, OpacityProperty, 1, Motion.Tile, from: 0, delay: baseDelay);
        Motion.To(headerTranslate, TranslateTransform.YProperty, 0, Motion.Tile, delay: baseDelay);
        FrameworkElement fallbackClock = ClockViewWordClock.Visibility == Visibility.Visible ? ClockViewWordClock : ClockViewClock;
        FrameworkElement activeClock = ClockViewDigitalClock.Visibility == Visibility.Visible ? ClockViewDigitalClock : fallbackClock;
        var clockScale = activeClock.RenderTransform as ScaleTransform ?? new ScaleTransform(Motion.TileFromScale, Motion.TileFromScale);
        activeClock.RenderTransform = clockScale;
        activeClock.RenderTransformOrigin = new Point(0.5, 0.5);
        Motion.To(clockScale, ScaleTransform.ScaleXProperty, 1, Motion.TileScale, delay: baseDelay + Motion.Stagger(1));
        Motion.To(clockScale, ScaleTransform.ScaleYProperty, 1, Motion.TileScale, delay: baseDelay + Motion.Stagger(1));
        FrameworkElement[] rows = [CountdownWidget.TimerControlBar, WhiteNoiseWidget];
        for (int i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            var translate = row.RenderTransform as TranslateTransform ?? new TranslateTransform(0, 16);
            row.RenderTransform = translate;
            var delay = baseDelay + Motion.Stagger(i + 2);
            Motion.To(row, OpacityProperty, 1, Motion.Tile, from: 0, delay: delay);
            Motion.To(translate, TranslateTransform.YProperty, 0, Motion.Tile, delay: delay);
        }
    }

    private void SwitchFromTimerToPrimaryView(long? transitionId = null)
    {
        if (transitionId == null)
        {
            _transitionCoordinator.RequestView(VNotch.Models.NotchView.Media, "SwitchFromTimerToPrimaryView");
            return;
        }

        int generation = (int)transitionId;
        _viewTransitionGeneration = generation;
        CountdownWidget.CancelEditing();
        _isTimerView = false;
        _isAnimating = true;

        UpdateNavIconsActiveState();
        NavIconsBackground.BeginAnimation(OpacityProperty, null);
        NavIconsBackground.Opacity = 0;
        NavIconsBackground.Visibility = Visibility.Collapsed;

        MusicCompactContent.BeginAnimation(OpacityProperty, null);
        MusicCompactContent.Opacity = 0;
        MusicCompactContent.Visibility = Visibility.Collapsed;
        CollapsedContent.BeginAnimation(OpacityProperty, null);
        CollapsedContent.Opacity = 0;
        CollapsedContent.Visibility = Visibility.Collapsed;

        NotchBorder.IsHitTestVisible = false;

        var durScroll = new Duration(TimeSpan.FromMilliseconds(420));
        var durIn = new Duration(TimeSpan.FromMilliseconds(440));
        var inDelay = TimeSpan.FromMilliseconds(30);
        int fps = VNotch.Services.AnimationConfig.TargetFps;

        var timerTranslate = new TranslateTransform(0, 0);
        TimerContent.RenderTransform = timerTranslate;
        TimerContent.RenderTransformOrigin = new Point(0.5, 0.5);

        var timerFadeOut = MakeAnim(1, 0, new Duration(TimeSpan.FromMilliseconds(210)), _easeQuadInOut);
        Timeline.SetDesiredFrameRate(timerFadeOut, fps);

        timerFadeOut.Completed += (s, ev) =>
        {
            if (generation != _viewTransitionGeneration) return;
            if (_isTimerView) return;
            TimerContent.Visibility = Visibility.Collapsed;
            TimerContent.RenderTransform = null;
            TimerContent.BeginAnimation(OpacityProperty, null);
            TimerContent.Opacity = 0;
        };

        TimerContent.BeginAnimation(OpacityProperty, timerFadeOut);
        Motion.To(timerTranslate, TranslateTransform.YProperty, 40, Motion.Exit);

        double currentH = NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight : _clockViewHeight;
        double currentWidthExit = NotchBorder.ActualWidth > 0 ? NotchBorder.ActualWidth : _clockViewWidth;
        _shellState.UpdateExpandedWidth(IsNoWidgetMode, _isLyricsActive, _isMusicExpanded);
        AnimateClockViewNotchResize(currentWidthExit, currentH, _expandedWidth, _expandedHeight, durIn, inDelay, RestoreExpandedWindowSize, generation: generation);

        ExpandedContent.Visibility = Visibility.Visible;
        ExpandedContent.BeginAnimation(OpacityProperty, null);
        ExpandedContent.Opacity = 0;
        ExpandedContent.Effect = null;
        CancelMediaWidthAnimation();
        ExpandedContent.Width = _expandedWidth - 16;
        ExpandedContent.Height = _expandedHeight - 10;
        ExpandedContent.HorizontalAlignment = HorizontalAlignment.Center;
        ExpandedContent.VerticalAlignment = VerticalAlignment.Top;
        ExpandedContent.UseLayoutRounding = false;
        ExpandedContent.UpdateLayout();

        PrepareExpandedContentLayoutForReveal();

        if (_currentMediaInfo?.Thumbnail != null && _currentMediaInfo.IsAnyMediaPlaying)
        {
            var palette = DynamicIslandColorExtractor.GetDynamicIslandPalette(_currentMediaInfo.Thumbnail);
            var subColor = LiftDarkColor(palette.Sub);
            var vibrantColor = Color.FromRgb(subColor.R, subColor.G, subColor.B);
            var darkColor = Color.FromArgb(vibrantColor.A,
                (byte)(vibrantColor.R * 0.65),
                (byte)(vibrantColor.G * 0.65),
                (byte)(vibrantColor.B * 0.65));

            MediaPlayback.ProgressBarGradientStart.BeginAnimation(GradientStop.ColorProperty, null);
            MediaPlayback.ProgressBarGradientEnd.BeginAnimation(GradientStop.ColorProperty, null);
            MediaPlayback.ProgressBarGradientStart.Color = vibrantColor;
            MediaPlayback.ProgressBarGradientEnd.Color = darkColor;
        }

        var primaryTranslate = new TranslateTransform(0, ExpandedContentRestY - 16);
        ExpandedContent.RenderTransform = primaryTranslate;

        var primaryFadeIn = MakeAnim(0, 1, durIn, _easeAppleOut, inDelay);
        Timeline.SetDesiredFrameRate(primaryFadeIn, fps);

        primaryFadeIn.Completed += (s, ev) =>
        {
            if (generation != _viewTransitionGeneration) return;
            _isAnimating = false;
            NotchBorder.IsHitTestVisible = true;
            ExpandedContent.Opacity = 1;
            ExpandedContent.BeginAnimation(OpacityProperty, null);
            RestoreExpandedContentRestLayout();
            ResumeSpotifyCanvasLifecycle();
            _transitionCoordinator.CompleteTransition(generation);

            ShowMediaBackground();

            FadeInLyricsBlurBackgroundIfActive();
        };

        ExpandedContent.BeginAnimation(OpacityProperty, primaryFadeIn);
        Motion.To(primaryTranslate, TranslateTransform.YProperty, ExpandedContentRestY, Motion.Tile, delay: inDelay);
        PlayMediaContentReveal(inDelay);
    }

    private void UpdateTimerNavIconsState()
    {
        // Expansion can call this before the local view flags are committed.
        // Set the timer state explicitly and release any previous opacity clocks.
        if (_navDragItem != HomeIconButton) AnimateNavIconOpacity(HomeIconButton, 0.45, animate: false);
        if (_navDragItem != ClipboardIconButton) AnimateNavIconOpacity(ClipboardIconButton, 0.45, animate: false);
        if (_navDragItem != TimerIconButton) AnimateNavIconOpacity(TimerIconButton, 1.0, animate: false);
        if (_navDragItem != AudioIconButton) AnimateNavIconOpacity(AudioIconButton, 0.45, animate: false);
        if (_navDragItem != ClaudeIconButton) AnimateNavIconOpacity(ClaudeIconButton, 0.45, animate: false);
        if (_navDragItem != PiIconButton) AnimateNavIconOpacity(PiIconButton, 0.45, animate: false);
        if (_navDragItem != SchoolIconButton) AnimateNavIconOpacity(SchoolIconButton, 0.45, animate: false);
        if (_navDragItem != WeatherIconButton) AnimateNavIconOpacity(WeatherIconButton, 0.45, animate: false);
    }

    #endregion

    #region Countdown Logic

    private void TimerControlButton_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Border button) Controls.CountdownView.AnimateTimerButtonScale(button, 1.045);
    }

    private void TimerControlButton_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Border button) Controls.CountdownView.AnimateTimerButtonScale(button, 1);
    }

    private void OnCountdownCompleted()
    {
        CountdownWidget.SetCountdownStartVisual(false);

        SystemSounds.Exclamation.Play();

        _transitionCoordinator.NotifyCountdownCompleted();
    }

    private bool _isCountdownCompleteVisible = false;

    private bool IsCountdownCompletionVisualActive =>
        _isCountdownCompleteVisible || CountdownCompleteOverlay.Visibility == Visibility.Visible;

    private void SuppressCompactMediaChromeForCountdownCompletion(bool animate = false)
    {
        _pendingFlipThumbnail = null;
        ResetAnimationThumbnailOverlay();
        CancelThumbnailSwitchAnimations(_currentMediaInfo?.Thumbnail);

        Motion.Stop(CompactThumbnailScale, ScaleTransform.ScaleXProperty);
        Motion.Stop(CompactThumbnailScale, ScaleTransform.ScaleYProperty);
        CompactThumbnailScale.ScaleX = 1.0;
        CompactThumbnailScale.ScaleY = 1.0;

        CompactThumbnailBorder.BeginAnimation(OpacityProperty, null);
        if (animate && CompactThumbnailBorder.Visibility == Visibility.Visible && CompactThumbnailBorder.Opacity > 0.01)
        {
            var thumbFade = MakeAnim(CompactThumbnailBorder.Opacity, 0.0,
                new Duration(TimeSpan.FromMilliseconds(180)), _easeQuadIn);
            Timeline.SetDesiredFrameRate(thumbFade, VNotch.Services.AnimationConfig.TargetFps);
            thumbFade.Completed += (_, _) =>
            {
                if (!IsCountdownCompletionVisualActive) return;
                CompactThumbnailBorder.BeginAnimation(OpacityProperty, null);
                CompactThumbnailBorder.Opacity = 0.0;
                CompactThumbnailBorder.Visibility = Visibility.Collapsed;
            };
            CompactThumbnailBorder.BeginAnimation(OpacityProperty, thumbFade);
        }
        else
        {
            CompactThumbnailBorder.Opacity = 0.0;
            CompactThumbnailBorder.Visibility = Visibility.Collapsed;
        }

        MusicViz.BeginAnimation(OpacityProperty, null);
        if (animate && MusicViz.Visibility == Visibility.Visible && MusicViz.Opacity > 0.01)
        {
            var vizFade = MakeAnim(MusicViz.Opacity, 0.0,
                new Duration(TimeSpan.FromMilliseconds(160)), _easeQuadIn);
            Timeline.SetDesiredFrameRate(vizFade, VNotch.Services.AnimationConfig.TargetFps);
            vizFade.Completed += (_, _) =>
            {
                if (!IsCountdownCompletionVisualActive) return;
                MusicViz.BeginAnimation(OpacityProperty, null);
                MusicViz.Opacity = 0.0;
                MusicViz.Visibility = Visibility.Collapsed;
            };
            MusicViz.BeginAnimation(OpacityProperty, vizFade);
        }
        else
        {
            MusicViz.Opacity = 0.0;
            MusicViz.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowCountdownCompletionOnPill(long? transitionId = null)
    {
        _isCountdownCompleteVisible = true;
        _hoverCollapseTimer.Stop();
        _hoverThumbnailDelayTimer.Stop();
        SuppressCompactMediaChromeForCountdownCompletion(animate: true);
        AnimateCornerRadius(_cornerRadiusCollapsed, TimeSpan.FromMilliseconds(360));

        AnimateCountdownCompletionToClockView(transitionId);
    }

    private void AnimateCountdownCompletionToClockView(long? transitionId = null)
    {
        int generation = (int)(transitionId ?? _transitionCoordinator.ActiveTransitionId);
        _viewTransitionGeneration = generation;

        if (_isAudioView)
        {
            _isAudioView = false;
            StopAudioPoll();
            AudioContent.ReleaseSessionCache();
        }

        if (_isSecondaryView)
        {
            _isSecondaryView = false;
            StopCameraPreviewForViewExit();
            DisableKeyboardInput();
        }

        _isTimerView = false;
        _isSecondaryView = false;
        _isAudioView = false;
        _isAnimating = true;
        NotchBorder.IsHitTestVisible = false;

        // Invalidate and cancel in-flight content presenter sessions so any old callbacks are ignored
        _notchContentPresenter?.CancelActiveTransition();

        var exitDuration = new Duration(TimeSpan.FromMilliseconds(220));
        var resizeDuration = new Duration(TimeSpan.FromMilliseconds(420));

        AnimateCountdownCompletionContentOut(ExpandedContent, exitDuration);
        AnimateCountdownCompletionContentOut(SecondaryContent, exitDuration);
        AnimateCountdownCompletionContentOut(AudioContent, exitDuration);
        if (AudioScrollViewer != null)
        {
            AudioScrollViewer.BeginAnimation(OpacityProperty, null);
        }
        AnimateCountdownCompletionContentOut(TimerContent, exitDuration);
        AnimateCountdownCompletionContentOut(CollapsedContent, exitDuration);
        AnimateCountdownCompletionContentOut(MusicCompactContent, exitDuration);
        AnimateCountdownCompletionNavOut(exitDuration);

        double currentWidth = NotchBorder.ActualWidth > 0 ? NotchBorder.ActualWidth : NotchBorder.Width;
        if (double.IsNaN(currentWidth) || currentWidth <= 0) currentWidth = _collapsedWidth;
        double currentHeight = NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight : NotchBorder.Height;
        if (double.IsNaN(currentHeight) || currentHeight <= 0) currentHeight = _collapsedHeight;
        double targetWidth = CountdownCompleteViewWidth;
        double targetHeight = CountdownCompleteViewHeight;

        Action onCompletedAction = () =>
        {
            if (transitionId.HasValue && generation != _viewTransitionGeneration) return;
            // Another surface may have taken over while the shell completion was queued.
            if (Math.Abs(NotchBorder.Width - targetWidth) > 0.5 || Math.Abs(NotchBorder.Height - targetHeight) > 0.5) return;
            _isAnimating = false;
            NotchBorder.IsHitTestVisible = true;
            Motion.Stop(NotchBorder, WidthProperty, targetWidth);
            Motion.Stop(NotchBorder, HeightProperty, targetHeight);
            RestoreExpandedWindowSize();
            ShowCompletionOverlayContent();

            if (transitionId.HasValue)
            {
                _transitionCoordinator.CompleteTransition(transitionId.Value);
            }

        };

        if (_notchShellPresenter != null)
        {
            var motion = new VNotch.Models.TransitionMotionConfig(
                Duration: resizeDuration,
                Easing: _easeExpOut6,
                TargetFps: VNotch.Services.AnimationConfig.TargetFps,
                ReduceMotion: false
            );

            var plan = new VNotch.Models.TransitionPlan(
                SessionId: generation,
                FromView: _isExpanded ? VNotch.Models.NotchView.Media : VNotch.Models.NotchView.Compact,
                TargetView: VNotch.Models.NotchView.Compact,
                TargetShape: VNotch.Controllers.NotchShapeState.Collapsed,
                TargetWidth: targetWidth,
                TargetHeight: targetHeight,
                TargetCornerRadius: _cornerRadiusCollapsed,
                Motion: motion
            );

            _notchShellPresenter.AnimateShell(plan, res =>
            {
                if (res.Status == VNotch.Models.TransitionExecutionStatus.Completed)
                {
                    onCompletedAction();
                }
            });
        }
        else
        {
            Motion.To(NotchBorder, WidthProperty, targetWidth, Motion.PopBack);
            Motion.To(NotchBorder, HeightProperty, targetHeight, Motion.PopBack, done: onCompletedAction);
        }
    }

    private static void AnimateCountdownCompletionContentOut(FrameworkElement? element, Duration duration)
    {
        if (element == null || element.Visibility != Visibility.Visible) return;
        element.Effect = null;
        var group = element.RenderTransform as TransformGroup;
        if (group == null || group.Children.Count != 2 || group.Children[0] is not ScaleTransform || group.Children[1] is not TranslateTransform)
        {
            group = new TransformGroup();
            group.Children.Add(new ScaleTransform());
            group.Children.Add(new TranslateTransform());
            element.RenderTransform = group;
        }
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        var scale = (ScaleTransform)group.Children[0];
        var translate = (TranslateTransform)group.Children[1];
        Motion.To(translate, TranslateTransform.YProperty, -14, Motion.Exit);
        Motion.To(scale, ScaleTransform.ScaleXProperty, Motion.ExitToScale, Motion.Exit);
        Motion.To(scale, ScaleTransform.ScaleYProperty, Motion.ExitToScale, Motion.Exit);
        Motion.To(element, OpacityProperty, 0, Motion.Exit, done: () => element.Visibility = Visibility.Collapsed);
    }

    private void AnimateCountdownCompletionNavOut(Duration duration)
    {
        if (NavIconsPanel.Visibility == Visibility.Visible || NavIconsPanel.Opacity > 0.01)
        {
            NavIconsPanel.BeginAnimation(OpacityProperty, null);
            Motion.Stop(NavIconsTranslate, TranslateTransform.YProperty);

            var navFade = MakeAnim(NavIconsPanel.Opacity, 0.0, duration, _easeQuadIn);
            Timeline.SetDesiredFrameRate(navFade, VNotch.Services.AnimationConfig.TargetFps);
            navFade.Completed += (_, _) =>
            {
                NavIconsPanel.BeginAnimation(OpacityProperty, null);
                NavIconsPanel.Opacity = 0;
                NavIconsPanel.Visibility = Visibility.Collapsed;
                Motion.Stop(NavIconsTranslate, TranslateTransform.YProperty);
                NavIconsTranslate.Y = 0;
            };
            NavIconsPanel.BeginAnimation(OpacityProperty, navFade);
            Motion.To(NavIconsTranslate, TranslateTransform.YProperty, -8, Motion.Exit);
        }

        if (NavIconsBackground.Visibility == Visibility.Visible || NavIconsBackground.Opacity > 0.01)
        {
            NavIconsBackground.BeginAnimation(OpacityProperty, null);
            var navBgFade = MakeAnim(NavIconsBackground.Opacity, 0.0, duration, _easeQuadIn);
            Timeline.SetDesiredFrameRate(navBgFade, VNotch.Services.AnimationConfig.TargetFps);
            navBgFade.Completed += (_, _) =>
            {
                NavIconsBackground.BeginAnimation(OpacityProperty, null);
                NavIconsBackground.Opacity = 0;
                NavIconsBackground.Visibility = Visibility.Collapsed;
            };
            NavIconsBackground.BeginAnimation(OpacityProperty, navBgFade);
        }
    }

    private void ShowCompletionOverlayContent()
    {
        ExpandedContent.Visibility = Visibility.Collapsed;
        TimerContent.Visibility = Visibility.Collapsed;
        SecondaryContent.Visibility = Visibility.Collapsed;
        AudioContent.Visibility = Visibility.Collapsed;
        AudioContent.Opacity = 0;
        if (AudioScrollViewer != null)
        {
            AudioScrollViewer.BeginAnimation(OpacityProperty, null);
            AudioScrollViewer.Visibility = Visibility.Collapsed;
            AudioScrollViewer.Opacity = 0;
        }
        SuppressCompactMediaChromeForCountdownCompletion();

        CountdownCompleteOverlay.BeginAnimation(OpacityProperty, null);
        CountdownCompleteOverlay.RenderTransform = new TranslateTransform(0, -10);
        CountdownCompleteOverlay.Visibility = Visibility.Visible;
        CountdownCompleteOverlay.Opacity = 0;

        Motion.To(CountdownCompleteOverlay, OpacityProperty, 1, Motion.Tile, from: 0);
        Motion.To((TranslateTransform)CountdownCompleteOverlay.RenderTransform, TranslateTransform.YProperty, 0, Motion.Tile, from: -10);

        CountdownCompleteSurface.BeginAnimation(OpacityProperty, null);
        CountdownCompleteSurface.Opacity = 0;

        ResetCountdownCompleteActionButtonsVisual();
        PrepareCountdownCompleteElement(CountdownCompleteText, CountdownCompleteTextTranslate, CountdownCompleteTextScale, 0.82);
        PrepareCountdownCompleteElement(CountdownRestartHost, CountdownRestartTranslate, CountdownRestartRevealScale, 0.68);
        PrepareCountdownCompleteElement(CountdownDismissHost, CountdownDismissTranslate, CountdownDismissRevealScale, 0.68);

        var surfaceFade = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(180)))
        {
            EasingFunction = _easeQuadOut
        };
        Timeline.SetDesiredFrameRate(surfaceFade, VNotch.Services.AnimationConfig.TargetFps);
        CountdownCompleteSurface.BeginAnimation(OpacityProperty, surfaceFade);

        AnimateCountdownCompleteElement(
            CountdownCompleteText,
            CountdownCompleteTextTranslate,
            CountdownCompleteTextScale,
            fromScale: 0.82,
            TimeSpan.Zero,
            (_, _) => StartCountdownCompleteTextFlash());
        AnimateCountdownCompleteElement(
            CountdownRestartHost,
            CountdownRestartTranslate,
            CountdownRestartRevealScale,
            fromScale: 0.68,
            Motion.Stagger(1));
        AnimateCountdownCompleteElement(
            CountdownDismissHost,
            CountdownDismissTranslate,
            CountdownDismissRevealScale,
            fromScale: 0.68,
            Motion.Stagger(2));
    }

    private static void PrepareCountdownCompleteElement(
        FrameworkElement element,
        TranslateTransform translate,
        ScaleTransform scale,
        double initialScale)
    {
        element.BeginAnimation(OpacityProperty, null);
        Motion.Stop(translate, TranslateTransform.YProperty);
        Motion.Stop(scale, ScaleTransform.ScaleXProperty);
        Motion.Stop(scale, ScaleTransform.ScaleYProperty);
        element.Opacity = 0;
        translate.Y = -10;
        scale.ScaleX = initialScale;
        scale.ScaleY = initialScale;
    }

    private void AnimateCountdownCompleteElement(
        FrameworkElement element,
        TranslateTransform translate,
        ScaleTransform scale,
        double fromScale,
        TimeSpan beginTime,
        EventHandler? completed = null)
    {
        Motion.To(element, OpacityProperty, 1, Motion.Tile, from: 0, delay: beginTime,
            done: () => completed?.Invoke(element, EventArgs.Empty));
        Motion.To(translate, TranslateTransform.YProperty, 0, Motion.Tile, from: -10, delay: beginTime);
        Motion.To(scale, ScaleTransform.ScaleXProperty, 1, Motion.TileScale, from: Motion.TileFromScale, delay: beginTime);
        Motion.To(scale, ScaleTransform.ScaleYProperty, 1, Motion.TileScale, from: Motion.TileFromScale, delay: beginTime);
    }

    private bool _countdownFlashVisibilityHooked;

    private void RefreshCountdownCompleteTextFlash()
    {
        if (AmbientAnimationsAllowed && CountdownCompleteText.IsVisible) return;
        Motion.Stop(CountdownCompleteText, OpacityProperty, 1);
    }

    private void StartCountdownCompleteTextFlash()
    {
        if (!_countdownFlashVisibilityHooked)
        {
            _countdownFlashVisibilityHooked = true;
            CountdownCompleteText.IsVisibleChanged += (_, _) => RefreshCountdownCompleteTextFlash();
        }
        if (!AmbientAnimationsAllowed || !CountdownCompleteText.IsVisible) return;
        // Three blinks catch the eye, then settle; hidden content never keeps a clock alive.
        var flash = new DoubleAnimation(1, 0.2, new Duration(TimeSpan.FromMilliseconds(500)))
        {
            AutoReverse = true,
            RepeatBehavior = new RepeatBehavior(3)
        };
        Timeline.SetDesiredFrameRate(flash, Math.Min(AnimationConfig.TargetFps, 30));
        CountdownCompleteText.BeginAnimation(OpacityProperty, flash);
    }

    private void CountdownCompleteOverlay_MouseEnter(object sender, MouseEventArgs e)
    {
        AnimateCountdownCompleteHover(true);
    }

    private void CountdownCompleteOverlay_MouseLeave(object sender, MouseEventArgs e)
    {
        AnimateCountdownCompleteHover(false);
    }

    private void CountdownRestartButton_MouseEnter(object sender, MouseEventArgs e)
    {
        AnimateCountdownActionButton(CountdownRestartBtnScale, CountdownRestartBtnRotate, 1.18, -32.0, 280);
    }

    private void CountdownRestartButton_MouseLeave(object sender, MouseEventArgs e)
    {
        AnimateCountdownActionButton(CountdownRestartBtnScale, CountdownRestartBtnRotate, 1.0, 0.0, 260);
    }

    private void CountdownDismissButton_MouseEnter(object sender, MouseEventArgs e)
    {
        AnimateCountdownActionButton(CountdownDismissBtnScale, CountdownDismissBtnRotate, 1.18, 90.0, 280);
    }

    private void CountdownDismissButton_MouseLeave(object sender, MouseEventArgs e)
    {
        AnimateCountdownActionButton(CountdownDismissBtnScale, CountdownDismissBtnRotate, 1.0, 0.0, 260);
    }

    private void CountdownActionBtn_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(sender, CountdownRestartBtn))
        {
            AnimateCountdownActionButton(CountdownRestartBtnScale, CountdownRestartBtnRotate, 0.88, -64.0, 110);
        }
        else if (ReferenceEquals(sender, CountdownDismissBtn))
        {
            AnimateCountdownActionButton(CountdownDismissBtnScale, CountdownDismissBtnRotate, 0.88, 90.0, 110);
        }
    }

    private void CountdownActionBtn_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(sender, CountdownRestartBtn))
        {
            AnimateCountdownActionButton(CountdownRestartBtnScale, CountdownRestartBtnRotate, 1.12, -180.0, 240);
        }
        else if (ReferenceEquals(sender, CountdownDismissBtn))
        {
            AnimateCountdownActionButton(CountdownDismissBtnScale, CountdownDismissBtnRotate, 1.12, 90.0, 200);
        }
    }

    private static void AnimateCountdownActionButton(
        ScaleTransform scale,
        RotateTransform rotate,
        double targetScale,
        double targetAngle,
        int durationMs)
    {
        var spring = targetScale < 1 ? Motion.PressDown : Motion.Press;
        Motion.To(scale, ScaleTransform.ScaleXProperty, targetScale, spring);
        Motion.To(scale, ScaleTransform.ScaleYProperty, targetScale, spring);
        Motion.To(rotate, RotateTransform.AngleProperty, targetAngle, spring);
    }

    private void ResetCountdownCompleteActionButtonsVisual()
    {
        if (CountdownRestartBtnScale != null)
        {
            Motion.Stop(CountdownRestartBtnScale, ScaleTransform.ScaleXProperty);
            Motion.Stop(CountdownRestartBtnScale, ScaleTransform.ScaleYProperty);
            CountdownRestartBtnScale.ScaleX = 1.0;
            CountdownRestartBtnScale.ScaleY = 1.0;
        }
        if (CountdownRestartBtnRotate != null)
        {
            Motion.Stop(CountdownRestartBtnRotate, RotateTransform.AngleProperty);
            CountdownRestartBtnRotate.Angle = 0.0;
        }
        if (CountdownDismissBtnScale != null)
        {
            Motion.Stop(CountdownDismissBtnScale, ScaleTransform.ScaleXProperty);
            Motion.Stop(CountdownDismissBtnScale, ScaleTransform.ScaleYProperty);
            CountdownDismissBtnScale.ScaleX = 1.0;
            CountdownDismissBtnScale.ScaleY = 1.0;
        }
        if (CountdownDismissBtnRotate != null)
        {
            Motion.Stop(CountdownDismissBtnRotate, RotateTransform.AngleProperty);
            CountdownDismissBtnRotate.Angle = 0.0;
        }
    }

    private void AnimateCountdownCompleteHover(bool isHovered)
    {
        if (isHovered && (!_isCountdownCompleteVisible || _isAnimating)) return;
        Motion.To(NotchScale, ScaleTransform.ScaleXProperty, isHovered ? 1.004 : 1, Motion.Press);
        Motion.To(NotchScale, ScaleTransform.ScaleYProperty, isHovered ? 1.004 : 1, Motion.Press);
        Motion.To(NotchShadowScale, ScaleTransform.ScaleXProperty, isHovered ? 1.0015 : 1, Motion.Press);
        Motion.To(NotchShadowScale, ScaleTransform.ScaleYProperty, isHovered ? 1.0015 : 1, Motion.Press);
    }

    private void CountdownRestart_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_isAnimating) return;

        CountdownController.Reset();
        CountdownController.Start();

        AnimateCountdownRestartToTimerView();
    }

    private void AnimateCountdownRestartToTimerView()
    {
        _transitionCoordinator.NextGeneration();
        _transitionCoordinator.ForceState(VNotch.Models.NotchView.Timer, VNotch.Controllers.NotchShapeState.Expanded, "CountdownRestart");
        long restartGen = _transitionCoordinator.ActiveTransitionId;
        _isAnimating = true;
        _isCountdownCompleteVisible = false;
        _isTimerView = true;
        _isSecondaryView = false;
        _isAudioView = false;
        NotchBorder.IsHitTestVisible = false;

        AnimateCountdownCompleteHover(false);

        CountdownCompleteText.BeginAnimation(OpacityProperty, null);
        CountdownCompleteText.Opacity = 1;

        ExpandedContent.Visibility = Visibility.Collapsed;
        ExpandedContent.Opacity = 0;
        SecondaryContent.Visibility = Visibility.Collapsed;
        SecondaryContent.Opacity = 0;
        AudioContent.Visibility = Visibility.Collapsed;
        AudioContent.Opacity = 0;
        if (AudioScrollViewer != null)
        {
            AudioScrollViewer.BeginAnimation(OpacityProperty, null);
            AudioScrollViewer.Visibility = Visibility.Collapsed;
            AudioScrollViewer.Opacity = 0;
        }
        CollapsedContent.Visibility = Visibility.Collapsed;
        CollapsedContent.Opacity = 0;
        MusicCompactContent.Visibility = Visibility.Collapsed;
        MusicCompactContent.Opacity = 0;

        CountdownWidget.RefreshProgress();
        CountdownWidget.SetCountdownStartVisual(true);
        UpdateTimerNavIconsState();
        AnimateStatusBarReveal(true);

        NavIconsPanel.BeginAnimation(OpacityProperty, null);
        NavIconsPanel.Visibility = Visibility.Visible;
        NavIconsPanel.Opacity = 0;
        Motion.Stop(NavIconsTranslate, TranslateTransform.YProperty);
        NavIconsTranslate.Y = -6;

        NavIconsBackground.BeginAnimation(OpacityProperty, null);
        NavIconsBackground.Visibility = Visibility.Visible;
        NavIconsBackground.Opacity = 0;

        TimerContent.BeginAnimation(OpacityProperty, null);
        TimerContent.Visibility = Visibility.Visible;
        TimerContent.Opacity = 0;
        TimerContent.Effect = null;

        var timerGroup = new TransformGroup();
        var timerScale = new ScaleTransform(0.96, 0.96);
        var timerTranslate = new TranslateTransform(0, -22);
        timerGroup.Children.Add(timerScale);
        timerGroup.Children.Add(timerTranslate);
        TimerContent.RenderTransform = timerGroup;
        TimerContent.RenderTransformOrigin = new Point(0.5, 0.5);

        var overlayTranslate = new TranslateTransform(0, 0);
        CountdownCompleteOverlay.RenderTransform = overlayTranslate;

        var durOut = new Duration(TimeSpan.FromMilliseconds(220));
        var durIn = new Duration(TimeSpan.FromMilliseconds(430));
        var inDelay = Motion.Stagger(1);
        int fps = VNotch.Services.AnimationConfig.TargetFps;

        var overlayFade = MakeAnim(CountdownCompleteOverlay.Opacity, 0, durOut, _easeQuadIn);
        Timeline.SetDesiredFrameRate(overlayFade, fps);

        overlayFade.Completed += (s, e) =>
        {
            DismissCountdownCompletion();
            CountdownCompleteOverlay.RenderTransform = null;
        };

        CountdownCompleteOverlay.BeginAnimation(OpacityProperty, overlayFade);
        Motion.To(overlayTranslate, TranslateTransform.YProperty, 18, Motion.Exit);

        void FinishRestart()
        {
            if (!_isTimerView || restartGen != _transitionCoordinator.ActiveTransitionId ||
                !ReferenceEquals(TimerContent.RenderTransform, timerGroup) ||
                Math.Abs(TimerContent.Opacity - 1) > .001 || Math.Abs(timerTranslate.Y) > .01 ||
                Math.Abs(timerScale.ScaleX - 1) > .001 || Math.Abs(timerScale.ScaleY - 1) > .001) return;
            _isAnimating = false;
            NotchBorder.IsHitTestVisible = true;
            TimerContent.RenderTransform = null;
            UpdateTimerNavIconsState();
            CountdownWidget.RefreshProgress();
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, () =>
            {
                RestoreTimerContentOpacity();
            });
        }

        Motion.To(TimerContent, OpacityProperty, 1, Motion.Tile, from: 0, delay: inDelay, done: FinishRestart);
        Motion.To(timerTranslate, TranslateTransform.YProperty, 0, Motion.Tile, delay: inDelay, done: FinishRestart);
        Motion.To(timerScale, ScaleTransform.ScaleXProperty, 1, Motion.TileScale, delay: inDelay, done: FinishRestart);
        Motion.To(timerScale, ScaleTransform.ScaleYProperty, 1, Motion.TileScale, delay: inDelay, done: FinishRestart);

        Motion.To(NavIconsPanel, OpacityProperty, 1, Motion.Tile, from: 0, delay: Motion.Stagger(1));
        Motion.To(NavIconsTranslate, TranslateTransform.YProperty, 0, Motion.Tile, delay: Motion.Stagger(1));
        Motion.To(NavIconsBackground, OpacityProperty, 1, Motion.Tile, from: 0, delay: Motion.Stagger(1));

        ApplyClockViewWindowSize();
        PrepareClockViewContentSize();
        RefreshClockView();

        double currentHeight = NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight : _collapsedHeight;
        double currentWidth = NotchBorder.ActualWidth > 0 ? NotchBorder.ActualWidth : NotchBorder.Width;
        if (double.IsNaN(currentWidth) || currentWidth <= 0) currentWidth = _collapsedWidth;
        AnimateCornerRadius(_cornerRadiusExpanded, durIn.TimeSpan);
        AnimateClockViewNotchResize(currentWidth, currentHeight, _clockViewWidth, _clockViewHeight, durIn, TimeSpan.Zero, generation: (int)restartGen);
    }

    private void CountdownDismiss_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_isAnimating) return;

        _transitionCoordinator.NextGeneration();
        _transitionCoordinator.ForceState(VNotch.Models.NotchView.Compact, VNotch.Controllers.NotchShapeState.Collapsed, "CountdownDismiss");
        long transitionId = _transitionCoordinator.ActiveTransitionId;
        AnimateCountdownCompleteOverlayOut();
        _viewModel.Timer.Remaining = _countdownDuration;
        CountdownWidget.SetCountdownStartVisual(false);
        AnimateCountdownDismissCollapse(transitionId);
    }

    private void AnimateCountdownDismissCollapse(long transitionId)
    {
        _viewTransitionGeneration = (int)transitionId;
        _isTimerView = false;
        _isSecondaryView = false;

        _isAnimating = true;
        AnimateCornerRadius(_cornerRadiusCollapsed, TimeSpan.FromMilliseconds(360));

        // A shell session that is still open would ride along on these springs and report its own view at the end.
        _notchShellPresenter?.CancelCurrentAnimation();
        Motion.To(NotchBorder, WidthProperty, _collapsedWidth, Motion.PopBack);
        // Kept: a hover or a content measurement may take the height over, and the dismissal must still end.
        Motion.To(NotchBorder, HeightProperty, _collapsedHeight, Motion.PopBack, keep: true, done: () => Dispatcher.BeginInvoke(() =>
        {
            // Posted, not called: a spring with nowhere to go ends inside Motion.To, before the click has returned.
            if (transitionId != _transitionCoordinator.ActiveTransitionId) return;
            _transitionCoordinator.CompleteTransition(transitionId);
            _transitionCoordinator.ForceState(VNotch.Models.NotchView.Compact, VNotch.Controllers.NotchShapeState.Collapsed, "CountdownDismissCompleted");
            _isAnimating = false;
            NotchBorder.IsHitTestVisible = true;

            if (_isTimerView) return;
            TimerContent.BeginAnimation(OpacityProperty, null);
            TimerContent.Visibility = Visibility.Collapsed;
            TimerContent.Opacity = 0;
            TimerContent.RenderTransform = null;
            ExpandedContent.BeginAnimation(OpacityProperty, null);
            ExpandedContent.Visibility = Visibility.Collapsed;
            ExpandedContent.Opacity = 0;
            ExpandedContent.RenderTransform = null;
            SecondaryContent.BeginAnimation(OpacityProperty, null);
            SecondaryContent.Visibility = Visibility.Collapsed;
            SecondaryContent.Opacity = 0;
            SecondaryContent.RenderTransform = null;
            NavIconsPanel.BeginAnimation(OpacityProperty, null);
            NavIconsPanel.Opacity = 0;
            NavIconsPanel.Visibility = Visibility.Collapsed;
            NavIconsBackground.BeginAnimation(OpacityProperty, null);
            NavIconsBackground.Opacity = 0;
            NavIconsBackground.Visibility = Visibility.Collapsed;
            DisableKeyboardInput();

            if (_isMusicCompactMode)
            {
                RestoreMusicCompactPillAfterCountdownDismiss();
            }
            else
            {
                CollapsedContent.BeginAnimation(OpacityProperty, null);
                CollapsedContent.Visibility = Visibility.Visible;
                CollapsedContent.Opacity = 0;
                AnimateCountdownCollapsedContentIn(CollapsedContent);
            }
        }));

        CountdownWidget.SetCountdownStartVisual(false);
    }

    private void AnimateCountdownCompleteOverlayOut()
    {
        _isCountdownCompleteVisible = false;
        AnimateCountdownCompleteHover(false);
        AnimateCountdownCompleteElementsFadeOut();
        var overlayTranslate = CountdownCompleteOverlay.RenderTransform as TranslateTransform ?? new TranslateTransform();
        CountdownCompleteOverlay.RenderTransform = overlayTranslate;
        Motion.To(overlayTranslate, TranslateTransform.YProperty, 18, Motion.Exit);
        Motion.To(CountdownCompleteOverlay, OpacityProperty, 0, Motion.Exit, done: () =>
        {
            if (!_isCountdownCompleteVisible)
            {
                DismissCountdownCompletion();
                CountdownCompleteOverlay.RenderTransform = null;
            }
        });
    }

    private void AnimateCountdownCompleteElementsFadeOut()
    {
        FrameworkElement[] elements = [CountdownCompleteText, CountdownRestartHost, CountdownDismissHost];
        TranslateTransform[] shifts = [CountdownCompleteTextTranslate, CountdownRestartTranslate, CountdownDismissTranslate];
        ScaleTransform[] scales = [CountdownCompleteTextScale, CountdownRestartRevealScale, CountdownDismissRevealScale];
        for (int i = 0; i < elements.Length; i++)
        {
            if (elements[i].Visibility != Visibility.Visible) continue;
            Motion.To(elements[i], OpacityProperty, 0, Motion.Exit);
            Motion.To(shifts[i], TranslateTransform.YProperty, -8, Motion.Exit);
            Motion.To(scales[i], ScaleTransform.ScaleXProperty, Motion.ExitToScale, Motion.Exit);
            Motion.To(scales[i], ScaleTransform.ScaleYProperty, Motion.ExitToScale, Motion.Exit);
        }
        Motion.To(CountdownCompleteSurface, OpacityProperty, 0, Motion.Exit);
    }

    private static void AnimateCountdownCollapsedContentIn(FrameworkElement content)
    {
        var group = new TransformGroup();
        var scale = new ScaleTransform(Motion.TileFromScale, Motion.TileFromScale);
        var translate = new TranslateTransform(0, -6);
        group.Children.Add(scale);
        group.Children.Add(translate);
        content.RenderTransform = group;
        content.RenderTransformOrigin = new Point(0.5, 0.5);
        void Finish()
        {
            if (content.Visibility == Visibility.Visible && Math.Abs(content.Opacity - 1) < 0.001 && Math.Abs(translate.Y) < 0.01 &&
                Math.Abs(scale.ScaleX - 1) < 0.001 && Math.Abs(scale.ScaleY - 1) < 0.001 &&
                ReferenceEquals(content.RenderTransform, group)) content.RenderTransform = null;
        }
        // Opacity and drift can settle in either order; the last spring releases the shared transform.
        Motion.To(content, OpacityProperty, 1, Motion.Tile, from: 0, done: Finish);
        Motion.To(translate, TranslateTransform.YProperty, 0, Motion.Tile, done: Finish);
        Motion.To(scale, ScaleTransform.ScaleXProperty, 1, Motion.TileScale, done: Finish);
        Motion.To(scale, ScaleTransform.ScaleYProperty, 1, Motion.TileScale, done: Finish);
    }

    private void RestoreMusicCompactPillAfterCountdownDismiss()
    {
        MusicCompactContent.BeginAnimation(OpacityProperty, null);
        MusicCompactContent.Visibility = Visibility.Visible;
        MusicCompactContent.Opacity = 0;

        CollapsedContent.BeginAnimation(OpacityProperty, null);
        CollapsedContent.Visibility = Visibility.Collapsed;
        CollapsedContent.Opacity = 0;

        MusicCompactContentBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
        MusicCompactContentBlur.Radius = 0;
        CompactHoverInfo.BeginAnimation(OpacityProperty, null);
        CompactHoverInfo.Opacity = 0;
        CompactHoverInfo.Visibility = Visibility.Collapsed;

        if (CompactThumbnailBorder != null && !_isClipboardPeekActive && !_isVolumeIndicatorActive)
        {
            CompactThumbnailBorder.BeginAnimation(OpacityProperty, null);
            Motion.Stop(CompactThumbnailScale, ScaleTransform.ScaleXProperty);
            Motion.Stop(CompactThumbnailScale, ScaleTransform.ScaleYProperty);
            CompactThumbnailBorder.Visibility = Visibility.Visible;

            if (_currentMediaInfo?.Thumbnail != null)
            {
                CompactThumbnail.Source = _currentMediaInfo.Thumbnail;
                ThumbnailImage.Source = _currentMediaInfo.Thumbnail;
            }

            PlayThumbnailRevealAnimation();
        }

        if (_currentMediaInfo != null && !_isClipboardPeekActive && !_isVolumeIndicatorActive)
        {
            MusicViz.IsPlaying = _currentMediaInfo.IsPlaying;
            MusicViz.TrackId = _currentMediaInfo.GetSignature();

            if (_currentMediaInfo.IsPlaying)
            {
                MusicViz.BeginAnimation(OpacityProperty, null);
                MusicViz.Opacity = 0;
                MusicViz.Visibility = Visibility.Visible;
                ShowMusicVisualizer(duration: _dur250);
            }
        }

        MusicCompactContent.InvalidateArrange();
        MusicCompactContent.UpdateLayout();
        AnimateCountdownCollapsedContentIn(MusicCompactContent);
    }

    private void DismissCountdownCompletion()
    {
        _isCountdownCompleteVisible = false;
        AnimateCountdownCompleteHover(false);
        ResetCountdownCompleteActionButtonsVisual();

        CountdownCompleteText.BeginAnimation(OpacityProperty, null);
        CountdownCompleteText.Opacity = 1;
        Motion.Stop(CountdownCompleteTextTranslate, TranslateTransform.YProperty);
        Motion.Stop(CountdownCompleteTextScale, ScaleTransform.ScaleXProperty);
        Motion.Stop(CountdownCompleteTextScale, ScaleTransform.ScaleYProperty);
        CountdownRestartHost.BeginAnimation(OpacityProperty, null);
        Motion.Stop(CountdownRestartTranslate, TranslateTransform.YProperty);
        Motion.Stop(CountdownRestartRevealScale, ScaleTransform.ScaleXProperty);
        Motion.Stop(CountdownRestartRevealScale, ScaleTransform.ScaleYProperty);
        CountdownDismissHost.BeginAnimation(OpacityProperty, null);
        Motion.Stop(CountdownDismissTranslate, TranslateTransform.YProperty);
        Motion.Stop(CountdownDismissRevealScale, ScaleTransform.ScaleXProperty);
        Motion.Stop(CountdownDismissRevealScale, ScaleTransform.ScaleYProperty);
        CountdownCompleteSurface.BeginAnimation(OpacityProperty, null);
        CountdownCompleteSurface.Opacity = 0;

        CountdownCompleteOverlay.Visibility = Visibility.Collapsed;
        CountdownCompleteOverlay.Opacity = 0;
    }

    #endregion
}
