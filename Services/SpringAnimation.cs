using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace VNotch.Services;

/// <summary>
/// How a spring moves, in the terms of SwiftUI's <c>Spring(duration:bounce:)</c>: <paramref name="Response"/>
/// is the period of the undamped spring, <paramref name="Bounce"/> 0 settles without overshoot and higher
/// values overshoot more (0.2 is about 1.5 %).
/// </summary>
public sealed record SpringMotion(TimeSpan Response, double Bounce)
{
    internal double AngularFrequency => 2 * Math.PI / Response.TotalSeconds;
    internal double DampingRatio => Math.Clamp(1 - Bounce, 0.05, 1);

    /// <param name="settle">How close counts as arrived, in the property's own unit: a quarter pixel for sizes,
    /// far less for scales and opacities that only travel a fraction of 1.</param>
    /// <param name="floor">The value never goes below this, however far the spring swings (a size may not be negative).</param>
    internal SpringDoubleAnimation Animate(double from, double to, double velocity = 0, double settle = SpringDoubleAnimation.PixelSettle,
        double floor = double.NegativeInfinity) =>
        new(from, to, velocity, AngularFrequency, DampingRatio, settle, floor);
}

/// <summary>
/// A damped spring as a WPF animation. It is solved in closed form, so it looks the same at any frame rate,
/// and it can start with a velocity: a spring sent somewhere new while still moving keeps its momentum
/// instead of starting again from rest. Its duration is the time it needs to settle.
/// </summary>
internal sealed class SpringDoubleAnimation : DoubleAnimationBase
{
    // Sizes are settled once the remaining swing is under a quarter of a device-independent pixel.
    internal const double PixelSettle = 0.25;
    private readonly double _settle;
    private static readonly TimeSpan LongestSettle = TimeSpan.FromSeconds(2);

    private readonly double _from;
    private readonly double _to;
    private readonly double _velocity;
    private readonly double _omega;
    private readonly double _zeta;
    private readonly double _floor;

    internal SpringDoubleAnimation(double from, double to, double velocity, double angularFrequency, double dampingRatio, double settle = PixelSettle,
        double floor = double.NegativeInfinity)
    {
        _floor = double.IsNaN(floor) ? double.NegativeInfinity : floor;
        _settle = settle > 0 && double.IsFinite(settle) ? settle : PixelSettle;
        _from = from;
        _to = to;
        _velocity = double.IsFinite(velocity) ? velocity : 0;
        _omega = angularFrequency;
        _zeta = dampingRatio;
        Duration = new Duration(SettleTime());
        FillBehavior = FillBehavior.HoldEnd;
    }

    internal double To => _to;

    /// <summary>Position after <paramref name="seconds"/>.</summary>
    internal double ValueAt(double seconds) => _to + Offset(seconds, out _);

    /// <summary>Speed in units per second after <paramref name="seconds"/>; zero once settled.</summary>
    internal double VelocityAt(double seconds)
    {
        if (seconds >= Duration.TimeSpan.TotalSeconds) return 0;
        Offset(seconds, out double velocity);
        return velocity;
    }

    protected override double GetCurrentValueCore(double defaultOriginValue, double defaultDestinationValue, AnimationClock animationClock)
    {
        if (animationClock.CurrentProgress is not { } progress || progress >= 1) return _to;
        return Math.Max(_floor, ValueAt(progress * Duration.TimeSpan.TotalSeconds));
    }

    protected override Freezable CreateInstanceCore() => new SpringDoubleAnimation(_from, _to, _velocity, _omega, _zeta, _settle, _floor);

    // Distance from the target (and its rate of change) for x'' + 2 zeta omega x' + omega^2 x = 0.
    private double Offset(double t, out double velocity)
    {
        double x0 = _from - _to, v0 = _velocity;
        if (t <= 0)
        {
            velocity = v0;
            return x0;
        }
        if (_zeta >= 1)
        {
            double decay = Math.Exp(-_omega * t);
            double b = v0 + _omega * x0;
            velocity = decay * (b - _omega * (x0 + b * t));
            return decay * (x0 + b * t);
        }
        double wd = _omega * Math.Sqrt(1 - _zeta * _zeta);
        double envelope = Math.Exp(-_zeta * _omega * t);
        double c = (v0 + _zeta * _omega * x0) / wd;
        double cos = Math.Cos(wd * t), sin = Math.Sin(wd * t);
        double offset = envelope * (x0 * cos + c * sin);
        velocity = envelope * ((c * wd - _zeta * _omega * x0) * cos - (x0 * wd + _zeta * _omega * c) * sin);
        return offset;
    }

    private TimeSpan SettleTime()
    {
        if (Math.Abs(_from - _to) < _settle && Math.Abs(_velocity) < _settle * _omega) return TimeSpan.Zero;
        const double step = 1.0 / 480;
        double lastMoving = 0;
        for (double t = step; t < LongestSettle.TotalSeconds; t += step)
        {
            double offset = Offset(t, out double velocity);
            if (Math.Abs(offset) >= _settle || Math.Abs(velocity) >= _settle * _omega) lastMoving = t;
            else if (t - lastMoving > 0.05) break;
        }
        return TimeSpan.FromSeconds(Math.Min(lastMoving + step, LongestSettle.TotalSeconds));
    }
}
