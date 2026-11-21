using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VNotch.Controls;
using VNotch.Services;
using VNotch.Services.ControlCenter;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class HomeDetailMotionTests
{
    [Fact]
    public void ClimateAndSocketNumbersRollAndReverseFromTheirLiveValue() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var scene = new Scene();
        scene.Update(27, 26, 72, 240, 2.3, 64);
        var current = scene.Tween(scene.Heating, "CurrentRoll");
        Assert.NotEqual(27, current.Value);
        await WpfFrameWaiter.UntilAsync(() => current.Value > 22, "climate number moves", ct);
        double value = current.Value;
        double velocity = Motion.VelocityOf(current, WeatherTween.ValueProperty);
        scene.Update(19, 20, 38, 90, 1.2, 46);
        Assert.Equal(value, current.Value, 5);
        Assert.Equal(Math.Sign(velocity), Math.Sign(Motion.VelocityOf(current, WeatherTween.ValueProperty)));
        await TestWait.UntilStableAsync(() => !Motion.IsRunning(current, WeatherTween.ValueProperty), "latest climate settles", cancellationToken: ct);
        Assert.Equal(19, current.Value);
        Assert.Matches("^19[.,]0°$", scene.Current.Text);
        Assert.Equal("20°", scene.Target.Text);
        Assert.Contains("90 W", scene.Plug.Text);
        Assert.Matches("1[.,]20", scene.Plug.Text);
        Assert.Contains("46°", scene.Pi.Text);
    });

    [Fact]
    public void HiddenOrReducedReadoutsSetTheirFinalValuesWithoutClocks() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var scene = new Scene();
        AnimationConfig.SetReduceMotion(true);
        scene.Update(24, 25, 61, 210, 1.8, 62);
        Assert.Equal("24.0°", scene.Current.Text);
        Assert.Equal("25°", scene.Target.Text);
        Assert.False(scene.Tween(scene.Heating, "TargetRoll").HasAnimatedProperties);
        AnimationConfig.SetReduceMotion(false);
        scene.Update(18, 19, 32, 80, 1.1, 42);
        await WpfFrameWaiter.UntilAsync(() => scene.Tween(scene.Heating, "CurrentRoll").Value < 24, "roll starts", ct);
        scene.Root.Visibility = Visibility.Collapsed;
        scene.Update(22, 23, 47, 120, 1.4, 51);
        Assert.Equal("22.0°", scene.Current.Text);
        Assert.Equal("23°", scene.Target.Text);
        Assert.Contains("120 W", scene.Plug.Text);
        Assert.False(scene.Tween(scene.Heating, "CurrentRoll").HasAnimatedProperties);
        Assert.False(scene.GlowScale.HasAnimatedProperties);
        Assert.Equal(0, scene.LightIcon.RenderTransform.Value.OffsetY);
    });

    [Fact]
    public void LightReversalKeepsTheFrameAndStopsCompletelyWhenHidden() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var scene = new Scene();
        scene.Light(true);
        await WpfFrameWaiter.UntilAsync(() => scene.GlowScale.ScaleX > .3 && Motion.IsRunning(scene.GlowScale, ScaleTransform.ScaleXProperty), "light blooms", ct);
        double position = scene.GlowScale.ScaleX;
        double velocity = Motion.VelocityOf(scene.GlowScale, ScaleTransform.ScaleXProperty);
        scene.Light(false);
        Assert.Equal(position, scene.GlowScale.ScaleX, 5);
        Assert.Equal(Math.Sign(velocity), Math.Sign(Motion.VelocityOf(scene.GlowScale, ScaleTransform.ScaleXProperty)));
        await TestWait.UntilStableAsync(() => !Motion.IsRunning(scene.GlowScale, ScaleTransform.ScaleXProperty), "light exit settles", cancellationToken: ct);
        Assert.Equal(.2, scene.GlowScale.ScaleX);
        scene.Light(true);
        scene.Root.Visibility = Visibility.Collapsed;
        Assert.False(scene.GlowScale.HasAnimatedProperties);
        Assert.Equal(0, scene.LightIcon.RenderTransform.Value.OffsetY);
        Assert.True(((LoopSet)Scene.Field(scene.View, "_homeLoops")).IsEmpty);
    });

    internal sealed class Scene : IDisposable
    {
        private readonly bool _reduced = AnimationConfig.ReduceMotion;
        private readonly string _language = Loc.CurrentLanguage;
        private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
        private readonly bool? _clientAnimations = AnimationConfig.ClientAreaAnimationOverride;
        private readonly object _light;
        private readonly object _plugReadout, _piReadout;
        private readonly DetailMotionFrames _frames;
        private readonly HomeLight _lamp = new("test-lamp", "Desk light", false, true, false, null, null);
        private readonly HomePlug _socket = new("test-socket", "PC", true, true, false, 140, 1.5);
        private readonly HomePi _computer = new(55, 7200, Array.Empty<HomePiIssue>());
        internal readonly ControlCenterView View;
        internal readonly Border Root;
        internal readonly BackgroundWindow Window;
        internal readonly object Heating;
        internal TextBlock Current => (TextBlock)Field(Heating, "Current");
        internal TextBlock Target => (TextBlock)Field(Heating, "Target");
        internal TextBlock Plug { get; }
        internal TextBlock Pi { get; }
        internal ScaleTransform GlowScale => (ScaleTransform)Field(_light, "GlowScale");
        internal FrameworkElement LightIcon => (FrameworkElement)Field(_light, "Icon");

        internal Scene()
        {
            Loc.SetLanguage("en");
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            AnimationConfig.ClientAreaAnimationOverride = true;
            AnimationConfig.SetReduceMotion(false);
            View = new ControlCenterView { Page = ControlCenterPage.Pi };
            var heatingCard = (FrameworkElement)Invoke(View, "HeatingCard", new HomeClimate("heat", "idle", 23, 4, 35, 21, 44, false))!;
            Heating = Field(View, "_heating");
            _light = Invoke(View, "NewLightTile", _lamp, Array.Empty<HomeColor>())!;
            var lightCard = (FrameworkElement)Field(_light, "Card");
            var home = new PiHome(Array.Empty<HomeLight>(), Array.Empty<HomeColor>(), null, Array.Empty<HomeAction>(), new[] { _socket }, Array.Empty<HomeAction>(), _computer);
            Invoke(View, "BuildPlugs", home);
            Invoke(View, "BuildPiLine", home);
            var plugs = (IDictionary)Field(View, "_plugReadouts");
            _plugReadout = plugs[_socket.Id]!;
            _piReadout = Field(View, "_piReadout");
            Plug = ((Dictionary<string, TextBlock>)Field(View, "_plugStatus"))[_socket.Id];
            Pi = (TextBlock)Field(View, "_piLine");
            var cards = new Grid();
            cards.ColumnDefinitions.Add(new ColumnDefinition());
            cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            cards.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetColumn(heatingCard, 2);
            cards.Children.Add(lightCard);
            cards.Children.Add(heatingCard);
            var body = new StackPanel { Children = { cards, (StackPanel)Field(View, "_homePlugSection"), (StackPanel)Field(View, "_homePiSection") } };
            Root = new Border { Width = 680, Height = 300, Padding = new Thickness(6), Background = new SolidColorBrush(Color.FromRgb(18, 18, 23)), Child = body };
            Window = new BackgroundWindow { Content = Root, Width = 680, Height = 300, Left = -20000, Top = -20000 };
            Window.Show();
            Root.UpdateLayout();
            _frames = new DetailMotionFrames(Root);
        }

        internal void Update(double current, double target, double humidity, double watts, double energy, double temperature)
        {
            Invoke(Heating, "Show", new HomeClimate("heat", current < target ? "heating" : "idle", target, 4, 35, current, humidity, false), false);
            Invoke(_plugReadout, "Show", _socket with { Watts = watts, TodayKwh = energy });
            Invoke(_piReadout, "Show", _computer with { Temp = temperature, UptimeSeconds = 7260 });
        }

        internal void Light(bool on) => Invoke(_light, "Show", _lamp with { On = on }, true);
        internal WeatherTween Tween(object parts, string roll) => (WeatherTween)Field(Field(parts, roll), "_tween");
        internal static object Field(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(target)!;
        private static object? Invoke(object target, string method, params object[] args) => target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(target, args);
        public void Dispose()
        {
            _frames.Dispose();
            Window.Close();
            View.Dispose();
            Loc.SetLanguage(_language);
            CultureInfo.CurrentCulture = _culture;
            AnimationConfig.ClientAreaAnimationOverride = _clientAnimations;
            AnimationConfig.SetReduceMotion(_reduced);
        }
    }
}
