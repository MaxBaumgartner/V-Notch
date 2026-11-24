using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace VNotch.Services;

internal enum QuickLookCommand { Toggle, Switch, Close }

internal sealed partial class QuickLookBridge
{
    internal sealed record PreviewFile(string Path, Func<Task>? Release = null, Func<bool>? CanOpen = null);
    private sealed record Preview(object Owner, object Key, PreviewFile File);
    private sealed record Availability(string? Executable, bool HasPipe, DateTime CheckedAt)
    {
        public bool Available => HasPipe || Executable != null;
    }

    private const string PipePrefix = "QuickLook.App.Pipe.";
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RecheckInterval = TimeSpan.FromMinutes(3);
    private readonly SemaphoreSlim _commands = new(1);
    private readonly Func<string?> _findExecutable;
    private readonly Func<bool> _pipeExists;
    private readonly Func<string, TimeSpan, Task<bool>> _send;
    private readonly Action<string> _start;
    private readonly Func<DateTime> _utcNow;
    private readonly object _availabilityGate = new();
    private volatile Availability? _availability;
    private Task<bool>? _availabilityCheck;
    private volatile Preview? _preview;
    private volatile bool _enabled = true;
    private DateTime? _lastStart;

    internal static QuickLookBridge Shared { get; } = new(FindExecutable, PipeExists, SendAsync, Start, () => DateTime.UtcNow);
    private static readonly Timer AvailabilityRefresh = new(state => _ = Shared.RefreshAvailabilityAsync(),
        null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    internal event Action? Changed;
    internal bool IsAvailable => _availability?.Available == true;
    internal bool IsEnabled => _enabled;
    internal bool IsPreviewing(object owner) => ReferenceEquals(_preview?.Owner, owner);
    internal bool HasPreviewKey(object key, object? owner) => _preview is { } active &&
        (owner == null || ReferenceEquals(active.Owner, owner)) && Equals(active.Key, key);

    internal QuickLookBridge(Func<string?> findExecutable, Func<bool> pipeExists, Func<string, TimeSpan, Task<bool>> send,
        Action<string> start, Func<DateTime> utcNow)
    {
        _findExecutable = findExecutable;
        _pipeExists = pipeExists;
        _send = send;
        _start = start;
        _utcNow = utcNow;
    }

    internal void SetEnabled(bool enabled)
    {
        if (_enabled == enabled) return;
        _enabled = enabled;
        if (!enabled) _ = CloseCoreAsync(null);
        Changed?.Invoke();
    }

    internal Task<bool> RefreshAvailabilityAsync()
    {
        lock (_availabilityGate)
        {
            if (_availability is { } cached && _utcNow() - cached.CheckedAt < RecheckInterval)
                return Task.FromResult(cached.Available);
            if (_availabilityCheck is { IsCompleted: false }) return _availabilityCheck;
            return _availabilityCheck = Task.Run(() =>
            {
                var found = new Availability(_findExecutable(), _pipeExists(), _utcNow());
                bool wasAvailable = IsAvailable;
                _availability = found;
                if (wasAvailable != found.Available) Changed?.Invoke();
                return found.Available;
            });
        }
    }

    internal Task<bool> ToggleAsync(object owner, string path) =>
        ToggleAsync(owner, path, () => Task.FromResult<PreviewFile?>(new(path)));

    internal async Task<bool> ToggleAsync(object owner, object key, Func<Task<PreviewFile?>> resolve)
    {
        await _commands.WaitAsync();
        try
        {
            if (!_enabled || !await RefreshAvailabilityAsync()) return false;
            if (_preview is { } current && ReferenceEquals(current.Owner, owner) && Equals(current.Key, key))
            {
                if (!await SendCommandAsync(QuickLookCommand.Toggle, current.File.Path, allowStart: false))
                    await SendCommandAsync(QuickLookCommand.Close, null, allowStart: false);
                await SetPreviewAsync(null);
                return false;
            }

            var file = await resolve();
            if (file == null) return IsPreviewing(owner);
            bool opened = false;
            try
            {
                if (_enabled && await IsPreviewFileAsync(file.Path) && file.CanOpen?.Invoke() != false)
                    opened = await SendCommandAsync(QuickLookCommand.Toggle, file.Path, allowStart: true);
                if (opened) await SetPreviewAsync(new(owner, key, file));
                return IsPreviewing(owner);
            }
            finally
            {
                if (!opened && file.Release != null) await file.Release();
            }
        }
        finally { _commands.Release(); }
    }

    internal async Task<bool> SwitchAsync(object owner, string path)
    {
        await _commands.WaitAsync();
        try
        {
            if (!_enabled || _preview is not { } previous || !ReferenceEquals(previous.Owner, owner)) return false;
            if (string.Equals(previous.File.Path, path, StringComparison.OrdinalIgnoreCase)) return true;
            if (!await IsPreviewFileAsync(path)) return true;
            if (await SendCommandAsync(QuickLookCommand.Switch, path, allowStart: false))
                await SetPreviewAsync(new(owner, path, new(path)));
            else
                await SetPreviewAsync(null);
            return IsPreviewing(owner);
        }
        finally { _commands.Release(); }
    }

    internal Task CloseAsync(object owner) => CloseCoreAsync(owner);

    private async Task CloseCoreAsync(object? owner)
    {
        await _commands.WaitAsync();
        try
        {
            if (_preview is not { } current || (owner != null && !ReferenceEquals(current.Owner, owner))) return;
            await SendCommandAsync(QuickLookCommand.Close, null, allowStart: false);
            await SetPreviewAsync(null);
        }
        finally { _commands.Release(); }
    }

    private async Task SetPreviewAsync(Preview? preview)
    {
        var previous = _preview;
        _preview = preview;
        Changed?.Invoke();
        if (previous?.File.Release is { } release) await release();
    }

    internal static bool IsPreviewPath(string? path) =>
        !string.IsNullOrEmpty(path) && Path.IsPathFullyQualified(path) && path.IndexOfAny(['|', '\r', '\n', '\0']) < 0;

    private static Task<bool> IsPreviewFileAsync(string path) => Task.Run(() => IsPreviewPath(path) && File.Exists(path));

    internal static string FormatMessage(QuickLookCommand command, string? path = null) =>
        $"QuickLook.App.PipeMessages.{command}|{(command == QuickLookCommand.Close ? null : path)}|";

    private async Task<bool> SendCommandAsync(QuickLookCommand command, string? path, bool allowStart)
    {
        string message = FormatMessage(command, path);
        if (await _send(message, ConnectTimeout)) return true;
        if (!allowStart || _availability?.Executable is not { } exe) return false;
        if (await Task.Run(_pipeExists)) return false;

        try
        {
            if (_lastStart is not { } last || _utcNow() - last >= RecheckInterval)
            {
                _lastStart = _utcNow();
                await Task.Run(() => _start(exe));
            }
            return await _send(message, StartupTimeout);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }

    private static void Start(string exe)
    {
        using var process = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true });
    }

