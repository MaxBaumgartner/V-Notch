using System.IO;
using Microsoft.Win32;

namespace VNotch.Services;

/// <summary>
/// Hears Lenovo's power mode change (Fn+Q, Vantage, the notch's own system tab). Vantage mirrors the mode into a
/// registry value any user may read, so this needs no administrator and does not poll: Windows signals the key.
/// </summary>
internal sealed class LenovoPowerModeWatcher : IDisposable
{
    private const string LogTag = "LENOVO-KEYS";
    private const string EventKey =
        @"SOFTWARE\Lenovo\VantageService\AddinData\LenovoGamingAddin\Event\FanManagerPlugin\SmartFanModeEvent";
    private const string ModeValue = "SmartFanMode";

    private readonly object _gate = new();
    private RegistryChangeMonitor? _monitor;
    private int _last = -1;
    private bool _disposed;

    /// <summary>The raw mode (see <see cref="LegionMode"/>); raised on a thread-pool thread.</summary>
    public event Action<int>? Changed;

    public void Start() => Task.Run(() =>
    {
        lock (_gate)
        {
            if (_disposed || _monitor != null) return;
            int? mode = ReadMode();
            if (mode == null)
            {
                RuntimeLog.Log(LogTag, "No Vantage power mode in the registry; Fn+Q stays Lenovo's.");
                return;
            }
            _last = mode.Value;
            _monitor = new RegistryChangeMonitor(Registry.LocalMachine, EventKey, onChanged: OnKeyChanged);
            _monitor.ConsumeChange(); // arms the watch
        }
    });

    private void OnKeyChanged()
    {
        int mode;
        lock (_gate)
        {
            if (_disposed || _monitor == null) return;
            _monitor.ConsumeChange(); // re-arms before reading, so a change right after is not lost
            int? read = ReadMode();
            if (read == null || read.Value == _last) return;
            _last = mode = read.Value;
        }
        Changed?.Invoke(mode);
    }

    internal static int? ReadMode()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(EventKey);
            return key?.GetValue(ModeValue) switch
            {
                int value => value,
                string text when int.TryParse(text, out int parsed) => parsed,
                _ => null
            };
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _monitor?.Dispose();
            _monitor = null;
        }
    }
}
