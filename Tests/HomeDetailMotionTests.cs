using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VNotch.Controls;
using VNotch.Services;
using VNotch.Services.ControlCenter;
using Xunit;
using static VNotch.Tests.HomeSchoolTabTests;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class HomeDetailMotionTests
{
    [Fact]
    public void DrawerNumbersRollAndReverseFromTheirLiveValue() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var scene = new Scene();
        scene.Update(240, 2.3, 64);
        var watts = scene.Watts;
        Assert.NotEqual(240, watts.Value);
        await WpfFrameWaiter.UntilAsync(() => watts.Value > 150, "socket wattage moves", ct);
        double value = watts.Value;
        double velocity = Motion.VelocityOf(watts, WeatherTween.ValueProperty);
        scene.Update(90, 1.2, 46);
        Assert.Equal(value, watts.Value, 5);
        Assert.Equal(Math.Sign(velocity), Math.Sign(Motion.VelocityOf(watts, WeatherTween.ValueProperty)));
        await TestWait.UntilStableAsync(() => !Motion.IsRunning(watts, WeatherTween.ValueProperty), "latest wattage settles", cancellationToken: ct);
        Assert.Equal(90, watts.Value);
        Assert.Contains("90 W", scene.Plug.Text);
        Assert.Matches("1[.,]20 kWh", scene.Energy.Text);
        Assert.Contains("46°", scene.Pi.Text);
    });

    [Fact]
    public void HiddenOrReducedDrawerReadoutsSetTheirFinalValuesWithoutClocks() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var scene = new Scene();
        AnimationConfig.SetReduceMotion(true);
        scene.Update(210, 1.8, 62);
        Assert.Contains("210 W", scene.Plug.Text);
        Assert.False(scene.Watts.HasAnimatedProperties);
        AnimationConfig.SetReduceMotion(false);
        scene.Update(80, 1.1, 42);
        await WpfFrameWaiter.UntilAsync(() => scene.Watts.Value < 210, "wattage roll starts", ct);
        scene.Root.Visibility = Visibility.Collapsed;
        Assert.False(scene.Watts.HasAnimatedProperties);
        scene.Update(120, 1.4, 51);
        Assert.Contains("120 W", scene.Plug.Text);
        Assert.Contains("51°", scene.Pi.Text);
        Assert.False(scene.Watts.HasAnimatedProperties);
    });

    private sealed class Scene : IDisposable
    {
        private readonly bool _reduced = AnimationConfig.ReduceMotion;
        private readonly string _language = Loc.CurrentLanguage;
        private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
        private readonly bool? _clientAnimations = AnimationConfig.ClientAreaAnimationOverride;
        private readonly PiDashboardClient _client = new(() => "http://127.0.0.1:1");
        private readonly DetailMotionFrames _frames;
        private readonly ControlCenterView _view = new() { Page = ControlCenterPage.Pi };
        private readonly BackgroundWindow _window;
        private readonly HomePlug _socket = new("test-socket", "PC", true, true, false, 140, 1.5);
        private readonly HomePi _computer = new(55, 7200, []);
        internal Border Root { get; }
        internal TextBlock Plug { get; }
        internal TextBlock Energy { get; }
        internal TextBlock Pi { get; }
        internal WeatherTween Watts { get; }

        internal Scene()
        {
            Loc.SetLanguage("en");
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            AnimationConfig.ClientAreaAnimationOverride = true;
            AnimationConfig.SetReduceMotion(true);
            var home = new PiHome([], [], null, [], [_socket], [], _computer);
            SetField(_view, "_piRefreshing", true);
            SetField(_view, "_home", home);
            _view.AttachPi(_client, () => false, () => false, () => "", () => "");
            Root = new Border { Background = Brushes.Black, Child = _view };
            _window = new BackgroundWindow { Content = Root, Width = 568, Height = 420, Left = -20000, Top = -20000 };
            _window.Show();
            _view.SetDrawerOpen(true);
            Root.UpdateLayout();
            _frames = new DetailMotionFrames(Root);

            var plugs = (StackPanel)Field(_view, "_homePlugSection");
            Plug = Descendants<TextBlock>(plugs).Single(label => label.Text == "140 W");
            Energy = Descendants<TextBlock>(plugs).Single(label => label.Text == "1.50 kWh");
            Pi = Descendants<TextBlock>((StackPanel)Field(_view, "_homePiSection")).Single();
            // Inspect the actual wattage clock to check continuity below the label's rounding precision.
            var updates = (List<Action<PiHome>>)Field(_view, "_homeDrawerUpdates");
            var updatePlug = updates.Single(update => update.Target!.GetType().GetField("showWatts") != null);
            var showWatts = (Action<double?>)Field(updatePlug.Target!, "showWatts");
            Watts = (WeatherTween)Field(showWatts.Target!, "tween");
            AnimationConfig.SetReduceMotion(false);
        }

        internal void Update(double watts, double energy, double temperature)
        {
            var home = (PiHome)Field(_view, "_home");
            home = home with
            {
                Plugs = [_socket with { Watts = watts, TodayKwh = energy }],
                Pi = _computer with { Temp = temperature, UptimeSeconds = 7260 }
            };
            SetField(_view, "_home", home);
            Invoke(_view, "UpdateHome", home);
        }

        public void Dispose()
        {
            _frames.Dispose();
            _window.Close();
            _view.Dispose();
            _client.Dispose();
            Loc.SetLanguage(_language);
            CultureInfo.CurrentCulture = _culture;
            AnimationConfig.ClientAreaAnimationOverride = _clientAnimations;
            AnimationConfig.SetReduceMotion(_reduced);
        }
    }
}
