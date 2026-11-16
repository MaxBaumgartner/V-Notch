using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using VNotch.Controls;
using VNotch.Services.Displays;
using Xunit;

namespace VNotch.Tests;

public sealed class DisplaysSectionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Type ViewType = typeof(ControlCenterView);
    private static void Build(ControlCenterView view) => ViewType.GetMethod("BuildDisplaysSection", Private)!.Invoke(view, null);
    private static StackPanel Section(ControlCenterView view) => (StackPanel)ViewType.GetField("_displaysSection", Private)!.GetValue(view)!;
    private static Dictionary<string, bool> Expanded(ControlCenterView view) => (Dictionary<string, bool>)ViewType.GetField("_expanded", Private)!.GetValue(view)!;
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static T Named<T>(ControlCenterView view, string name) where T : FrameworkElement =>
        Descendants(Section(view)).OfType<T>().Single(x => x.Name == name);
    private static void Tap(UIElement element) => element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
    private static ControlCenterView Create(FakeApi api, bool expanded = true)
    {
        var view = new ControlCenterView { Page = ControlCenterPage.System, DisplayApi = api };
        Expanded(view)["displays"] = expanded;
        Build(view);
        return view;
    }

    [Fact]
    public void CollapsedByDefaultWithSummary() => SharedStaTestRunner.Run(() =>
    {
        using var view = new ControlCenterView { Page = ControlCenterPage.System, DisplayApi = new FakeApi() };
        Assert.False(Expanded(view)["displays"]);
        Build(view);
        Assert.Equal("3 · 2560×1440 · 360 Hz", Named<TextBlock>(view, "DisplaysSummary").Text);
    });

    [Fact]
    public void SelectingAMonitorShowsItsControls() => SharedStaTestRunner.Run(() =>
    {
        using var view = Create(new FakeApi());
        var second = Named<Canvas>(view, "DisplaysLayout").Children.OfType<Rectangle>().Single(x => (string)x.Tag == "two");
        Tap(second);
        var tags = Descendants(Section(view)).OfType<Border>().Select(x => x.Tag).OfType<string>().ToArray();
        Assert.Contains("144 Hz", tags);
        Assert.DoesNotContain("360 Hz", tags);
    });

    [Fact]
    public void DragSnapsEdgeToEdge()
    {
        var other = new Int32Rect(0, 0, 100, 100);
        Assert.Equal(new Int32Point(-100, 0), ControlCenterView.SnapToNeighbours(new Int32Rect(-120, 0, 100, 100), [other]));
        Assert.Equal(new Int32Point(0, -100), ControlCenterView.SnapToNeighbours(new Int32Rect(0, -120, 100, 100), [other]));
        var p = ControlCenterView.SnapToNeighbours(new Int32Rect(80, 0, 100, 100), [other]);
        Assert.Equal(new Int32Point(100, 0), p);
    }

    [Fact]
    public void HzChipAppliesAndShowsKeepBar() => SharedStaTestRunner.Run(() =>
    {
        var api = new FakeApi();
        using var view = Create(api);
        Tap(Descendants(Section(view)).OfType<Border>().Single(x => Equals(x.Tag, "144 Hz")));
        Assert.Equal(144, api.Displays[0].Mode.RefreshHz);
        Assert.Equal(Visibility.Visible, Named<StackPanel>(view, "DisplaysKeepBar").Visibility);
        Assert.Contains(Descendants(Named<StackPanel>(view, "DisplaysKeepBar")).OfType<TextBlock>(), x => x.Text.Contains("15"));
    });

    [Fact]
    public void KeepBarRevertsWhenIgnored() => SharedStaTestRunner.Run(() =>
    {
        var api = new FakeApi();
        var now = DateTime.UnixEpoch;
        using var view = new ControlCenterView { Page = ControlCenterPage.System, DisplayApi = api, DisplayNow = () => now };
        Expanded(view)["displays"] = true;
        Build(view);
        var window = new Window { Content = view, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        try
        {
            window.Show();
            Tap(Descendants(Section(view)).OfType<Border>().Single(x => Equals(x.Tag, "144 Hz")));
            for (int i = 0; i < 15; i++) { now = now.AddSeconds(1); view.TickDisplayChanges(); }
            Assert.Equal(360, api.Displays[0].Mode.RefreshHz);
            Assert.DoesNotContain(Descendants(Section(view)).OfType<FrameworkElement>(), x => x.Name == "DisplaysKeepBar" && x.Visibility == Visibility.Visible);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void NoBrightnessBarWithoutDdc() => SharedStaTestRunner.RunAsync(async ct =>
    {
        var api = new FakeApi();
        using var view = Create(api);
        await WpfFrameWaiter.UntilAsync(() => api.ReadCompleted, "brightness read completed", ct);
        Assert.Equal(Visibility.Collapsed, Named<StackPanel>(view, "DisplaysBrightness").Visibility);
    });

    [Fact]
    public void DisplayChangeRebuildsSection() => SharedStaTestRunner.Run(() =>
    {
        var api = new FakeApi();
        using var view = Create(api);
        api.Displays.RemoveAt(2);
        // Refresh is gated by visibility, as it is for a closed tab.
        var window = new Window { Content = view, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        try { window.Show(); view.RefreshDisplaysOnDisplayChange(); Assert.Equal(2, Named<Canvas>(view, "DisplaysLayout").Children.Count); }
        finally { window.Close(); }
    });

    private sealed class FakeApi : IDisplayApi
    {
        private static readonly DisplayMode Mode = new(2560, 1440, 360);
        public volatile bool ReadCompleted;
        public List<DisplayInfo> Displays { get; } = [Screen("one", 0, true), Screen("two", 2560, false), Screen("three", 5120, false)];
        private static DisplayInfo Screen(string id, int x, bool primary) => new(id, id, id, true, primary,
            new Int32Rect(x, 0, 2560, 1440), primary ? Mode : Mode with { RefreshHz = 60 },
            primary ? [Mode, Mode with { RefreshHz = 144 }] : [Mode with { RefreshHz = 60 }, Mode with { RefreshHz = 144 }],
            false, false, 100, [100, 125], false);
        public IReadOnlyList<DisplayInfo> List() => Displays.ToArray();
        public bool SetModes(IReadOnlyDictionary<string, DisplayMode> modes) { foreach (var (id, mode) in modes) Update(id, d => d with { Mode = mode }); return true; }
        public bool SetPositions(IReadOnlyDictionary<string, Int32Point> positions) { foreach (var (id, p) in positions) Update(id, d => d with { Bounds = new Int32Rect(p.X, p.Y, d.Bounds.Width, d.Bounds.Height) }); return true; }
        public bool SetPrimary(string id) { foreach (var d in Displays.ToArray()) Update(d.Id, x => x with { Primary = x.Id == id }); return true; }
        public bool SetActive(string id, bool on) { Update(id, d => d with { Active = on }); return true; }
        public bool SetHdr(string id, bool on) { Update(id, d => d with { HdrOn = on }); return true; }
        public bool SetZoom(string id, int percent) { Update(id, d => d with { ZoomPercent = percent }); return true; }
        public int? GetBrightness(string id) { ReadCompleted = true; return null; }
        public bool SetBrightness(string id, int percent) => true;
        private void Update(string id, Func<DisplayInfo, DisplayInfo> update) { int index = Displays.FindIndex(d => d.Id == id); Displays[index] = update(Displays[index]); }
    }
}
