using VNotch.Services;

namespace VNotch;

/// <summary>
/// The notch window is see-through by DWM composition rather than by WPF's layered window, with clicks falling
/// through wherever nothing visible is under the cursor (see <see cref="DwmTransparentWindow"/>).
/// </summary>
public partial class MainWindow
{
    private static readonly System.DateTime TransparencyChosenUtc = System.DateTime.UtcNow;
    private bool _transparencyRestartAsked;

    /// <summary>Must run before the window gets its handle (right after InitializeComponent).</summary>
    private void InitializeTransparency(ISettingsService settingsService)
    {
        string? screen = null;
        try { screen = MonitorSelection.Resolve(settingsService.Load()).DeviceName; }
        catch (System.Exception ex) { RuntimeLog.Warn("TRANSPARENCY", $"Screen unknown at start: {ex.Message}"); }
        DwmTransparentWindow.Apply(this, screen);
        RuntimeLog.Info("TRANSPARENCY", $"{(DwmTransparentWindow.ActiveLayered == true ? "Layered" : "DWM")} window on {screen ?? "?"} ({DisplayAdapters.AdapterOf(screen) ?? "adapter unknown"})");
    }

    /// <summary>
    /// The window kind cannot change once the window exists, so when the notch lands on a screen that needs the other
    /// kind (the external monitors were unplugged and only the laptop panel is left, or the other way round), the app
    /// starts itself again. Only once per run, and not in the first seconds, so a flapping display change cannot loop.
    /// </summary>
    private void RestartIfTransparencyNoLongerFits()
    {
        if (_transparencyRestartAsked || DwmTransparentWindow.ActiveLayered is not { } layered) return;
        if (System.DateTime.UtcNow - TransparencyChosenUtc < System.TimeSpan.FromSeconds(20)) return;
        string screen = MonitorSelection.Resolve(_settings).DeviceName;
        if (DwmTransparentWindow.UseLayeredFallback(screen) == layered) return;
        _transparencyRestartAsked = true;
        RuntimeLog.Warn("TRANSPARENCY", $"{screen} needs the {(layered ? "DWM" : "layered")} window now; restarting");
        App.RestartApplication();
    }

    // Also used by the OSD hiders (MainWindow.WindowsOsd, MainWindow.LenovoOsd) for their empty regions.
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern System.IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool DeleteObject(System.IntPtr handle);
}
