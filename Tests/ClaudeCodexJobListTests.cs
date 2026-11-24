using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using VNotch.Controls;
using VNotch.Services;
using VNotch.Services.ClaudeCode;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class ClaudeCodexJobListTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const double ContentLimit = 580 - 34;

    [Fact]
    public void ThreeSlotsAndJobRowsFitAlongsideClaudeDetails() => SharedStaTestRunner.Run(() =>
    {
        using var fixture = new Fixture();
        var now = DateTimeOffset.UtcNow;
        var first = Job("first", "C", now.AddSeconds(-60));
        var earlier = Job("earlier", "A", now.AddSeconds(-150));
        var recent = new CodexJobResult(Job("recent", "B", now.AddMinutes(-8)), now.AddMinutes(-5), 1, false,
            TimeSpan.FromMinutes(3), "Checks failed.\r\nSee the log.");
        var older = recent with { Job = Job("older", "C", now.AddMinutes(-9)), EndedAt = now.AddMinutes(-8) };
        fixture.View.SetCodexJobList([first, earlier], [recent, older], ContentLimit);
        Assert.False(fixture.List.IsVisible);
        fixture.Open();
        var rows = fixture.Rows;
        Assert.Equal(new[] { "first", "earlier", "recent" }, rows.Select(row => Labels(row)[0].Text));
        Assert.Equal("Sol · C", Labels(rows[0])[1].Text);
        Assert.Equal("Checks failed. See the log.", Labels(rows[2])[1].Text);
        Assert.Equal("03:00", Labels(rows[2])[2].Text);
        Assert.Equal(54, rows[0].ActualHeight);
        Assert.Equal(259, fixture.List.TranslatePoint(new Point(), fixture.Face).Y);
        Assert.Equal(461, fixture.Face.ActualHeight);
        Assert.True(fixture.View.MeasureContentHeight(628) <= ContentLimit);
        Assert.Equal(0, fixture.View.ControlScrollViewer.ScrollableHeight);
        fixture.View.SetCodexJobList([], [], ContentLimit);
        Assert.False(fixture.List.IsVisible);
        Assert.Empty(fixture.Rows);
    });

    [Fact]
    public void DataChangesMoveOnceAndHiddenRowsStop() => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool reduced = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(false);
        try
        {
            using var fixture = new Fixture();
            var now = DateTimeOffset.UtcNow;
            var first = Job("first", "A", now.AddMinutes(-4));
            fixture.View.SetCodexJobList([first], [], ContentLimit);
            fixture.Open();
            var firstRow = Assert.Single(fixture.Rows);
            var clock = Labels(firstRow)[2];
            var firstSlide = Assert.IsType<TranslateTransform>(firstRow.RenderTransform);
            Assert.False(firstSlide.HasAnimatedProperties);
            Assert.False(Motion.IsRunning(clock, UIElement.OpacityProperty));

            var second = Job("second", "B", now);
            fixture.View.SetCodexJobList([second, first], [], ContentLimit);
            var secondSlide = Assert.IsType<TranslateTransform>(fixture.Rows[0].RenderTransform);
            Assert.True(Motion.IsRunning(secondSlide, TranslateTransform.XProperty));
            var completion = new CodexJobResult(first, now, 0, false, TimeSpan.FromMinutes(4), "Build passed");
            fixture.View.SetCodexJobList([second], [completion], ContentLimit);
            Assert.Same(firstRow, fixture.Rows[1]);
            Assert.True(Motion.IsRunning(clock, UIElement.OpacityProperty));
            Assert.Equal("04:00", clock.Text);
            await WpfFrameWaiter.UntilAsync(() => !Motion.IsRunning(clock, UIElement.OpacityProperty) &&
                !Motion.IsRunning(secondSlide, TranslateTransform.XProperty), "job motions settle", ct);
            fixture.Refresh();
            Assert.False(Motion.IsRunning(clock, UIElement.OpacityProperty));
            Assert.False(Motion.IsRunning(secondSlide, TranslateTransform.XProperty));

            var third = Job("third", "C", now.AddSeconds(1));
            fixture.View.SetCodexJobList([third, second], [completion], ContentLimit);
            var thirdSlide = Assert.IsType<TranslateTransform>(fixture.Rows[0].RenderTransform);
            Assert.True(Motion.IsRunning(thirdSlide, TranslateTransform.XProperty));
            fixture.View.Visibility = Visibility.Collapsed;
            fixture.View.SetCodexJobList([], [completion], ContentLimit);
            Assert.False(thirdSlide.HasAnimatedProperties);
            Assert.False(Motion.IsRunning(clock, UIElement.OpacityProperty));
        }
        finally { AnimationConfig.SetReduceMotion(reduced); }
    });

    [Fact]
    public void StartupHistoryIsSortedAndSilentAndExpiresFromTheList() => SharedStaTestRunner.Run(() =>
    {
        string folder = Path.Combine(Path.GetTempPath(), "vnotch-job-history-" + Guid.NewGuid().ToString("N"));
        string done = Path.Combine(folder, "workers", "done");
        Directory.CreateDirectory(done);
        try
        {
            var now = DateTimeOffset.UtcNow;
            void Write(string id, DateTimeOffset end) => File.WriteAllText(Path.Combine(done, id + ".json"),
                JsonSerializer.Serialize(new { id, title = id, model = "gpt-6.2-sol", provider = "codex",
                    startedAt = end.AddMinutes(-1), endedAt = end, exit = 0, timedOut = false,
                    durationSec = 60, summary = "Build passed" }));

            Write("older", now.AddMinutes(-20));
            Write("expired", now.AddHours(-2));
            Write("recent", now.AddMinutes(-5));
            using var watcher = new CodexJobWatcher(Dispatcher.CurrentDispatcher, folder);
            var events = new List<CodexJobResult>();
            watcher.Finished += events.Add;
            watcher.Refresh();
            Assert.Empty(events);
            Assert.Equal(new[] { "recent", "older" }, watcher.RecentFinished.Select(result => result.Job.Id));
            Assert.Equal("Build passed", watcher.RecentFinished[0].Summary);

            Write("new", DateTimeOffset.UtcNow.AddSeconds(1));
            File.Delete(Path.Combine(done, "older.json"));
            watcher.Refresh();
            watcher.Refresh();
            Assert.Equal("new", Assert.Single(events).Job.Id);
            Assert.Equal(new[] { "new", "recent" }, watcher.RecentFinished.Select(result => result.Job.Id));
        }
        finally { Directory.Delete(folder, recursive: true); }
    });

    private static CodexJob Job(string title, string slot, DateTimeOffset started) =>
        new(title, "codex", "gpt-6.2-sol", slot, title, Environment.ProcessId, started);

    private static TextBlock[] Labels(Grid row) =>
        row.Children.OfType<StackPanel>().Single().Children.OfType<TextBlock>()
            .Concat(row.Children.OfType<TextBlock>()).ToArray();

    private sealed class Fixture : IDisposable
    {
        private readonly BackgroundWindow _window;
        public ControlCenterView View { get; } = new() { Page = ControlCenterPage.Claude };
        public Grid Face => Get<Grid>("_claudeFace");
        public StackPanel List => Get<StackPanel>("_codexJobs");
        public Grid[] Rows => List.Children.OfType<Grid>().ToArray();

        public Fixture()
        {
            typeof(ControlCenterView).GetField("_claude", Private)!.SetValue(View, new ClaudeSessionTracker());
            typeof(ControlCenterView).GetField("_codexReading", Private)!.SetValue(View, true);
            foreach (string slot in new[] { "A", "B", "C" })
                Get<List<CodexAccount>>("_codexAccounts").Add(new(new(slot, Path.GetTempPath()),
                    new CodexUsageClient(codexDirectory: Path.GetTempPath(), useRolloutFallback: false)));
            _window = new BackgroundWindow { Width = 628, Height = ContentLimit, Content = View, WindowStyle = WindowStyle.None };
            _window.Show();
            Refresh();
        }

        public void Open()
        {
            typeof(ControlCenterView).GetMethod("SetAccountsOpen", Private)!.Invoke(View, [true, false]);
            _window.UpdateLayout();
        }

        public void Refresh() => typeof(ControlCenterView).GetMethod("BuildClaudeSection", Private)!.Invoke(View, null);

        private T Get<T>(string name) => (T)typeof(ControlCenterView).GetField(name, Private)!.GetValue(View)!;

        public void Dispose()
        {
            View.Dispose();
            _window.Close();
        }
    }
}
