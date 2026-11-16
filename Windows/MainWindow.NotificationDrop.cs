using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using VNotch.Controllers;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

public partial class MainWindow
{
    #region Tapping a notification pill: the app drops out of the notch

    // A tap on a notification pill brings its app up. The app's logo leaves the pill as a drop of the notch: it
    // swells out under the notch on a neck that thins and tears like the Dynamic Island splitting, the notch
    // recoils, and the drop falls toward the window that came up, growing and fading as it gets there.
    // It lives on its own layer behind the notch and runs one frame loop for about 0.65 s, then is gone.
    private const double DropSize = 26;
    private const double DropSplitMs = 260;
    // How far the drop swells out below the notch before it tears free.
    private const double DropHang = 40;

    private Canvas? _dropLayer;
    private Border? _drop;
    private Path? _dropNeck;
    private ScaleTransform? _dropScale;
    private TranslateTransform? _dropMove;
    private EventHandler? _dropFrame;
    private int _dropToken;

    private void PlayNotificationDrop(IntPtr foregroundBefore)
    {
        if (!AmbientAnimationsAllowed || _cleanedUp) return;
        if (NotificationAppIcon.Parent is not FrameworkElement icon || !icon.IsVisible || icon.ActualWidth <= 0) return;
        if (NotchBorder.ActualHeight <= 0) return;
        EnsureDropLayer();
        bool arriving = _dropLayer!.Visibility != Visibility.Visible;
        if (_dropFrame != null) CompositionTarget.Rendering -= _dropFrame;
        int token = ++_dropToken;

        Point iconCenter = icon.TranslatePoint(new Point(icon.ActualWidth / 2, icon.ActualHeight / 2), NotchContainer);
        double notchBottom = NotchBorder.TranslatePoint(new Point(0, NotchBorder.ActualHeight), NotchContainer).Y;
        double startX = iconCenter.X;

        ((Image)_drop!.Child).Source = SnapshotElement(icon);
        _drop.BeginAnimation(OpacityProperty, MakeAnim(arriving ? 0 : _drop.Opacity, 1,
            TimeSpan.FromMilliseconds(120), _easeQuadOut));
        _drop.Visibility = Visibility.Visible;
        _dropNeck!.Visibility = Visibility.Visible;
        _dropLayer!.Visibility = Visibility.Visible;

        // The logo left with the drop.
        Motion.To(ClaudeNotificationIconScale, ScaleTransform.ScaleXProperty, Motion.ExitToScale, Motion.Exit);
        Motion.To(ClaudeNotificationIconScale, ScaleTransform.ScaleYProperty, Motion.ExitToScale, Motion.Exit);
        PlayDropRecoil();

        var clock = Stopwatch.StartNew();
        IntPtr notchHwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        Point? target = null;
        double hangFrom = notchBottom - DropSize * 0.5;
        Point split = new(startX, hangFrom + DropHang);
        Motion.To(_dropMove!, TranslateTransform.XProperty, startX - DropSize / 2, Motion.Pop,
            from: arriving ? startX - DropSize / 2 : null);
        Motion.To(_dropMove!, TranslateTransform.YProperty, split.Y - DropSize / 2, Motion.Pop,
            from: arriving ? hangFrom - DropSize / 2 : null);
        Motion.To(_dropScale!, ScaleTransform.ScaleXProperty, 0.88, Motion.Pop,
            from: arriving ? Motion.TileFromScale : null);
        Motion.To(_dropScale!, ScaleTransform.ScaleYProperty, 1.22, Motion.Pop,
            from: arriving ? Motion.TileFromScale : null);
        bool falling = false;
        double lastFrame = -1000;

        _dropFrame = (_, _) =>
        {
            if (!AmbientAnimationsAllowed || _cleanedUp || !_dropLayer.IsVisible)
            {
                StopNotificationDrop();
                return;
            }
            double t = clock.Elapsed.TotalMilliseconds;
            if (t - lastFrame < 1000.0 / Math.Min(AnimationConfig.TargetFps, 60)) return;
            lastFrame = t;
            if (target == null && t < DropSplitMs + 60) target = FindDropTarget(foregroundBefore, notchHwnd);

            if (t < DropSplitMs)
            {
                // The liquid neck follows the rendered spring, including an interrupted launch.
                double x = _dropMove!.X + DropSize / 2;
                double y = _dropMove.Y + DropSize / 2;
                double neck = DropSize * 0.7 * (1 - SmoothStep(0.55, 1.0, t / DropSplitMs));
                DrawNeck(x, notchBottom - 3, y, neck, DropSize * _dropScale!.ScaleX * 0.72);
                return;
            }

            if (falling) return;
            falling = true;
            _dropNeck.Visibility = Visibility.Collapsed;
            Vector toward = target is { } goal ? goal - split : new Vector(0, 1);
            double distance = Math.Clamp(toward.Length, 90, 240);
            if (toward.Length < 0.001) toward = new Vector(0, 1);
            toward.Normalize();
            Point end = split + toward * distance;
            Motion.To(_dropMove!, TranslateTransform.XProperty, end.X - DropSize / 2, Motion.Exit);
            Motion.To(_dropMove!, TranslateTransform.YProperty, end.Y - DropSize / 2, Motion.Exit);
            Motion.To(_dropScale!, ScaleTransform.ScaleXProperty, Motion.ExitToScale, Motion.Exit);
            Motion.To(_dropScale!, ScaleTransform.ScaleYProperty, Motion.ExitToScale, Motion.Exit);
            var fade = MakeAnim(_drop.Opacity, 0, TimeSpan.FromMilliseconds(180), _easeQuadIn);
            fade.Completed += (_, _) =>
            {
                if (token == _dropToken) StopNotificationDrop();
            };
            _drop.BeginAnimation(OpacityProperty, fade);
        };
        CompositionTarget.Rendering += _dropFrame;
    }

