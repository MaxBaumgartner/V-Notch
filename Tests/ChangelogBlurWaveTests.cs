using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VNotch.Controls;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class ChangelogBlurWaveTests
{
    [Theory]
    [InlineData(Orientation.Horizontal, true)]
    [InlineData(Orientation.Horizontal, false)]
    [InlineData(Orientation.Vertical, true)]
    [InlineData(Orientation.Vertical, false)]
    public void OutgoingSnapshotDriftsAndCanBeInterrupted(Orientation axis, bool forward) => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool oldMotion = AnimationConfig.ReduceMotion;
        var oldSystem = AnimationConfig.ClientAreaAnimationOverride;
        var host = new Grid { Width = 280, Height = 280 };
        var page = new Border { Background = Brushes.Black };
        var wave = new ChangelogBlurWave();
        host.Children.Add(page);
        host.Children.Add(wave);
        var window = new BackgroundWindow { Content = host, SizeToContent = SizeToContent.WidthAndHeight };
        try
        {
            AnimationConfig.SetReduceMotion(false);
            AnimationConfig.ClientAreaAnimationOverride = true;
            window.Show();
            window.UpdateLayout();
            wave.Capture(host, axis, forward);
            page.Background = Brushes.Blue;
            var offset = Assert.IsType<TranslateTransform>(wave.RenderTransform);
            await WpfFrameWaiter.UntilAsync(() => wave.Progress is > 0 and < 1, "snapshot exits", ct);
            double travel = axis == Orientation.Horizontal ? offset.X : offset.Y;
            Assert.InRange(travel * (forward ? -1 : 1), 0, 6);
            Assert.Equal(0, axis == Orientation.Horizontal ? offset.Y : offset.X);

            AnimationConfig.SetReduceMotion(true);
            Assert.False(wave.IsVisible);
            Assert.False(offset.HasAnimatedProperties);
            Assert.Equal(Matrix.Identity, offset.Value);
        }
        finally
        {
            window.Close();
            AnimationConfig.ClientAreaAnimationOverride = oldSystem;
            AnimationConfig.SetReduceMotion(oldMotion);
        }
    });
}
