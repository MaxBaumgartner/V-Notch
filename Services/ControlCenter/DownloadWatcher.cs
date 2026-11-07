using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace VNotch.Services.ControlCenter;

/// <summary>A download in progress; <paramref name="Percent"/> is null where the source does not say how far it is.</summary>
internal sealed record DownloadInfo(string Key, string Source, string Name, double? Percent, double BytesPerSecond);

/// <summary>
/// Finds downloads on this PC without asking any app: Steam's app manifests (bytes done of bytes to do), the Riot
/// Client's log (its patcher reports a percentage per game), half-finished browser files in the Downloads folder
/// (Chrome, Edge, Brave, Opera, Firefox: size and speed, no total) and, for launchers that keep their progress to
/// themselves (Epic, EA, Ubisoft, Battle.net, GOG), how fast they write to disk. Each poll is a handful of
/// directory listings and small file reads; nothing runs between polls.
/// </summary>
internal sealed class DownloadWatcher
{
    private const double LauncherMinBytesPerSecond = 1.5 * 1024 * 1024;
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(12);

    private readonly Dictionary<string, (DateTime AtUtc, long Bytes)> _last = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _lastGrowthUtc = new(StringComparer.Ordinal);
    private readonly Func<DateTime> _now;

    public DownloadWatcher(Func<DateTime>? now = null) => _now = now ?? (() => DateTime.UtcNow);

    public List<DownloadInfo> Poll()
    {
        var found = new List<DownloadInfo>();
        // One look at the running processes for every source that needs it.
        var processes = Process.GetProcesses();
        try
        {
            var names = new HashSet<string>(processes.Select(p => p.ProcessName), StringComparer.OrdinalIgnoreCase);
            Try(() => Steam(found));
            if (names.Contains("RiotClientServices")) Try(() => Riot(found));
            Try(() => Browsers(found));
            Try(() => Launchers(found, processes));
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
        // Forget what is gone, so a new download of the same file starts from zero.
        var keys = new HashSet<string>(found.Select(d => d.Key), StringComparer.Ordinal);
        foreach (var key in _last.Keys.Where(k => !keys.Contains(k) && !k.StartsWith("io:", StringComparison.Ordinal)).ToList())
        {
            _last.Remove(key);
            _lastGrowthUtc.Remove(key);
        }
        return found;

        static void Try(Action read)
        {
            try { read(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // One source failing must not hide the others.
            }
        }
    }

    /// <summary>Bytes per second since the last poll, and whether the count grew lately (a paused download stops growing).</summary>
    private (double Rate, bool Growing) Track(string key, long bytes)
    {
        var now = _now();
        double rate = 0;
        bool seen = _last.TryGetValue(key, out var before);
        if (seen && now > before.AtUtc)
            rate = Math.Max(0, (bytes - before.Bytes) / (now - before.AtUtc).TotalSeconds);
        // Only growth counts: a leftover file of a cancelled download, seen for the first time, is not a download.
        if (seen && bytes > before.Bytes) _lastGrowthUtc[key] = now;
        _last[key] = (now, bytes);
        return (rate, _lastGrowthUtc.TryGetValue(key, out var grew) && now - grew <= StaleAfter);
    }

    #region Steam

    private void Steam(List<DownloadInfo> found)
    {
        if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is not string steam || !Directory.Exists(steam)) return;
        foreach (string library in SteamLibraries(steam))
        {
            string apps = Path.Combine(library, "steamapps");
            string downloading = Path.Combine(apps, "downloading");
            if (!Directory.Exists(downloading)) continue;
            foreach (string dir in Directory.EnumerateDirectories(downloading))
            {
                string id = Path.GetFileName(dir);
                string manifest = Path.Combine(apps, $"appmanifest_{id}.acf");
                if (!File.Exists(manifest)) continue;
                var fields = ParseAcf(File.ReadAllText(manifest));
                long done = Long(fields, "BytesDownloaded"), total = Long(fields, "BytesToDownload");
                if (total <= 0 || done >= total) continue;
                var (rate, growing) = Track("steam:" + id, done);
                if (!growing) continue; // paused or queued
                found.Add(new DownloadInfo("steam:" + id, "Steam", fields.GetValueOrDefault("name", id), 100.0 * done / total, rate));
            }
        }
    }

    internal static IEnumerable<string> SteamLibraries(string steam)
    {
        yield return steam;
        string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) yield break;
        foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
        {
            string path = m.Groups[1].Value.Replace(@"\\", @"\");
            if (!string.Equals(Path.GetFullPath(path).TrimEnd('\\'), Path.GetFullPath(steam).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                yield return path;
        }
    }

    /// <summary>The flat key/value pairs of a Steam .acf file (nested blocks are read through, keys stay as they are).</summary>
    internal static Dictionary<string, string> ParseAcf(string text)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(text, "\"([^\"]+)\"\\s+\"([^\"]*)\""))
            fields.TryAdd(m.Groups[1].Value, m.Groups[2].Value);
        return fields;
    }

    private static long Long(Dictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out var v) && long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n) ? n : 0;

    #endregion

    #region Riot

    private static readonly Regex RiotStatus = new(
        @"HandleProductPatchlineStatusUpdate: productId: (?<product>[\w-]+), patchlineId: (?<line>[\w-]+), status: (?<status>\w+), progress: (?<progress>\d+(\.\d+)?)",
        RegexOptions.Compiled);

