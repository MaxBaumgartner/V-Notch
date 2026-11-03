using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace VNotch.Services;

/// <summary>Lenovo's power modes as GetSmartFanMode reports them (Fn+Q cycles through them).</summary>
public enum LegionMode { Quiet = 1, Balanced = 2, Performance = 3, Extreme = 224, Custom = 255 }

public sealed record LegionState(int Mode, bool WinKeyLocked, bool TouchpadLocked, int CpuTemp, int GpuTemp);

/// <summary>
/// Talks to Assets/LegionHelper.ps1, which LegionSetup.ps1 installs once (UAC) as an elevated logon
/// task, because Lenovo's GameZone WMI interface only answers administrators and the notch runs
/// without elevation. One request per pipe connection.
/// </summary>
public static class LegionService
{
    private const string PipeName = "vnotch-legion";
    // Bump together with "version" in LegionHelper.ps1: an older installed copy then counts as missing.
    internal const int HelperVersion = 2;
    private const string LogCategory = "LEGION";
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(4);

    /// <summary>A Lenovo Legion, read from the BIOS strings Windows keeps (no WMI, no admin).</summary>
    public static bool IsSupported { get; } = DetectLegion();

    public static Task<LegionState?> GetStateAsync(CancellationToken token = default) => SendAsync("state", token);

    public static Task<LegionState?> SetModeAsync(LegionMode mode, CancellationToken token = default) =>
        SendAsync($"mode {(int)mode}", token);

    public static Task<LegionState?> SetWinKeyLockedAsync(bool locked, CancellationToken token = default) =>
        SendAsync($"winkey {(locked ? 1 : 0)}", token);

    public static Task<LegionState?> SetTouchpadLockedAsync(bool locked, CancellationToken token = default) =>
        SendAsync($"touchpad {(locked ? 1 : 0)}", token);

    /// <summary>Runs the one-time setup elevated; false when the UAC prompt was declined or it failed.</summary>
    public static async Task<bool> RunSetupAsync()
    {
        string script = Path.Combine(AppContext.BaseDirectory, "Assets", "LegionSetup.ps1");
        string? sid = WindowsIdentity.GetCurrent().User?.Value;
        if (!File.Exists(script) || sid == null) return false;
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\" -User {sid}",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            });
            if (process == null) return false;
            await process.WaitForExitAsync().ConfigureAwait(false);
            RuntimeLog.Log(LogCategory, $"Setup exited with {process.ExitCode}.");
            return process.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            return false; // UAC declined
        }
    }

    private static async Task<LegionState?> SendAsync(string request, CancellationToken token)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync((int)ConnectTimeout.TotalMilliseconds, token).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(AnswerTimeout);
            byte[] line = Encoding.UTF8.GetBytes(request + "\n");
            await pipe.WriteAsync(line, timeout.Token).ConfigureAwait(false);
            await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
            using var reader = new StreamReader(pipe, Encoding.UTF8);
            string? answer = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            return answer == null ? null : ParseState(answer);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            if (token.IsCancellationRequested) throw;
            RuntimeLog.Debug(LogCategory, () => $"{request}: {ex.GetType().Name}");
            return null; // helper not installed or not running
        }
    }

    /// <summary>The helper's JSON answer; null for an error answer or an outdated helper.</summary>
    internal static LegionState? ParseState(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("mode", out _)) return null;
            int Read(string name) =>
                root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
                value.TryGetInt32(out int number) ? number : -1;
            if (Read("version") < HelperVersion) return null;
            return new LegionState(Read("mode"), Read("winKey") == 1, Read("touchpad") == 1, Read("cpuTemp"), Read("gpuTemp"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool DetectLegion()
    {
        using var bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        return bios?.GetValue("SystemManufacturer") is string maker && maker.Contains("LENOVO", StringComparison.OrdinalIgnoreCase) &&
            bios.GetValue("SystemFamily") is string family && family.Contains("Legion", StringComparison.OrdinalIgnoreCase);
    }
}
