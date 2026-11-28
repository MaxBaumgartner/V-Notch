using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VNotch.Controls;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class ControlCenterDetailMotionTests
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;

    [Fact]
    public void TextDipUsesTheLatestRequestAndReversesWithoutJumping() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var scene = new Scene();
        Change(scene.Text, "Connected");
        await WpfFrameWaiter.UntilAsync(() => scene.Text.Opacity < .7, "text starts leaving", ct);
        double opacity = scene.Text.Opacity;
        Change(scene.Text, "Working");
        Assert.Equal(opacity, scene.Text.Opacity, 6);
        await WpfFrameWaiter.UntilAsync(() => scene.Text.Text == "Working" &&
            !Motion.IsRunning(scene.Text, UIElement.OpacityProperty), "newest text settles", ct);
        Assert.Equal(1, scene.Text.Opacity);
        Change(scene.Text, "Idle");
        await WpfFrameWaiter.UntilAsync(() => scene.Text.Opacity < .8, "second dip starts", ct);
        opacity = scene.Text.Opacity;
        Change(scene.Text, "Working");
        Assert.Equal(opacity, scene.Text.Opacity, 6);
        await WpfFrameWaiter.UntilAsync(() => !Motion.IsRunning(scene.Text, UIElement.OpacityProperty), "dip reverses", ct);
        Assert.Equal("Working", scene.Text.Text);
        Assert.Equal(1, scene.Text.Opacity);
    });

    [Fact]
    public void HidingCommitsPendingTextAndStopsGlyphAndTextClocks() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var scene = new Scene();
        Change(scene.Text, "Connected");
        Invoke("DetailReaction", scene.Glyph);
        await WpfFrameWaiter.UntilAsync(() => scene.Text.Opacity < .8, "detail motion starts", ct);
        scene.Root.Visibility = Visibility.Collapsed;
        Assert.Equal("Connected", scene.Text.Text);
        Assert.Equal(1, scene.Text.Opacity);
        Assert.False(scene.Text.HasAnimatedProperties);
        Assert.All(Transforms(scene.Glyph).Concat(Transforms(scene.Text)), part => Assert.False(part.HasAnimatedProperties));
    });

    [Fact]
    public void GlyphHoverAndReactionMoveSmallDistancesAndSettleExactly() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var scene = new Scene();
        Invoke("DetailHover", scene.Button, scene.Glyph, 2d, -2d);
        scene.Button.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = UIElement.MouseEnterEvent });
        var hover = Transforms(scene.Glyph).OfType<TranslateTransform>().Single();
        await WpfFrameWaiter.UntilAsync(() => hover.X > .3, "glyph follows pointer", ct);
        double x = hover.X;
        scene.Button.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = UIElement.MouseLeaveEvent });
        Assert.Equal(x, hover.X, 6);
        await WpfFrameWaiter.UntilAsync(() => !Motion.IsRunning(hover, TranslateTransform.XProperty), "glyph returns", ct);
        Assert.Equal(0, hover.X);
        Invoke("DetailReaction", scene.Glyph);
        var reaction = Transforms(scene.Glyph).OfType<TranslateTransform>().Last();
        await TestWait.UntilAsync(() => reaction.Y < -.1, "state hop moves", ct);
        Assert.InRange(reaction.Y, -4, 0);
        double y = reaction.Y;
        Invoke("DetailReaction", scene.Glyph);
        Assert.Equal(y, reaction.Y, 6);
        await WpfFrameWaiter.UntilAsync(() => !Motion.IsRunning(reaction, TranslateTransform.YProperty), "state hop settles", ct);
        Assert.Equal(0, reaction.Y);
    });

    [Fact]
    public void ReducedMotionSetsTextAndLeavesNoDetailClocks() => SharedStaTestRunner.Run(() =>
    {
        using var scene = new Scene();
        AnimationConfig.SetReduceMotion(true);
        Change(scene.Text, "Working");
        Invoke("DetailReaction", scene.Glyph);
        Invoke("DetailArrive", scene.Text, 1);
        Assert.Equal("Working", scene.Text.Text);
        Assert.Equal(1, scene.Text.Opacity);
        Assert.False(scene.Text.HasAnimatedProperties);
        Assert.All(Transforms(scene.Glyph).Concat(Transforms(scene.Text)), part => Assert.False(part.HasAnimatedProperties));
    });

    internal static void Change(TextBlock text, string value) => Invoke("SetDetailText", text, value);
    internal static void Invoke(string name, params object[] args)
    {
        // Optional parameters keep their defaults, so the helpers can grow without breaking these calls.
        var method = typeof(ControlCenterView).GetMethod(name, Static)!;
        var all = method.GetParameters().Select((p, i) => i < args.Length ? args[i] : p.DefaultValue).ToArray();
        method.Invoke(null, all);
    }
    private static IEnumerable<Transform> Transforms(FrameworkElement element) => element.RenderTransform is TransformGroup group
        ? group.Children : [element.RenderTransform];

    internal sealed class Scene : IDisposable
    {
        private readonly bool _reduced = AnimationConfig.ReduceMotion;
        private readonly bool? _override = AnimationConfig.ClientAreaAnimationOverride;
        public readonly TextBlock Text = new() { Text = "Idle", Foreground = Brushes.White, FontSize = 18, Margin = new Thickness(12) };
        public readonly TextBlock Glyph = new() { Text = "↗", Foreground = Brushes.LightSlateGray, FontSize = 20, Margin = new Thickness(10) };
        public readonly StackPanel Root = new() { Background = new SolidColorBrush(Color.FromRgb(22, 23, 25)) };
        public readonly Border Button;
        public readonly BackgroundWindow Window;
        private readonly DetailMotionFrames _frames;

        public Scene()
        {
            AnimationConfig.ClientAreaAnimationOverride = true;
            AnimationConfig.SetReduceMotion(false);
            Button = new Border { Child = Glyph, Background = new SolidColorBrush(Color.FromRgb(38, 40, 44)), Width = 60, Height = 50, Margin = new Thickness(12) };
            Root.Children.Add(Text);
            Root.Children.Add(Button);
            Window = new BackgroundWindow { Left = -20000, Top = -20000, Width = 240, Height = 150, Content = Root };
            Window.Show();
            Window.UpdateLayout();
            _frames = new DetailMotionFrames(Root);
        }

        public void Dispose()
        {
            _frames.Dispose();
            Window.Close();
            AnimationConfig.SetReduceMotion(_reduced);
            AnimationConfig.ClientAreaAnimationOverride = _override;
        }
    }
}

// Transparent test windows can be culled by WPF; rendering their content keeps their clocks observable.
internal sealed class DetailMotionFrames : IDisposable
{
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };

    internal DetailMotionFrames(FrameworkElement content)
    {
        _timer.Tick += (_, _) =>
        {
            content.InvalidateVisual();
            content.UpdateLayout();
            var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(content.ActualWidth)),
                Math.Max(1, (int)Math.Ceiling(content.ActualHeight)), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(content);
        };
        _timer.Start();
    }

    public void Dispose() => _timer.Stop();
}