    private static readonly Dictionary<string, string> RiotNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["valorant"] = "VALORANT",
        ["league_of_legends"] = "League of Legends",
        ["league_of_legends_game"] = "League of Legends",
        ["teamfighttactics"] = "Teamfight Tactics",
        ["bacon"] = "Legends of Runeterra",
        ["lion"] = "2XKO",
        ["riot_client"] = "Riot Client"
    };

    private void Riot(List<DownloadInfo> found)
    {
        string logs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Riot Games", "Riot Client", "Logs", "Riot Client Logs");
        if (!Directory.Exists(logs)) return;
        var newest = new DirectoryInfo(logs).EnumerateFiles("*.log").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
        if (newest == null || DateTime.UtcNow - newest.LastWriteTimeUtc > TimeSpan.FromMinutes(5)) return;
        foreach (var (product, progress) in RiotPatching(Tail(newest.FullName, 96 * 1024)))
        {
            string key = "riot:" + product;
            Track(key, (long)(progress * 100));
            found.Add(new DownloadInfo(key, "Riot", RiotNames.GetValueOrDefault(product, product), progress, 0));
        }
    }

    /// <summary>Products whose latest status line says they are being patched, with their percentage.</summary>
    internal static List<(string Product, double Progress)> RiotPatching(string log)
    {
        var latest = new Dictionary<string, (string Status, double Progress)>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in RiotStatus.Matches(log))
        {
            if (m.Groups["line"].Value != "live") continue;
            latest[m.Groups["product"].Value] = (m.Groups["status"].Value,
                double.Parse(m.Groups["progress"].Value, CultureInfo.InvariantCulture));
        }
        var patching = new List<(string, double)>();
        foreach (var (product, (status, progress)) in latest)
        {
            if (status is "UpToDate" or "NotInstalled" or "Unknown" or "Invalid") continue;
            if (progress >= 100) continue;
            patching.Add((product, Math.Clamp(progress, 0, 100)));
        }
        return patching;
    }

    private static string Tail(string path, int bytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long start = Math.Max(0, stream.Length - bytes);
        stream.Seek(start, SeekOrigin.Begin);
        var buffer = new byte[stream.Length - start];
        int read = stream.Read(buffer, 0, buffer.Length);
        return Encoding.UTF8.GetString(buffer, 0, read);
    }

    #endregion

    #region Browsers

    // Chrome, Edge and Brave write ".crdownload", Opera ".opdownload", Firefox ".part"; Firefox also keeps an empty placeholder under the final name.
    private static readonly string[] PartialExtensions = [".crdownload", ".opdownload", ".part", ".partial"];

    private void Browsers(List<DownloadInfo> found)
    {
        string? folder = DownloadsFolder();
        if (folder == null || !Directory.Exists(folder)) return;
        foreach (var file in new DirectoryInfo(folder).EnumerateFiles())
        {
            string ext = file.Extension.ToLowerInvariant();
            if (Array.IndexOf(PartialExtensions, ext) < 0) continue;
            string key = "file:" + file.FullName.ToLowerInvariant();
            var (rate, growing) = Track(key, file.Length);
            if (!growing) continue; // an old leftover of a cancelled download
            found.Add(new DownloadInfo(key, BrowserFor(ext), DisplayName(file.Name), null, rate));
        }
    }

    private static string BrowserFor(string extension) => extension switch
    {
        ".opdownload" => "Opera",
        ".part" => "Firefox",
        _ => "Browser"
    };

    /// <summary>"setup.exe.crdownload" → "setup.exe"; Chrome's "Unconfirmed 123456.crdownload" stays as it is.</summary>
    internal static string DisplayName(string partial)
    {
        string name = Path.GetFileNameWithoutExtension(partial);
        return name.Length > 0 ? name : partial;
    }

    /// <summary>The user's Downloads folder, wherever it was moved to.</summary>
    private static string? DownloadsFolder()
    {
        if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out IntPtr path) == 0)
        {
            try { return Marshal.PtrToStringUni(path); }
            finally { Marshal.FreeCoTaskMem(path); }
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

    #endregion

    #region Launchers without progress

    private static readonly (string Process, string Name)[] LauncherProcesses =
    [
        ("EpicGamesLauncher", "Epic Games"),
        ("EpicWebHelper", "Epic Games"),
        ("EADesktop", "EA app"),
        ("upc", "Ubisoft Connect"),
        ("UbisoftConnect", "Ubisoft Connect"),
        ("Agent", "Battle.net"),
        ("GalaxyClient", "GOG Galaxy"),
        ("GalaxyClientService", "GOG Galaxy")
    ];

    private void Launchers(List<DownloadInfo> found, Process[] processes)
    {
        var byName = new Dictionary<string, double>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var process in processes)
        {
            string? name = null;
            foreach (var (processName, launcher) in LauncherProcesses)
                if (string.Equals(process.ProcessName, processName, StringComparison.OrdinalIgnoreCase)) name = launcher;
            if (name == null) continue;
            try
            {
                if (!GetProcessIoCounters(process.Handle, out var io)) continue;
                string key = $"io:{process.Id}";
                seen.Add(key);
                var (rate, _) = Track(key, (long)io.WriteTransferCount);
                byName[name] = byName.GetValueOrDefault(name) + rate;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // A launcher part that runs as a service: not ours to read.
            }
        }
        foreach (var key in _last.Keys.Where(k => k.StartsWith("io:", StringComparison.Ordinal) && !seen.Contains(k)).ToList())
        {
            _last.Remove(key);
            _lastGrowthUtc.Remove(key);
        }
        foreach (var (name, rate) in byName)
            if (rate >= LauncherMinBytesPerSecond) found.Add(new DownloadInfo("launcher:" + name, name, name, null, rate));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);

    #endregion
}
