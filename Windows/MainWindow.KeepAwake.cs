using System.Linq;
using VNotch.Services;
using VNotch.Services.ClaudeCode;

namespace VNotch;

/// <summary>
/// Keeps the PC from falling asleep while something is still going on: Claude Code working, a download running, a
/// timer counting down (or always, or never). The system tab's "System" header shows the mode and, when it holds the
/// PC awake, why; tapping it steps through off, automatic and always.
/// </summary>
public partial class MainWindow
{
    private KeepAwakeService? _keepAwake;
    private KeepAwakeReason _keepAwakeReason = KeepAwakeReason.None;

    private void InitializeKeepAwake()
    {
        _keepAwake = new KeepAwakeService();
        ControlCenter.AttachKeepAwake(KeepAwakeLabel, CycleKeepAwake);
        UpdateKeepAwake();
    }

    private void DisposeKeepAwake() => _keepAwake?.Dispose();

    /// <summary>Called whenever what is live changes (Claude, downloads, the timer) and when the battery reports.</summary>
    private void UpdateKeepAwake()
    {
        if (_keepAwake == null || _settings == null) return;
        bool claudeWorking = _claudeLiveSessions.Any(s => s.Status == ClaudeSessionStatus.Working);
        var battery = _chargeBattery;
        bool onBattery = battery is { HasBattery: true } && !battery.IsPowerConnected;
        var reason = KeepAwakePolicy.Decide(_settings.KeepAwake, claudeWorking, _downloads.Count > 0, IsCountdownLive,
            onBattery, battery?.Percentage ?? 100);
        _keepAwake.Set(reason != KeepAwakeReason.None);
        if (reason == _keepAwakeReason) return;
        _keepAwakeReason = reason;
        ControlCenter.RefreshKeepAwake();
    }

    private string KeepAwakeLabel()
    {
        if (_settings == null) return "";
        return _keepAwakeReason switch
        {
            KeepAwakeReason.Claude => Loc.Get("control.keepAwake.claude"),
            KeepAwakeReason.Download => Loc.Get("control.keepAwake.download"),
            KeepAwakeReason.Timer => Loc.Get("control.keepAwake.timer"),
            _ => _settings.KeepAwake switch
            {
                KeepAwakeMode.Off => Loc.Get("control.keepAwake.off"),
                KeepAwakeMode.Always => Loc.Get("control.keepAwake.always"),
                _ => Loc.Get("control.keepAwake.auto")
            }
        };
    }

    private void CycleKeepAwake()
    {
        if (_settings == null) return;
        _settings.KeepAwake = _settings.KeepAwake switch
        {
            KeepAwakeMode.Off => KeepAwakeMode.Automatic,
            KeepAwakeMode.Automatic => KeepAwakeMode.Always,
            _ => KeepAwakeMode.Off
        };
        _settingsService.SaveAsync(_settings).SafeFireAndForget("SETTINGS-SAVE");
        // A new mode always shows its own name first, even if a reason would keep the label the same.
        _keepAwakeReason = (KeepAwakeReason)(-1);
        UpdateKeepAwake();
    }
}
