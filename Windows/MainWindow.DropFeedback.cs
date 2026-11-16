using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Controls;
using VNotch.Services;

namespace VNotch;

public partial class MainWindow
{
    private bool _clipboardDropHover;
    private UiScheduledTimer? _clipboardDropBadgeHold;
    private bool _clipboardDropBadgeHoldHooked;

    private void StopClipboardDropBadgeHold()
    {
        var timer = _clipboardDropBadgeHold;
        if (timer == null) return;
        _clipboardDropBadgeHold = null;
        timer.Dispose();
        Motion.Stop(ClipboardDropBadge, OpacityProperty, 0);
    }

    private void ResetClipboardDropFeedback()
    {
        StopClipboardDropBadgeHold();
        _clipboardDropHover = false;
        Motion.Stop(ClipboardDropGlow, OpacityProperty, 0);
        Motion.Stop(ClipboardDropBadge, OpacityProperty, 0);
        Motion.Stop(ClipboardDropBadgeScale, ScaleTransform.ScaleXProperty, 1);
        Motion.Stop(ClipboardDropBadgeScale, ScaleTransform.ScaleYProperty, 1);
    }

    private void SetClipboardDropHover(bool active)
    {
        StopClipboardDropBadgeHold();
        if (_clipboardDropHover == active) return;
        _clipboardDropHover = active;
        AnimateDropValue(ClipboardDropGlow, OpacityProperty, active ? 0.65 : 0, 140);
        if (active) AnimateDropValue(ClipboardDropBadge, OpacityProperty, 0, 140);
    }

    private void PlayClipboardDropFeedback()
    {
        StopClipboardDropBadgeHold();
        if (_cleanedUp || !_isSecondaryView || _spotlightMorphSessionActive) return;
        _clipboardDropHover = false;
        // Confirmation follows the completed import, never a rejected or pending drop.
        AnimateDropValue(ClipboardDropGlow, OpacityProperty, 0, 250, from: 1);
        if (AnimationConfig.ReduceMotion)
        {
            if (!ClipboardDropBadge.IsVisible) return;
            if (!_clipboardDropBadgeHoldHooked)
            {
                _clipboardDropBadgeHoldHooked = true;
                ClipboardDropBadge.IsVisibleChanged += (_, _) =>
                {
                    if (!ClipboardDropBadge.IsVisible) StopClipboardDropBadgeHold();
                };
            }
            // Confirmation still has its reading time without keeping an opacity clock alive.
            Motion.Stop(ClipboardDropBadge, OpacityProperty, 1);
            var hold = new UiScheduledTimer { Interval = TimeSpan.FromMilliseconds(650) };
            _clipboardDropBadgeHold = hold;
            hold.Tick += (_, _) => StopClipboardDropBadgeHold();
            hold.Start();
            return;
        }
        AnimateDropValue(ClipboardDropBadge, OpacityProperty, 0, 180, from: 1, delay: 650);
        PlayBadgeSpringBounce();
    }

    private void PlayBadgeSpringBounce()
    {
        Motion.To(ClipboardDropBadgeScale, ScaleTransform.ScaleXProperty, 1, Motion.TileScale, from: Motion.TileFromScale);
        Motion.To(ClipboardDropBadgeScale, ScaleTransform.ScaleYProperty, 1, Motion.TileScale, from: Motion.TileFromScale);
    }


    private static void AnimateDropValue(FrameworkElement target, DependencyProperty property,
        double value, int duration, double? from = null, int delay = 0)
    {
        if (AnimationConfig.ReduceMotion)
        {
            Motion.Stop(target, property, value);
            return;
        }
        double current = from ?? (double)target.GetValue(property);
        target.BeginAnimation(property, null);
        target.SetValue(property, from.HasValue && delay > 0 ? current : value);
        var animation = new DoubleAnimation(current, value,
            TimeSpan.FromMilliseconds(duration))
        {
            BeginTime = TimeSpan.FromMilliseconds(delay),
            FillBehavior = FillBehavior.HoldEnd,
            EasingFunction = TrayMotion.EaseOut
        };
        Timeline.SetDesiredFrameRate(animation, AnimationConfig.TargetFps);
        target.BeginAnimation(property, animation);
    }
}
