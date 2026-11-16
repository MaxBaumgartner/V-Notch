using System.IO;
using System.Text.Json;
using VNotch.Services.ClaudeCode;
using Xunit;

namespace VNotch.Tests;

public sealed class CodexSessionReaderTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private readonly string _directory = Path.Combine(AppContext.BaseDirectory, "codex-reader-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ExcludesExecWorkersAndOldFilesBeforeReading()
    {
        WriteSession("desktop", "Codex Desktop", Now, "task_started");
        WriteSession("cli", "codex_cli_rs", Now, "task_complete");
        WriteSession("worker", "codex_exec", Now, "task_started");
        WriteSession("old", "Codex Desktop", Now.AddMinutes(-31), "task_started");
        WriteSession("older-folder", "Codex Desktop", Now, "task_started", day: Now.AddDays(-2));

        var sessions = CodexSessionReader.Read(_directory, Now);

        Assert.Equal(new[] { "desktop", "cli" }, sessions.Select(s => s.Id));
        Assert.Equal(CodexSessionStatus.Working, sessions[0].Status);
        Assert.Equal(CodexSessionStatus.Idle, sessions[1].Status);
        Assert.Equal("Example", sessions[0].Project);
    }

    [Fact]
    public void LatestTaskEventDeterminesWorkingOrIdleAndLastLineGivesActivity()
    {
        string path = WriteSession("interactive", "Codex Desktop", Now, "task_started");
        Append(path, Event(Now.AddMinutes(1), "task_complete"));
        Assert.Equal(CodexSessionStatus.Idle, Assert.Single(CodexSessionReader.Read(_directory, Now.AddMinutes(1))).Status);

        Append(path, Event(Now.AddMinutes(2), "task_started"));
        Append(path, new { timestamp = Now.AddMinutes(3), type = "response_item", payload = new { role = "assistant", content = "Working" } });
        File.AppendAllText(path, "{\"timestamp\":");

        var session = Assert.Single(CodexSessionReader.Read(_directory, Now.AddMinutes(3)));
        Assert.Equal(CodexSessionStatus.Working, session.Status);
        Assert.Equal(Now.AddMinutes(3), session.LastActivity);
    }

    [Fact]
    public void IndexTitleOverridesUserMessageAndLaterIndexLinesWin()
    {
        string path = WriteSession("named", "Codex Desktop", Now, "task_started");
        Append(path, User("First prompt"));
        string index = Path.Combine(_directory, "session_index.jsonl");
        Append(index, new { id = "named", thread_name = "Old title", updated_at = Now });
        Append(index, new { id = "other", thread_name = "Other", updated_at = Now });
        Append(index, new { id = "named", thread_name = "Current title", updated_at = Now });
        File.AppendAllText(index, "{partial");

        Assert.Equal("Current title", Assert.Single(CodexSessionReader.Read(_directory, Now)).Title);
    }

    [Fact]
    public void SkipsContextMessagesAndTrimsFirstUserPrompt()
    {
        string path = WriteSession("prompt", "Codex Desktop", Now, "task_complete");
        Append(path, User("  <environment_context>ignore this</environment_context>"));
        Append(path, User("   Build a widget\n" + new string('x', 80)));
        Append(path, User("Later prompt"));

        string title = Assert.Single(CodexSessionReader.Read(_directory, Now)).Title;
        Assert.StartsWith("Build a widget ", title);
        Assert.Equal(60, title.Length);
        Assert.EndsWith("…", title);
    }

    [Fact]
    public void FallsBackToProjectAndIncludesYesterdayIfModifiedRecently()
    {
        WriteSession("yesterday", "Codex Desktop", Now.AddMinutes(-5), "task_complete", day: Now.AddDays(-1));
        var session = Assert.Single(CodexSessionReader.Read(_directory, Now));
        Assert.Equal("Example", session.Title);
        Assert.Equal(CodexSessionStatus.Idle, session.Status);
    }

    [Fact]
    public void ReadsWhileTheWriterKeepsTheFileOpen()
    {
        string path = WriteSession("live", "Codex Desktop", Now, "task_started");
        using var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        Assert.Equal("live", Assert.Single(CodexSessionReader.Read(_directory, Now)).Id);
    }

    [Fact]
    public void MissingDirectoryReturnsNoSessions() => Assert.Empty(CodexSessionReader.Read(_directory, Now));

    private string WriteSession(string id, string originator, DateTimeOffset modified, string eventType, DateTimeOffset? day = null)
    {
        string folder = Path.Combine(_directory, "sessions", (day ?? Now).ToString("yyyy/MM/dd", System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "rollout-" + id + ".jsonl");
        Append(path, new { timestamp = Now.AddMinutes(-10), type = "session_meta", payload = new { id, cwd = @"C:\Projects\Example", originator, timestamp = Now.AddMinutes(-10) } });
        Append(path, Event(Now, eventType));
        File.SetLastWriteTimeUtc(path, modified.UtcDateTime);
        return path;
    }

    private static object Event(DateTimeOffset timestamp, string type) => new { timestamp, type = "event_msg", payload = new { type } };
    private static object User(string text) => new { timestamp = Now, type = "response_item", payload = new { role = "user", content = new[] { new { type = "input_text", text } } } };
    private static void Append(string path, object value) => File.AppendAllText(path, JsonSerializer.Serialize(value) + "\n");

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
