using System.Collections;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VNotch.Controls;
using VNotch.Services;
using VNotch.Services.ControlCenter;
using VNotch.Services.Displays;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class SystemDetailMotionTests
{
    [Fact]
    public void OutputAndAppLevelsMoveRetargetAndSettleWithoutAJump() => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool before = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(false);
        using var rig = new Rig();
        try
        {
            await rig.Settle(ct);
            rig.Output(.88f);
            rig.App(.88f);
            await WpfFrameWaiter.UntilAsync(() => rig.OutputFill.ScaleX > .35,
                "output starts moving", ct);
            double output = rig.OutputFill.ScaleX, app = rig.AppFill.ScaleX;
            rig.Output(.18f);
            rig.App(.18f);
            Assert.Equal(output, rig.OutputFill.ScaleX, 7);
            Assert.Equal(app, rig.AppFill.ScaleX, 7);
            await rig.Settle(ct);
            Assert.Equal(.18, rig.OutputFill.ScaleX, 6);
            Assert.Equal(.18, rig.AppFill.ScaleX, 6);
            Assert.Equal("18 %", rig.OutputValue.Text);
            Assert.Equal("18 %", rig.AppValue.Text);
        }
        finally { AnimationConfig.SetReduceMotion(before); }
    });

    [Fact]
    public void MuteReactsAndHidingClearsTheLevelAndGlyphClocks() => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool before = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(false);
        using var rig = new Rig();
        try
        {
            await rig.Settle(ct);
            rig.Output(.6f, true);
            rig.App(.6f, true);
            await WpfFrameWaiter.UntilAsync(() => rig.OutputValue.Text == "🔇" &&
                !Motion.IsRunning(rig.OutputValue, UIElement.OpacityProperty),
                "mute glyph reacts", ct);
            Assert.Equal("🔇", rig.OutputValue.Text);
            rig.Surface.Visibility = Visibility.Collapsed;
            Assert.False(rig.OutputFill.HasAnimatedProperties);
            Assert.False(rig.AppFill.HasAnimatedProperties);
            Assert.Equal(0, rig.OutputFill.ScaleX);
            Assert.Equal(0, rig.AppFill.ScaleX);
            Assert.Equal(0, rig.OutputValue.RenderTransform.Value.OffsetY);
            Assert.All(Transforms(rig.OutputValue.RenderTransform), transform => Assert.False(transform.HasAnimatedProperties));
        }
        finally { AnimationConfig.SetReduceMotion(before); }
    });

    [Fact]
    public void DisplayChoicesAndOutputArrowRespondAndReleaseUnderThePointer() => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool before = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(false);
        using var rig = new Rig();
        try
        {
            await rig.Settle(ct);
            rig.Hover(rig.DisplayChip, true);
            rig.Hover(rig.OutputCard, true);
            await WpfFrameWaiter.UntilAsync(() => rig.DisplayLabel.RenderTransform.Value.OffsetX > .2,
                "display choice label follows hover", ct);
            Assert.True(rig.OutputArrow.RenderTransform.Value.OffsetY < 0);
            double x = rig.DisplayLabel.RenderTransform.Value.OffsetX;
            rig.Hover(rig.DisplayChip, false);
            rig.Hover(rig.OutputCard, false);
            Assert.Equal(x, rig.DisplayLabel.RenderTransform.Value.OffsetX, 7);
            await WpfFrameWaiter.UntilAsync(() => Transforms(rig.DisplayLabel.RenderTransform).All(t =>
                !Motion.IsRunning(t, TranslateTransform.XProperty)), "display hover returns", ct);
            Assert.Equal(0, rig.DisplayLabel.RenderTransform.Value.OffsetX, 7);
            Assert.Equal(0, rig.OutputArrow.RenderTransform.Value.OffsetY, 7);
        }
        finally { AnimationConfig.SetReduceMotion(before); }
    });

    [Fact]
    public void ReducedMotionChangesLevelsAndMuteWithoutClocks() => SharedStaTestRunner.Run(() =>
    {
        bool before = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(true);
        using var rig = new Rig();
        try
        {
            rig.Output(.73f);
            rig.App(.73f);
            Assert.Equal(.73, rig.OutputFill.ScaleX, 6);
            Assert.Equal(.73, rig.AppFill.ScaleX, 6);
            Assert.Equal("73 %", rig.OutputValue.Text);
            rig.Output(.73f, true);
            rig.App(.73f, true);
            rig.Hover(rig.DisplayChip, true);
            Assert.Equal("🔇", rig.OutputValue.Text);
            Assert.False(rig.OutputFill.HasAnimatedProperties);
            Assert.False(rig.AppFill.HasAnimatedProperties);
            Assert.Equal(0, rig.DisplayLabel.RenderTransform.Value.OffsetX);
        }
        finally { AnimationConfig.SetReduceMotion(before); }
    });

    internal static IEnumerable<Transform> Transforms(Transform transform) =>
        transform is TransformGroup group ? group.Children.SelectMany(Transforms) : [transform];

    internal sealed class Rig : IDisposable
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private const BindingFlags StaticPrivate = BindingFlags.Static | BindingFlags.NonPublic;
        private readonly ControlCenterView _view = new() { Page = ControlCenterPage.System, DisplayApi = new DisplayApi() };
        private readonly object _output;
        private readonly object _app;
        private readonly Window _window;
        private readonly DetailMotionFrames _frames;
        public StackPanel Surface { get; } = new() { Width = 420, Background = new SolidColorBrush(Color.FromRgb(0x0C, 0x0C, 0x0F)) };
        public Border OutputCard => Part<Border>(_output, "Card");
        public TextBlock OutputValue => Part<TextBlock>(_output, "Sub");
        public ScaleTransform OutputFill => Part<ScaleTransform>(_output, "Fill");
        public TextBlock OutputArrow { get; }
        public TextBlock AppValue => Part<TextBlock>(_app, "Value");
        public ScaleTransform AppFill => Part<ScaleTransform>(_app, "Fill");
        public Border DisplayChip { get; }
        public TextBlock DisplayLabel => (TextBlock)DisplayChip.Child;

        internal Rig()
        {
            Field("_outputs").SetValue(_view, new List<OutputDevice> { new("fake", "Headphones", "USB Headphones", true, false) });
            var outputGrid = new Grid { Margin = new Thickness(16), Width = 388 };
            outputGrid.ColumnDefinitions.Add(new ColumnDefinition());
            _output = Call("AddOutputTile", outputGrid, 0, 1)!;
            OutputArrow = Descendants(OutputCard).OfType<TextBlock>().First(t => t.FontSize == 9);
            Output(.26f, animate: false);
            Surface.Children.Add(outputGrid);
            var expanded = (Dictionary<string, bool>)Field("_expanded").GetValue(_view)!;
            expanded["apps"] = true;
            Call("BuildApps", new List<AudioSessionInfo> { new() { ProcessId = 123456, DisplayName = "Music", Volume = .26f } });
            _app = ((IDictionary)Field("_appBars").GetValue(_view)!)[(uint)123456]!;
            var apps = (StackPanel)Field("_appsSection").GetValue(_view)!;
            ((Panel)VisualTreeHelper.GetParent(apps)).Children.Remove(apps);
            apps.Margin = new Thickness(16, 0, 16, 12);
            Surface.Children.Add(apps);
            Call("AddDisplayChoices", "Refresh rate", new (string, bool, Action)[] { ("60 Hz", false, () => { }), ("144 Hz", true, () => { }), ("240 Hz", false, () => { }) }, false);
            var displays = (StackPanel)Field("_displaysSection").GetValue(_view)!;
            ((Panel)VisualTreeHelper.GetParent(displays)).Children.Remove(displays);
            displays.Margin = new Thickness(16, 0, 16, 16);
            Surface.Children.Add(displays);
            DisplayChip = Descendants(displays).OfType<Border>().First(b => Equals(b.Tag, "144 Hz"));
            _window = new BackgroundWindow { Left = -20000, Top = -20000, Width = 420, Height = 270,
                ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None, Content = Surface };
            _window.Show();
            _window.UpdateLayout();
            _frames = new DetailMotionFrames(Surface);
        }

        internal void Output(float volume, bool muted = false, bool animate = true) =>
            typeof(ControlCenterView).GetMethod("SetOutputLevel", StaticPrivate)!.Invoke(null, [_output, volume, muted, animate]);
        internal void App(float volume, bool muted = false) =>
            typeof(ControlCenterView).GetMethod("SetAppBar", StaticPrivate)!.Invoke(null, [_app, volume, muted, true]);
        internal void Hover(UIElement element, bool enter) => element.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0)
            { RoutedEvent = enter ? UIElement.MouseEnterEvent : UIElement.MouseLeaveEvent });
        internal Task Settle(CancellationToken ct) => WpfFrameWaiter.UntilAsync(() =>
            !Motion.IsRunning(OutputFill, ScaleTransform.ScaleXProperty) && !Motion.IsRunning(AppFill, ScaleTransform.ScaleXProperty),
            "level springs settle", ct);
        private static T Part<T>(object item, string field) => (T)item.GetType().GetField(field)!.GetValue(item)!;
        private static FieldInfo Field(string field) => typeof(ControlCenterView).GetField(field, Private)!;
        private object? Call(string method, params object[] args) => typeof(ControlCenterView).GetMethod(method, Private)!.Invoke(_view, args);
        internal static IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            yield return root;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                foreach (var item in Descendants(VisualTreeHelper.GetChild(root, i))) yield return item;
        }
        public void Dispose() { _frames.Dispose(); _window.Close(); _view.Dispose(); }
    }

    private sealed class DisplayApi : IDisplayApi
    {
        public IReadOnlyList<DisplayInfo> List() => [];
        public bool SetModes(IReadOnlyDictionary<string, DisplayMode> modes) => false;
        public bool SetPositions(IReadOnlyDictionary<string, Int32Point> positions) => false;
        public bool SetPrimary(string id) => false;
        public bool SetActive(string id, bool on) => false;
        public bool SetHdr(string id, bool on) => false;
        public bool SetZoom(string id, int percent) => false;
        public int? GetBrightness(string id) => null;
        public bool SetBrightness(string id, int percent) => false;
    }
}
