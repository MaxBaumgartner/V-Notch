using System.IO;
using Microsoft.Win32;
using VNotch.Models;

namespace VNotch.Services.Spotlight;

/// <summary>What an empty Spotlight shows when it stands in for the Start menu: pinned apps first, then the ones used lately.</summary>
internal static class SpotlightHome
{
    internal const int MaxApps = 14;

    internal static string TaskbarPinsFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");

    /// <summary>Shortcuts pinned to the Windows taskbar, in folder order.</summary>
    internal static IReadOnlyList<SpotlightSearchItem> TaskbarPins()
    {
        try
        {
            string folder = TaskbarPinsFolder;
            if (!Directory.Exists(folder)) return Array.Empty<SpotlightSearchItem>();
            return Directory.EnumerateFiles(folder, "*.lnk")
                .Select(path => new SpotlightSearchItem($"app:{path}", SpotlightResultKind.Application,
                    Path.GetFileNameWithoutExtension(path), Loc.Get("spotlight.kind.application"), path, path))
                .ToArray();
        }
        catch (Exception ex)
        {
            RuntimeLog.Log("SPOTLIGHT-HOME", $"Taskbar pins unreadable: {ex.Message}");
            return Array.Empty<SpotlightSearchItem>();
        }
    }

    // Windows counts every program started from Start, the taskbar or Explorer here (ROT13-encoded names).
    private const string UserAssistShortcuts =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist\{F4E57C4B-2036-45F0-A9AB-443BCFE33D9F}\Count";

    /// <summary>Start menu and taskbar shortcuts Windows saw launched, most recent first.</summary>
    internal static IReadOnlyList<SpotlightSearchItem> RecentlyLaunchedShortcuts(int max)
    {
        var found = new List<(string Path, long Last)>();
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UserAssistShortcuts);
            if (key == null) return Array.Empty<SpotlightSearchItem>();
            foreach (string encoded in key.GetValueNames())
            {
                if (key.GetValue(encoded) is not byte[] data || data.Length < 68) continue;
                long last = BitConverter.ToInt64(data, 60);
                if (last <= 0) continue;
                string name = Rot13(encoded);
                if (!name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) continue;
                string path = TrayIconBridge.ResolveKnownFolderPath(name);
                if (path == name || !File.Exists(path)) continue;
                found.Add((path, last));
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Log("SPOTLIGHT-HOME", $"Recent programs unreadable: {ex.Message}");
        }
        return found.OrderByDescending(f => f.Last).Take(max)
            .Select(f => new SpotlightSearchItem($"app:{f.Path}", SpotlightResultKind.Application,
                Path.GetFileNameWithoutExtension(f.Path), Loc.Get("spotlight.kind.application"), f.Path, f.Path)
            { IsRecent = true })
            .ToArray();
    }

    internal static string Rot13(string text) => string.Create(text.Length, text, static (span, source) =>
    {
        for (int i = 0; i < source.Length; i++)
        {
            char c = source[i];
            span[i] = c switch
            {
                >= 'a' and <= 'z' => (char)('a' + (c - 'a' + 13) % 26),
                >= 'A' and <= 'Z' => (char)('A' + (c - 'A' + 13) % 26),
                _ => c,
            };
        }
    });

    /// <summary>
    /// Spotlight's own pins, then taskbar pins, then recently launched apps; each app once (by title, since a
    /// taskbar shortcut and a Start menu shortcut for the same app have different paths).
    /// </summary>
    internal static IReadOnlyList<SpotlightSearchItem> BuildApps(
        IEnumerable<SpotlightSearchItem> spotlightPins,
        IEnumerable<SpotlightSearchItem> taskbarPins,
        IEnumerable<SpotlightSearchItem> recent,
        int max = MaxApps)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var apps = new List<SpotlightSearchItem>(max);
        foreach (var item in spotlightPins.Concat(taskbarPins).Concat(recent.Where(r => r.Kind == SpotlightResultKind.Application)))
        {
            if (apps.Count >= max) break;
            if (string.IsNullOrWhiteSpace(item.Title) || string.IsNullOrWhiteSpace(item.Target)) continue;
            if (!seen.Add(item.Title.Trim()) | !seen.Add("target:" + item.Target)) continue;
            apps.Add(item);
        }
        return apps;
    }

    /// <summary>V-Notch's own icon and empty status entries do not belong in the background row.</summary>
    internal static bool ShowInTrayRow(string name) =>
        name.Length > 0 && !name.Equals("V-Notch", StringComparison.OrdinalIgnoreCase);
}
