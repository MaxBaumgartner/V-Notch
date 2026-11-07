using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

/// <summary>
/// The notch window takes a click only where something is drawn, as the layered window did with its per-pixel
/// alpha: transparent backgrounds, faded-out elements and non-hit-testable decoration let clicks through.
/// </summary>
public sealed class ClickThroughControllerTests
{
    [Fact]
    public void OnlyDrawnContentTakesTheClick() => SharedStaTestRunner.Run(() =>
    {
        // A transparent 400 × 200 surface (like the notch window) holding a black notch, a faded-out card,
        // a transparent hover zone and a shadow that is not hit-testable.
        var root = new Canvas { Width = 400, Height = 200, Background = Brushes.Transparent };
        root.Children.Add(Place(new Border { Width = 100, Height = 30, Background = Brushes.Black }, 150, 0));
        root.Children.Add(Place(new Border { Width = 80, Height = 40, Background = Brushes.White, Opacity = 0 }, 10, 100));
        root.Children.Add(Place(new Grid { Width = 120, Height = 60, Background = Brushes.Transparent }, 260, 100));
        root.Children.Add(Place(new Border { Width = 100, Height = 20, Background = Brushes.Gray, IsHitTestVisible = false }, 150, 30));
        var label = new TextBlock { Text = "12:30", FontSize = 14 };
        root.Children.Add(Place(label, 160, 140));
        root.Measure(new Size(400, 200));
        root.Arrange(new Rect(0, 0, 400, 200));

        Assert.True(ClickThroughController.IsSolidAt(root, new Point(200, 10)));    // the notch
        Assert.False(ClickThroughController.IsSolidAt(root, new Point(50, 10)));    // empty window area
        Assert.False(ClickThroughController.IsSolidAt(root, new Point(40, 120)));   // faded-out card
        Assert.False(ClickThroughController.IsSolidAt(root, new Point(300, 120)));  // transparent hover zone
        Assert.False(ClickThroughController.IsSolidAt(root, new Point(200, 40)));   // shadow, not hit-testable
        Assert.True(ClickThroughController.IsSolidAt(root, new Point(165, 150)));   // text
    });

    private static UIElement Place(FrameworkElement element, double x, double y)
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        return element;
    }
}
