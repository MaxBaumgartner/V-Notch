using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;

namespace VNotch.Services;

internal enum ScriptJobState { Running, Done, Warning, Failed }

internal sealed record ScriptJobStatus(string Id, string Title, string? Icon, ScriptJobState State, string? Phase,
    double? Progress, string? Detail, int? Pid, DateTimeOffset? StartedAt, DateTimeOffset UpdatedAt,
    DateTimeOffset? EndedAt, string? Log, bool Stopped = false)
{
    internal static readonly TimeSpan MaxAge = TimeSpan.FromHours(12);
    private static readonly TimeSpan ResultAge = TimeSpan.FromMinutes(2);

    public bool IsFreshResult(DateTimeOffset now) =>
        State != ScriptJobState.Running && EndedAt is { } ended && ended <= now && ended > now - ResultAge;

    public ScriptJobStatus CheckProcess(DateTimeOffset now)
    {
        if (State != ScriptJobState.Running || Pid is not { } pid) return this;
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.HasExited && (StartedAt is not { } start || process.StartTime.ToUniversalTime() <= start))
                return this;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { }
        catch (Win32Exception) { return this; } // Access denied does not establish that a process stopped.
        return this with { State = ScriptJobState.Failed, EndedAt = now, Stopped = true };
    }

    internal static ScriptJobStatus? Parse(string json, string fallbackId)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            ScriptJobState? state = ReadText(root, "state") switch
            {
                "running" => ScriptJobState.Running,
                "done" => ScriptJobState.Done,
                "warning" => ScriptJobState.Warning,
                "failed" => ScriptJobState.Failed,
                _ => null
            };
            var ended = ReadTime(root, "endedAt");
            var updated = ReadTime(root, "updatedAt") ?? ended;
            if (state is not { } status || updated is not { } timestamp) return null;
            var id = ReadText(root, "id") ?? fallbackId;
            double? progress = root.TryGetProperty("progress", out var fraction) && fraction.ValueKind == JsonValueKind.Number &&
                fraction.TryGetDouble(out double value) && value is >= 0 and <= 1 ? value : null;
            int? pid = root.TryGetProperty("pid", out var process) && process.ValueKind == JsonValueKind.Number &&
                process.TryGetInt32(out int number) && number > 0 ? number : null;
            return new(id, ReadText(root, "title") ?? id, ReadText(root, "icon"), status, ReadText(root, "phase"),
                progress, ReadText(root, "detail"), pid, ReadTime(root, "startedAt"), timestamp, ended, ReadText(root, "log"));
        }
        catch (JsonException) { return null; }
    }

    private static string? ReadText(JsonElement root, string name) =>
        root.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String &&
        field.GetString()?.Trim() is { Length: > 0 } value ? value : null;

    private static DateTimeOffset? ReadTime(JsonElement root, string name) =>
        root.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String &&
        field.TryGetDateTimeOffset(out var timestamp) ? timestamp : null;
}

internal sealed class ScriptJobWatcher : IDisposable
{
    private readonly string _directory;
    private readonly DispatcherTimer _debounce, _pidTimer;
    private readonly FileSystemWatcher? _watcher;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Dictionary<string, ScriptJobStatus> _files = new(StringComparer.OrdinalIgnoreCase);
    private int _queued;
    private bool _reading, _readAgain, _disposed;

    public IReadOnlyList<ScriptJobStatus> Running { get; private set; } = [];
    public IReadOnlyList<ScriptJobStatus> Results { get; private set; } = [];
    public event Action? Changed;

