using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using VNotch.Services;

namespace VNotch.Controls;

// A short snapshot exit leaves the destination page live under it.
public sealed class ChangelogBlurWave : FrameworkElement
{
    private const int MaxSnapshotPixels = 640 * 1024;
    private static readonly Brush Backdrop = FrozenBackdrop();
    private readonly DrawingGroup _drawing = new();
    private readonly TranslateTransform _travel = new();

    internal double Progress => IsVisible ? 1 - Opacity : 0;

    public ChangelogBlurWave()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
        RenderTransform = _travel;
        Visibility = Visibility.Hidden;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.Linear);
        IsVisibleChanged += (_, _) => { if (!IsVisible) Stop(); };
        IsEnabledChanged += (_, _) => { if (!IsEnabled) Stop(); };
    }

    internal void Capture(FrameworkElement viewport, Orientation axis, bool forward)
    {
        var size = RenderSize;
        if (!IsEnabled || !viewport.IsVisible || size.Width < 1 || size.Height < 1 ||
            AnimationConfig.ReduceMotion || !AnimationConfig.ClientAreaAnimation)
        {
            Stop();
            return;
        }

        var bounds = new Rect(size);
        double scale = Math.Min(VisualTreeHelper.GetDpi(viewport).DpiScaleX,
            Math.Sqrt(MaxSnapshotPixels / (size.Width * size.Height)));
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Backdrop, null, bounds);
            var viewbox = new Rect(TranslatePoint(new Point(-_travel.X, -_travel.Y), viewport) + VisualTreeHelper.GetOffset(viewport), size);
            dc.DrawRectangle(new VisualBrush(viewport)
            {
                ViewboxUnits = BrushMappingMode.Absolute, Viewbox = viewbox, Stretch = Stretch.Fill,
                AutoLayoutContent = false
            }, null, bounds);
        }
        var sharp = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(size.Width * scale)),
            Math.Max(1, (int)Math.Ceiling(size.Height * scale)), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        sharp.Render(visual);
        sharp.Freeze();
        Stop();
        using (var dc = _drawing.Open()) dc.DrawImage(sharp, bounds);

        var axisProperty = axis == Orientation.Vertical ? TranslateTransform.YProperty : TranslateTransform.XProperty;
        Effect = new BlurEffect { Radius = 2, RenderingBias = RenderingBias.Performance };
        Opacity = 1;
        Visibility = Visibility.Visible;
        AnimationConfig.ReduceMotionChanged += OnReduceMotionChanged;
        Motion.To(_travel, axisProperty, forward ? -6 : 6, Motion.PressDown);
        Motion.To(this, OpacityProperty, 0, Motion.PressDown, done: Stop);
    }

    internal void Stop()
    {
        if (Visibility != Visibility.Visible) return;
        AnimationConfig.ReduceMotionChanged -= OnReduceMotionChanged;
        Motion.Stop(_travel, TranslateTransform.XProperty, 0);
        Motion.Stop(_travel, TranslateTransform.YProperty, 0);
        Motion.Stop(this, OpacityProperty, 1);
        _drawing.Children.Clear();
        Effect = null;
        Visibility = Visibility.Hidden;
    }

    private void OnReduceMotionChanged() { if (AnimationConfig.ReduceMotion) Stop(); }
    protected override void OnRender(DrawingContext dc) => dc.DrawDrawing(_drawing);
    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        Stop();
    }

    private static Brush FrozenBackdrop()
    {
        var brush = new SolidColorBrush(Color.FromRgb(9, 9, 10));
        brush.Freeze();
        return brush;
    }
}
