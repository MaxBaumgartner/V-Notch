using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using VNotch.Controls;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class ControlCenterLegionPageTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void FoldingASectionKeepsThePageAndCanOpenItAgain() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await Task.Yield();
        var view = new ControlCenterView { Page = ControlCenterPage.System };
        view.AttachLegion();
        var type = typeof(ControlCenterView);
        type.GetField("_legionBusy", Private)!.SetValue(view, true); // no live helper in tests
        type.GetField("_legion", Private)!.SetValue(view, new LegionState((int)LegionMode.Quiet, false, false, 60, 50));
        type.GetField("_legionReachable", Private)!.SetValue(view, true);
        type.GetMethod("BuildLegionPage", Private)!.Invoke(view, null);
        object Parts() => type.GetField("_legionParts", Private)!.GetValue(view)!;
        Grid KeysGrid() => (Grid)Parts().GetType().GetField("KeysGrid")!.GetValue(Parts())!;
        var before = Parts();
        var expanded = (Dictionary<string, bool>)type.GetField("_expanded", Private)!.GetValue(view)!;

        // What a tap on the "Keys" header does once its arrow has turned.
        expanded["legion.keys"] = false;
        type.GetMethod("RebuildSection", Private)!.Invoke(view, new object[] { "legion.keys" });
        Assert.Equal(Visibility.Collapsed, KeysGrid().Visibility);
        Assert.Same(before, Parts()); // folded in place: the pill and rings were not rebuilt

        expanded["legion.keys"] = true;
        type.GetMethod("RebuildSection", Private)!.Invoke(view, new object[] { "legion.keys" });
        Assert.Equal(Visibility.Visible, KeysGrid().Visibility);
        Assert.Equal(1.0, KeysGrid().Opacity);
        Assert.Same(before, Parts());
        view.Dispose();
    });
}
