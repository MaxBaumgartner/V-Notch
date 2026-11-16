using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace VNotch.Services;

/// <summary>
/// The one window model: a window grows out of the notch when it opens and squishes back into it when it
/// closes, like Settings does. The shell is scaled about its centre and the window itself travels, so the
/// shell's centre starts and ends on the notch's centre.
/// </summary>
internal static partial class Motion
{
    // Settings closes in 520 ms with a 824 px wide shell; smaller windows close a little quicker.
    private const double SettingsShellWidth = 824;

    private static bool TryNotchCentre(out Point centre, out Size size)
    {
        centre = default;
        size = default;
        var main = Application.Current?.Windows.OfType<MainWindow>().FirstOrDefault();
        if (main == null || !main.IsVisible) return false;
        var rect = main.GetNotchScreenRect();
        if (!(rect.Width > 0) || !(rect.Height > 0)) return false;
        centre = new Point(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2);
        size = new Size(rect.Width, rect.Height);
        return true;
    }

    /// <summary>Where the window must sit so that the shell's centre lies on <paramref name="centre"/>.</summary>
    private static Point WindowPlaceFor(Window window, FrameworkElement shell, Point centre)
    {
        var mid = shell.TransformToAncestor(window).Transform(new Point(shell.ActualWidth / 2, shell.ActualHeight / 2));
        return new Point(centre.X - mid.X, centre.Y - mid.Y);
    }

    /// <summary>
    /// Opens <paramref name="window"/> out of the notch: it starts notch-sized on the notch and springs to its
    /// own place and size. Without a notch to grow from it only rises in place.
    /// </summary>
    public static void GrowFromNotch(Window window, FrameworkElement shell, ScaleTransform scale, TranslateTransform? translate = null)
    {
        if (translate != null)
        {
            Stop(translate, TranslateTransform.XProperty, 0);
            Stop(translate, TranslateTransform.YProperty, 0);
        }
        if (AnimationConfig.ReduceMotion || shell.ActualWidth <= 0 || shell.ActualHeight <= 0 ||
            !TryNotchCentre(out var centre, out var notch))
        {
            To(shell, UIElement.OpacityProperty, 1, Tile, from: 0);
            To(scale, ScaleTransform.ScaleXProperty, 1, TileScale, from: TileFromScale);
            To(scale, ScaleTransform.ScaleYProperty, 1, TileScale, from: TileFromScale);
            return;
        }

        double restLeft = window.Left, restTop = window.Top;
        var start = WindowPlaceFor(window, shell, centre);
        // The notch's own open: width with a touch more spring than height; the window glides along.
        To(window, Window.LeftProperty, restLeft, Glide, from: start.X,
            done: () => Stop(window, Window.LeftProperty, restLeft));
        To(window, Window.TopProperty, restTop, Glide, from: start.Y,
            done: () => Stop(window, Window.TopProperty, restTop));
        To(scale, ScaleTransform.ScaleXProperty, 1, ShellOpenWidth, from: Math.Clamp(notch.Width / shell.ActualWidth, 0.02, 1));
        To(scale, ScaleTransform.ScaleYProperty, 1, ShellOpenHeight, from: Math.Clamp(notch.Height / shell.ActualHeight, 0.02, 1));
        To(shell, UIElement.OpacityProperty, 1, Tile, from: 0);
    }

    /// <summary>
    /// Closes <paramref name="window"/> into the notch with Settings' collapse: it accelerates into the notch,
    /// shrinking to its size, and fades only at the very end. A close that interrupts the opening starts from
    /// wherever the opening got to. <paramref name="closed"/> runs once, also if the animation clock never ticks.
    /// </summary>
    public static void SquishIntoNotch(Window window, FrameworkElement shell, ScaleTransform scale, TranslateTransform? translate,
        Action closed)
    {
        bool done = false;
        DispatcherTimer? fallback = null;
        void Finish()
        {
            if (done) return;
            done = true;
            fallback?.Stop();
            closed();
        }

        if (AnimationConfig.ReduceMotion || shell.ActualWidth <= 0 || shell.ActualHeight <= 0)
        {
            Finish();
            return;
        }

        double ms = 360 + 160 * Math.Clamp(shell.ActualWidth / SettingsShellWidth, 0, 1);
        var span = TimeSpan.FromMilliseconds(ms);
        var easeIn = new ExponentialEase { EasingMode = EasingMode.EaseIn, Exponent = 6 };
        var easeInStrong = new ExponentialEase { EasingMode = EasingMode.EaseIn, Exponent = 7 };
        int fps = AnimationConfig.TargetFps;

        void Run(DependencyObject target, DependencyProperty property, double to, IEasingFunction ease, Action? completed = null)
        {
            double from = (double)target.GetValue(property);
            Stop(target, property, from);
            var animation = new DoubleAnimation(from, to, span) { EasingFunction = ease };
            Timeline.SetDesiredFrameRate(animation, fps);
            if (completed != null) animation.Completed += (_, _) => completed();
            ((IAnimatable)target).BeginAnimation(property, animation);
        }

        bool hasNotch = TryNotchCentre(out var centre, out var notch);
        if (translate != null)
        {
            Run(translate, TranslateTransform.XProperty, 0, easeInStrong);
            Run(translate, TranslateTransform.YProperty, 0, easeInStrong);
        }
        if (hasNotch)
        {
            // Measured with the shell's current scale about its centre, which leaves the centre where it is.
            var end = WindowPlaceFor(window, shell, centre);
            if (translate != null) end = new Point(end.X + translate.X, end.Y + translate.Y);
            Run(window, Window.LeftProperty, end.X, easeInStrong);
            Run(window, Window.TopProperty, end.Y, easeInStrong);
            Run(scale, ScaleTransform.ScaleXProperty, Math.Clamp(notch.Width / shell.ActualWidth, 0.02, 1), easeInStrong, Finish);
            Run(scale, ScaleTransform.ScaleYProperty, Math.Clamp(notch.Height / shell.ActualHeight, 0.02, 1), easeInStrong);
        }
        else
        {
            Run(scale, ScaleTransform.ScaleXProperty, ExitToScale, easeIn, Finish);
            Run(scale, ScaleTransform.ScaleYProperty, ExitToScale, easeIn);
        }
        // The black shell melts into the black notch; the fade only shows in the last stretch.
        Run(shell, UIElement.OpacityProperty, 0, new ExponentialEase { EasingMode = EasingMode.EaseIn, Exponent = 9 });

        // Safety net in case the animation clock does not tick (a headless test run).
        fallback = new DispatcherTimer(DispatcherPriority.Normal, window.Dispatcher) { Interval = span + TimeSpan.FromMilliseconds(150) };
        fallback.Tick += (_, _) => Finish();
        fallback.Start();
    }
}
