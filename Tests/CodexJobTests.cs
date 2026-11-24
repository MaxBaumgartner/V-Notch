using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using VNotch.Services.ClaudeCode;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class CodexJobTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public void WatcherIgnoresOldCompletionsAndDeadWrappersAndRetriesUnreadableFiles() => SharedStaTestRunner.Run(() =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "vnotch-jobs-" + Guid.NewGuid().ToString("N"));
        string workers = Path.Combine(directory, "workers");
        string done = Path.Combine(workers, "done");
        Directory.CreateDirectory(done);
        try
        {
            var job = Sample("live", "Build the notch", "A", DateTimeOffset.UtcNow.AddMinutes(-2));
            string live = Path.Combine(workers, "live.json");
            File.WriteAllText(live, "{");
            File.WriteAllText(Path.Combine(workers, "dead.json"), JsonSerializer.Serialize(job with { Id = "dead", Pid = int.MaxValue }, Json));
            WriteDone(Path.Combine(done, "old.json"), job with { Id = "old" }, DateTimeOffset.UtcNow.AddMinutes(-1));
            using var watcher = new CodexJobWatcher(Dispatcher.CurrentDispatcher, directory);
            var finished = new List<CodexJobResult>();
            watcher.Finished += finished.Add;
            watcher.Refresh();
            Assert.Empty(watcher.Running);
            Assert.Empty(finished);

            File.WriteAllText(live, JsonSerializer.Serialize(job, Json));
            watcher.Refresh();
            Assert.Equal(job, Assert.Single(watcher.Running));
            using (File.Open(live, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                watcher.Refresh();
                Assert.Equal(job, Assert.Single(watcher.Running));
            }
            var second = job with { Id = "newer", StartedAt = job.StartedAt.AddMinutes(1) };
            string newer = Path.Combine(workers, "newer.json");
            File.WriteAllText(newer, JsonSerializer.Serialize(second, Json));
            watcher.Refresh();
            Assert.Equal(new[] { second, job }, watcher.Running);
            File.Delete(newer);

            string completed = Path.Combine(done, "live.json");
            File.WriteAllText(completed, "{");
            watcher.Refresh();
            Assert.Empty(finished);
            WriteDone(completed, job, DateTimeOffset.UtcNow.AddMilliseconds(100));
            watcher.Refresh();
            watcher.Refresh();
            Assert.Empty(watcher.Running);
            Assert.Equal(job, Assert.Single(finished).Job);
        }
        finally { Directory.Delete(directory, recursive: true); }
    });

    [Fact]
    public void RunningPillListsEveryJobAndClaudeCanTakeItOver() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = new GreetingAcceptanceTests.MainWindowFixture("en", false, options =>
        {
            options.EnableHelloGreeting = false;
            options.EnableSpotlight = false;
            options.AutoCheckUpdates = false;
            options.HideOnExclusiveFullscreen = false;
            options.HideOnWindowedFullscreen = false;
        });
        var window = fixture.Window;
        window.Left = window.Top = -20000;
        window.Show();
        var now = DateTimeOffset.UtcNow;
        CodexJob[] jobs = [
            Sample("one", "Build the notch", "A", now.AddMinutes(-4)),
            Sample("two", "Refresh account usage", "B", now.AddMinutes(-12)),
            Sample("three", "Verify the release", "C", now.AddMinutes(-75))
        ];
        Invoke(window, "ShowCodexJobsRunning", jobs, now);
        Assert.Equal("Build the notch", window.ClaudeNotificationTitle.Text);
        Assert.Equal("+2  ·  04:00", window.ClaudeNotificationStatus.Text);
        Invoke(window, "SetCodexJobDetails", true);
        var rows = window.CodexJobDetails.Children.OfType<Grid>().ToArray();
        Assert.Equal(jobs.Length, rows.Length);
        Assert.Equal("Sol · C", ((TextBlock)rows[2].Children[1]).Text);
        await WpfFrameWaiter.UntilAsync(() => window.NotchBorder.Height > 85, "Codex job list fits", ct);
        Invoke(window, "ShowCodexJobsRunning", jobs, now.AddSeconds(1));
        Assert.Same(rows[0], window.CodexJobDetails.Children[0]);
        Assert.Equal("+2  ·  04:01", window.ClaudeNotificationStatus.Text);
        Assert.True((bool)Invoke(window, "ShowNotchPill", "Claude", "Allow tool?", null, true, null, null, null, null, null)!);
        Assert.Equal(Visibility.Collapsed, window.CodexJobDetails.Visibility);
        Assert.Equal("Claude", window.ClaudeNotificationTitle.Text);
    });

    private static CodexJob Sample(string id, string title, string slot, DateTimeOffset started) =>
        new(id, "codex", "gpt-6.2-sol", slot, title, Environment.ProcessId, started);

    private static void WriteDone(string path, CodexJob job, DateTimeOffset endedAt) =>
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            job.Id, job.Provider, job.Model, job.Account, job.Title, job.Pid, job.StartedAt,
            EndedAt = endedAt, Exit = 0, TimedOut = false, DurationSec = (endedAt - job.StartedAt).TotalSeconds
        }, Json));

    private static object? Invoke(object target, string name, params object?[] args) => target.GetType().GetMethod(name, Private)!.Invoke(target, args);
}
