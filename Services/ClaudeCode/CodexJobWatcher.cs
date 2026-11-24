using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;

namespace VNotch.Services.ClaudeCode;

internal sealed record CodexJob(string Id, string Provider, string Model, string? Account, string Title, int Pid, DateTimeOffset StartedAt)
{
    public string ModelFamily => Model.Split('-').Last().ToLowerInvariant() switch
    {
        "astra" => "Astra", "sol" => "Sol", "terra" => "Terra", "luna" => "Luna", _ => Model
    };

    public static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        return elapsed.TotalHours < 1 ? elapsed.ToString(@"mm\:ss", CultureInfo.InvariantCulture)
            : FormattableString.Invariant($"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}");
    }
}

internal sealed record CodexJobResult(CodexJob Job, DateTimeOffset EndedAt, int Exit, bool TimedOut, TimeSpan Duration,
    string? Summary = null);

// The worker owns the files. Unreadable snapshots are retried, including a file observed before its write finished.
internal sealed class CodexJobWatcher : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static readonly TimeSpan HistoryLifetime = TimeSpan.FromHours(1);
    private readonly DispatcherTimer _poll;
    private readonly string _workers, _done;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private readonly Dictionary<string, CodexJob> _knownRunning = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CodexJobResult> _knownDone = new(StringComparer.Ordinal);
    private readonly HashSet<string> _startupDone;
    private FileSystemWatcher? _watcher;
    private int _queued;
    private bool _disposed;

    public IReadOnlyList<CodexJob> Running { get; private set; } = [];
    public IReadOnlyList<CodexJobResult> RecentFinished { get; private set; } = [];
    public event Action? Changed;
    public event Action<CodexJobResult>? Finished;

    public CodexJobWatcher(Dispatcher dispatcher, string? directory = null)
    {
        _workers = Path.Combine(directory ?? CodexAccounts.DirectoryPath, "workers");
        _done = Path.Combine(_workers, "done");
        _startupDone = new(Files(_done) ?? [], StringComparer.Ordinal);
        _poll = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(3) };
        _poll.Tick += (_, _) => Refresh();
        _poll.Start();
        QueueRefresh();
    }

    internal void Refresh()
    {
        if (_disposed) return;
        if (!Directory.Exists(_workers))
        {
            _watcher?.Dispose();
            _watcher = null;
        }
        else if (_watcher == null)
        {
            try
            {
                _watcher = new FileSystemWatcher(_workers, "*.json") { IncludeSubdirectories = true };
                _watcher.Changed += QueueRefresh;
                _watcher.Created += QueueRefresh;
                _watcher.Deleted += QueueRefresh;
                _watcher.Renamed += QueueRefresh;
                _watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _watcher?.Dispose();
                _watcher = null;
            }
        }

        if (Files(_workers) is { } runningFiles)
        {
            foreach (string file in _knownRunning.Keys.Except(runningFiles).ToArray()) _knownRunning.Remove(file);
            foreach (string file in runningFiles)
                if (CodexAccounts.Read(file, ParseJob) is { } job) _knownRunning[file] = job;
        }
        var active = _knownRunning.Where(entry => entry.Value.Provider == "codex" && IsAlive(entry.Value.Pid)).ToArray();

        // Orchestra publishes the result before deleting the running record. Read results last so both changes
        // reach the view together even if that handoff takes place during this refresh.
        var finished = new List<CodexJobResult>();
        if (Files(_done) is { } doneFiles)
        {
            _startupDone.IntersectWith(doneFiles);
            foreach (string file in _knownDone.Keys.Except(doneFiles).ToArray()) _knownDone.Remove(file);
            foreach (string file in doneFiles)
            {
                if (_knownDone.ContainsKey(file) || CodexAccounts.Read(file, ParseResult) is not { } result) continue;
                _knownDone.Add(file, result);
                if (!_startupDone.Remove(file) && result.EndedAt > _startedAt) finished.Add(result);
            }
        }

        var running = active.Where(entry => !_knownDone.ContainsKey(Path.Combine(_done, Path.GetFileName(entry.Key))))
            .Select(entry => entry.Value)
            .OrderByDescending(job => job.StartedAt).ThenBy(job => job.Id, StringComparer.Ordinal).ToArray();
        var cutoff = DateTimeOffset.UtcNow - HistoryLifetime;
        var recent = _knownDone.Values.Where(result => result.Job.Provider == "codex" && result.EndedAt > cutoff)
            .OrderByDescending(result => result.EndedAt).ThenBy(result => result.Job.Id, StringComparer.Ordinal).ToArray();
        bool changed = !Running.SequenceEqual(running) || !RecentFinished.SequenceEqual(recent);
        Running = running;
        RecentFinished = recent;
        foreach (var result in finished.Where(result => result.Job.Provider == "codex").OrderBy(result => result.EndedAt))
            Finished?.Invoke(result);
        if (changed) Changed?.Invoke();
    }

    private void QueueRefresh(object? sender = null, FileSystemEventArgs? args = null)
    {
        if (Interlocked.Exchange(ref _queued, 1) != 0) return;
        _poll.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            Interlocked.Exchange(ref _queued, 0);
            Refresh();
        });
    }

    private static string[]? Files(string directory)
    {
        try { return Directory.GetFiles(directory, "*.json"); }
        catch (DirectoryNotFoundException) { return []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return true; }
    }

    private static CodexJob? ParseJob(JsonElement element) => ParseJob(element, requirePid: true);

    private static CodexJob? ParseJob(JsonElement element, bool requirePid)
    {
        var job = element.Deserialize<CodexJob>(Json);
        return job != null && !string.IsNullOrWhiteSpace(job.Id) && !string.IsNullOrWhiteSpace(job.Title) &&
            !string.IsNullOrWhiteSpace(job.Model) && job.Provider is "codex" or "agy" && (!requirePid || job.Pid > 0) &&
            job.StartedAt != default ? job : null;
    }

    private static CodexJobResult? ParseResult(JsonElement element) =>
        ParseJob(element, requirePid: false) is { } job && element.TryGetProperty("endedAt", out var end) &&
        end.ValueKind == JsonValueKind.String && end.TryGetDateTimeOffset(out var endedAt) &&
        element.TryGetProperty("exit", out var exit) && exit.ValueKind == JsonValueKind.Number && exit.TryGetInt32(out int code) &&
        element.TryGetProperty("timedOut", out var timedOut) && timedOut.ValueKind is JsonValueKind.True or JsonValueKind.False &&
        element.TryGetProperty("durationSec", out var duration) && duration.ValueKind == JsonValueKind.Number &&
        duration.TryGetDouble(out double seconds) && seconds >= 0 && seconds < TimeSpan.MaxValue.TotalSeconds
            ? new(job, endedAt, code, timedOut.GetBoolean(), TimeSpan.FromSeconds(seconds),
                element.TryGetProperty("summary", out var summary) && summary.ValueKind == JsonValueKind.String
                    ? summary.GetString() : null) : null;

    public void Dispose()
    {
        _disposed = true;
        _poll.Stop();
        _watcher?.Dispose();
    }
}
