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
            var progress = new DoubleAnimation(to, TimeSpan.FromMilliseconds(milliseconds));
            Timeline.SetDesiredFrameRate(progress, AnimationConfig.TargetFps);
            target.BeginAnimation(property, progress);
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
        if (milliseconds <= 0 || AnimationConfig.ReduceMotion)
        {
            SetAppNotificationOpacity(target, to);
            return;
        }
        Motion.To(target, OpacityProperty, to, milliseconds <= NotificationFeedbackMs ? Motion.PressDown : Motion.Exit);
    }

    private void PresentAppNotification(bool replacing)
    {
        // The attachment reveals the whole face with the shell; only a message replacement swaps in place.
        SetAppNotificationOpacity(AppNotificationBanner, 1);
        SetAppNotificationValue(AppNotificationBannerTranslate, TranslateTransform.YProperty, 0);
        TransitionAppNotificationBlurCover(entering: true);
        Motion.To(AppNotificationBannerTranslate, TranslateTransform.XProperty, 0, Motion.Exit,
            from: replacing ? 6 : null);
        SetAppNotificationValue(AppNotificationBannerScale, ScaleTransform.ScaleXProperty, 1);
        SetAppNotificationValue(AppNotificationBannerScale, ScaleTransform.ScaleYProperty, 1);
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
        _notchShellPresenter?.CancelCurrentAnimation();
        SetAppNotificationValue(NotchScale, ScaleTransform.ScaleXProperty, 1);
        SetAppNotificationValue(NotchShadowScale, ScaleTransform.ScaleXProperty, 1);
        if (AnimationConfig.ReduceMotion || milliseconds == 0)
        {
            _appBannerAttachment?.Detach(!_isAppNotificationBannerExiting);
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
        Motion.To(this, CurrentCornerRadiusProperty, radius, spring);
        _appBannerAttachment ??= new(NotchBorder, AppNotificationBanner,
            () => new Size(GetCollapsedWidth(), GetCollapsedHeight()));
        _appBannerAttachment.MoveTo(width, height, spring, shown: !_isAppNotificationBannerExiting, done: completed);
    }

    private void BeginAppNotificationDismiss(int direction, bool immediate = false, double velocity = 0)
    {
        if (!_isAppNotificationBannerVisible || _isAppNotificationBannerExiting) return;
        StopAppNotificationDismissTimer();
        ResetAppNotificationPointer();
        _isAppNotificationBannerExiting = true;
        _isAppNotificationHovered = false;
        int generation = ++_appNotificationGeneration;
        AppNotificationBanner.IsHitTestVisible = false;
        if (!immediate) TransitionAppNotificationBlurCover(entering: false);
        AnimateAppNotificationHoverFeedback();
        if (direction != 0)
        {
            double travel = Math.Abs(AppNotificationShellTranslate.X) + 80;
            Motion.To(AppNotificationBannerTranslate, TranslateTransform.XProperty,
                direction * travel, Motion.Exit, velocity: velocity);
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
                    Motion.To(MusicCompactContent, OpacityProperty, 1, Motion.Exit);
                    RestoreCompactMediaPresentation();
                }
                else
                {
                    CollapsedContent.Visibility = Visibility.Visible;
                    Motion.To(CollapsedContent, OpacityProperty, 1, Motion.Exit);
                }
            }, easing: _easeAppleInOut);
    }

    private void ResetAppNotificationVisuals()
    {
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
