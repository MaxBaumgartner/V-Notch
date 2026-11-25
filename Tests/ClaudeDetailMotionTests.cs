using System.Collections;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VNotch.Controls;
using VNotch.Services;
using VNotch.Services.ClaudeCode;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class ClaudeDetailMotionTests
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [Fact]
    public void LimitBarsFillAndRetargetAcrossRebuildsWithoutLosingPositionOrVelocity() => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(false);
        try
        {
            using var fixture = new Fixture(25);
            var bar = fixture.Bar;
            var grow = Field<ScaleTransform>(bar, "Grow");
            var number = Field<WeatherTween>(Field<object>(bar, "Number"), "_tween");
            Assert.Equal(.25, grow.ScaleX, 8);
            await Settled(fixture.Bar, ct);
            Assert.Equal(.25, grow.ScaleX, 8);
            fixture.SetUsage(85);
            await WpfFrameWaiter.UntilAsync(() => grow.ScaleX > .35, "limit advancing", ct);
            double position = grow.ScaleX, velocity = Motion.VelocityOf(grow, ScaleTransform.ScaleXProperty);
            double shown = number.Value;
            fixture.SetUsage(10);
            Assert.Same(bar, fixture.Bar);
            Assert.Equal(position, grow.ScaleX, 6);
            Assert.Equal(shown, number.Value, 6);
            // Rebuilding and laying out the card takes real time; momentum must survive that interval.
            Assert.InRange(Motion.VelocityOf(grow, ScaleTransform.ScaleXProperty) / velocity, .5, 1.5);
            await Settled(fixture.Bar, ct);
            Assert.Equal(.1, grow.ScaleX, 8);
            Assert.Equal(10, number.Value, 8);
            fixture.View.Visibility = Visibility.Collapsed;
            Assert.False(grow.HasAnimatedProperties);
            Assert.False(number.HasAnimatedProperties);
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    [Fact]
    public void ReducedMotionSetsExactLimitsAndHidingStopsAnUnfinishedFill() => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(true);
        try
        {
            using var fixture = new Fixture(40);
            var grow = Field<ScaleTransform>(fixture.Bar, "Grow");
            var number = Field<WeatherTween>(Field<object>(fixture.Bar, "Number"), "_tween");
            fixture.SetUsage(80);
            Assert.Equal(.8, grow.ScaleX);
            Assert.Equal(80, number.Value);
            Assert.False(grow.HasAnimatedProperties);
            Assert.False(number.HasAnimatedProperties);
            AnimationConfig.SetReduceMotion(false);
            fixture.SetUsage(20);
            await WpfFrameWaiter.UntilAsync(() => grow.ScaleX < .7, "limit starts leaving", ct);
            fixture.View.Visibility = Visibility.Collapsed;
            Assert.False(grow.HasAnimatedProperties);
            Assert.False(number.HasAnimatedProperties);
            Assert.Equal(.2, grow.ScaleX);
            Assert.Equal(20, number.Value);
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    [Fact]
    public void ReopeningKeepsUnchangedRowsStillAndRollsOnlyTheChangedSession() => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(false);
        try
        {
            using var fixture = new Fixture(30);
            var lead = new ClaudeSession { Id = "lead", Title = "Lead", Status = ClaudeSessionStatus.Waiting, Prompt = "Read?" };
            var worker = new ClaudeSession { Id = "worker", Title = "Worker", Status = ClaudeSessionStatus.Working, Activity = "Reading" };
            fixture.SetSession(lead);
            fixture.SetSession(worker);
            var texts = Field<Dictionary<(string Session, string Part), TextBlock>>(fixture.View, "_claudeTexts");
            var leadText = texts[(lead.Id, "state")];
            var workerText = texts[(worker.Id, "chip-state")];
            var leadShift = WeatherMotion.Transform<TranslateTransform>(leadText);
            var workerShift = WeatherMotion.Transform<TranslateTransform>(workerText);
            fixture.View.Visibility = Visibility.Collapsed;
            fixture.View.Visibility = Visibility.Visible;
            var rows = Field<Dictionary<string, Border>>(fixture.View, "_claudeSessionRows");
            foreach (var row in rows.Values)
            {
                var transforms = Assert.IsType<TransformGroup>(row.RenderTransform);
                var shift = Assert.IsType<TranslateTransform>(transforms.Children[1]);
                Assert.False(Motion.IsRunning(shift, TranslateTransform.XProperty));
                Assert.False(Motion.IsRunning(shift, TranslateTransform.YProperty));
            }
            Assert.False(Motion.IsRunning(leadShift, TranslateTransform.YProperty));
            Assert.False(Motion.IsRunning(workerShift, TranslateTransform.YProperty));

            fixture.View.Visibility = Visibility.Collapsed;
            worker.Activity = "Checking";
            fixture.SetSession(worker);
            Assert.Equal("Reading", workerText.Text);
            fixture.View.Visibility = Visibility.Visible;
            Assert.False(Motion.IsRunning(leadShift, TranslateTransform.YProperty));
            await WpfFrameWaiter.UntilAsync(() => workerShift.Y < -1, "old activity rolls upward", ct);
            Assert.True(Motion.IsRunning(workerShift, TranslateTransform.YProperty));
            await WpfFrameWaiter.UntilAsync(() => workerText.Text == "Checking" &&
                !Motion.IsRunning(workerShift, TranslateTransform.YProperty), "new activity settles", ct);
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    [Fact]
    public void NewRowsSlideInAndEndedRowsStayUntilTheFoldFinishes() => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(false);
        try
        {
            using var fixture = new Fixture(30);
            var session = new ClaudeSession { Id = "arrival", Title = "Arrival", Cwd = @"C:\project" };
            fixture.SetSession(session);
            var rows = Field<Dictionary<string, Border>>(fixture.View, "_claudeSessionRows");
            var row = rows[session.Id];
            var shift = WeatherMotion.Transform<TranslateTransform>(row);
            Assert.True(Motion.IsRunning(shift, TranslateTransform.YProperty));
            fixture.RemoveSessions();
            Assert.Same(fixture.Section, row.Parent);
            var fold = Assert.IsType<ScaleTransform>(row.LayoutTransform);
            Assert.True(Motion.IsRunning(fold, ScaleTransform.ScaleYProperty));
            await WpfFrameWaiter.UntilAsync(() => row.Parent == null, "ended session folds away", ct);
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    [Fact]
    public void ReopeningGlidesTheLimitFromItsLastVisibleFrame() => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(false);
        try
        {
            using var fixture = new Fixture(25);
            var bar = fixture.Bar;
            var grow = Field<ScaleTransform>(bar, "Grow");
            var number = Field<WeatherTween>(Field<object>(bar, "Number"), "_tween");
            fixture.SetUsage(85);
            await WpfFrameWaiter.UntilAsync(() => grow.ScaleX > .35, "limit advances before hiding", ct);
            double lastFill = grow.ScaleX, lastNumber = number.Value;
            fixture.View.Visibility = Visibility.Collapsed;
            fixture.SetUsage(60);
            Assert.False(Motion.IsRunning(grow, ScaleTransform.ScaleXProperty));
            Assert.False(Motion.IsRunning(number, WeatherTween.ValueProperty));
            fixture.View.Visibility = Visibility.Visible;
            Assert.Same(bar, fixture.Bar);
            Assert.Equal(lastFill, grow.ScaleX, 6);
            Assert.Equal(lastNumber, number.Value, 6);
            Assert.True(Motion.IsRunning(grow, ScaleTransform.ScaleXProperty));
            Assert.True(Motion.IsRunning(number, WeatherTween.ValueProperty));
            await Settled(bar, ct);
            fixture.View.Visibility = Visibility.Collapsed;
            fixture.View.Visibility = Visibility.Visible;
            Assert.False(Motion.IsRunning(grow, ScaleTransform.ScaleXProperty));
            Assert.False(Motion.IsRunning(number, WeatherTween.ValueProperty));
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    [Theory]
    [InlineData(80, true)]
    [InlineData(-80, false)]
    public void PermissionSwipeDecidesOnlyPastTheThreshold(int distance, bool allow) => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(false);
        try
        {
            using var fixture = new Fixture(30);
            var request = new ClaudePermissionRequest { Id = "request", SessionId = "permission", Tool = "Read" };
            var card = fixture.SetPermission(request);
            var shift = Assert.IsType<TranslateTransform>(card.RenderTransform);
            card.Begin(new Point(), 0);
            Assert.False(card.Move(new Point(4, 12), 30));
            Assert.False(card.Release(new Point(80, 13), 60));
            Assert.False(request.Answer.Task.IsCompleted);

            card.Begin(new Point(), 100);
            Assert.True(card.Move(new Point(25, 1), 150));
            Assert.Equal(15, shift.X);
            Assert.True(card.Release(new Point(26, 1), 160));
            Assert.False(request.Answer.Task.IsCompleted);
            Assert.True(Motion.IsRunning(shift, TranslateTransform.XProperty));
            Assert.True(Motion.VelocityOf(shift, TranslateTransform.XProperty) > 0);
            await WpfFrameWaiter.UntilAsync(() => shift.X == 0 &&
                !Motion.IsRunning(shift, TranslateTransform.XProperty), "short swipe springs back", ct);

            card.Begin(new Point(), 200);
            Assert.True(card.Move(new Point(distance, 1), 300));
            Assert.Equal(Math.Sign(distance) * 41.6, shift.X, 6);
            Assert.True(card.Release(new Point(distance, 1), 310));
            var decision = allow ? ClaudeDecision.Allow : ClaudeDecision.Deny;
            Assert.Equal(ClaudeSessionTracker.DecisionBody(decision, null), await request.Answer.Task.WaitAsync(ct));
            Assert.True(Motion.IsRunning(shift, TranslateTransform.XProperty));
            await WpfFrameWaiter.UntilAsync(() => card.Parent == null, "decided card folds away", ct);
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    [Fact]
    public void TaskShareCountsToItsLatestValueAndStopsWhenHidden() => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(false);
        try
        {
            using var fixture = new Fixture(30);
            var session = new ClaudeSession { Id = "fake" };
            var host = (FrameworkElement)Invoke(fixture.View, "TokenStats", session)!;
            fixture.Section.Children.Add(host);
            object row = Field<object>(fixture.View, "_tokenRow");
            void Show(double? share) => Invoke(fixture.View, "ShowTokens", row,
                new ClaudeSessionTokens(default, default, 1000, share));
            Show(12);
            Show(68);
            var number = Field<WeatherTween>(Field<object>(row, "ShareNumber"), "_tween");
            Assert.Equal(12, number.Value);
            await WpfFrameWaiter.UntilAsync(() => number.Value > 20, "task share counts", ct);
            double shown = number.Value;
            Show(25);
            Assert.Equal(shown, number.Value, 6);
            await WpfFrameWaiter.UntilAsync(() => !Motion.IsRunning(number, WeatherTween.ValueProperty), "task share settles", ct);
            Assert.Equal(ControlCenterView.FormatShare(25), Field<TextBlock>(row, "Share").Text);
            Show(null);
            await WpfFrameWaiter.UntilAsync(() => Field<TextBlock>(row, "Share").Text == "–", "unknown share fades in", ct);
            Assert.Equal("–", Field<TextBlock>(row, "Share").Text);
            Assert.False(number.HasAnimatedProperties);
            Show(75);
            fixture.View.Visibility = Visibility.Collapsed;
            Assert.False(number.HasAnimatedProperties);
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    [Fact]
    public void RepeatedUnknownSharesAndAnInterruptedPlaceholderKeepTheLatestReadout() => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(false);
        try
        {
            using var fixture = new Fixture(30);
            var host = (FrameworkElement)Invoke(fixture.View, "TokenStats", new ClaudeSession { Id = "fake" })!;
            fixture.Section.Children.Add(host);
            object row = Field<object>(fixture.View, "_tokenRow");
            var text = Field<TextBlock>(row, "Share");
            var number = Field<WeatherTween>(Field<object>(row, "ShareNumber"), "_tween");
            void Show(double? share) => Invoke(fixture.View, "ShowTokens", row,
                new ClaudeSessionTokens(default, default, 1000, share));
            async Task Settle() => await WpfFrameWaiter.UntilAsync(() =>
                !Motion.IsRunning(number, WeatherTween.ValueProperty) && !Motion.IsRunning(text, UIElement.OpacityProperty),
                "latest task share and label settle", ct);

            Show(12);
            await Settle();
            Show(null);
            await WpfFrameWaiter.UntilAsync(() => text.Opacity < .9, "unknown share starts its dip", ct);
            double opacity = text.Opacity;
            Show(65);
            Assert.Equal(opacity, text.Opacity, 6);
            await Settle();
            Assert.Equal(ControlCenterView.FormatShare(65), text.Text);
            Assert.Equal(1, text.Opacity);

            Show(null);
            await Settle();
            Assert.Equal("–", text.Text);
            Show(null);
            Assert.Equal("–", text.Text);
            Assert.False(number.HasAnimatedProperties);

            Show(42);
            await Settle();
            Assert.Equal(ControlCenterView.FormatShare(42), text.Text);
            fixture.View.Visibility = Visibility.Collapsed;
            Assert.False(number.HasAnimatedProperties);
            Assert.False(text.HasAnimatedProperties);
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    [Fact]
    public void ClaudeActivityRebuildKeepsTheLiveRollAndStopsDetachedLabels() => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(false);
        try
        {
            using var fixture = new Fixture(30);
            var session = new ClaudeSession
            {
                Id = "detail-session", Title = "Motion tests", Status = ClaudeSessionStatus.Working,
                Activity = "Reading motion", LastEventUtc = DateTime.UtcNow, TurnStartedUtc = DateTime.UtcNow
            };
            fixture.SetSession(session);
            var texts = Field<Dictionary<(string Session, string Part), TextBlock>>(fixture.View, "_claudeTexts");
            var activity = texts[(session.Id, "activity")];
            session.Activity = "Writing motion";
            fixture.SetSession(session);
            await WpfFrameWaiter.UntilAsync(() => activity.Opacity < .8, "Claude activity starts its roll", ct);
            double opacity = activity.Opacity;
            var shift = WeatherMotion.Transform<TranslateTransform>(activity);
            double y = shift.Y;
            double velocity = Motion.VelocityOf(shift, TranslateTransform.YProperty);
            session.Activity = "Checking motion";
            fixture.SetSession(session);
            Assert.Same(activity, texts[(session.Id, "activity")]);
            Assert.Equal(opacity, activity.Opacity, 6);
            Assert.Equal(y, shift.Y, 6);
            Assert.Equal(Math.Sign(velocity), Math.Sign(Motion.VelocityOf(shift, TranslateTransform.YProperty)));
            await WpfFrameWaiter.UntilAsync(() => activity.Text == "Checking motion" &&
                !Motion.IsRunning(activity, UIElement.OpacityProperty), "latest Claude activity settles", ct);

            session.Activity = "Finishing motion";
            fixture.SetSession(session);
            await WpfFrameWaiter.UntilAsync(() => activity.Opacity < .8, "detached activity starts leaving", ct);
            fixture.RemoveSessions();
            Assert.False(activity.HasAnimatedProperties);
            Assert.False(shift.HasAnimatedProperties);
            Assert.Empty(texts);
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    [Fact]
    public void ReusedLimitCaptionsFollowLanguageChanges() => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        string language = Loc.CurrentLanguage;
        AnimationConfig.SetReduceMotion(false);
        try
        {
            using var fixture = new Fixture(30);
            var bar = fixture.Bar;
            var caption = Field<TextBlock>(bar, "Label");
            Loc.SetLanguage(language == "en" ? "de" : "en");
            string expected = Loc.CurrentLanguage == "de" ? "5-Stunden-Nutzung" : "5-hour usage";
            await WpfFrameWaiter.UntilAsync(() => caption.Text == expected &&
                !Motion.IsRunning(caption, UIElement.OpacityProperty), "cached limit caption changes language", ct);
            Assert.Same(bar, fixture.Bar);
            Assert.Equal(expected, caption.Text);
            fixture.View.Visibility = Visibility.Collapsed;
            Assert.False(caption.HasAnimatedProperties);
        }
        finally
        {
            Loc.SetLanguage(language);
            AnimationConfig.SetReduceMotion(previous);
        }
    });

    internal static async Task Settled(object bar, CancellationToken ct)
    {
        var grow = Field<ScaleTransform>(bar, "Grow");
        var number = Field<WeatherTween>(Field<object>(bar, "Number"), "_tween");
        double used = Field<ClaudeLimitWindow>(bar, "_window").UsedPercent;
        await WpfFrameWaiter.UntilAsync(() => !Motion.IsRunning(grow, ScaleTransform.ScaleXProperty) &&
            !Motion.IsRunning(number, WeatherTween.ValueProperty) && grow.ScaleX == used / 100 && number.Value == used,
            "Claude limit settles", ct);
    }

    internal static T Field<T>(object target, string name) => (T)(target.GetType().GetField(name, Fields)?.GetValue(target) ??
        target.GetType().GetProperty(name, Fields)?.GetValue(target))!;
    internal static object? Invoke(object target, string name, params object?[] args) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);

    internal sealed class Fixture : IDisposable
    {
        internal ControlCenterView View { get; } = new() { Page = ControlCenterPage.Claude };
        internal Window Window { get; }
        private readonly DetailMotionFrames _frames;
        private readonly ClaudeSessionTracker _tracker = new();
        private readonly ClaudeUsageClient _usage = new(new FakeHandler(), Path.Combine(Path.GetTempPath(), "vnotch-no-claude-profile"));
        internal StackPanel Section => Field<StackPanel>(View, "_claudeSection");
        internal object Bar => Field<Array>(View, "_claudeFrontMeters").GetValue(0)!;

        internal Fixture(double percent)
        {
            Set("_claude", _tracker);
            Set("_claudeInstalled", true);
            Set("_usage", _usage);
            Set("_codexReading", true);
            Set("_tokenReading", true);
            _usage.Latest = Limits(percent);
            Window = new BackgroundWindow
            {
                Left = -20000, Top = -20000, Width = 620, Height = 410, WindowStyle = WindowStyle.None,
                ShowActivated = false, ShowInTaskbar = false, Background = new SolidColorBrush(Color.FromRgb(23, 23, 25)), Content = View
            };
            Window.Show();
            Window.UpdateLayout();
            _frames = new DetailMotionFrames(View);
        }

        internal void SetUsage(double percent)
        {
            _usage.Latest = Limits(percent);
            Invoke(View, "BuildClaudeSection");
            Window.UpdateLayout();
        }

        internal void SetSession(ClaudeSession session)
        {
            Field<Dictionary<string, ClaudeSession>>(_tracker, "_sessions")[session.Id] = session;
            Invoke(View, "BuildClaudeSection");
            Window.UpdateLayout();
        }

        internal void RemoveSessions()
        {
            Field<Dictionary<string, ClaudeSession>>(_tracker, "_sessions").Clear();
            Invoke(View, "BuildClaudeSection");
            Window.UpdateLayout();
        }

        internal ControlCenterView.PermissionSwipeCard SetPermission(ClaudePermissionRequest request)
        {
            Field<List<ClaudePermissionRequest>>(_tracker, "_pending").Add(request);
            SetSession(new ClaudeSession { Id = request.SessionId, Status = ClaudeSessionStatus.Waiting });
            var cards = Field<Dictionary<string, FrameworkElement>>(View, "_requestCards");
            return Assert.IsType<ControlCenterView.PermissionSwipeCard>(cards[request.Id]);
        }

        private static ClaudeLimits Limits(double percent) => new(new ClaudeLimitWindow(percent, null), new ClaudeLimitWindow(33, null));
        private void Set(string name, object value) => View.GetType().GetField(name, Fields)!.SetValue(View, value);
        public void Dispose() { _frames.Dispose(); View.Dispose(); Window.Close(); _usage.Dispose(); }
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
    }
}
