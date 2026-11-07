using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Services;

namespace VNotch.Controls;

/// <summary>
/// The endless animations of one part of the weather tab, so they can all be stopped at once when the tab
/// goes off screen or the sky changes. Nothing here runs while reduced motion is on.
/// </summary>
internal sealed class LoopSet
{
    private readonly List<(IAnimatable Target, DependencyProperty Property)> _running = new();

    public bool IsEmpty => _running.Count == 0;

    /// <param name="fps">A lower rate for slow, ambient loops (a breathing glow), so the notch repaints less often.</param>
    public void Run(IAnimatable target, DependencyProperty property, AnimationTimeline animation, int? fps = null)
    {
        if (AnimationConfig.ReduceMotion) return;
        Timeline.SetDesiredFrameRate(animation, fps ?? AnimationConfig.TargetFps);
        target.BeginAnimation(property, animation);
        _running.Add((target, property));
    }

    /// <summary>A sine swing between two values, forever.</summary>
    public void Swing(IAnimatable target, DependencyProperty property, double from, double to, double seconds, double delaySeconds = 0)
    {
        Run(target, property, new DoubleAnimation(from, to, TimeSpan.FromSeconds(seconds))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = WeatherMotion.Sine,
            BeginTime = TimeSpan.FromSeconds(delaySeconds)
        });
    }

    public void StopAll()
    {
        foreach (var (target, property) in _running) target.BeginAnimation(property, null);
        _running.Clear();
    }
}

/// <summary>An animatable number that reports every frame, for things a plain property animation cannot move (a count, a dot on an arc).</summary>
internal sealed class WeatherTween : Animatable
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(WeatherTween),
        new PropertyMetadata(0.0, (d, e) => ((WeatherTween)d).Step?.Invoke((double)e.NewValue)));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public Action<double>? Step { get; set; }

    /// <summary>Runs from <paramref name="from"/> to <paramref name="to"/>; with reduced motion it jumps.</summary>
    public void Play(double from, double to, double ms, IEasingFunction ease, double delayMs = 0)
    {
        if (AnimationConfig.ReduceMotion || ms <= 0)
        {
            BeginAnimation(ValueProperty, null);
            Value = to;
            Step?.Invoke(to);
            return;
        }
        Value = from;
        Step?.Invoke(from);
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = ease,
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            FillBehavior = FillBehavior.HoldEnd
        };
        Timeline.SetDesiredFrameRate(animation, AnimationConfig.TargetFps);
        // WPF only holds animated objects weakly: nothing else keeps a tween, so it would be collected mid-run.
        Running.Add(this);
        animation.Completed += (_, _) => Running.Remove(this);
        BeginAnimation(ValueProperty, animation);
    }

    private static readonly HashSet<WeatherTween> Running = new();

    protected override Freezable CreateInstanceCore() => new WeatherTween();
}

/// <summary>Easings and one-shot entrances shared by the weather tab.</summary>
internal static class WeatherMotion
{
    public static readonly IEasingFunction Sine = Frozen(new SineEase { EasingMode = EasingMode.EaseInOut });
    public static readonly IEasingFunction Out = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });
    public static readonly IEasingFunction OutQuint = Frozen(new QuinticEase { EasingMode = EasingMode.EaseOut });
    public static readonly IEasingFunction OutBack = Frozen(new BackEase { Amplitude = 0.45, EasingMode = EasingMode.EaseOut });
    public static readonly IEasingFunction Spring = Frozen(new ElasticEase { Oscillations = 2, Springiness = 5, EasingMode = EasingMode.EaseOut });

    private static IEasingFunction Frozen(EasingFunctionBase ease)
    {
        ease.Freeze();
        return ease;
    }

    public static DoubleAnimation To(double from, double to, double ms, IEasingFunction? ease, double delayMs = 0)
    {
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = ease,
            BeginTime = TimeSpan.FromMilliseconds(delayMs)
        };
        Timeline.SetDesiredFrameRate(animation, AnimationConfig.TargetFps);
        return animation;
    }

    /// <summary>Fades in while rising (or sliding in from the side) into place.</summary>
    public static void Rise(UIElement element, double delayMs, double dy = 10, double dx = 0, double ms = 460)
    {
        if (AnimationConfig.ReduceMotion)
        {
            element.Opacity = 1;
            return;
        }
        var shift = new TranslateTransform(dx, dy);
        element.RenderTransform = shift;
        element.Opacity = 0;
        var fade = To(0, 1, ms * 0.8, Out, delayMs);
        fade.Completed += (_, _) =>
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = 1;
        };
        element.BeginAnimation(UIElement.OpacityProperty, fade);
        if (dy != 0) shift.BeginAnimation(TranslateTransform.YProperty, To(dy, 0, ms, OutQuint, delayMs));
        if (dx != 0) shift.BeginAnimation(TranslateTransform.XProperty, To(dx, 0, ms, OutQuint, delayMs));
    }

    /// <summary>Pops in from small with a little overshoot.</summary>
    public static void Pop(UIElement element, double delayMs, double fromScale = 0.3, double ms = 420)
    {
        if (AnimationConfig.ReduceMotion)
        {
            element.Opacity = 1;
            return;
        }
        var scale = new ScaleTransform(fromScale, fromScale);
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.RenderTransform = scale;
        element.Opacity = 0;
        element.BeginAnimation(UIElement.OpacityProperty, To(0, 1, ms * 0.6, Out, delayMs));
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, To(fromScale, 1, ms, OutBack, delayMs));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, To(fromScale, 1, ms, OutBack, delayMs));
    }

    /// <summary>Grows out of one edge (origin given in relative coordinates).</summary>
    public static void Grow(UIElement element, double delayMs, bool horizontal, Point origin, double ms = 640)
    {
        if (AnimationConfig.ReduceMotion) return;
        var scale = new ScaleTransform(horizontal ? 0 : 1, horizontal ? 1 : 0);
        element.RenderTransformOrigin = origin;
        element.RenderTransform = scale;
        scale.BeginAnimation(horizontal ? ScaleTransform.ScaleXProperty : ScaleTransform.ScaleYProperty, To(0, 1, ms, OutQuint, delayMs));
    }

    /// <summary>Animates a brush colour (the brush must not be frozen).</summary>
    public static void Tint(SolidColorBrush brush, Color to, double ms = 160)
    {
        if (AnimationConfig.ReduceMotion)
        {
            brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            brush.Color = to;
            return;
        }
        var animation = new ColorAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Out };
        Timeline.SetDesiredFrameRate(animation, AnimationConfig.TargetFps);
        brush.BeginAnimation(SolidColorBrush.ColorProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>Glides a value to a new target from wherever it is now.</summary>
    public static void Glide(IAnimatable target, DependencyProperty property, double to, double ms = 170)
    {
        if (AnimationConfig.ReduceMotion)
        {
            target.BeginAnimation(property, null);
            ((DependencyObject)target).SetValue(property, to);
            return;
        }
        var animation = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Out };
        Timeline.SetDesiredFrameRate(animation, AnimationConfig.TargetFps);
        target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }
}