    private static readonly Lazy<string?> PipeName = new(() =>
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User is { } sid ? PipePrefix + sid.Value : null;
    });

    private static bool PipeExists() => PipeName.Value is { } name &&
        (WaitNamedPipe(@"\\.\pipe\" + name, 1) || Marshal.GetLastPInvokeError() == 121);

    [LibraryImport("kernel32.dll", EntryPoint = "WaitNamedPipeW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WaitNamedPipe(string name, uint milliseconds);

    private static async Task<bool> SendAsync(string message, TimeSpan timeout)
    {
        if (PipeName.Value is not { } name) return false;
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            using var pipe = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(cancellation.Token).ConfigureAwait(false);
            await pipe.WriteAsync(Encoding.UTF8.GetBytes(message + "\n"), cancellation.Token).ConfigureAwait(false);
            await pipe.FlushAsync(cancellation.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or TimeoutException)
        {
            return false;
        }
    }

    private static string? FindExecutable()
    {
        foreach (var candidate in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "QuickLook", "QuickLook.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "QuickLook", "QuickLook.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "QuickLook", "QuickLook.exe")
        })
            if (File.Exists(candidate)) return candidate;

        try
        {
            using var appPath = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\QuickLook.exe");
            if (ExistingExecutable(appPath?.GetValue(null) as string) is { } portable) return portable;

            using var uninstall = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
            foreach (var key in uninstall?.GetSubKeyNames() ?? [])
            {
                using var entry = uninstall!.OpenSubKey(key);
                if (entry?.GetValue("DisplayName") is not string name ||
                    !(name.Equals("QuickLook", StringComparison.OrdinalIgnoreCase) || name.StartsWith("QuickLook ", StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (entry.GetValue("InstallLocation") is string location &&
                    ExistingExecutable(Path.Combine(location.Trim().Trim('"'), "QuickLook.exe")) is { } installed)
                    return installed;
                if (ExistingExecutable((entry.GetValue("DisplayIcon") as string)?.Split(',')[0]) is { } iconPath)
                    return iconPath;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException) { }
        return null;
    }

    private static string? ExistingExecutable(string? candidate)
    {
        string path = candidate?.Trim().Trim('"') ?? "";
        return IsPreviewPath(path) && Path.GetFileName(path).Equals("QuickLook.exe", StringComparison.OrdinalIgnoreCase)
            && File.Exists(path) ? path : null;
    }
}