    public ScriptJobWatcher(Dispatcher dispatcher, string? directory = null)
    {
        _directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "V-Notch", "jobs");
        _debounce = new(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(250) };
        _debounce.Tick += async (_, _) => await RefreshAsync();
        _pidTimer = new(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(30) };
        _pidTimer.Tick += (_, _) =>
        {
            if (_reading) return;
            var now = DateTimeOffset.UtcNow;
            foreach (string file in _files.Keys.ToArray())
                _files[file] = _files[file].CheckProcess(now);
            Publish(now);
        };
        try
        {
            Directory.CreateDirectory(_directory);
            _watcher = new(_directory, "*.json");
            _watcher.Changed += QueueRefresh;
            _watcher.Created += QueueRefresh;
            _watcher.Deleted += QueueRefresh;
            _watcher.Renamed += QueueRefresh;
            _watcher.Error += (_, _) => QueueRefresh();
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or Win32Exception)
        {
            _watcher?.Dispose();
            RuntimeLog.Warn("SCRIPT-JOBS", $"Could not watch jobs: {ex.Message}");
        }
        QueueRefresh();
    }

    private void QueueRefresh(object? sender = null, FileSystemEventArgs? args = null)
    {
        if (Interlocked.Exchange(ref _queued, 1) != 0) return;
        _debounce.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            Interlocked.Exchange(ref _queued, 0);
            if (_disposed) return;
            _debounce.Stop();
            _debounce.Start();
        });
    }

    private async Task RefreshAsync()
    {
        _debounce.Stop();
        if (_reading)
        {
            _readAgain = true;
            return;
        }
        _reading = true;
        try
        {
            var token = _shutdown.Token;
            var snapshot = await Task.Run(() => ReadFilesAsync(token), token);
            if (_disposed) return;
            var now = DateTimeOffset.UtcNow;
            if (snapshot != null)
            {
                foreach (string file in _files.Keys.Except(snapshot.Keys, _files.Comparer).ToArray()) _files.Remove(file);
                foreach (var (file, status) in snapshot)
                {
                    if (status == null) continue;
                    // Keep the first observed stop across duplicate file events and later snapshots.
                    if (status.State == ScriptJobState.Running && _files.GetValueOrDefault(file) is { Stopped: true } previous &&
                        (previous.Id, previous.Pid, previous.StartedAt) == (status.Id, status.Pid, status.StartedAt))
                        continue;
                    _files[file] = status.CheckProcess(now);
                }
            }
            Publish(now);
        }
        catch (OperationCanceledException) when (_disposed) { }
        finally
        {
            _reading = false;
            if (_readAgain && !_disposed)
            {
                _readAgain = false;
                _debounce.Start();
            }
        }
    }

    private async Task<Dictionary<string, ScriptJobStatus?>?> ReadFilesAsync(CancellationToken token)
    {
        string[] files;
        try { files = Directory.GetFiles(_directory, "*.json"); }
        catch (DirectoryNotFoundException) { files = []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }

        var snapshot = new Dictionary<string, ScriptJobStatus?>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files) snapshot[file] = await ReadFileAsync(file, token).ConfigureAwait(false);
        var retry = snapshot.Where(entry => entry.Value == null).Select(entry => entry.Key).ToArray();
        if (retry.Length > 0)
        {
            await Task.Delay(150, token).ConfigureAwait(false);
            foreach (var file in retry) snapshot[file] = await ReadFileAsync(file, token).ConfigureAwait(false);
        }
        return snapshot;
    }

    private static async Task<ScriptJobStatus?> ReadFileAsync(string file, CancellationToken token)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return ScriptJobStatus.Parse(await reader.ReadToEndAsync(token).ConfigureAwait(false), Path.GetFileNameWithoutExtension(file));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private void Publish(DateTimeOffset now)
    {
        foreach (var (file, status) in _files.ToArray())
        {
            var timestamp = status.Stopped || status.State == ScriptJobState.Running
                ? status.UpdatedAt : status.EndedAt ?? status.UpdatedAt;
            if (timestamp <= now - ScriptJobStatus.MaxAge) _files.Remove(file);
        }
        var running = _files.Values.Where(job => job.State == ScriptJobState.Running)
            .OrderByDescending(job => job.StartedAt ?? job.UpdatedAt).ThenBy(job => job.Id, StringComparer.Ordinal).ToArray();
        var results = _files.Values.Where(job => job.State != ScriptJobState.Running)
            .OrderBy(job => job.EndedAt).ThenBy(job => job.Id, StringComparer.Ordinal).ToArray();
        if (running.Length > 0) _pidTimer.Start();
        else _pidTimer.Stop();
        bool changed = !Running.SequenceEqual(running) || !Results.SequenceEqual(results);
        Running = running;
        Results = results;
        if (changed) Changed?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _debounce.Stop();
        _pidTimer.Stop();
        _watcher?.Dispose();
        _shutdown.Cancel();
        _shutdown.Dispose();
    }
}
