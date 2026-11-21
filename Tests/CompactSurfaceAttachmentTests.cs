using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VNotch.Presenters;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class CompactSurfaceAttachmentTests
{
    [Theory]
    [InlineData(320, 46, 400, 52)]
    [InlineData(320, 40, 400, 40)]
    public void FaceUsesFinalLayoutAndTracksShellProgress(double width, double height, double openWidth, double openHeight)
        => SharedStaTestRunner.RunAsync(async _ =>
    {
        var face = new Border { Margin = new Thickness(14, 6, 14, 6) };
        var shell = new Grid { Width = width, Height = height };
        shell.Children.Add(face);
        var attachment = new CompactSurfaceAttachment(shell, face, () => new Size(300, 40));
        attachment.Attach(openWidth, openHeight);
        var layer = Assert.IsAssignableFrom<Grid>(face.Parent);
        var scale = Assert.IsType<ScaleTransform>(layer.RenderTransform);
        Assert.Equal(openWidth, layer.Width);
        Assert.Equal(openHeight, layer.Height);
        Assert.Equal(new Point(.5, 0), layer.RenderTransformOrigin);
        Assert.Equal(width / openWidth, scale.ScaleX, 6);
        Assert.Equal(height / openHeight, scale.ScaleY, 6);
        double p = openHeight > 40 ? (height - 40) / (openHeight - 40) : (width - 300) / (openWidth - 300);
        Assert.Equal(Math.Clamp((p - .08) * 2.2, 0, 1), layer.Opacity, 6);
        attachment.Detach(true);
        Assert.True(double.IsNaN(layer.Width));
        Assert.Equal(1, scale.ScaleX);
        Assert.Equal(new Thickness(14, 6, 14, 6), face.Margin);
        await Task.CompletedTask;
    });

    [Fact]
    public void ReversalKeepsPositionAndVelocityAndDropsTheOldCompletion() => SharedStaTestRunner.RunAsync(async ct =>
    {
        var shell = new Grid { Width = 300, Height = 40 };
        var face = new Border();
        shell.Children.Add(face);
        var attachment = new CompactSurfaceAttachment(shell, face, () => new Size(300, 40));
        using var host = new RenderHost(shell);
        int opened = 0, closed = 0;
        attachment.MoveTo(400, 64, Motion.Pop, true, () => opened++);
        await Task.Delay(100, ct);
        double width = shell.Width, height = shell.Height;
        double speed = Motion.VelocityOf(shell, FrameworkElement.WidthProperty);
        attachment.MoveTo(300, 40, Motion.PopBack, false, () => closed++);
        Assert.Equal(width, shell.Width, 3);
        Assert.Equal(height, shell.Height, 3);
        Assert.InRange(Math.Abs(Motion.VelocityOf(shell, FrameworkElement.WidthProperty) - speed), 0, 35);
        await WpfFrameWaiter.UntilAsync(() => closed == 1, "attached face returns to rest", ct);
        Assert.Equal(0, opened);
        Assert.Equal(0, ((FrameworkElement)face.Parent).Opacity);
        Assert.Equal(300, shell.Width);
        Assert.Equal(40, shell.Height);
    });

    [Fact]
    public void OpeningTheNotchDuringALeaveEndsItHidden() => SharedStaTestRunner.RunAsync(async ct =>
    {
        var shell = new Grid { Width = 380, Height = 56 };
        var face = new Border();
        shell.Children.Add(face);
        var attachment = new CompactSurfaceAttachment(shell, face, () => new Size(300, 40));
        using var host = new RenderHost(shell);
        int closed = 0;
        attachment.MoveTo(300, 40, Motion.PopBack, false, () => closed++);
        await Task.Delay(60, ct);
        // The opening shell takes the size over, so the leave's own springs never arrive.
        Motion.To(shell, FrameworkElement.WidthProperty, 640, Motion.Shift);
        Motion.To(shell, FrameworkElement.HeightProperty, 220, Motion.Shift);
        await WpfFrameWaiter.UntilAsync(() => closed == 1, "leave ends when the shell grows again", ct);
        var layer = (FrameworkElement)face.Parent;
        Assert.Equal(0, layer.Opacity);
        Assert.True(double.IsNaN(layer.Width));
        await WpfFrameWaiter.UntilAsync(() => shell.Height > 200, "shell opens", ct);
        Assert.Equal(0, layer.Opacity);
        Assert.Equal(1, closed);
    });

    [Fact]
    public void ExpandingAReadableFaceKeepsItsOpacity() => SharedStaTestRunner.RunAsync(async ct =>
    {
        var shell = new Grid { Width = 300, Height = 40 };
        var face = new Border();
        shell.Children.Add(face);
        var attachment = new CompactSurfaceAttachment(shell, face, () => new Size(300, 40));
        using var host = new RenderHost(shell);
        attachment.MoveTo(380, 56, Motion.Pop, true);
        var layer = (FrameworkElement)face.Parent;
        await WpfFrameWaiter.UntilAsync(() => double.IsNaN(layer.Width), "compact face settles", ct);
        attachment.MoveTo(440, 140, Motion.Shift, true);
        Assert.Equal(1, layer.Opacity);
        await Task.Delay(100, ct);
        Assert.Equal(1, layer.Opacity);
        attachment.Detach(true);
    });

    [Fact]
    public void ReducedMotionCompletesInlineExactlyOnce() => SharedStaTestRunner.RunAsync(async _ =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(true);
        try
        {
            var shell = new Grid { Width = 300, Height = 40 };
            var face = new Border();
            shell.Children.Add(face);
            var attachment = new CompactSurfaceAttachment(shell, face, () => new Size(300, 40));
            int count = 0;
            attachment.MoveTo(400, 64, Motion.Pop, true, () => count++);
            attachment.MoveTo(300, 40, Motion.PopBack, false, () => count++);
            Assert.Equal(2, count);
            Assert.Equal(0, ((FrameworkElement)face.Parent).Opacity);
            Assert.False(Motion.IsRunning(shell, FrameworkElement.WidthProperty));
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
        await Task.CompletedTask;
    });

    private sealed class RenderHost : IDisposable
    {
        private readonly Window _window;
        internal RenderHost(FrameworkElement content)
        {
            _window = new BackgroundWindow { Left = -20000, Top = -20000, Width = 700, Height = 240,
                Content = content, ShowActivated = false, ShowInTaskbar = false };
            _window.Show();
            _window.UpdateLayout();
        }
        public void Dispose() => _window.Close();
    }
}
