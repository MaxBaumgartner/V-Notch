using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VNotch.Controls;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class WeatherMotionTests
{
    [Fact]
    public void EntranceRetargetKeepsItsLiveTransformAndVelocity() => SharedStaTestRunner.RunAsync(async () =>
    {
        bool reduced = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(false);
        try
        {
            var card = new Border();
            WeatherMotion.Pop(card, 0);
            var scale = WeatherMotion.Transform<ScaleTransform>(card);
            await Task.Delay(90);
            double position = scale.ScaleX;
            double speed = Motion.VelocityOf(scale, ScaleTransform.ScaleXProperty);
            WeatherMotion.Pop(card, 0);
            Assert.Same(scale, WeatherMotion.Transform<ScaleTransform>(card));
            Assert.InRange(Math.Abs(scale.ScaleX - position), 0, .01);
            Assert.Equal(Math.Sign(speed), Math.Sign(Motion.VelocityOf(scale, ScaleTransform.ScaleXProperty)));
        }
        finally { AnimationConfig.SetReduceMotion(reduced); }
    });

    [Fact]
    public void HiddenLoopGateAndReducedMotionLeaveNoClocks() => SharedStaTestRunner.Run(() =>
    {
        var loops = new LoopSet(() => false);
        var shift = new TranslateTransform();
        loops.Swing(shift, TranslateTransform.XProperty, -2, 2, 1);
        Assert.True(loops.IsEmpty);
        Assert.False(shift.HasAnimatedProperties);
        bool reduced = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(true);
        try
        {
            var card = new Border();
            WeatherMotion.Pop(card, 120);
            var scale = WeatherMotion.Transform<ScaleTransform>(card);
            Assert.Equal(1, card.Opacity);
            Assert.Equal(1, scale.ScaleX);
            Assert.False(scale.HasAnimatedProperties);
        }
        finally { AnimationConfig.SetReduceMotion(reduced); }
    });

    private sealed class Closer(Action close) : IDisposable { public void Dispose() => close(); }
}
