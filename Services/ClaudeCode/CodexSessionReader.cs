using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace VNotch.Services.ClaudeCode;

internal enum CodexSessionStatus { Idle, Working }

internal sealed record CodexSession(string Id, string Title, string Project, string Cwd,
    CodexSessionStatus Status, DateTimeOffset LastActivity);

/// <summary>Recent interactive Codex sessions; reads live files without locking their writer.</summary>
internal static class CodexSessionReader
{
    public static List<CodexSession> Read(string? codexDirectory = null, DateTimeOffset? now = null)
    {
        codexDirectory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        var instant = now ?? DateTimeOffset.Now;
        var cutoff = instant.UtcDateTime.AddMinutes(-30);
        var sessions = new Dictionary<string, CodexSession>(StringComparer.Ordinal);
        // Rollout folders can follow the CLI's local date or the service's UTC date.
        var days = new[] { instant.Date, instant.Date.AddDays(-1), instant.UtcDateTime.Date, instant.UtcDateTime.Date.AddDays(-1) };
        foreach (var day in days.Distinct())
        {
            string directory = Path.Combine(codexDirectory, "sessions", day.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture));
            try
            {
                if (!Directory.Exists(directory)) continue;
                foreach (string path in Directory.EnumerateFiles(directory, "rollout-*.jsonl"))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(path) < cutoff) continue;
                        var session = ReadSession(path);
                        if (session != null && (!sessions.TryGetValue(session.Id, out var previous) || session.LastActivity >= previous.LastActivity))
                            sessions[session.Id] = session;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        if (sessions.Count == 0) return new List<CodexSession>();
        ReadLines(Path.Combine(codexDirectory, "session_index.jsonl"), root =>
        {
            string? id = Text(root, "id"), title = Text(root, "thread_name");
            if (id != null && !string.IsNullOrWhiteSpace(title) && sessions.TryGetValue(id, out var session))
                sessions[id] = session with { Title = title.Trim() };
            return true;
        });
        return sessions.Values.OrderByDescending(s => s.Status == CodexSessionStatus.Working)
            .ThenByDescending(s => s.LastActivity).ThenBy(s => s.Id, StringComparer.Ordinal).ToList();
    }

    private static CodexSession? ReadSession(string path)
    {
        string? id = null, cwd = null, title = null;
        bool excluded = false;
        DateTimeOffset last = DateTimeOffset.MinValue;
        DateTimeOffset? started = null, completed = null;
        ReadLines(path, root =>
        {
            DateTimeOffset? timestamp = Timestamp(root);
            if (timestamp is { } at) last = at;
            if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object) return true;
            string? type = Text(root, "type");
            if (type == "session_meta")
            {
                excluded = Text(payload, "originator") == "codex_exec";
                id = Text(payload, "id");
                cwd = Text(payload, "cwd");
                if (last == DateTimeOffset.MinValue && Timestamp(payload) is { } metaTime) last = metaTime;
            }
            else if (type == "event_msg")
            {
                string? eventType = Text(payload, "type");
                if (eventType == "task_started") started = timestamp ?? last;
                else if (eventType == "task_complete") completed = timestamp ?? last;
            }
            else if (title == null && type == "response_item" && Text(payload, "role") == "user")
            {
                string? text = MessageText(payload)?.Trim();
                if (!string.IsNullOrWhiteSpace(text) && !text.StartsWith('<')) title = ShortTitle(text);
            }
            return !excluded;
        });
        if (excluded || string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(cwd)) return null;
        string project = Path.GetFileName(cwd.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return new CodexSession(id, title ?? project, project, cwd,
            started.HasValue && (!completed.HasValue || started > completed) ? CodexSessionStatus.Working : CodexSessionStatus.Idle, last);
    }

    private static string? MessageText(JsonElement payload)
    {
        if (!payload.TryGetProperty("content", out var content)) return null;
        if (content.ValueKind == JsonValueKind.String) return content.GetString();
        if (content.ValueKind != JsonValueKind.Array) return null;
        return string.Join(" ", content.EnumerateArray().Select(part => Text(part, "text")).Where(text => text != null));
    }

    private static string ShortTitle(string title)
    {
        string line = string.Join(" ", title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length > 60 ? line[..59] + "…" : line;
    }

    private static string? Text(JsonElement root, string property) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static DateTimeOffset? Timestamp(JsonElement root) =>
        DateTimeOffset.TryParse(Text(root, "timestamp"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null;

    private static void ReadLines(string path, Func<JsonElement, bool> read)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                try
                {
                    using var document = JsonDocument.Parse(line);
                    if (document.RootElement.ValueKind == JsonValueKind.Object && !read(document.RootElement)) break;
                }
                catch (JsonException) { } // The writer may not have finished its last line yet.
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>A chip only focuses an existing Codex window; it never starts the app.</summary>
    public static bool FocusApp()
    {
        var processes = Process.GetProcessesByName("Codex");
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    IntPtr window = process.MainWindowHandle;
                    if (window == IntPtr.Zero) continue;
                    if (Win32Interop.IsIconic(window)) Win32Interop.ShowWindow(window, 9);
                    Win32Interop.SetForegroundWindow(window);
                    return true;
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
            }
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
        return false;
    }
}
