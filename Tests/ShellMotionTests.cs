using System.Windows;
using System.Windows.Controls;
using VNotch.Controllers;
using VNotch.Models;
using VNotch.Presenters;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class ShellMotionTests
{
    [Fact]
    public void ContentHeightRetargetStillCompletesTheShellSession() => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        try
        {
            AnimationConfig.SetReduceMotion(false);
            var border = new Border { Width = 300, Height = 40 };
            using var presenter = new NotchShellPresenter(new() { NotchBorder = border });
            int completions = 0;
            presenter.AnimateShell(new TransitionPlan(1, NotchView.Compact, NotchView.Pi, NotchShapeState.Expanding,
                680, 300, 24, new(TimeSpan.FromMilliseconds(400), AnimationPrimitives._easeUiOut, 60, false)), _ => completions++);
            await WpfFrameWaiter.UntilAsync(() => border.Height > 70, "opening starts", ct);
            double height = border.Height;
            Assert.True(presenter.RetargetHeight(360, () => { }));
            Assert.Equal(height, border.Height, 6);
            await WpfFrameWaiter.UntilAsync(() => completions == 1, "retargeted shell completes", ct);
            Assert.Equal(360, border.Height);
            Assert.Equal(680, border.Width);
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    [Fact]
    public void AHoverTakingTheSizeOverMidOpeningStillCompletesTheShellSession() => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        try
        {
            AnimationConfig.SetReduceMotion(false);
            var border = new Border { Width = 300, Height = 40 };
            using var presenter = new NotchShellPresenter(new() { NotchBorder = border });
            int completions = 0;
            presenter.AnimateShell(new TransitionPlan(1, NotchView.Compact, NotchView.Pi, NotchShapeState.Expanding,
                680, 300, 24, new(TimeSpan.FromMilliseconds(400), AnimationPrimitives._easeUiOut, 60, false)), _ => completions++);
            await WpfFrameWaiter.UntilAsync(() => border.Height > 70, "opening starts", ct);
            // The pointer leaves and comes back: the hover springs replace the shell's own on both dimensions.
            Motion.To(border, FrameworkElement.WidthProperty, 332, Motion.Press);
            Motion.To(border, FrameworkElement.HeightProperty, 76, Motion.Press);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            await WpfFrameWaiter.UntilAsync(() => completions == 1, "taken-over shell completes", ct);
            Assert.InRange(clock.ElapsedMilliseconds, 0, 2500);
            // The hover owns the size now; the session ending must not yank it back.
            Assert.Equal(76, border.Height, 1);
            Assert.Equal(332, border.Width, 1);
            await Task.Delay(200, ct);
            Assert.Equal(1, completions);
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    [Fact]
    public void APlainAnimationCuttingTheSpringsOffStillEndsTheShellSession() => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        try
        {
            AnimationConfig.SetReduceMotion(false);
            var border = new Border { Width = 300, Height = 40 };
            using var presenter = new NotchShellPresenter(new() { NotchBorder = border });
            int completions = 0;
            presenter.AnimateShell(new TransitionPlan(1, NotchView.Compact, NotchView.Pi, NotchShapeState.Expanding,
                680, 300, 24, new(TimeSpan.FromMilliseconds(400), AnimationPrimitives._easeUiOut, 60, false)), _ => completions++);
            await WpfFrameWaiter.UntilAsync(() => border.Height > 70, "opening starts", ct);
            // What a pill or a banner that is not on the shared springs yet does to the notch.
            border.BeginAnimation(FrameworkElement.WidthProperty, null);
            border.BeginAnimation(FrameworkElement.HeightProperty,
                new System.Windows.Media.Animation.DoubleAnimation(90, TimeSpan.FromMilliseconds(150)));
            var clock = System.Diagnostics.Stopwatch.StartNew();
            await WpfFrameWaiter.UntilAsync(() => completions == 1, "cut-off shell completes", ct);
            Assert.InRange(clock.ElapsedMilliseconds, 0, 2500);
            await Task.Delay(300, ct);
            Assert.Equal(1, completions);
            Assert.Equal(90, border.Height, 1);
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    [Fact]
    public void ReversingTheShellCarriesBothDimensionsAndTheCornerFromTheRenderedFrame() => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        try
        {
            AnimationConfig.SetReduceMotion(false);
            var border = new Border { Width = 68, Height = 28, CornerRadius = new CornerRadius(14) };
            using var presenter = new NotchShellPresenter(new() { NotchBorder = border });
            var motion = new TransitionMotionConfig(TimeSpan.FromMilliseconds(400), AnimationPrimitives._easeUiOut, 60, false);
            var opening = new TransitionPlan(1, NotchView.Compact, NotchView.Media, NotchShapeState.Expanding,
                528, 300, 36, motion);
            int staleCompletions = 0;
            presenter.AnimateShell(opening, _ => staleCompletions++);
            await WpfFrameWaiter.UntilAsync(() => border.Width > 120, "shell starts expanding", ct);
            double width = border.Width, height = border.Height, radius = border.CornerRadius.BottomLeft;
            double widthSpeed = Motion.VelocityOf(border, FrameworkElement.WidthProperty);
            double heightSpeed = Motion.VelocityOf(border, FrameworkElement.HeightProperty);
            Assert.True(widthSpeed > 100);
            Assert.True(heightSpeed > 100);
            presenter.CancelCurrentAnimation();
            bool completed = false;
            presenter.AnimateShell(opening with
            {
                SessionId = 2, FromView = NotchView.Media, TargetView = NotchView.Compact,
                TargetWidth = 68, TargetHeight = 28, TargetCornerRadius = 14
            }, _ => completed = true);
            Assert.Equal(width, border.Width, 6);
            Assert.Equal(height, border.Height, 6);
            Assert.Equal(radius, border.CornerRadius.BottomLeft, 6);
            Assert.InRange(Motion.VelocityOf(border, FrameworkElement.WidthProperty), widthSpeed * .9, widthSpeed * 1.1);
            Assert.InRange(Motion.VelocityOf(border, FrameworkElement.HeightProperty), heightSpeed * .9, heightSpeed * 1.1);
            await WpfFrameWaiter.UntilAsync(() => completed, "reversed shell settles", ct);
            Assert.Equal(68, border.Width);
            Assert.Equal(28, border.Height);
            Assert.Equal(0, staleCompletions);
            await WpfFrameWaiter.UntilAsync(() => Math.Abs(border.CornerRadius.BottomLeft - 14) < .001,
                "corner settles with shell", ct);
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    [Fact]
    public void ReducedMotionSetsTheWholeShellImmediately() => SharedStaTestRunner.Run(() =>
    {
        var border = new Border { Width = 68, Height = 28, CornerRadius = new CornerRadius(14) };
        using var presenter = new NotchShellPresenter(new() { NotchBorder = border });
        bool completed = false;
        presenter.AnimateShell(new TransitionPlan(1, NotchView.Compact, NotchView.Media, NotchShapeState.Expanding,
            528, 300, 36, new(TimeSpan.FromMilliseconds(400), AnimationPrimitives._easeUiOut, 60, true)), _ => completed = true);
        Assert.True(completed);
        Assert.Equal(528, border.Width);
        Assert.Equal(300, border.Height);
        Assert.Equal(new CornerRadius(36), border.CornerRadius);
        Assert.False(border.HasAnimatedProperties);
    });
}
