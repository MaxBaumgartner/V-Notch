namespace VNotch.Services.Displays;

public sealed partial class Win32DisplayApi
{
    private readonly object _brightnessGate = new();
    private DdcBrightness? _brightness;
    private Win32DdcPort? _ddcPort;
    private BrightnessWatcher? _internalBrightness;
    private bool _brightnessDisposed;

    /// <summary>Returns cached brightness promptly; a first or expired read runs on a worker.</summary>
    public partial int? GetBrightness(string id)
    {
        Task<int?> read = GetBrightnessAsync(id);
        return read.IsCompletedSuccessfully ? read.Result : null;
    }

    /// <summary>Reads brightness without blocking the caller, including slow or unsupported DDC monitors.</summary>
    public Task<int?> GetBrightnessAsync(string id)
    {
        try
        {
            lock (_brightnessGate)
                return Brightness()?.GetAsync(id) ?? Task.FromResult<int?>(null);
        }
        catch (Exception) { return Task.FromResult<int?>(null); }
    }

    /// <summary>Returns the device result. Call from a worker; DDC writes can take seconds.</summary>
    public partial bool SetBrightness(string id, int percent)
    {
        try { return SetBrightnessAsync(id, percent).GetAwaiter().GetResult(); }
        catch (Exception) { return false; }
    }

    /// <summary>Returns false for failure, invalidation, or a pending value superseded by newer slider input.</summary>
    public Task<bool> SetBrightnessAsync(string id, int percent)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(id) || !List().Any(display => display.Id == id && display.Active))
                return Task.FromResult(false);
            lock (_brightnessGate)
            {
                var brightness = Brightness();
                return brightness == null || brightness.IsKnownUnsupported(id)
                    ? Task.FromResult(false) : brightness.SetAsync(id, percent);
            }
        }
        catch (Exception) { return Task.FromResult(false); }
    }

    private DdcBrightness? Brightness()
    {
        if (_brightnessDisposed) return null;
        if (_brightness != null) return _brightness;
        _internalBrightness = new BrightnessWatcher();
        _ddcPort = new Win32DdcPort(id => List().FirstOrDefault(display => display.Id == id && display.Active)?.DeviceName);
        _brightness = new DdcBrightness(new PanelBrightnessPort(this, _ddcPort, _internalBrightness), () => DateTime.UtcNow);
        _ddcPort.Invalidated += _brightness.Invalidate;
        return _brightness;
    }

    public partial void Dispose()
    {
        lock (_brightnessGate)
        {
            if (_brightnessDisposed) return;
            _brightnessDisposed = true;
            _brightness?.Dispose();
            if (_ddcPort != null)
            {
                if (_brightness != null) _ddcPort.Invalidated -= _brightness.Invalidate;
                _ddcPort.Dispose();
            }
            _internalBrightness?.Dispose();
        }
    }

    private sealed class PanelBrightnessPort(Win32DisplayApi owner, Win32DdcPort external, BrightnessWatcher panel) : IDdcPort
    {
        public int? Read(string id)
        {
            var display = owner.List().FirstOrDefault(display => display.Id == id && display.Active);
            if (display == null) return null;
            return display.Internal ? BrightnessWatcher.ReadCurrentBrightness() : external.Read(id);
        }

        public bool Write(string id, int value)
        {
            var display = owner.List().FirstOrDefault(display => display.Id == id && display.Active);
            if (display == null) return false;
            return display.Internal ? panel.TrySetBrightness(value) : external.Write(id, value);
        }
    }
}
