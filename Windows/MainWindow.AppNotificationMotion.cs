using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

public partial class MainWindow
{
    private const int NotificationFeedbackMs = 90;
    private const int NotificationTransitionMs = 240;
    private const int NotificationOpenMs = 360;

    private static DoubleAnimationUsingKeyFrames AppNotificationMotion(double from, double to, int milliseconds,
        int delay = 0, bool linear = false, IEasingFunction? easing = null)
    {
        milliseconds = Math.Clamp(milliseconds, 120, 200);
        var motion = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.HoldEnd };
        motion.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        if (delay > 0) motion.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay))));
        var end = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay + Math.Max(1, milliseconds)));
        if (linear) motion.KeyFrames.Add(new LinearDoubleKeyFrame(to, end));
        else motion.KeyFrames.Add(new EasingDoubleKeyFrame(to, end, easing ?? _easeAppleOut));
        Timeline.SetDesiredFrameRate(motion, AnimationConfig.TargetFps);
        return motion;
    }

    private static void SetAppNotificationValue(Animatable target, DependencyProperty property, double value)
    {
        Motion.Stop(target, property, value);
    }

    private static void AnimateAppNotificationValue(Animatable target, DependencyProperty property, double to,
        int milliseconds, int delay = 0, bool linear = false, IEasingFunction? easing = null)
    {
        if (AnimationConfig.ReduceMotion || milliseconds <= 0)
        {
            SetAppNotificationValue(target, property, to);
            return;
        }
        if (linear)
        {
            target.BeginAnimation(property, new DoubleAnimation(to, TimeSpan.FromMilliseconds(milliseconds)));
            return;
        }
        Motion.To(target, property, to, easing == _easeQuadIn ? Motion.Exit :
            milliseconds <= 180 ? (to == 0 ? Motion.Press : Motion.PressDown) : Motion.Pop);
    }

    private static void SetAppNotificationOpacity(UIElement target, double value)
    {
        Motion.Stop(target, OpacityProperty, value);
    }

    private static void AnimateAppNotificationOpacity(UIElement target, double to, int milliseconds, int delay = 0, IEasingFunction? easing = null)
    {
        double from = target.Opacity;
        if (milliseconds <= 0 || AnimationConfig.ReduceMotion)
        {
            SetAppNotificationOpacity(target, to);
            return;
        }
        SetAppNotificationOpacity(target, from);
        target.BeginAnimation(OpacityProperty, AppNotificationMotion(from, to, milliseconds, easing: easing));
    }

    private void PresentAppNotification(bool replacing)
    {
        if (AppNotificationBanner.Opacity <= 0 &&
            !Motion.IsRunning(AppNotificationBannerTranslate, TranslateTransform.YProperty))
        {
            SetAppNotificationOpacity(AppNotificationBanner, 0);
            SetAppNotificationValue(AppNotificationBannerTranslate, TranslateTransform.YProperty,
                AnimationConfig.ReduceMotion ? 0 : 6);
        }
        TransitionAppNotificationBlurCover(entering: true);
        AnimateAppNotificationOpacity(AppNotificationBanner, 1, NotificationTransitionMs, easing: _easeAppleOut);
        AnimateAppNotificationValue(AppNotificationBannerTranslate, TranslateTransform.YProperty, 0,
            NotificationTransitionMs, easing: _easeAppleOut);
        Motion.To(AppNotificationBannerTranslate, TranslateTransform.XProperty, 0, Motion.Pop);
        Motion.To(AppNotificationBannerScale, ScaleTransform.ScaleXProperty, 1, Motion.TileScale);
        Motion.To(AppNotificationBannerScale, ScaleTransform.ScaleYProperty, 1, Motion.TileScale);
        AppNotificationHoverSurface.Visibility = Visibility.Visible;
        AnimateAppNotificationHoverFeedback();
    }

    private void FreezeAppNotificationShell()
    {
        double width = NotchBorder.Width, height = NotchBorder.Height;
        Motion.Stop(NotchBorder, WidthProperty, double.IsFinite(width) ? width : GetCollapsedWidth());
        Motion.Stop(NotchBorder, HeightProperty, double.IsFinite(height) ? height : GetCollapsedHeight());
    }

    private void AnimateAppNotificationShell(double width, double height, double radius, int milliseconds,
        Action? completed = null, IEasingFunction? easing = null)
    {
        int morph = ++_appNotificationMorphGeneration;
        SetAppNotificationValue(NotchScale, ScaleTransform.ScaleXProperty, 1);
        SetAppNotificationValue(NotchShadowScale, ScaleTransform.ScaleXProperty, 1);
        if (AnimationConfig.ReduceMotion || milliseconds == 0)
        {
            Motion.Stop(NotchBorder, WidthProperty, width);
            Motion.Stop(NotchBorder, HeightProperty, height);
            Motion.Stop(this, CurrentCornerRadiusProperty, radius);
            completed?.Invoke();
            return;
        }
        var spring = _isAppNotificationBannerExiting ? Motion.PopBack : Motion.Pop;
        // Auto layout has no numeric width until the first presentation; start at the resting notch.
        if (!double.IsFinite(NotchBorder.Width))
            Motion.Stop(NotchBorder, WidthProperty, NotchBorder.ActualWidth > 0 ? NotchBorder.ActualWidth : GetCollapsedWidth());
        if (!double.IsFinite(NotchBorder.Height))
            Motion.Stop(NotchBorder, HeightProperty, NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight : GetCollapsedHeight());
        Motion.To(NotchBorder, WidthProperty, width, spring, done: () =>
        {
            if (morph == _appNotificationMorphGeneration && Math.Abs(NotchBorder.Width - width) < .5)
                Motion.Stop(NotchBorder, WidthProperty, width);
        });
        Motion.To(NotchBorder, HeightProperty, height, spring, done: () =>
        {
            if (morph != _appNotificationMorphGeneration) return;
            if (Math.Abs(NotchBorder.Height - height) > .5) return;
            Motion.Stop(NotchBorder, HeightProperty, height);
            completed?.Invoke();
        });
        Motion.To(this, CurrentCornerRadiusProperty, radius, spring);
    }

    private void BeginAppNotificationDismiss(int direction, bool immediate = false)
    {
        if (!_isAppNotificationBannerVisible || _isAppNotificationBannerExiting) return;
        StopAppNotificationDismissTimer();
        ResetAppNotificationPointer();
        _isAppNotificationBannerExiting = true;
        _isAppNotificationHovered = false;
        int generation = ++_appNotificationGeneration;
        AppNotificationBanner.IsHitTestVisible = false;
        if (!immediate) TransitionAppNotificationBlurCover(entering: false);
        AnimateAppNotificationOpacity(AppNotificationBanner, 0, immediate ? 0 : 180, easing: _easeQuadIn);
        Motion.To(AppNotificationBannerScale, ScaleTransform.ScaleXProperty, Motion.ExitToScale, Motion.Exit);
        Motion.To(AppNotificationBannerScale, ScaleTransform.ScaleYProperty, Motion.ExitToScale, Motion.Exit);
        AnimateAppNotificationHoverFeedback();
        AnimateAppNotificationValue(AppNotificationBannerTranslate, TranslateTransform.YProperty, direction == 0 ? -3 : 0,
            180, easing: _easeQuadIn);
        if (direction != 0)
        {
            double travel = Math.Abs(AppNotificationShellTranslate.X) + 80;
            AnimateAppNotificationValue(AppNotificationBannerTranslate, TranslateTransform.XProperty,
                direction * travel, 180, easing: _easeQuadIn);
        }
        foreach (var translate in new[] { AppNotificationShellTranslate, AppNotificationShadowTranslate })
        {
            AnimateAppNotificationValue(translate, TranslateTransform.XProperty, 0, NotificationTransitionMs);
            AnimateAppNotificationValue(translate, TranslateTransform.YProperty, 0, NotificationTransitionMs);
        }
        AnimateAppNotificationShell(_collapsedWidth > 0 ? _collapsedWidth : GetCollapsedWidth(),
            _collapsedHeight > 0 ? _collapsedHeight : GetCollapsedHeight(), _cornerRadiusCollapsed,
            immediate ? 0 : NotificationTransitionMs, () =>
            {
                if (generation != _appNotificationGeneration) return;
                ResetAppNotificationVisuals();
                AppNotificationBanner.Opacity = 0;
                AppNotificationBanner.Visibility = Visibility.Collapsed;
                _isAppNotificationBannerVisible = false;
                _isAppNotificationBannerExiting = false;
                _activeAppNotification = null;
                RestorePrivacyDotVisibility();
                if (_isExpanded || _isAnimating) return;
                if (_isMusicCompactMode && _currentMediaInfo != null)
                {
                    MusicCompactContent.Visibility = Visibility.Visible;
                    MusicCompactContent.BeginAnimation(OpacityProperty, MakeCompactContentRestore());
                    RestoreCompactMediaPresentation();
                }
                else
                {
                    CollapsedContent.Visibility = Visibility.Visible;
                    CollapsedContent.BeginAnimation(OpacityProperty, MakeCompactContentRestore());
                }
            }, easing: _easeAppleInOut);
    }

    private void ResetAppNotificationVisuals()
    {
        if (AppNotificationBanner.Visibility == Visibility.Visible && !_isAppNotificationBannerExiting)
        {
            KeepCompactDeparture(AppNotificationBanner);
        }
        ResetAppNotificationBlurCover();
        // The frozen old face owns departure; new text and artwork reveal together on the live face.
        SetAppNotificationOpacity(AppNotificationBanner, 0);
        foreach (var element in new UIElement[] { AppNotificationIconHost,
                     AppNotificationAppNameText, AppNotificationTitleText, AppNotificationBodyText })
            SetAppNotificationOpacity(element, 1);
        SetAppNotificationOpacity(HoverGlow, 0);
        SetAppNotificationOpacity(AppNotificationHoverSurface, 0);
        AppNotificationHoverSurface.Visibility = Visibility.Collapsed;
        foreach (var translate in new[] { AppNotificationShellTranslate, AppNotificationShadowTranslate,
                     AppNotificationPressTranslate, AppNotificationIconTranslate, AppNotificationAppNameTranslate,
                     AppNotificationTitleTranslate, AppNotificationBodyTranslate })
        {
            SetAppNotificationValue(translate, TranslateTransform.XProperty, 0);
            SetAppNotificationValue(translate, TranslateTransform.YProperty, 0);
        }
        foreach (var scale in new[] { AppNotificationIconScale })
        {
            SetAppNotificationValue(scale, ScaleTransform.ScaleXProperty, 1);
            SetAppNotificationValue(scale, ScaleTransform.ScaleYProperty, 1);
        }
        SetAppNotificationValue(AppNotificationIconRotate, RotateTransform.AngleProperty, 0);
        SetAppNotificationValue(AppNotificationProgressScale, ScaleTransform.ScaleXProperty, 1);
    }
}
