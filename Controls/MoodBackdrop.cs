using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using VNotch.Services;

namespace VNotch.Controls;

/// <summary>
/// The system tab's living background: three soft colour fields in the tab's mood, the way the blurred cover
/// sits behind the music tab. They drift slowly while the tab is open and fade to a new colour when the PC's
/// load changes. The soft edges are radial gradients, not a blur effect, so a frame costs next to nothing.
/// It lives behind everything in the notch's content (whose clip gives it the notch's corners) and follows
/// the system tab's opacity, so it fades in and out with the tab.
/// </summary>
public sealed class MoodBackdrop : Grid
{
    // Laid out for the open system tab (720 wide); the notch's clip cuts what does not fit while it opens.
    private const double Width0 = 720, Height0 = 400;
    private const int DriftFps = 24;

    private readonly Canvas _canvas = new() { Width = Width0, Height = Height0, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
    private readonly Field[] _fields;
    private bool _drifting;

    private sealed class Field
    {
        public required Ellipse Shape;
        public required GradientStop Core, Middle, Edge;
        public required TranslateTransform Shift;
        public required ScaleTransform Scale;
        public required double Strength;
        public required double DriftX, DriftY, DriftScale, DriftSeconds;
    }

    public MoodBackdrop()
    {
        IsHitTestVisible = false;
        Focusable = false;
        Children.Add(_canvas);
        _fields =
        [
            // Strength is the opacity at the centre; drift is how far and how slowly each field wanders.
            AddField(-110, -130, 540, 360, 0.50, 70, 30, 1.18, 17),
            AddField(380, 30, 470, 320, 0.32, -80, -20, 0.92, 23),
            AddField(150, 170, 440, 280, 0.30, 40, -40, 1.15, 19)
        ];
    }

    /// <summary>Hides the fields without touching the opacity the tab's transition drives (Liquid Glass has its own look).</summary>
    public bool Suppressed
    {
        get => _canvas.Visibility != Visibility.Visible;
        set => _canvas.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
    }

    private Field AddField(double x, double y, double w, double h, double strength, double dx, double dy, double ds, double seconds)
    {
        var core = new GradientStop(Colors.Transparent, 0);
        var middle = new GradientStop(Colors.Transparent, 0.48);
        var edge = new GradientStop(Colors.Transparent, 1);
        var shift = new TranslateTransform();
        var scale = new ScaleTransform(1, 1);
        var shape = new Ellipse
        {
            Width = w,
            Height = h,
            Fill = new RadialGradientBrush { GradientStops = { core, middle, edge } },
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new TransformGroup { Children = { scale, shift } }
        };
        Canvas.SetLeft(shape, x);
        Canvas.SetTop(shape, y);
        _canvas.Children.Add(shape);
        return new Field
        {
            Shape = shape, Core = core, Middle = middle, Edge = edge, Shift = shift, Scale = scale, Strength = strength,
            DriftX = dx, DriftY = dy, DriftScale = ds, DriftSeconds = seconds
        };
    }

    /// <summary>
    /// Paints the fields: the main colour, a warmer neighbour and a cooler one, so the background is never a
    /// flat wash. <paramref name="ms"/> 0 sets them at once (the first paint).
    /// </summary>
    public void SetMood(Color main, Color warmer, Color cooler, double ms)
    {
        Paint(_fields[0], main, ms);
        Paint(_fields[1], warmer, ms);
        Paint(_fields[2], cooler, ms);
    }

    private static void Paint(Field field, Color color, double ms)
    {
        Fade(field.Core, WithAlpha(color, field.Strength), ms);
        Fade(field.Middle, WithAlpha(color, field.Strength * 0.55), ms);
        Fade(field.Edge, WithAlpha(color, 0), ms);
    }

    private static void Fade(GradientStop stop, Color to, double ms)
    {
        if (ms <= 0 || AnimationConfig.ReduceMotion)
        {
            stop.BeginAnimation(GradientStop.ColorProperty, null);
            stop.Color = to;
            return;
        }
        var fade = new ColorAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = WeatherMotion.Sine };
        Timeline.SetDesiredFrameRate(fade, DriftFps);
        stop.BeginAnimation(GradientStop.ColorProperty, fade, HandoffBehavior.SnapshotAndReplace);
    }

    private static Color WithAlpha(Color color, double alpha) => Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255), color.R, color.G, color.B);

    /// <summary>
    /// Starts or stops the slow wander. Only while the tab is on screen: any running animation repaints the
    /// whole notch, so nothing may keep moving once it is hidden.
    /// </summary>
    public void SetDrifting(bool on)
    {
        if (on && AnimationConfig.ReduceMotion) on = false;
        if (on == _drifting) return;
        _drifting = on;
        foreach (var field in _fields)
        {
            if (!on)
            {
                field.Shift.BeginAnimation(TranslateTransform.XProperty, null);
                field.Shift.BeginAnimation(TranslateTransform.YProperty, null);
                field.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                field.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                continue;
            }
            field.Shift.BeginAnimation(TranslateTransform.XProperty, Wander(0, field.DriftX, field.DriftSeconds));
            field.Shift.BeginAnimation(TranslateTransform.YProperty, Wander(0, field.DriftY, field.DriftSeconds * 1.13));
            var grow = Wander(1, field.DriftScale, field.DriftSeconds * 0.87);
            field.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            field.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        }
    }

    private static DoubleAnimation Wander(double from, double to, double seconds)
    {
        var wander = new DoubleAnimation(from, to, TimeSpan.FromSeconds(seconds))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = WeatherMotion.Sine
        };
        // Slow enough that 24 frames a second look smooth, and the notch repaints far less often.
        Timeline.SetDesiredFrameRate(wander, DriftFps);
        return wander;
    }
}
