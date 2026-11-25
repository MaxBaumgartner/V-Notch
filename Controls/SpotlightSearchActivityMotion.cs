using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Services;

namespace VNotch.Controls;

internal sealed class SpotlightSearchActivityMotion(FrameworkElement indicator, UIElement panel,
    RotateTransform rotation)
{
    private bool _searchActivityActive;
    private bool _searchActivityRotating;

    internal bool Active => _searchActivityActive;

    internal void SetActive(bool active)
    {
        if (_searchActivityActive == active) return;
        _searchActivityActive = active;
        indicator.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        // Fade only on entering the waiting state, never on each keystroke.
        if (active) Motion.To(panel, UIElement.OpacityProperty, 1, Motion.Tile, from: 0);
        else Motion.Stop(panel, UIElement.OpacityProperty, 1);
        Refresh();
    }

    internal void Refresh()
    {
        bool rotate = _searchActivityActive && indicator.IsVisible && !AnimationConfig.ReduceMotion;
        if (_searchActivityRotating == rotate) return;
        _searchActivityRotating = rotate;
        rotation.BeginAnimation(RotateTransform.AngleProperty, null);
        rotation.Angle = 0;
        if (!rotate) return;

        // Constant velocity makes progress legible without pulsing the text.
        var turn = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1))
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        Timeline.SetDesiredFrameRate(turn, Math.Min(60, AnimationConfig.TargetFps));
        rotation.BeginAnimation(RotateTransform.AngleProperty, turn);
    }
}
