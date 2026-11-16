using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Services;
using Path = System.Windows.Shapes.Path;

namespace VNotch.Controls;

internal static class ButtonFeedback
{
    private static readonly DependencyProperty TransitionVersionProperty = DependencyProperty.RegisterAttached(
        "TransitionVersion", typeof(int), typeof(ButtonFeedback), new PropertyMetadata(0));

    private static int NextTransitionVersion(DependencyObject target)
    {
        int next = ((int)target.GetValue(TransitionVersionProperty)) + 1;
        target.SetValue(TransitionVersionProperty, next);
        return next;
    }

    private static bool IsTransitionVersionCurrent(DependencyObject target, int version)
        => (int)target.GetValue(TransitionVersionProperty) == version;

    internal static void AnimateIconSwitch(Canvas fromIcon, Canvas toIcon, TimeSpan duration, EasingFunctionBase easing)
    {
        if (ReferenceEquals(fromIcon, toIcon)) return;
        int fromVersion = NextTransitionVersion(fromIcon);
        int toVersion = NextTransitionVersion(toIcon);
        var fromTransform = fromIcon.RenderTransform as ScaleTransform ?? new ScaleTransform(1, 1);
        var toTransform = toIcon.RenderTransform as ScaleTransform ?? new ScaleTransform(1, 1);
        fromIcon.RenderTransform = fromTransform;
        toIcon.RenderTransform = toTransform;
        bool entering = toIcon.Visibility != Visibility.Visible;
        toIcon.Visibility = Visibility.Visible;
        int leaving = 3, arriving = 3;
        void Left()
        {
            if (--leaving != 0 || !IsTransitionVersionCurrent(fromIcon, fromVersion)) return;
            fromIcon.Visibility = Visibility.Collapsed;
            Motion.Stop(fromTransform, ScaleTransform.ScaleXProperty, 1);
            Motion.Stop(fromTransform, ScaleTransform.ScaleYProperty, 1);
            Motion.Stop(fromIcon, UIElement.OpacityProperty, 1);
        }
        void Arrived()
        {
            if (--arriving != 0 || !IsTransitionVersionCurrent(toIcon, toVersion)) return;
            Motion.Stop(toTransform, ScaleTransform.ScaleXProperty, 1);
            Motion.Stop(toTransform, ScaleTransform.ScaleYProperty, 1);
            Motion.Stop(toIcon, UIElement.OpacityProperty, 1);
        }
        Motion.To(fromTransform, ScaleTransform.ScaleXProperty, Motion.ExitToScale, Motion.Exit, done: Left);
        Motion.To(fromTransform, ScaleTransform.ScaleYProperty, Motion.ExitToScale, Motion.Exit, done: Left);
        Motion.To(fromIcon, UIElement.OpacityProperty, 0, Motion.Exit, done: Left);
        Motion.To(toTransform, ScaleTransform.ScaleXProperty, 1, Motion.Press,
            from: entering ? Motion.TileFromScale : null, done: Arrived);
        Motion.To(toTransform, ScaleTransform.ScaleYProperty, 1, Motion.Press,
            from: entering ? Motion.TileFromScale : null, done: Arrived);
        Motion.To(toIcon, UIElement.OpacityProperty, 1, Motion.Press, from: entering ? 0 : null, done: Arrived);
    }

    internal static void PlayButtonPressAnimation(Border button, double pressedScale = 0.82)
    {
        var transform = button.RenderTransform as ScaleTransform ?? new ScaleTransform(1, 1);
        button.RenderTransform = transform;
        button.RenderTransformOrigin = new Point(0.5, 0.5);
        int version = NextTransitionVersion(button);
        Motion.To(transform, ScaleTransform.ScaleYProperty, pressedScale, Motion.PressDown);
        Motion.To(transform, ScaleTransform.ScaleXProperty, pressedScale, Motion.PressDown, done: () =>
        {
            if (!IsTransitionVersionCurrent(button, version)) return;
            Motion.To(transform, ScaleTransform.ScaleXProperty, 1, Motion.Press,
                done: () => Motion.Stop(transform, ScaleTransform.ScaleXProperty, 1));
            Motion.To(transform, ScaleTransform.ScaleYProperty, 1, Motion.Press,
                done: () => Motion.Stop(transform, ScaleTransform.ScaleYProperty, 1));
        });
    }

    internal static void PlayNextSkipAnimation(Path arrow0, Path arrow1) => PlaySkipAnimation(arrow0, arrow1, 220);

    internal static void PlayPrevSkipAnimation(Path arrow0, Path arrow2) => PlaySkipAnimation(arrow0, arrow2, -220);

    private static void PlaySkipAnimation(Path incoming, Path outgoing, double distance)
    {
        int version = NextTransitionVersion(outgoing);
        var outgoingTransform = outgoing.RenderTransform as TranslateTransform ?? new TranslateTransform();
        var incomingTransform = incoming.RenderTransform as TranslateTransform ?? new TranslateTransform();
        outgoing.RenderTransform = outgoingTransform;
        incoming.RenderTransform = incomingTransform;
        bool entering = incoming.Opacity <= 0 && !incoming.HasAnimatedProperties;
        int pending = 4;
        void Finished()
        {
            if (--pending != 0 || !IsTransitionVersionCurrent(outgoing, version)) return;
            // The arriving copy now covers the resting glyph, so exchanging them is invisible.
            Motion.Stop(outgoingTransform, TranslateTransform.XProperty, 0);
            Motion.Stop(incomingTransform, TranslateTransform.XProperty, 0);
            Motion.Stop(outgoing, UIElement.OpacityProperty, 1);
            Motion.Stop(incoming, UIElement.OpacityProperty, 0);
        }
        Motion.To(outgoingTransform, TranslateTransform.XProperty, distance, Motion.Exit, done: Finished);
        Motion.To(outgoing, UIElement.OpacityProperty, 0, Motion.Exit, done: Finished);
        Motion.To(incomingTransform, TranslateTransform.XProperty, 0, Motion.Press,
            from: entering ? -distance : null, done: Finished);
        Motion.To(incoming, UIElement.OpacityProperty, 1, Motion.Press, done: Finished);
    }
}
