using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.ClaudeCode;
using VNotch.Tests.Fakes;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class ScriptJobTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public void ParsesStatusAndToleratesMissingOrInvalidOptionalFields()
    {
        var now = DateTimeOffset.Now;
        var running = ScriptJobStatus.Parse(JsonSerializer.Serialize(new
        {
            id = "backup", title = "Backup", icon = "backup", state = "running", phase = "Copying to Google Drive",
            progress = 0.42, pid = Environment.ProcessId, startedAt = now.AddMinutes(-3), updatedAt = now
        }), "unused");
        Assert.NotNull(running);
        Assert.Equal(("backup", "Backup", "backup", ScriptJobState.Running, "Copying to Google Drive", (double?)0.42),
            (running.Id, running.Title, running.Icon, running.State, running.Phase, running.Progress));
        Assert.Equal(now, running.UpdatedAt);
        Assert.Null(running.EndedAt);
        Assert.Null(ScriptJobStatus.Parse("{\"state\":", "backup"));
        Assert.Null(ScriptJobStatus.Parse("[]", "backup"));
        Assert.Null(ScriptJobStatus.Parse($$"""{"state":"unknown","updatedAt":"{{now:O}}"}""", "backup"));
        var minimal = ScriptJobStatus.Parse($$"""{"state":"running","progress":2,"title":false,"updatedAt":"{{now:O}}"}""", "backup");
        Assert.NotNull(minimal);
        Assert.Equal("backup", minimal.Title);
        Assert.Null(minimal.Progress);
        Assert.Null(minimal.Pid);
    }

    [Fact]
    public void DeadAndReusedPidsBecomeStoppedFailures()
    {
        var now = DateTimeOffset.UtcNow;
        using var process = Process.GetCurrentProcess();
        var processStart = new DateTimeOffset(process.StartTime);
        var job = new ScriptJobStatus("backup", "Backup", "backup", ScriptJobState.Running, null,
            null, null, process.Id, processStart, now, null, null);
        Assert.Same(job, job.CheckProcess(now));
        foreach (var stopped in new[] { job with { Pid = int.MaxValue }, job with { StartedAt = processStart.AddMinutes(-1) } })
        {
            var result = stopped.CheckProcess(now);
            Assert.True(result.Stopped);
            Assert.Equal(ScriptJobState.Failed, result.State);
            Assert.Equal(now, result.EndedAt);
            Assert.True(result.IsFreshResult(now));
        }
    }

    [Fact]
    public void WatcherRetriesPartialFilesAndPicksUpAtomicReplacement() => SharedStaTestRunner.RunAsync(async ct =>
    {
        string folder = Path.Combine(Path.GetTempPath(), "vnotch-script-jobs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var now = DateTimeOffset.UtcNow;
            string file = Path.Combine(folder, "backup.json");
            File.WriteAllText(file, "{");
            WriteStatus(Path.Combine(folder, "stale.json"), "running", now.AddHours(-13), id: "stale");
            WriteStatus(Path.Combine(folder, "dead.json"), "running", now, id: "dead", pid: int.MaxValue);
            using var watcher = new ScriptJobWatcher(Dispatcher.CurrentDispatcher, folder);
            await WaitForAsync(() => watcher.Results.Count == 1, ct);
            Assert.Empty(watcher.Running);
            var stopped = Assert.Single(watcher.Results);
            Assert.Equal("dead", stopped.Id);
            Assert.True(stopped.Stopped);

            WriteStatus(file, "running", now);
            await WaitForAsync(() => watcher.Running.Count == 1, ct);
            Assert.Equal("backup", Assert.Single(watcher.Running).Id);
            File.WriteAllText(file, "{");
            await Task.Delay(500, ct);
            Assert.Equal("backup", Assert.Single(watcher.Running).Id);

            ReplaceStatus(file, "done", DateTimeOffset.UtcNow, detail: "+26 MB");
            await WaitForAsync(() => watcher.Running.Count == 0 && watcher.Results.Count == 2, ct);
            Assert.Contains(watcher.Results, job => job.State == ScriptJobState.Done && job.Detail == "+26 MB");
            Assert.Contains(watcher.Results, job => job == stopped);
        }
        finally { Directory.Delete(folder, recursive: true); }
    });

    [Fact]
    public void RunningOrbLeavesAndOnlyFreshDoneResultsShow() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture();
        string file = Path.Combine(fixture.ScriptJobsDirectory, "backup.json");
        Directory.CreateDirectory(fixture.ScriptJobsDirectory);
        WriteStatus(Path.Combine(fixture.ScriptJobsDirectory, "old.json"), "done", DateTimeOffset.UtcNow.AddMinutes(-3), id: "old");
        WriteStatus(file, "running", DateTimeOffset.UtcNow);

        var window = fixture.Window;
        ShowOffscreen(window);
        await WaitForAsync(() => window.BubbleJobFace.IsVisible, ct);
        Assert.Equal(Visibility.Collapsed, window.ClaudeNotification.Visibility);
        Assert.Equal("\uE753", window.BubbleJobGlyph.Text);
        Invoke(window, "OpenBubble");
        Assert.Equal("Backup", window.BubbleTitle.Text);
        Assert.Equal("Copying to Google Drive", window.BubbleSubtitle.Text);
        Assert.StartsWith("42 %\n", window.BubbleTrailing.Text);
        Assert.False(window.BubbleJobArc.Data.IsEmpty());

        ReplaceStatus(file, "done", DateTimeOffset.UtcNow, detail: "+26 MB · 125,493 files");
        await WaitForAsync(() => window.ClaudeNotification.IsVisible && !window.ActivityBubble.IsVisible, ct);
        Assert.Equal("Backup done", window.ClaudeNotificationTitle.Text);
        Assert.Equal("+26 MB · 125,493 files", window.ClaudeNotificationStatus.Text);
        Assert.Equal("\uE73E", window.NotificationBellIcon.Text);
        var timer = (DispatcherTimer)typeof(MainWindow).GetField("_claudeNotificationTimer", Private)!.GetValue(window)!;
        Assert.True(timer.IsEnabled);
        Assert.Equal(TimeSpan.FromSeconds(6), timer.Interval);
    });

    [Fact]
    public void FailedPillStaysReturnsAfterClaudeAndCanBeDismissed() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture();
        string file = Path.Combine(fixture.ScriptJobsDirectory, "backup.json");
        Directory.CreateDirectory(fixture.ScriptJobsDirectory);
        var endedAt = DateTimeOffset.UtcNow;
        WriteStatus(file, "failed", endedAt, detail: "Copy failed");
        var window = fixture.Window;
        ShowOffscreen(window);
        await WaitForAsync(() => window.ClaudeNotification.IsVisible, ct);
        Assert.Equal("Backup failed", window.ClaudeNotificationTitle.Text);
        Assert.Equal("\uE711", window.NotificationBellIcon.Text);
        var timer = (DispatcherTimer)typeof(MainWindow).GetField("_claudeNotificationTimer", Private)!.GetValue(window)!;
        Assert.False(timer.IsEnabled);
        await Task.Delay(TimeSpan.FromSeconds(6.1), ct);
        Assert.True(window.ClaudeNotification.IsVisible);

        Assert.True((bool)Invoke(window, "ShowNotchPill", "Claude", "Allow tool?", null, true, null, null, null,
            TimeSpan.FromMilliseconds(200), null)!);
        Assert.Equal("Claude", window.ClaudeNotificationTitle.Text);
        await WaitForAsync(() => window.ClaudeNotification.IsVisible && window.ClaudeNotificationTitle.Text == "Backup failed", ct);
        var rightClick = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right)
        {
            RoutedEvent = Mouse.PreviewMouseUpEvent
        };
        Invoke(window, "DismissScriptJobFromRightClick", window.NotchWrapper, rightClick);
        Assert.True(rightClick.Handled);
        await WaitForAsync(() => !window.ClaudeNotification.IsVisible, ct);
        ReplaceStatus(file, "failed", endedAt, detail: "Copy failed");
        await Task.Delay(450, ct);
        Assert.False(window.ClaudeNotification.IsVisible);
    });

    [Fact]
    public void ScriptJobOnlyTakesAFreeOrb()
    {
        Assert.Equal((LiveActivity.None, LiveActivity.Job), LiveActivities.Arrange(false, null, false, job: true));
        Assert.Equal((LiveActivity.Music, LiveActivity.Job), LiveActivities.Arrange(true, null, false, job: true));
        Assert.Equal(LiveActivity.Music, LiveActivities.Arrange(true, ClaudeSessionStatus.Waiting, false, job: true).Bubble);
        Assert.Equal(LiveActivity.Claude, LiveActivities.Arrange(true, ClaudeSessionStatus.Working, false, job: true).Bubble);
        Assert.Equal(LiveActivity.Countdown, LiveActivities.Arrange(false, null, true, job: true).Bubble);
        Assert.Equal(LiveActivity.Download, LiveActivities.Arrange(false, null, false, download: true, job: true).Bubble);
        Assert.Equal(LiveActivity.Charging, LiveActivities.Arrange(false, null, false, charging: true, job: true).Bubble);
    }

    internal static GreetingAcceptanceTests.MainWindowFixture CreateFixture() => new("en", false, options =>
    {
        options.ShowCodexJobsInNotch = false;
        options.DisableMouseLeaveAutoClose = true;
        options.Width = 230;
        options.Height = 34;
        options.SlimIdleNotch = true;
    }, services =>
    {
        services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService());
        services.AddSingleton<IBatteryService>(new FakeBatteryService(new BatteryInfo { Percentage = 64 }));
    });

    internal static void ShowOffscreen(MainWindow window)
    {
        window.Left = window.Top = -20000;
        window.Show();
    }

    internal static void ReplaceStatus(string file, string state, DateTimeOffset now, string? detail = null)
    {
        string temp = file + ".tmp";
        WriteStatus(temp, state, now, detail: detail);
        File.Move(temp, file, overwrite: true);
    }

    internal static void WriteStatus(string file, string state, DateTimeOffset now,
        string id = "backup", int? pid = null, string? detail = null)
    {
        using var proc = Process.GetCurrentProcess();
        var started = new DateTimeOffset(proc.StartTime);
        var status = new
        {
            Id = id, Title = id == "backup" ? "Backup" : id, Icon = "backup", State = state,
            Phase = "Copying to Google Drive", Progress = 0.42, Detail = detail,
            Pid = pid ?? proc.Id, StartedAt = started, UpdatedAt = now, EndedAt = state == "running" ? (DateTimeOffset?)null : now
        };
        File.WriteAllText(file, JsonSerializer.Serialize(status, Json));
    }

    internal static async Task WaitForAsync(Func<bool> condition, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        while (!condition()) await Task.Delay(25, deadline.Token);
    }

    internal static object? Invoke(MainWindow window, string method, params object?[] args) =>
        typeof(MainWindow).GetMethod(method, Private)!.Invoke(window, args);
}
