using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Services;

namespace VNotch;

public partial class SpotlightWindow
{
    private bool _searchActivityActive;
    private bool _searchActivityRotating;
    private bool _searchInputPending;

    private void SetSearchActivity(bool active)
    {
        if (_searchActivityActive == active) return;
        _searchActivityActive = active;
        SearchActivity.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        // Fade only on entering the waiting state, never on each keystroke.
        if (active) Motion.To(StatusPanel, OpacityProperty, 1, Motion.Tile, from: 0);
        else Motion.Stop(StatusPanel, OpacityProperty, 1);
        RefreshSearchActivityMotion();
    }

    private void RefreshSearchActivityMotion()
    {
        bool rotate = _searchActivityActive && SearchActivity.IsVisible && !AnimationConfig.ReduceMotion;
        if (_searchActivityRotating == rotate) return;
        _searchActivityRotating = rotate;
        SearchActivityRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        SearchActivityRotation.Angle = 0;
        if (!rotate) return;

        // Constant velocity makes progress legible without pulsing the text.
        var turn = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1))
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        Timeline.SetDesiredFrameRate(turn, Math.Min(60, AnimationConfig.TargetFps));
        SearchActivityRotation.BeginAnimation(RotateTransform.AngleProperty, turn);
    }

    private void OnSearchMotionPreferenceChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(OnSearchMotionPreferenceChanged);
            return;
        }
        if (AnimationConfig.ReduceMotion) Motion.Stop(StatusPanel, OpacityProperty, 1);
        RefreshSearchActivityMotion();
    }
}
