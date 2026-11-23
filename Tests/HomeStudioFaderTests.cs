using System.Net;
using System.Net.Http;
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
public sealed class HomeStudioFaderTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal const string Sample = """
        {
          "lights": [
            { "id":"switch.main_light", "name":"Hauptlicht", "available":true, "on":false },
            { "id":"switch.side_light", "name":"Seitenlicht", "available":true, "on":true },
            { "id":"light.govee_h6125_govee_h6125", "name":"Govee Strip", "rgb":true, "available":true, "on":true, "color":[127,166,204], "brightness":38 }
          ],
          "stripColors": [
            { "name":"Lilac", "rgb":[161,155,210] }, { "name":"Mist", "rgb":[127,166,204] },
            { "name":"Sand", "rgb":[210,174,124] }, { "name":"Rose", "rgb":[210,133,126] }, { "name":"Sage", "rgb":[157,189,177] }
          ],
          "climate": { "mode":"heat", "action":"heating", "target":22, "current":21.2, "humidity":45, "windowOpen":false },
          "ac": [
            { "id":"script.klima_power_toggle", "name":"An/Aus", "icon":"power" },
            { "id":"script.klima_speed_next", "name":"Stufe", "icon":"fan" },
            { "id":"script.klima_cool_toggle", "name":"Kühl", "icon":"snow" }
          ],
          "acOn": true,
          "plugs": [
            { "id":"switch.pc", "name":"PC", "available":true, "on":true, "guard":true, "watts":147, "todayKwh":1.38 },
            { "id":"switch.pi", "name":"Pi", "available":true, "on":true, "guard":true, "watts":5 },
            { "id":"switch.mikrofon", "name":"Mikrofon", "available":true, "on":true },
            { "id":"switch.steckdose_1_2", "name":"Steckdose 1", "available":true, "on":false },
            { "id":"switch.steckdose_4", "name":"Steckdose 4", "available":true, "on":true }
          ],
          "scenes": [
            { "id":"nacht", "name":"Gute Nacht", "icon":"moon" }, { "id":"zocken", "name":"Zocken", "icon":"game" },
            { "id":"lernen", "name":"Lernen", "icon":"book" }, { "id":"lueften", "name":"Lüften", "icon":"wind" }
          ]
        }
        """;

    [Fact]
    public void StudioFaderAndDetailsFitWithTheMockupData() => SharedStaTestRunner.Run(() =>
    {
        using var client = new PiDashboardClient(() => "http://127.0.0.1:1");
        using var view = HomeView(client);
        var window = Host(view);
        try
        {
            window.Show();
            window.UpdateLayout();
            var face = Descendants<Grid>(view).Single(grid => grid.Name == "HomeFace");
            var fader = Descendants<Border>(face).Single(border => border.Name == "HomeGoveeCapsule");
            Assert.Equal(184, fader.ActualHeight);
            Assert.Equal(43, fader.ActualWidth);
            Assert.Contains("38", Descendants<TextBlock>(face).Select(label => label.Text));
            Assert.Contains("Mikrofon", Descendants<TextBlock>(face).Select(label => label.Text));

            bool reduced = AnimationConfig.ReduceMotion;
            AnimationConfig.SetReduceMotion(true);
            try { view.SetDrawerOpen(true); }
            finally { AnimationConfig.SetReduceMotion(reduced); }
            window.UpdateLayout();
            var details = Descendants<Grid>(view).Single(grid => grid.Name == "HomeDetails");
            var labels = Descendants<TextBlock>(details).Select(label => label.Text).ToList();
            Assert.False(face.IsVisible);
            Assert.DoesNotContain("Mikrofon", labels);
            Assert.Contains("147 W", labels);
            Assert.Contains("1.38 kWh", labels);
            Assert.Contains("A/C power", labels);
            Assert.True(view.DrawerHeight >= details.DesiredSize.Height);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(HttpStatusCode.OK, "62")]
    [InlineData(HttpStatusCode.GatewayTimeout, "62")]
    [InlineData(HttpStatusCode.BadRequest, "38")]
    public void BrightnessStaysOnTheDraftUntilThePiConfirmsOrRejectsIt(HttpStatusCode status, string expected) =>
        SharedStaTestRunner.RunAsync(async ct =>
        {
            bool reduced = AnimationConfig.ReduceMotion;
            AnimationConfig.SetReduceMotion(true);
            using var client = new PiDashboardClient(() => "http://127.0.0.1:1");
            var http = typeof(PiDashboardClient).GetField("_http", Private)!;
            ((HttpClient)http.GetValue(client)!).Dispose();
            var handler = new BrightnessHandler();
            http.SetValue(client, new HttpClient(handler));
            using var view = HomeView(client);
            var window = Host(view);
            try
            {
                window.Show();
                var draftType = typeof(ControlCenterView).GetNestedType("BrightnessDraft", BindingFlags.NonPublic)!;
                const string id = "light.govee_h6125_govee_h6125";
                var draft = Activator.CreateInstance(draftType, id, 62)!;
                var draftField = typeof(ControlCenterView).GetField("_homeBrightnessDraft", Private)!;
                draftField.SetValue(view, draft);
                var refresh = typeof(ControlCenterView).GetMethod("UpdateHomeFace", Private)!;
                var reported = (PiHome)typeof(ControlCenterView).GetField("_home", Private)!.GetValue(view)!;
                var send = (Task)typeof(ControlCenterView).GetMethod("SendHomeBrightnessAsync", Private)!
                    .Invoke(view, [Task.CompletedTask, draft])!;
                var request = await handler.Request.Task.WaitAsync(ct);
                Assert.Equal("light", request["action"]!.GetValue<string>());
                Assert.Equal(id, request["id"]!.GetValue<string>());
                Assert.Equal(62, request["value"]!["brightness_pct"]!.GetValue<int>());
                refresh.Invoke(view, [reported]);
                Assert.Contains(Descendants<TextBlock>(view), label => label.FontSize == 41 && label.Text == "62");
                handler.Response.SetResult(new HttpResponseMessage(status));
                await send.WaitAsync(ct);
                refresh.Invoke(view, [reported]);
                Assert.Contains(Descendants<TextBlock>(view), label => label.FontSize == 41 && label.Text == expected);
                if (status != HttpStatusCode.BadRequest)
                {
                    Assert.Same(draft, draftField.GetValue(view));
                    var confirmed = reported with { Lights = reported.Lights.Select(light => light.Id == id ? light with { Brightness = 62 } : light).ToArray() };
                    refresh.Invoke(view, [confirmed]);
                }
                Assert.Null(draftField.GetValue(view));
                Assert.Equal(1, handler.Calls);
            }
            finally { window.Close(); AnimationConfig.SetReduceMotion(reduced); }
        }, 15);

    [Theory]
    [InlineData(5, null, false)]
    [InlineData(6, false, true)]
    [InlineData(9, null, true)]
    [InlineData(10, false, false)]
    [InlineData(11, true, true)]
    public void AirConditionerNeedsSummerOrAReportedOnState(int month, bool? on, bool wanted)
    {
        var method = typeof(ControlCenterView).GetMethod("ShowHomeAirConditioner", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.Equal(wanted, method.Invoke(null, [month, on]));
    }

    [Fact]
    public void TheHomeMusicMeterStopsWhileTheFaceOrTabIsHidden() => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool reduced = AnimationConfig.ReduceMotion;
        bool? animations = AnimationConfig.ClientAreaAnimationOverride;
        AnimationConfig.ClientAreaAnimationOverride = true;
        AnimationConfig.SetReduceMotion(false);
        using var client = new PiDashboardClient(() => "http://127.0.0.1:1");
        using var view = HomeView(client);
        var home = (PiHome)HomeSchoolTabTests.Field(view, "_home");
        HomeSchoolTabTests.SetField(view, "_home", home with { Climate = null });
        HomeSchoolTabTests.SetField(view, "_musicSync", true);
        HomeSchoolTabTests.SetField(view, "_homeAudioRefreshAt", TimeSpan.MaxValue);
        var window = Host(view);
        try
        {
            window.Show();
            Assert.True((bool)HomeSchoolTabTests.Field(view, "_homeStripHasMeter"));
            await WpfFrameWaiter.UntilAsync(() => HomeSchoolTabTests.Field(view, "_homeFrame") != null, "music meter starts", ct);

            view.SetDrawerOpen(true);
            await WpfFrameWaiter.UntilAsync(() => !view.IsLayoutFollowing, "home details open", ct);
            AssertStopped();

            view.SetDrawerOpen(false);
            await WpfFrameWaiter.UntilAsync(() => HomeSchoolTabTests.Field(view, "_homeFrame") != null, "music meter resumes", ct);
            view.Visibility = Visibility.Collapsed;
            AssertStopped();
        }
        finally
        {
            window.Close();
            AnimationConfig.SetReduceMotion(reduced);
            AnimationConfig.ClientAreaAnimationOverride = animations;
        }

        void AssertStopped()
        {
            Assert.False((bool)HomeSchoolTabTests.Field(view, "_homeRendering"));
            Assert.Null(HomeSchoolTabTests.Field(view, "_homeFrame"));
            Assert.Null(HomeSchoolTabTests.Field(view, "_homeAudioSessions"));
        }
    });

    [Fact]
    public void SeenStartsAChangedValueOnceAndSkipsHiddenUpdates() => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool reduced = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(false);
        using var client = new PiDashboardClient(() => "http://127.0.0.1:1");
        using var view = HomeView(client);
        var window = Host(view);
        try
        {
            window.Show();
            var frames = new List<double>();
            var track = (Action<double?>)typeof(ControlCenterView).GetMethod("TrackHomeValue", Private)!
                .Invoke(view, [view, "test", (Action<double?>)(value => frames.Add(value ?? 0)), true, false, null])!;
            track(0);
            Assert.All(frames, value => Assert.Equal(0, value));
            track(1);
            await WpfFrameWaiter.UntilAsync(() => frames.Any(x => x is > 0 and < 1), "visible value animates", ct);
            await WpfFrameWaiter.UntilAsync(() => frames.LastOrDefault() == 1, "visible value settles", ct);
            int before = frames.Count;
            track(1);
            Assert.Equal(before, frames.Count);

            window.Hide();
            frames.Clear();
            track(.25);
            await Task.Delay(100, ct);
            Assert.NotEmpty(frames);
            Assert.All(frames, value => Assert.Equal(.25, value));
            Assert.Equal(1, ((Dictionary<string, double?>)typeof(ControlCenterView).GetField("_homeFaceLastSeen", Private)!
                .GetValue(view)!)["home.face.test"]);
            Assert.False((bool)typeof(ControlCenterView).GetField("_homeRendering", Private)!.GetValue(view)!);
        }
        finally { window.Close(); AnimationConfig.SetReduceMotion(reduced); }
    }, 15);

    [Theory]
    [InlineData(60)]
    [InlineData(144)]
    public void WheelScrubUsesFrameTimeAndKeepsTheInputContinuous(int hz) => SharedStaTestRunner.Run(() =>
    {
        bool reduced = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(false);
        using var client = new PiDashboardClient(() => "http://127.0.0.1:1");
        using var view = HomeView(client);
        var window = Host(view);
        try
        {
            window.Show();
            view.ScrubDrawer(-120);
            Assert.Equal(0, view.DrawerHeight);
            var frame = TimeSpan.FromSeconds(1.0 / hz);
            var steps = new List<double>();
            double height = 0;
            for (int i = 0; i < hz; i++)
            {
                if (i == hz / 16) view.ScrubDrawer(-120);
                Assert.Equal(height, view.DrawerHeight);
                view.AdvanceDrawerWheel(frame);
                steps.Add(view.DrawerHeight - height);
                height = view.DrawerHeight;
            }
            Assert.True(view.DrawerOpen);
            Assert.True(height > 180);
            Assert.All(steps, step => Assert.True(step >= 0));
            for (int i = 1; i < steps.Count - 1; i++)
                Assert.True(steps[i] <= 2 * Math.Max(steps[i - 1], steps[i + 1]) + .03);
        }
        finally { window.Close(); AnimationConfig.SetReduceMotion(reduced); }
    });

    private static ControlCenterView HomeView(PiDashboardClient client)
    {
        var view = new ControlCenterView { Page = ControlCenterPage.Pi };
        typeof(ControlCenterView).GetField("_piRefreshing", Private)!.SetValue(view, true);
        typeof(ControlCenterView).GetField("_home", Private)!.SetValue(view, PiDashboardClient.ParseHome((JsonObject)JsonNode.Parse(Sample)!));
        view.AttachPi(client, () => false, () => false, () => "", () => "");
        return view;
    }

    private static Window Host(ControlCenterView view)
    {
        Loc.SetLanguage("en");
        var window = new Window { Content = view, Width = 568, Height = 420, ShowInTaskbar = false };
        BackgroundTestWindows.ProtectInput(window);
        return window;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed) yield return typed;
            foreach (var match in Descendants<T>(child)) yield return match;
        }
    }

    private sealed class BrightnessHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public TaskCompletionSource<JsonNode> Request { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<HttpResponseMessage> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Request.TrySetResult(JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!);
            return await Response.Task.WaitAsync(cancellationToken);
        }
    }
}
