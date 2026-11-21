using System;
using System.Threading.Tasks;
using System.Windows.Media;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class MotionTests
{
    [Fact]
    public void ASpringSentElsewhereMidwayHandsItsSpeedOver() => SharedStaTestRunner.RunAsync(async () =>
    {
        var scale = new ScaleTransform(1, 1);
        Motion.To(scale, ScaleTransform.ScaleXProperty, 2, Motion.Glide);
        await WpfFrameWaiter.UntilAsync(() => Math.Abs(Motion.VelocityOf(scale, ScaleTransform.ScaleXProperty)) > .01,
            "spring has advanced on the WPF clock");

        double speed = Motion.VelocityOf(scale, ScaleTransform.ScaleXProperty);
        Assert.True(Math.Abs(speed) > 0.01, $"still moving, was {speed}");

        Motion.To(scale, ScaleTransform.ScaleXProperty, 1, Motion.Glide);
        // Sent back, it starts with the speed it had instead of from rest (the test machine may be slow, so
        // only the hand-over is checked, not how fast it was).
        double handed = Motion.VelocityOf(scale, ScaleTransform.ScaleXProperty);
        Assert.Equal(Math.Sign(speed), Math.Sign(handed));
        Assert.InRange(Math.Abs(handed), Math.Abs(speed) * 0.3, Math.Abs(speed) * 3);
    });

    [Fact]
    public void AKeptEndingSurvivesATakeoverAndADroppedOneDoesNot() => SharedStaTestRunner.RunAsync(async () =>
    {
        var scale = new ScaleTransform(1, 1);
        int kept = 0, dropped = 0, last = 0;
        Motion.To(scale, ScaleTransform.ScaleXProperty, 2, Motion.Glide, done: () => kept++, keep: true);
        Motion.To(scale, ScaleTransform.ScaleYProperty, 2, Motion.Glide, done: () => dropped++);
        await Task.Delay(30);
        // Something else (a hover) takes both over before they arrive.
        Motion.To(scale, ScaleTransform.ScaleXProperty, 1.2, Motion.PressDown, done: () => last++);
        Motion.To(scale, ScaleTransform.ScaleYProperty, 1.2, Motion.PressDown);
        Assert.Equal(0, kept);
        await WpfFrameWaiter.UntilAsync(() => last == 1, "the takeover spring settles");
        await Task.Delay(50);
        Assert.Equal(1, kept);
        Assert.Equal(0, dropped);
    });

    [Fact]
    public void AnEntranceWaitsAtItsStart() => SharedStaTestRunner.Run(() =>
    {
        var scale = new ScaleTransform(1, 1);
        Motion.To(scale, ScaleTransform.ScaleXProperty, 1, Motion.Tile, from: Motion.TileFromScale, delay: Motion.Stagger(3));
        Assert.Equal(Motion.TileFromScale, scale.ScaleX, 3);
    });

    [Fact]
    public void ReducedMotionGoesStraightThere() => SharedStaTestRunner.Run(() =>
    {
        bool before = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(true);
        try
        {
            var scale = new ScaleTransform(1, 1);
            bool done = false;
            Motion.To(scale, ScaleTransform.ScaleXProperty, 3, Motion.Pop, done: () => done = true);
            Assert.Equal(3, scale.ScaleX);
            Assert.True(done);
        }
        finally
        {
            AnimationConfig.SetReduceMotion(before);
        }
    });

    [Fact]
    public void LeavingIsShorterThanArrivingAndNeverOvershoots()
    {
        var arrive = Motion.Pop.Animate(0, 100);
        var leave = Motion.Exit.Animate(100, 0);
        Assert.True(leave.Duration.TimeSpan < arrive.Duration.TimeSpan);
        for (double t = 0; t < 1; t += 0.004) Assert.True(leave.ValueAt(t) >= -0.01);
        Assert.Equal(TimeSpan.FromMilliseconds(Motion.StaggerCapMs), Motion.Stagger(500));
    }
}
