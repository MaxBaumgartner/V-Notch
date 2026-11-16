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
    private readonly Func<bool>? _allowed;

    public LoopSet(Func<bool>? allowed = null) => _allowed = allowed;

    public bool IsEmpty => _running.Count == 0;

    /// <param name="fps">A lower rate for slow, ambient loops (a breathing glow), so the notch repaints less often.</param>
    public void Run(IAnimatable target, DependencyProperty property, AnimationTimeline animation, int? fps = null)
    {
        if (AnimationConfig.ReduceMotion || _allowed?.Invoke() == false) return;
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

    public void To(double to, double? from = null, TimeSpan? delay = null, Action? done = null)
    {
        Running.Add(this);
        Motion.To(this, ValueProperty, to, Motion.Glide, from: from, delay: delay,
            done: () => { Running.Remove(this); done?.Invoke(); });
    }

    public void Stop(double? value = null)
    {
        Running.Remove(this);
        Motion.Stop(this, ValueProperty, value);
    }

    /// <summary>Runs from <paramref name="from"/> to <paramref name="to"/>; with reduced motion it jumps.</summary>
    public void Play(double from, double to, double ms, IEasingFunction ease, double delayMs = 0)
    {
        if (AnimationConfig.ReduceMotion || ms <= 0)
        {
            Stop(to);
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
        bool fresh = FindTransform<TranslateTransform>(element) == null;
        var shift = Transform<TranslateTransform>(element);
        var delay = EntranceDelay(delayMs);
        FadeIn(element, fresh, delay);
        Motion.To(shift, TranslateTransform.YProperty, 0, Motion.Tile, from: fresh ? dy : null, delay: delay);
        Motion.To(shift, TranslateTransform.XProperty, 0, Motion.Tile, from: fresh ? dx : null, delay: delay);
    }

    /// <summary>Pops in from small with a little overshoot.</summary>
    public static void Pop(UIElement element, double delayMs, double fromScale = 0.3, double ms = 420)
    {
        bool fresh = FindTransform<ScaleTransform>(element) == null;
        var scale = Transform<ScaleTransform>(element);
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        var delay = EntranceDelay(delayMs);
        FadeIn(element, fresh, delay);
        Motion.To(scale, ScaleTransform.ScaleXProperty, 1, Motion.TileScale, from: fresh ? Motion.TileFromScale : null, delay: delay);
        Motion.To(scale, ScaleTransform.ScaleYProperty, 1, Motion.TileScale, from: fresh ? Motion.TileFromScale : null, delay: delay);
    }

    /// <summary>Grows out of one edge (origin given in relative coordinates).</summary>
    public static void Grow(UIElement element, double delayMs, bool horizontal, Point origin, double ms = 640)
    {
        bool fresh = FindTransform<ScaleTransform>(element) == null;
        var scale = Transform<ScaleTransform>(element);
        element.RenderTransformOrigin = origin;
        Motion.To(scale, horizontal ? ScaleTransform.ScaleXProperty : ScaleTransform.ScaleYProperty,
            1, Motion.Glide, from: fresh ? 0 : null, delay: EntranceDelay(delayMs));
    }

    private static TimeSpan EntranceDelay(double delayMs) => Motion.Stagger((int)Math.Round(delayMs / Motion.StaggerMs));

    private static T? FindTransform<T>(UIElement element) where T : Transform
    {
        if (element.RenderTransform is T direct && !direct.IsFrozen) return direct;
        if (element.RenderTransform is TransformGroup group)
            foreach (var child in group.Children)
                if (child is T found && !found.IsFrozen) return found;
        return null;
    }

    internal static T Transform<T>(UIElement element) where T : Transform, new()
    {
        if (FindTransform<T>(element) is { } existing) return existing;
        var transform = new T();
        if (element.RenderTransform is TransformGroup group && !group.IsFrozen) group.Children.Add(transform);
        // A live transform stays even while it reads identity: an entrance that has not ticked yet sits at its base value.
        else if (element.RenderTransform is { } old && (!old.IsFrozen || !old.Value.IsIdentity))
            element.RenderTransform = new TransformGroup { Children = { old, transform } };
        else element.RenderTransform = transform;
        return transform;
    }

    private static void FadeIn(UIElement element, bool fresh, TimeSpan delay)
    {
        if (AnimationConfig.ReduceMotion)
        {
            Motion.Stop(element, UIElement.OpacityProperty, 1);
            return;
        }
        if (fresh) element.Opacity = 0;
        element.BeginAnimation(UIElement.OpacityProperty,
            To(element.Opacity, 1, 160, Out, fresh ? delay.TotalMilliseconds : 0));
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
        Motion.To((DependencyObject)target, property, to, Motion.Glide);
    }
}
