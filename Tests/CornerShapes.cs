using System.Windows;
using System.Windows.Controls;
using Xunit;

namespace VNotch.Tests;

/// <summary>
/// WPF draws a corner radius above half a side as an ellipse quarter, so a 32×26 button with radius 20 turns into an
/// oval. CSS (where the designs come from) shrinks such a radius to a capsule instead. Every rounded border has to stay
/// within half its shorter side.
/// </summary>
internal static class CornerShapes
{
    internal static void AssertNoneStretched(FrameworkElement root)
    {
        root.UpdateLayout();
        var stretched = HomeSchoolTabTests.Descendants<Border>(root)
            .Where(border => border.IsVisible && border.ActualWidth > 0 && border.ActualHeight > 0)
            .Where(border => Largest(border.CornerRadius) > Math.Min(border.ActualWidth, border.ActualHeight) / 2 + .01)
            .Select(border => $"{Describe(border)} {border.ActualWidth:0.#}×{border.ActualHeight:0.#} radius {Largest(border.CornerRadius):0.#}")
            .ToArray();
        Assert.True(stretched.Length == 0, "Stretched corners: " + string.Join("; ", stretched));
    }

    private static double Largest(CornerRadius radius) =>
        Math.Max(Math.Max(radius.TopLeft, radius.TopRight), Math.Max(radius.BottomLeft, radius.BottomRight));

    private static string Describe(Border border)
    {
        if (border.Name.Length > 0) return border.Name;
        if (System.Windows.Automation.AutomationProperties.GetName(border) is { Length: > 0 } name) return $"\"{name}\"";
        if (border.TemplatedParent is FrameworkElement owner)
            return $"{owner.GetType().Name} \"{System.Windows.Automation.AutomationProperties.GetName(owner)}\"";
        return border.Child?.GetType().Name ?? "Border";
    }
}
