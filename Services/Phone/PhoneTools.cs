using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VNotch.Services.Phone;

public static class PhoneTools
{
    private static readonly string Extra = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "Escrcpy", "resources", "extra");
    private static readonly string ScrcpyDirectory = Path.Combine(Extra, "win", "scrcpy");

    public static string? Adb { get; } = Existing(Path.Combine(ScrcpyDirectory, "adb.exe"));
    public static string? Scrcpy { get; } = Existing(Path.Combine(ScrcpyDirectory, "scrcpy.exe"));
    public static string? ScrcpyServer { get; } = Existing(Path.Combine(Extra, "common", "scrcpy", "scrcpy-server"));
    public static bool Available => Adb is not null;

    private static string? Existing(string path) => File.Exists(path) ? path : null;

    public static ProcessStartInfo StartInfo(string exe, IEnumerable<string> args)
    {
        var info = new ProcessStartInfo(exe)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = ScrcpyDirectory
        };
        foreach (string arg in args) info.ArgumentList.Add(arg);
        if (ScrcpyServer is not null) info.Environment["SCRCPY_SERVER_PATH"] = ScrcpyServer;
        return info;
    }

    public static async Task<string?> RunAdbAsync(IEnumerable<string> args, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (Adb is null || ct.IsCancellationRequested) return null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(4));
        using var process = new Process { StartInfo = StartInfo(Adb, args) };
        try
        {
            if (!process.Start()) return null;
            // Cancellation must kill adb too, not just stop waiting for its output.
            using var registration = deadline.Token.Register(() => Kill(process));
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            if (deadline.IsCancellationRequested) return null;
            if (process.ExitCode == 0) return await stdout.ConfigureAwait(false);
            RuntimeLog.Warn("PHONE", "adb command failed.");
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException or OperationCanceledException)
        {
            // adb errors can contain serials, so keep diagnostics independent of its output.
            RuntimeLog.Warn("PHONE", "Could not complete adb command.");
        }
        finally
        {
            Kill(process);
        }
        return null;
    }

    internal static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            process.WaitForExit();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
    }
}
