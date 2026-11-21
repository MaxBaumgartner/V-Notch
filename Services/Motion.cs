using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media.Animation;

namespace VNotch.Services;

/// <summary>
/// The notch's one motion language: a handful of named springs and a way to send any number there.
/// Every spring started through <see cref="To"/> remembers where it is going, so sending the same property
/// somewhere else while it still moves carries position and speed over instead of starting from rest.
/// </summary>
internal static partial class Motion
{
    // Shell: the notch itself opening and closing. Width leads, height swings a little longer.
    public static readonly SpringMotion ShellOpenWidth = Ms(400, 0.48);
    public static readonly SpringMotion ShellOpenHeight = Ms(520, 0.39);
    public static readonly SpringMotion ShellCloseWidth = Ms(340, 0.24);
    public static readonly SpringMotion ShellCloseHeight = Ms(300, 0.10);

    // Pop: whatever grows out of the closed notch (pills, HUDs, the orb, the phone) and goes back in.
    public static readonly SpringMotion Pop = Ms(440, 0.50);
    public static readonly SpringMotion PopBack = Ms(340, 0.22);

    // Morph: Spotlight growing out of the notch and squishing back into it. A whole search window is a lot of
    // pixels, so it swings less than the notch's own open, and it lands quickly because results wait for it.
    public static readonly SpringMotion MorphOpen = Ms(380, 0.14);
    public static readonly SpringMotion MorphClose = Ms(320, 0.04);

    // Glide: size and layout changes (tab switch, folding sections, sliders settling).
    public static readonly SpringMotion Glide = Ms(420, 0.28);
    public static readonly SpringMotion Shift = Ms(380, 0.05);
    public static readonly SpringMotion Flick = Ms(480, 0.30);

    // Tile: content arriving, staggered by StaggerMs from TileFromScale.
    public static readonly SpringMotion Tile = Ms(450, 0.50);
    public const double TileFromScale = 0.5;
    // The same arrival for the scale of a card: a bounce in scale is a bounce in pixels on a wide card, and
    // Tile's would push its edges 8 % past the clip of its tab. The lively part stays in the drift.
    public static readonly SpringMotion TileScale = Ms(450, 0.22);
    public const int StaggerMs = 38;
    public const int StaggerCapMs = 300;

    // Press: hover, press release, switches, small glyph hops.
    public static readonly SpringMotion Press = Ms(340, 0.58);
    // How fast something gives way under the pointer before Press brings it back.
    public static readonly SpringMotion PressDown = Ms(140, 0);

    // Exit: leaving is shorter than arriving and never overshoots.
    public static readonly SpringMotion Exit = Ms(220, 0);
    public const double ExitToScale = 0.86;

    private static SpringMotion Ms(double response, double bounce) => new(TimeSpan.FromMilliseconds(response), bounce);

    private sealed class Running
    {
        public required SpringDoubleAnimation Spring;
        public required AnimationClock Clock;
        /// <summary>Endings that outlive this spring: whichever spring replaces it carries them on.</summary>
        public Action? Kept;
    }

    private static readonly ConditionalWeakTable<DependencyObject, Dictionary<DependencyProperty, Running>> Live = new();

    /// <summary>The delay of the <paramref name="index"/>th item of a staggered group.</summary>
    public static TimeSpan Stagger(int index, int stepMs = StaggerMs) =>
        TimeSpan.FromMilliseconds(Math.Min(Math.Max(index, 0) * stepMs, StaggerCapMs));