    private void StopNotificationDrop()
    {
        ++_dropToken;
        if (_dropFrame != null) CompositionTarget.Rendering -= _dropFrame;
        _dropFrame = null;
        if (_dropLayer != null) _dropLayer.Visibility = Visibility.Collapsed;
        if (_dropMove != null)
        {
            Motion.Stop(_dropMove, TranslateTransform.XProperty);
            Motion.Stop(_dropMove, TranslateTransform.YProperty);
        }
        if (_dropScale != null)
        {
            Motion.Stop(_dropScale, ScaleTransform.ScaleXProperty);
            Motion.Stop(_dropScale, ScaleTransform.ScaleYProperty);
        }
        _drop?.BeginAnimation(OpacityProperty, null);
        if (_drop?.Child is Image image) image.Source = null;
    }

    private void EnsureDropLayer()
    {
        if (_dropLayer != null) return;
        var notchBrush = (Brush)FindResource("NotchGradient");
        _dropScale = new ScaleTransform(1, 1);
        _dropMove = new TranslateTransform();
        var transforms = new TransformGroup();
        transforms.Children.Add(_dropScale);
        transforms.Children.Add(_dropMove);
        _drop = new Border
        {
            Width = DropSize,
            Height = DropSize,
            CornerRadius = new CornerRadius(DropSize / 2),
            Background = notchBrush,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = transforms,
            Child = new Image { Width = 14, Height = 14, Stretch = Stretch.Uniform },
            IsHitTestVisible = false
        };
        _dropNeck = new Path { Fill = notchBrush, IsHitTestVisible = false };
        _dropLayer = new Canvas { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        _dropLayer.IsVisibleChanged += (_, _) =>
        {
            if (!_dropLayer.IsVisible) StopNotificationDrop();
        };
        _dropLayer.Children.Add(_dropNeck);
        _dropLayer.Children.Add(_drop);
        // Behind the notch, so the neck comes out from under it.
        NotchContainer.Children.Insert(NotchContainer.Children.IndexOf(NotchWrapper), _dropLayer);
    }

    /// <summary>Two soft curves from the notch's edge, narrowing to the waist and widening onto the drop.</summary>
    private void DrawNeck(double x, double top, double bottom, double waist, double foot)
    {
        if (waist < 0.8 || bottom <= top)
        {
            _dropNeck!.Visibility = Visibility.Collapsed;
            return;
        }
        double mid = (top + bottom) / 2;
        double rim = Math.Max(waist, foot) * 1.15;
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(x - rim / 2, top), true, true);
            g.QuadraticBezierTo(new Point(x - waist / 2, mid), new Point(x - foot / 2, bottom), true, true);
            g.LineTo(new Point(x + foot / 2, bottom), true, true);
            g.QuadraticBezierTo(new Point(x + waist / 2, mid), new Point(x + rim / 2, top), true, true);
        }
        geometry.Freeze();
        _dropNeck!.Data = geometry;
        _dropNeck.Visibility = Visibility.Visible;
    }

    /// <summary>The notch gives a little as the drop pulls, and springs back when it tears.</summary>
    private void PlayDropRecoil()
    {
        Motion.To(NotchScale, ScaleTransform.ScaleYProperty, 1, Motion.Press,
            velocity: Motion.VelocityOf(NotchScale, ScaleTransform.ScaleYProperty) + 1.3);
    }

    /// <summary>The window that came up, in this layer's coordinates; null while nothing new is in front.</summary>
    private Point? FindDropTarget(IntPtr foregroundBefore, IntPtr notchHwnd)
    {
        IntPtr foreground = OverlayWindowController.GetForegroundWindowHandle();
        if (foreground == IntPtr.Zero || foreground == foregroundBefore || foreground == notchHwnd || IsIconic(foreground)) return null;
        if (!GetWindowRect(foreground, out var rect) || rect.Right <= rect.Left || rect.Bottom <= rect.Top) return null;
        try
        {
            return NotchContainer.PointFromScreen(new Point((rect.Left + rect.Right) / 2.0, (rect.Top + rect.Bottom) / 2.0));
        }
        catch (InvalidOperationException)
        {
            return null; // not connected to a presentation source any more
        }
    }

    private static ImageSource? SnapshotElement(FrameworkElement element)
    {
        var dpi = VisualTreeHelper.GetDpi(element);
        int w = Math.Max(1, (int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX));
        int h = Math.Max(1, (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY));
        var bitmap = new RenderTargetBitmap(w, h, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawRectangle(new VisualBrush(element) { Stretch = Stretch.None }, null,
                new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static double SmoothStep(double from, double to, double x)
    {
        double t = Math.Clamp((x - from) / (to - from), 0, 1);
        return t * t * (3 - 2 * t);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DropRect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out DropRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hwnd);

    #endregion
}
