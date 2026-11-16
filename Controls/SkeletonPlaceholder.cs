using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Services;

namespace VNotch.Controls;

/// <summary>One shared shimmer clock, alive only while this placeholder is on screen.</summary>
internal sealed class SkeletonPlaceholder : StackPanel, IDisposable
{
    private readonly TranslateTransform _sweep = new(-1, 0);
    private readonly bool _unreachable;
    private bool _disposed;

    internal bool IsAnimating => _sweep.HasAnimatedProperties;

    internal SkeletonPlaceholder(bool unreachable, string reason, params double[] rowHeights)
    {
        _unreachable = unreachable;
        ToolTip = reason;
        AutomationProperties.SetName(this, reason);
        Opacity = unreachable ? 0.65 : 1;
        var fill = new LinearGradientBrush
        {
            StartPoint = new Point(-0.4, 0), EndPoint = new Point(0.4, 0),
            SpreadMethod = GradientSpreadMethod.Pad,
            RelativeTransform = _sweep,
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x12, 255, 255, 255), 0),
                new GradientStop(Color.FromArgb(0x1A, 255, 255, 255), 0.5),
                new GradientStop(Color.FromArgb(0x12, 255, 255, 255), 1)
            }
        };
        foreach (double height in rowHeights)
            Children.Add(new Border
            {
                Height = height, CornerRadius = new CornerRadius(12),
                Background = fill, Margin = new Thickness(0, 0, 0, 8)
            });

        Loaded += (_, _) =>
        {
            if (_disposed) return;
            AnimationConfig.ReduceMotionChanged += RefreshMotion;
            RefreshMotion();
        };
        IsVisibleChanged += (_, _) => RefreshMotion();
        Unloaded += (_, _) =>
        {
            AnimationConfig.ReduceMotionChanged -= RefreshMotion;
            Stop();
        };
    }

    protected override void OnVisualParentChanged(DependencyObject oldParent)
    {
        base.OnVisualParentChanged(oldParent);
        // Removal can precede Unloaded; release the clock immediately.
        if (VisualTreeHelper.GetParent(this) == null) Stop();
    }

    private void RefreshMotion()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(RefreshMotion));
            return;
        }
        if (_disposed || !IsLoaded || !IsVisible || _unreachable || AnimationConfig.ReduceMotion)
        {
            Stop();
            return;
        }
        if (IsAnimating) return;
        var animation = new DoubleAnimation(-1, 2, TimeSpan.FromSeconds(1.9))
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        Timeline.SetDesiredFrameRate(animation, 30);
        _sweep.BeginAnimation(TranslateTransform.XProperty, animation);
    }

    private void Stop()
    {
        _sweep.BeginAnimation(TranslateTransform.XProperty, null);
        _sweep.X = -1;
    }

    public void Dispose()
    {
        _disposed = true;
        AnimationConfig.ReduceMotionChanged -= RefreshMotion;
        Stop();
    }
}
