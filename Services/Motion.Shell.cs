using System.Windows;
using System.Windows.Media;

namespace VNotch.Services;

internal static partial class Motion
{
    // A flying cover's clip follows the same two springs as its border, including reversals.
    private static readonly DependencyProperty ClipWidthProperty = DependencyProperty.RegisterAttached(
        "ClipWidth", typeof(double), typeof(Motion), new PropertyMetadata(0d, UpdateClipSize));
    private static readonly DependencyProperty ClipHeightProperty = DependencyProperty.RegisterAttached(
        "ClipHeight", typeof(double), typeof(Motion), new PropertyMetadata(0d, UpdateClipSize));

    internal static void ClipSizeTo(RectangleGeometry clip, double width, double height,
        SpringMotion widthSpring, SpringMotion heightSpring)
    {
        if (clip.ReadLocalValue(ClipWidthProperty) == DependencyProperty.UnsetValue)
        {
            var rect = clip.Rect;
            clip.SetValue(ClipWidthProperty, rect.Width);
            clip.SetValue(ClipHeightProperty, rect.Height);
        }
        To(clip, ClipWidthProperty, width, widthSpring);
        To(clip, ClipHeightProperty, height, heightSpring);
    }

    internal static void StopClipSize(RectangleGeometry clip, double width, double height)
    {
        Stop(clip, ClipWidthProperty, width);
        Stop(clip, ClipHeightProperty, height);
    }

    private static void UpdateClipSize(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        var clip = (RectangleGeometry)target;
        clip.Rect = new Rect(0, 0, System.Math.Max(0, (double)clip.GetValue(ClipWidthProperty)),
            System.Math.Max(0, (double)clip.GetValue(ClipHeightProperty)));
    }
}
