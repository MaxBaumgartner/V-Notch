using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace VNotch.Services.ControlCenter;

internal static class SchoolClassroom
{
    internal static string? FindClone(string subject, string course, string exercise, string? root = null)
    {
        if (subject is not ("POSE" or "WMC") || SchoolFiles.ExerciseFolder(subject, exercise) == null) return null;
        root ??= SchoolFiles.SchoolRoot;
        string folder = SchoolFiles.FolderFor($"{subject} {course}", exercise, root);
        if (!Directory.Exists(folder)) return null;
        for (DirectoryInfo? dir = new(folder); dir != null; dir = dir.Parent)
        {
            if ((dir.Attributes & FileAttributes.ReparsePoint) != 0) return null;
            if (string.Equals(dir.FullName, Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)) break;
            if (dir.Parent == null) return null;
        }
        if (IsClone(folder)) return folder;
        var clones = Directory.EnumerateDirectories(folder)
            .Where(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0 && IsClone(path)).Take(2).ToArray();
        return clones.Length == 1 ? clones[0] : null;

        static bool IsClone(string path)
        {
            string git = Path.Combine(path, ".git");
            return Directory.Exists(git) && (File.GetAttributes(git) & FileAttributes.ReparsePoint) == 0;
        }
    }

    internal static async Task PushAsync(string folder, string title)
    {
        string branch = await Git(folder, "branch", "--show-current").ConfigureAwait(false);
        if (branch is not ("main" or "master"))
            throw new InvalidOperationException("Switch the clone to its main branch first.");
        await Git(folder, "remote", "get-url", "origin").ConfigureAwait(false);
        string repository = await Git(folder, "rev-parse", "--show-toplevel").ConfigureAwait(false);
        if (!Path.GetFullPath(repository).Equals(Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The selected folder is no longer the root of the clone.");

        await Git(folder, "add", "-A", "--", ".").ConfigureAwait(false);
        if ((await Git(folder, "diff", "--cached", "--name-only").ConfigureAwait(false)).Length > 0)
            await Git(folder, "commit", "-m", title).ConfigureAwait(false);
        await Git(folder, "push", "origin", branch).ConfigureAwait(false);
    }

    private static async Task<string> Git(string folder, params string[] arguments)
    {
        var info = new ProcessStartInfo("git.exe")
        {
            WorkingDirectory = folder, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        using var git = Process.Start(info) ?? throw new Win32Exception("Git could not be started.");
        var stdout = git.StandardOutput.ReadToEndAsync();
        var stderr = git.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try { await git.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            if (!git.HasExited) git.Kill(entireProcessTree: true);
            await git.WaitForExitAsync().ConfigureAwait(false);
            throw new TimeoutException("Git took too long to respond.");
        }
        if (git.ExitCode != 0)
        {
            // Remote URLs can contain credentials. Keep the local message free of command output.
            _ = await stderr.ConfigureAwait(false);
            throw new InvalidOperationException($"git {arguments[0]} exited with code {git.ExitCode}.");
        }
        return (await stdout.ConfigureAwait(false)).Trim();
    }

    internal static void OpenIntelliJ(string folder)
    {
        string? registered = Registry.ClassesRoot.OpenSubKey(@"Applications\idea64.exe\shell\open\command") is { } registry
            ? ReadExecutable(registry) : null;
        var programFiles = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "JetBrains");
        var toolbox = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JetBrains", "Toolbox", "apps");
        string? exe = registered ?? FindIntelliJ(programFiles) ?? FindIntelliJ(toolbox);
        if (exe == null) throw new FileNotFoundException("IntelliJ IDEA could not be found.");
        var info = new ProcessStartInfo(exe) { UseShellExecute = false };
        info.ArgumentList.Add(folder);
        Process.Start(info)?.Dispose();

        static string? ReadExecutable(RegistryKey key)
        {
            using (key)
            {
                if (key.GetValue(null) is not string command || !command.StartsWith('"')) return null;
                int end = command.IndexOf('"', 1);
                return end > 1 && File.Exists(command[1..end]) ? command[1..end] : null;
            }
        }

        static string? FindIntelliJ(string path) => Directory.Exists(path)
            ? Directory.EnumerateDirectories(path).Where(d => Path.GetFileName(d).StartsWith("IntelliJ", StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileName(d).StartsWith("IDEA", StringComparison.OrdinalIgnoreCase))
                .OrderDescending(StringComparer.OrdinalIgnoreCase)
                .SelectMany(d => Directory.EnumerateFiles(d, "idea64.exe", new EnumerationOptions
                {
                    RecurseSubdirectories = true, MaxRecursionDepth = 4, AttributesToSkip = FileAttributes.ReparsePoint
                })).FirstOrDefault()
            : null;
    }
}
