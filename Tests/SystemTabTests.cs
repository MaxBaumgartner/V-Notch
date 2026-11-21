using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VNotch.Controls;
using VNotch.Services;
using VNotch.Services.ControlCenter;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class SystemTabTests
{
    [Fact]
    public void LoadWarningRequiresSustainedLoadAndClearsWithHysteresis()
    {
        var state = new ControlCenterView.LoadWarningState();
        var now = DateTime.UtcNow;
        Assert.Equal("", state.Update(now, 20, 97, 60, 70));
        Assert.Equal("", state.Update(now.AddSeconds(9), 20, 97, 60, 70));
        Assert.StartsWith("GPU 97", state.Update(now.AddSeconds(10), 20, 97, 60, 70));
        Assert.NotEmpty(state.Update(now.AddSeconds(11), 20, 79, 60, 79));
        Assert.NotEmpty(state.Update(now.AddSeconds(20), 20, 79, 60, 79));
        Assert.Equal("", state.Update(now.AddSeconds(21), 20, 79, 60, 79));
        Assert.StartsWith("CPU", state.Update(now.AddSeconds(22), 20, 40, 86, 60));
        state.Pause();
        Assert.NotEmpty(state.Update(now.AddHours(1), 20, 20, 60, 60));
        Assert.Equal("", state.Update(now.AddHours(1).AddSeconds(10), 20, 20, 60, 60));
    }

    [Fact]
    public void AHiddenIntervalDoesNotQualifyAsSustainedLoad()
    {
        var state = new ControlCenterView.LoadWarningState(); var now = DateTime.UtcNow;
        Assert.Equal("", state.Update(now, 95, 20, 60, 60));
        state.Pause();
        Assert.Equal("", state.Update(now.AddHours(1), 95, 20, 60, 60));
    }

    [Fact]
    public void WaveMuteSelectsTheHardwareInputAndRejectsUnknownOrMalformedDevices()
    {
        var result = JsonNode.Parse("""
        {"inputDevices":[
          {"id":"other","name":"Default microphone","inputs":[{"id":"wrong","isMuted":false}]},
          {"id":"xlr","name":"Elgato Wave XLR MK.2","inputs":[{"id":"mic","isMuted":true}]}]}
        """);
        Assert.Equal(new WaveXlrState("xlr", "mic", true), WaveXlrControl.Parse(result));
        Assert.Null(WaveXlrControl.Parse(JsonNode.Parse("[]")));
        Assert.Null(WaveXlrControl.Parse(JsonNode.Parse("{\"inputDevices\":[{\"name\":\"Wave XLR\",\"id\":\"bad\",\"inputs\":[{}]}]}")));
    }

    [Fact]
    public void WaveLinkRequestsLeaveOutEmptyParamsBecauseWaveLinkIgnoresNull()
    {
        Assert.Equal("""{"jsonrpc":"2.0","id":1,"method":"getInputDevices"}""", WaveXlrControl.BuildRequest(1, "getInputDevices", null).ToJsonString());
        Assert.Contains("\"params\":{\"id\":\"x\"}", WaveXlrControl.BuildRequest(1, "setInputDevice", new JsonObject { ["id"] = "x" }).ToJsonString());
    }

    [Theory]
    [InlineData("Wave Link Stream (Elgato Virtual Audio)", "Stream", "Wave")]
    [InlineData("Headphones (AirPods Pro)", "AirPods Pro", "AirPods")]
    [InlineData("Speakers", "Speakers", "Speakers")]
    public void OutputChipKeepsTheNameShort(string friendly, string name, string expected) =>
        Assert.Equal(expected, ControlCenterView.ShortOutputName(new("id", name, friendly, true, false)));

    [Theory]
    [InlineData("Kopfhörer (MOONDROP Rays)", 0xE7F6)]
    [InlineData("Headphones (AirPods Pro)", 0xE7F6)]
    [InlineData("Wave Link Stream", 0xE7F5)]
    public void OutputIconShowsTheDeviceTypeEvenWithoutABluetoothMatch(string friendly, int glyph) =>
        Assert.Equal(glyph, ControlCenterView.OutputGlyph(new("id", "", friendly, true, false)));

    [Fact]
    public void SystemFaceShowsTheOutputAndPicksAnotherFromItsMenu() => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion; bool? client = AnimationConfig.ClientAreaAnimationOverride;
        BackgroundTestWindows.EnsureApplicationResources();
        AnimationConfig.ClientAreaAnimationOverride = true; AnimationConfig.SetReduceMotion(false);
        using var view = CreateView();
        var scene = new Border { Width = 568, Background = Brushes.Black, Child = view };
        var window = new Window { Content = scene, Width = 600, Height = 420, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show(); view.UpdateLayout();
            var label = (TextBlock)HomeSchoolTabTests.Field(view, "_outputLabel");
            Assert.Equal("Wave", label.Text);
            Assert.InRange(view.MeasureContentHeight(568), 140, 200);
            // The drawer still lists every output.
            var sound = (StackPanel)HomeSchoolTabTests.Field(view, "_systemSound");
            var outputs = HomeSchoolTabTests.Descendants<Border>(sound).Where(b => b.ToolTip is string title &&
                (title == "Wave Link Stream" || title == "Headphones (AirPods Pro)")).ToArray();
            Assert.Equal(2, outputs.Length);
            // The label opens a menu with the current output ticked, instead of silently cycling.
            HomeSchoolTabTests.Invoke(view, "ToggleOutputMenu");
            var menu = (Border)HomeSchoolTabTests.Field(view, "_outputMenu");
            Assert.Equal(Visibility.Visible, menu.Visibility);
            var rows = ((StackPanel)menu.Child).Children.Cast<Border>().ToArray();
            Assert.Equal(2, rows.Length);
            var ticks = rows.Select(r => HomeSchoolTabTests.Descendants<TextBlock>(r).First(t => t.FontFamily.Source.Contains("Fluent", StringComparison.Ordinal)).Opacity).ToArray();
            Assert.Equal(1, ticks.Count(t => t == 1));
            HomeSchoolTabTests.Invoke(view, "CloseOutputMenu");
            await WpfFrameWaiter.UntilAsync(() => menu.Visibility == Visibility.Collapsed, "menu closes", ct);
        }
        finally
        {
            HomeSchoolTabTests.SetField(view, "_gpuSampling", false);
            window.Close(); AnimationConfig.ClientAreaAnimationOverride = client; AnimationConfig.SetReduceMotion(previous);
        }
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SystemFaceFitsTheRealNotchWidthAndTheThumbLandsOnEachMode(bool legion) => SharedStaTestRunner.RunAsync(async ct =>
    {
        BackgroundTestWindows.EnsureApplicationResources();
        using var view = CreateView(legion);
        var host = new BackgroundWindow { Width = 600, Height = 260, Left = -20000, Top = -20000,
            Content = new Border { Padding = new Thickness(16, 30, 16, 0), Child = view } };
        try
        {
            host.Show(); host.UpdateLayout();
            Rect Bounds(FrameworkElement element) => element.TransformToAncestor(view).TransformBounds(new Rect(element.RenderSize));
            var page = (StackPanel)HomeSchoolTabTests.Field(view, "_compactPage");
            var face = Assert.IsType<Grid>(page.Children[0]);
            var columns = face.Children.Cast<FrameworkElement>().Where(c => c.Visibility == Visibility.Visible).OrderBy(c => Bounds(c).Left).ToArray();
            Assert.All(columns, c => Assert.InRange(Bounds(c).Right, 0, 568.5));
            for (int i = 1; i < columns.Length; i++)
                Assert.True(Bounds(columns[i]).Left - Bounds(columns[i - 1]).Right >= -0.5, $"column {i} overlaps");
            var tile = HomeSchoolTabTests.Field(view, "_modeTile") as Border;
            if (!legion) { Assert.Null(tile); return; }
            Assert.NotNull(tile);
            var shift = (TranslateTransform)HomeSchoolTabTests.Field(view, "_modeThumbShift");
            var modes = new[] { LegionMode.Quiet, LegionMode.Balanced, LegionMode.Performance, LegionMode.Extreme };
            for (int i = 0; i < 4; i++)
            {
                HomeSchoolTabTests.SetField(view, "_legion", new LegionState((int)modes[i], false, false, 61, 44));
                HomeSchoolTabTests.Invoke(view, "ShowFaceMode", false);
                await WpfFrameWaiter.UntilAsync(() => !Motion.IsRunning(shift, TranslateTransform.XProperty), "thumb settles", ct);
                Assert.Equal(i * 50, shift.X, 1);
            }
        }
        finally { HomeSchoolTabTests.SetField(view, "_gpuSampling", false); host.Close(); }
    });

    [Fact]
    public void WifiAndBluetoothOpenInPlaceAndCloseWithTheNotch() => SharedStaTestRunner.RunAsync(async ct =>
    {
        BackgroundTestWindows.EnsureApplicationResources();
        using var view = CreateView();
        var host = new BackgroundWindow { Width = 600, Height = 320, Left = -20000, Top = -20000, Content = new Border { Padding = new Thickness(16, 30, 16, 0), Child = view } };
        try
        {
            host.Show(); host.UpdateLayout();
            var snapshot = HomeSchoolTabTests.Field(view, "_snapshot")!;
            snapshot.GetType().GetField("Networks")!.SetValue(snapshot, new List<WifiNetwork>
            {
                new("Home", "Home", 90, true, true), new("Cafe", null, 40, false, false), new("Neighbour", null, 70, true, false)
            });
            var pages = typeof(ControlCenterView).GetNestedType("QuickPage", BindingFlags.NonPublic)!;
            var face = (Grid)HomeSchoolTabTests.Field(view, "_faceGrid")!;
            HomeSchoolTabTests.Invoke(view, "OpenQuickPage", Enum.Parse(pages, "Wifi"));
            var panel = (StackPanel)HomeSchoolTabTests.Field(view, "_quickPanel")!;
            Assert.Equal(Visibility.Collapsed, face.Visibility);
            Assert.Equal(Visibility.Visible, panel.Visibility);
            // Header, three networks (connected first), the link to Windows' list.
            Assert.Equal(5, panel.Children.Count);
            Assert.Contains(HomeSchoolTabTests.Descendants<TextBlock>((FrameworkElement)panel.Children[1]), t => t.Text == "Home");

            HomeSchoolTabTests.Invoke(view, "CloseQuickPage", true);
            Assert.Equal(Visibility.Visible, face.Visibility);
            Assert.Equal(Visibility.Collapsed, panel.Visibility);

            HomeSchoolTabTests.Invoke(view, "OpenQuickPage", Enum.Parse(pages, "Bluetooth"));
            Assert.Contains(HomeSchoolTabTests.Descendants<TextBlock>(panel), t => t.Text == "AirPods Pro");
            // Closing the notch hides the tab: it opens on the face again.
            view.Visibility = Visibility.Collapsed;
            Assert.Equal(Visibility.Visible, face.Visibility);
            Assert.Equal(Visibility.Collapsed, panel.Visibility);
            await Task.CompletedTask;
        }
        finally { HomeSchoolTabTests.SetField(view, "_gpuSampling", false); host.Close(); }
    });

    internal static ControlCenterView CreateView(bool legion = true)
    {
        var view = new ControlCenterView { Page = ControlCenterPage.System };
        HomeSchoolTabTests.SetField(view, "_refreshing", true);
        HomeSchoolTabTests.SetField(view, "_gpuSampling", true);
        HomeSchoolTabTests.SetField(view, "_readingCompactDisplays", true);
        if (legion) view.AttachLegion();
        HomeSchoolTabTests.SetField(view, "_legionBusy", true);
        HomeSchoolTabTests.SetField(view, "_legion", new LegionState((int)LegionMode.Balanced, false, false, 61, 44));
        var type = typeof(ControlCenterView).GetNestedType("Snapshot", BindingFlags.NonPublic)!;
        var snapshot = Activator.CreateInstance(type)!;
        void Set(string name, object value) => type.GetField(name)!.SetValue(snapshot, value);
        Set("WifiOn", true); Set("BluetoothOn", true); Set("WaveXlr", new WaveXlrState("device", "mic", false));
        Set("Stats", new SystemStats(34, 56, 18, 32, 12, 9, 1024, 128));
        Set("Outputs", new List<OutputDevice> { new("wave", "Wave", "Wave Link Stream", true, false), new("air", "AirPods", "Headphones (AirPods Pro)", false, true) });
        Set("Apps", new List<AudioSessionInfo> { new() { ProcessId = 1, DisplayName = "Spotify", Volume = .8f }, new() { ProcessId = 2, DisplayName = "Discord", Volume = .45f } });
        Set("Devices", new List<BluetoothDevice> { new() { Name = "AirPods Pro", Address = "air", Battery = 80, Connected = true } });
        HomeSchoolTabTests.SetField(view, "_snapshot", snapshot);
        HomeSchoolTabTests.Invoke(view, "BuildCompactSystem", snapshot);
        HomeSchoolTabTests.Invoke(view, "ShowCompactVolume", .64f, false);
        return view;
    }
}