    /// <summary>
    /// Springs <paramref name="property"/> of <paramref name="target"/> to <paramref name="to"/>. A spring already
    /// running on it hands over its speed. <paramref name="from"/> places the value first (an entrance);
    /// leave it out to continue from wherever the value is. <paramref name="velocity"/> (units per second) starts
    /// it with a speed of its own, e.g. the pointer's when a drag lets go. <paramref name="done"/> is dropped when
    /// another spring takes the property over, unless <paramref name="keep"/> is set: then it runs once the
    /// property finally comes to rest, whoever moved it last (a session that must end, e.g. the shell's).
    /// </summary>
    public static void To(DependencyObject target, DependencyProperty property, double to, SpringMotion spring,
        double? from = null, TimeSpan? delay = null, int? fps = null, Action? done = null, double? velocity = null,
        bool keep = false, double acceleration = 0)
    {
        if (target is not IAnimatable animatable) throw new ArgumentException("Target cannot be animated.", nameof(target));
        var live = Live.GetOrCreateValue(target);
        Action? kept = live.TryGetValue(property, out var replaced) ? replaced.Kept : null;
        if (keep && done != null) { kept += done; done = null; }

        if (AnimationConfig.ReduceMotion)
        {
            live.Remove(property);
            animatable.BeginAnimation(property, null);
            target.SetValue(property, to);
            done?.Invoke();
            kept?.Invoke();
            return;
        }

        double speed = velocity ?? 0;
        if (velocity is null && from is null && live.TryGetValue(property, out var old))
            speed = old.Spring.VelocityAt(old.Clock.CurrentTime?.TotalSeconds ?? 0);
        double start = from ?? (double)target.GetValue(property);
        if (!double.IsFinite(start)) start = to;

        // Arrived means within half a percent of the way, so a scale going 0.72 -> 1 settles as finely as a
        // width going 150 -> 720.
        double settle = Math.Clamp(Math.Abs(to - start) * 0.005, 0.0005, SpringDoubleAnimation.PixelSettle);
        // A width or height that swings below zero throws, so sizes stop at zero.
        bool isSize = property == FrameworkElement.WidthProperty || property == FrameworkElement.HeightProperty;
        var animation = spring.Animate(start, to, speed, settle, isSize ? 0 : double.NegativeInfinity, acceleration);
        if (animation.Duration.TimeSpan <= TimeSpan.Zero)
        {
            live.Remove(property);
            animatable.BeginAnimation(property, null);
            target.SetValue(property, to);
            done?.Invoke();
            kept?.Invoke();
            return;
        }
        if (delay is { } wait && wait > TimeSpan.Zero) animation.BeginTime = wait;
        Timeline.SetDesiredFrameRate(animation, fps ?? AnimationConfig.TargetFps);

        // Read speed and arrival from the rendered clock; dispatcher stalls must not age them ahead of the picture.
        var clock = animation.CreateClock();
        var running = new Running { Spring = animation, Clock = clock, Kept = kept };
        live[property] = running;
        clock.Completed += (_, _) =>
        {
            if (live.TryGetValue(property, out var current) && ReferenceEquals(current, running)) live.Remove(property);
            else return; // replaced on the way: the newer spring owns the ending
            done?.Invoke();
            kept?.Invoke();
        };
        // An entrance that waits must already sit at its start, or it shows its resting look until its turn.
        if (from is { } placed && delay is { } d && d > TimeSpan.Zero) target.SetValue(property, placed);
        animatable.ApplyAnimationClock(property, clock, HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>Speed of the spring running on the property right now, in units per second; zero when at rest.</summary>
    public static double VelocityOf(DependencyObject target, DependencyProperty property) =>
        Live.TryGetValue(target, out var live) && live.TryGetValue(property, out var running)
            ? running.Spring.VelocityAt(running.Clock.CurrentTime?.TotalSeconds ?? 0)
            : 0;

    /// <summary>
    /// Whether a spring started here is still on its way, judged by its own clock: a spring that code outside
    /// this class cut off with a plain animation counts as over once its time has passed.
    /// </summary>
    public static bool IsRunning(DependencyObject target, DependencyProperty property) =>
        Live.TryGetValue(target, out var live) && live.TryGetValue(property, out var running) &&
        running.Clock.CurrentState != ClockState.Filling;

    /// <summary>
    /// Forgets the spring on the property without touching its value or clock, for code that retires the clock
    /// itself (<c>BeginAnimation(property, null)</c>) and must not hand a stale speed to the next spring.
    /// </summary>
    public static void Forget(DependencyObject target, DependencyProperty property)
    {
        if (Live.TryGetValue(target, out var live)) live.Remove(property);
    }

    /// <summary>Stops the spring and leaves the property at <paramref name="value"/> (its current value if omitted).</summary>
    public static void Stop(DependencyObject target, DependencyProperty property, double? value = null)
    {
        if (target is not IAnimatable animatable) return;
        double rest = value ?? (double)target.GetValue(property);
        if (Live.TryGetValue(target, out var live)) live.Remove(property);
        animatable.BeginAnimation(property, null);
        target.SetValue(property, rest);
    }
}
