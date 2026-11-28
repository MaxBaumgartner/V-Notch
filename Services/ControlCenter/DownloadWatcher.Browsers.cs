using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VNotch.Services.ControlCenter;

internal sealed partial class DownloadWatcher
{
    internal sealed record BrowserFile(string Path, long Bytes, DateTime LastWriteUtc);

    private static readonly TimeSpan FinishedRetention = TimeSpan.FromSeconds(60);
    private Dictionary<string, BrowserFile?> _browserPartials = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (DownloadInfo Download, DateTime Until)> _finishedBrowsers = new(StringComparer.OrdinalIgnoreCase);

    internal List<DownloadInfo> PollBrowsers(string? folder)
    {
        var files = new Dictionary<string, BrowserFile>(StringComparer.OrdinalIgnoreCase);
        if (folder != null && Directory.Exists(folder))
        {
            foreach (var file in new DirectoryInfo(folder).EnumerateFiles())
                files[file.FullName] = new(file.FullName, file.Length, file.LastWriteTimeUtc);
        }

        var now = _now();
        var found = new List<DownloadInfo>();
        var partials = new Dictionary<string, BrowserFile?>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files.Values)
        {
            if (BrowserFinalPath(file.Path) is not { } finalPath) continue;
            // Firefox creates a placeholder under the final name while the .part is growing.
            partials[file.Path] = _browserPartials.GetValueOrDefault(file.Path, files.GetValueOrDefault(finalPath));
            _finishedBrowsers.Remove(finalPath);
            string key = BrowserKey(file.Path);
            var (rate, growing) = Track(key, file.Bytes);
            if (growing)
                found.Add(new(key, BrowserFor(file.Path), DisplayName(Path.GetFileName(file.Path)), null, rate, file.Path));
        }

        foreach (var (partial, initialFinal) in _browserPartials)
        {
            if (partials.ContainsKey(partial)) continue;
            string key = BrowserKey(partial);
            _last.Remove(key);
            if (!_lastGrowthUtc.Remove(key) || CompletedBrowserPath(partial, initialFinal, files) is not { } finalPath) continue;
            var download = new DownloadInfo("finished:" + finalPath, BrowserFor(partial), Path.GetFileName(finalPath),
                100, 0, finalPath, IsFinished: true);
            _finishedBrowsers[finalPath] = (download, now + FinishedRetention);
        }
        _browserPartials = partials;

        foreach (var (path, (download, until)) in _finishedBrowsers.ToArray())
        {
            if (now >= until || !files.ContainsKey(path)) _finishedBrowsers.Remove(path);
            else found.Add(download);
        }
        return found;
    }

    internal static string? CompletedBrowserPath(string partial, BrowserFile? initialFinal, IReadOnlyDictionary<string, BrowserFile> files)
    {
        if (BrowserFinalPath(partial) is not { } path || files.ContainsKey(partial) || !files.TryGetValue(path, out var final))
            return null;
        if (initialFinal != null && final.Bytes == initialFinal.Bytes && final.LastWriteUtc == initialFinal.LastWriteUtc)
            return null;
        return final.Path;
    }

    private static string? BrowserFinalPath(string path)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        return (extension is ".crdownload" or ".opdownload" or ".part" or ".partial") && Path.GetFileNameWithoutExtension(path).Length > 0
            ? path[..^extension.Length] : null;
    }

    private static string BrowserKey(string path) => "file:" + path.ToLowerInvariant();

    private static string BrowserFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".opdownload" => "Opera",
        ".part" => "Firefox",
        _ => "Browser"
    };

    internal static string DisplayName(string partial)
    {
        string name = Path.GetFileNameWithoutExtension(partial);
        return name.Length > 0 ? name : partial;
    }
}
