using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using VNotch.Controls;
using VNotch.Services;
using VNotch.Services.ControlCenter;
using Xunit;
using static VNotch.Tests.HomeSchoolTabTests;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class HomeFaceTests
{
    internal static PiHome Sample(bool lit = false) => new(
        [
            new("switch.main_light", "Hauptlicht", false, true, lit, null, null),
            new("switch.side_light", "Seitenlicht", false, true, lit, null, null),
            new("light.strip", "Govee Strip", true, true, lit, Color.FromRgb(160, 60, 230), lit ? 70 : null)
        ],
        [new("Red", Color.FromRgb(255, 40, 20)), new("Lilac", Color.FromRgb(160, 60, 230))],
        new("heat", lit ? "heating" : "idle", 25, 5, 30, 21.2, 44.7, false),
        [new("power", "A/C power", "power"), new("fan", "Fan", "fan"), new("cool", "Cool", "snow")],
        [
            new("switch.pc", "PC", true, true, true, 146.9, 1.376),
            new("switch.pi", "Pi", true, true, true, 5, null),
            new("switch.mic", "Mikrofon", true, true, false, null, null),
            new("switch.socket1", "Steckdose 1", true, true, false, 3, null),
            new("switch.socket4", "Steckdose 4", true, false, false, null, null)
        ],
        [new("nacht", "Gute Nacht", "moon"), new("zocken", "Zocken", "game"), new("lernen", "Lernen", "book"), new("lueften", "Lüften", "wind")],
        new(57.9, 13600, []));

    [Fact]
    public void FaceFitsAndShowsThePiStates() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var scene = new Fixture(Sample(lit: true));
        await WpfFrameWaiter.UntilAsync(() => Descendants<Ellipse>(scene.Backdrop).Count() == 5, "room light layout", ct);
        var face = scene.Face;
        var lamps = new[] { Device("Hauptlicht"), Device("Seitenlicht") };
        var sockets = new[] { Device("Mikrofon"), Device("Steckdose 1"), Device("Steckdose 4") };
        Assert.All(lamps, lamp => Assert.Single(Descendants<Border>(lamp), disc => disc.Width == 39 && disc.Height == 39));
        Assert.All(sockets, socket => Assert.Single(Descendants<Border>(socket), disc => disc.Width == 37 && disc.Height == 37));
        Assert.Equal(Color.FromRgb(245, 245, 247), Assert.IsType<SolidColorBrush>(Descendants<Ellipse>(sockets[0]).Single().Fill).Color);
        Assert.Equal(Color.FromRgb(16, 16, 18), Assert.IsType<SolidColorBrush>(Descendants<Ellipse>(sockets[2]).Single().Fill).Color);
        var fader = Descendants<Border>(face).Single(border => border.Name == "HomeGoveeCapsule");
        var devices = lamps.Append(fader).Concat(sockets).ToArray();
        Assert.Contains(Descendants<TextBlock>(face), t => t.Text == "70");
        foreach (var item in devices)
        {
            Point origin = item.TranslatePoint(new Point(), face);
            Assert.InRange(origin.X + item.ActualWidth, 0, face.ActualWidth);
        }
        Assert.All(Sample().Scenes, s => Assert.NotNull(Device(s.Name)));
        Assert.DoesNotContain(Descendants<TextBlock>(face), t => t.Text is "On" or "Off" or "PC" or "Pi");
        var heat = Assert.IsType<Ellipse>(Field(scene.View, "_homeHeatingGlow"));
        Assert.True(heat.Opacity > 0);
        scene.Update(Sample());
        await WpfFrameWaiter.UntilAsync(() => heat.Opacity == 0, "heater settles", ct);
        Assert.Contains(Descendants<TextBlock>(face), t => t.Text == "21.2°");

        Border Device(string name) => Descendants<Border>(face).Single(b => AutomationProperties.GetName(b) == name);
    });

    [Fact]
    public void ChangedLampBloomsOnceAfterReturning() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var scene = new Fixture(Sample());
        var glow = scene.LampGlow;
        Assert.Equal(0, glow.Opacity);
        scene.View.Visibility = Visibility.Collapsed;
        scene.Now += TabLedger.Away + TimeSpan.FromSeconds(1);
        scene.Update(Sample(lit: true));
        Assert.Equal(1, glow.Opacity);
        scene.View.Visibility = Visibility.Visible;
        await WpfFrameWaiter.UntilAsync(() => glow.Opacity is > 0 and < 1, "lamp blooms after return", ct);
        scene.Update(Sample(lit: true));
        Assert.InRange(glow.Opacity, 0, .999999);
        await WpfFrameWaiter.UntilAsync(() => glow.Opacity == 1, "lamp bloom settles", ct);
        scene.View.Visibility = Visibility.Collapsed;
        scene.Now += TabLedger.Away + TimeSpan.FromSeconds(1);
        scene.View.Visibility = Visibility.Visible;
        await WpfFrameWaiter.NextAsync(ct);
        Assert.Equal(1, glow.Opacity);
    });

    [Fact]
    public void HidingSettlesTheLampAndTemperatureAndHiddenUpdatesStayStill() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var scene = new Fixture(Sample());
        var glow = scene.LampGlow;
        var temperature = Descendants<TextBlock>(scene.Face).Single(t => t.FontSize == 18 && t.Text.EndsWith('°'));
        var warmer = Sample(lit: true) with { Climate = Sample().Climate! with { Current = 25 } };
        scene.Update(warmer);
        await WpfFrameWaiter.UntilAsync(() => glow.Opacity is > 0 and < 1 && temperature.Text is not ("21.2°" or "25.0°"),
            "lamp and room temperature move", ct);
        scene.View.Visibility = Visibility.Collapsed;
        Assert.Equal(1, glow.Opacity);
        Assert.Equal("25.0°", temperature.Text);
        scene.Update(Sample());
        Assert.Equal(0, glow.Opacity);
        Assert.Equal("21.2°", temperature.Text);
        await WpfFrameWaiter.NextAsync(ct);
        Assert.Equal(0, glow.Opacity);
        Assert.Equal("21.2°", temperature.Text);
    });

    [Fact]
    public void RoundedPartsKeepTheirShape() => SharedStaTestRunner.Run(() =>
    {
        using var scene = new Fixture(Sample(lit: true));
        CornerShapes.AssertNoneStretched(scene.Face);
    });

    private sealed class Fixture : IDisposable
    {
        private readonly bool _reduced = AnimationConfig.ReduceMotion;
        private readonly bool? _clientAnimations = AnimationConfig.ClientAreaAnimationOverride;
        private readonly string _language = Loc.CurrentLanguage;
        private readonly System.Globalization.CultureInfo _culture = System.Globalization.CultureInfo.CurrentCulture;
        private readonly PiDashboardClient _client = new(() => "http://127.0.0.1:1");
        private readonly BackgroundWindow _window;
        private readonly DetailMotionFrames _frames;
        internal readonly ControlCenterView View = new() { Page = ControlCenterPage.Pi };
        internal readonly MoodBackdrop Backdrop = new();
        internal DateTime Now = DateTime.UtcNow;
        internal Grid Face => (Grid)Field(View, "_homeFace");
        internal Ellipse LampGlow => ((List<Ellipse>)Field(View, "_homeLampGlows"))[0];

        internal Fixture(PiHome home)
        {
            Loc.SetLanguage("en");
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            AnimationConfig.ClientAreaAnimationOverride = true;
            AnimationConfig.SetReduceMotion(false);
            SetField(View, "_piRefreshing", true);
            SetField(View, "_seen", new TabLedger(() => View.IsVisible, () => Now));
            View.AttachPi(_client, () => false, () => false, () => "", () => "");
            View.AttachBackdrop(Backdrop);
            Update(home);
            var root = new Grid { Background = Brushes.Black, Children = { Backdrop, View } };
            _window = new BackgroundWindow { Content = root, Width = 608, Height = 330, Left = -20000, Top = -20000 };
            _window.Show();
            root.UpdateLayout();
            _frames = new DetailMotionFrames(root);
        }

        internal void Update(PiHome home)
        {
            SetField(View, "_home", home);
            Invoke(View, "BuildHome");
        }

        public void Dispose()
        {
            _frames.Dispose();
            _window.Close();
            View.Dispose();
            _client.Dispose();
            Loc.SetLanguage(_language);
            System.Globalization.CultureInfo.CurrentCulture = _culture;
            AnimationConfig.ClientAreaAnimationOverride = _clientAnimations;
            AnimationConfig.SetReduceMotion(_reduced);
        }
    }
}
