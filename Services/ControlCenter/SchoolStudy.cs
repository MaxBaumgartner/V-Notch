using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VNotch.Services.ControlCenter;

internal static class SchoolStudy
{
    internal const string NoteName = "notiz.md";
    internal sealed record Session(Guid Id, string Title, DateTime LastWrite);

    /// <summary>
    /// A test's study folder sits in the user's own school folder, next to the subject's handouts and exercises:
    /// Desktop\Schule\&lt;class&gt;\&lt;subject&gt;\&lt;yyyy-MM-dd title&gt; ("1POSEOO", 27.11. "OO Programmieren Übung" →
    /// Schule\3AHIF\POSE\2026-11-27 OO Programmieren Übung). The class is found like for Moodle tasks (newest class folder).
    /// </summary>
    internal static string FolderFor(string subject, DateTime date, string title, string? root = null)
    {
        foreach (char bad in Path.GetInvalidFileNameChars()) title = title.Replace(bad, '_');
        return Path.Combine(SchoolFiles.FolderFor(SubjectFolder(subject), null, root),
            date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " " + SchoolFiles.SafeName(title, "Test"));
    }

    /// <summary>
    /// The folder an Untis subject already has in Schule\&lt;class&gt;: "1POSEOO"/"0POSETHI" → POSE, "0BWMRW" → REWE,
    /// "0BWMBO" → BO, "0GGPGW" → GW, Religion → REL; everything else by its chip name (DBI, WMC, E, D …).
    /// </summary>
    internal static string SubjectFolder(string untis)
    {
        string word = new((untis ?? "").Trim().ToUpperInvariant().SkipWhile(char.IsDigit).TakeWhile(char.IsLetter).ToArray());
        if (FolderAliases.TryGetValue(word, out var alias)) return alias;
        string chip = PiDashboardClient.UntisSubject(untis ?? "");
        return FolderAliases.TryGetValue(chip, out alias) ? alias : chip;
    }

    private static readonly IReadOnlyDictionary<string, string> FolderAliases = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["BWMRW"] = "REWE", ["BWMBO"] = "BO", ["GGPGW"] = "GW", ["RK"] = "REL"
    };

    internal static IReadOnlyList<string> Files(string folder) =>
        !Directory.Exists(folder) ? [] : Directory.EnumerateFiles(folder)
            .Where(path => !Path.GetFileName(path).Equals(NoteName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase).ToArray();

    internal static void CopyFiles(string folder, IEnumerable<string> paths)
    {
        Directory.CreateDirectory(folder);
        foreach (string source in paths.Where(File.Exists))
        {
            if (string.Equals(Path.GetDirectoryName(source), folder, StringComparison.OrdinalIgnoreCase)) continue;
            string name = Path.GetFileName(source);
            string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
            string destination = Path.Combine(folder, name);
            for (int i = 2; File.Exists(destination) || Directory.Exists(destination)
                || Path.GetFileName(destination).Equals(NoteName, StringComparison.OrdinalIgnoreCase); i++)
                destination = Path.Combine(folder, $"{stem} ({i}){ext}");
            File.Copy(source, destination, overwrite: false);
        }
    }

    internal static string ReadNote(string folder) =>
        File.Exists(Path.Combine(folder, NoteName)) ? File.ReadAllText(Path.Combine(folder, NoteName)) : "";

    internal static void SaveNote(string folder, string note)
    {
        Directory.CreateDirectory(folder);
        string draft = Path.Combine(folder, $".{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(draft, note);
            File.Move(draft, Path.Combine(folder, NoteName), overwrite: true);
        }
        finally { File.Delete(draft); }
    }

    internal static string ProjectKey(string folder) => Regex.Replace(folder, "[^a-zA-Z0-9]", "-");

    internal static IReadOnlyList<Session> Sessions(string folder, string? projects = null)
    {
        projects ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
        string project = Path.Combine(projects, ProjectKey(folder));
        if (!Directory.Exists(project)) return [];
        var sessions = new List<Session>();
        foreach (string path in Directory.EnumerateFiles(project, "*.jsonl"))
        {
            if (!Guid.TryParse(Path.GetFileNameWithoutExtension(path), out var id)) continue;
            var info = new FileInfo(path);
            try { sessions.Add(new Session(id, SessionTitle(path) ?? info.LastWriteTime.ToString("g"), info.LastWriteTime)); }
            catch (IOException) { }
        }
        return sessions.OrderByDescending(s => s.LastWrite).ToArray();
    }

    private static string? SessionTitle(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(file);
        string? prompt = null;
        string? title = null;
        for (int i = 0; i < 64 && reader.ReadLine() is { } line; i++)
        {
            try
            {
                using var entry = JsonDocument.Parse(line);
                var element = entry.RootElement;
                if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) continue;
                if (type.ValueEquals("custom-title") && element.TryGetProperty("customTitle", out var custom) && custom.ValueKind == JsonValueKind.String)
                    title = custom.GetString();
                if (prompt == null && type.ValueEquals("user")
                    && element.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object
                    && msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                    prompt = content.GetString()?.Split('\n')[0].Trim();
            }
            catch (JsonException) { }
        }
        return title ?? prompt;
    }

    internal static void OpenClaude(string folder, Guid? session = null)
    {
        Directory.CreateDirectory(folder);
        var process = new ProcessStartInfo("wt.exe") { WorkingDirectory = folder, UseShellExecute = false };
        foreach (string arg in new[] { "-w", "new", "new-tab", "-d", ".", "cmd.exe", "/d", "/c", "claude" })
            process.ArgumentList.Add(arg);
        if (session is { } id)
        {
            process.ArgumentList.Add("--resume");
            process.ArgumentList.Add(id.ToString());
        }
        Process.Start(process)?.Dispose();
    }
}
