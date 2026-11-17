using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VNotch.Controls;
using VNotch.Services;
using VNotch.Services.ControlCenter;
using Xunit;

namespace VNotch.Tests;

/// <summary>The notch never switches the laptop's performance mode on one tap: the first only asks.</summary>
[Collection("Localization")]
public sealed class LaptopModeGuardTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    // Only the first tap is pressed here: a second one would really switch this PC through the OMEN helper.
    [Fact]
    public void AFirstTapOnAnotherModeOnlyAsks() => SharedStaTestRunner.RunAsync(ct =>
    {
        var view = new ControlCenterView { Page = ControlCenterPage.System };
        var type = typeof(ControlCenterView);
        ((Dictionary<string, bool>)type.GetField("_expanded", Private)!.GetValue(view)!)["laptop"] = true;
        var status = new LaptopStatus("balanced", false, 3100, 2900, 52, null, 0, null);
        type.GetMethod("BuildLaptopSectionIfChanged", Private)!.Invoke(view, [status]);
        var section = (StackPanel)type.GetField("_laptopSection", Private)!.GetValue(view)!;

        Press(Button(section, Loc.Get("control.laptop.performance")));

        Assert.Equal("performance", type.GetField("_laptopModeArmed", Private)!.GetValue(view));
        Assert.NotNull(Button(section, Loc.Get("home.sure")));
        // The mode the laptop is in does nothing at all.
        Press(Button(section, Loc.Get("control.laptop.balanced")));
        Assert.Equal("performance", type.GetField("_laptopModeArmed", Private)!.GetValue(view));
        return Task.CompletedTask;
    });

    private static Border Button(DependencyObject root, string text)
    {
        foreach (var border in Descendants(root).OfType<Border>())
            if (border.Child is TextBlock label && label.Text == text) return border;
        throw new Xunit.Sdk.XunitException($"no button '{text}'");
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject node) continue;
            yield return node;
            foreach (var deeper in Descendants(node)) yield return deeper;
        }
    }

    private static void Press(UIElement button) =>
        button.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
}
