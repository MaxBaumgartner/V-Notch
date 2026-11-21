using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VNotch.Services;
using VNotch.Services.ControlCenter;
using VNotch.Services.Displays;

namespace VNotch.Controls;

public partial class ControlCenterView
{
    private bool _compactSystemReady;
    private readonly List<(ScaleTransform Fill, TextBlock Value)> _systemMasterBars = new();
    private readonly Dictionary<uint, (ScaleTransform Fill, TextBlock Value)> _systemAppBars = new();
    private readonly StackPanel _systemSound = new(), _systemReadouts = new(), _systemGear = new(), _systemDevices = new(), _systemDisplays = new();
    private TextBlock? _systemCpu, _systemGpu, _systemRam, _systemTraffic;
    private string _systemDrawerKey = "";
    private WaveXlrState? _waveXlr;
    private readonly LoadWarningState _loadWarning = new();
    private Border? _advancedDisplays;
    private bool _advancedDisplaysOpen;
    private int _compactDisplayRevision;
    private bool _readingCompactDisplays;
    private void BuildCompactSystem(Snapshot s)
    {
        _accent = Frozen("#8FA9C4");
        if (!_compactSystemReady)
        {
            _compactSystemReady = true;
            MountCompactPage();
            BuildSystemFace();
        }
        if (s.Outputs != null) _outputs = s.Outputs;
        // The OMEN laptop (modes, fans, keyboard light) sits in the drawer when its helper answers.
        if (s.Stats != null) BuildLaptopSectionIfChanged(s.Laptop);
        _waveXlr = s.WaveXlr;
        PaintFaceRow(_faceWifi, s.WifiOn, s.Networks.FirstOrDefault(n => n.IsConnected)?.Ssid ?? "An", "Aus", ConnectionBlue);
        PaintFaceRow(_faceBluetooth, s.BluetoothOn, "An", "Aus", ConnectionBlue);
        PaintFaceRow(_faceMic, _waveXlr == null ? null : !_waveXlr.Muted, "Wave XLR", "Stumm", MicSand);
        ShowFaceOutput();
        ShowFaceMode();
        RefreshQuickPage(s);
        var current = _volume?.Current ?? (0f, false);
        ShowCompactVolume(current.Item1, current.Item2);
        string key = string.Join(";", (s.Apps ?? new()).Select(a => $"{a.ProcessId}:{a.DisplayName}")) + "|" +
            string.Join(";", _outputs.Select(o => $"{o.Id}:{o.Name}:{o.IsDefault}:{o.Bluetooth}")) + "|" +
            string.Join(";", s.Devices.Select(d => $"{d.Address}:{d.Name}:{d.Battery}:{d.IsConnected}")) + "|" +
            s.WifiOn + s.BluetoothOn + string.Join(";", s.Networks.Select(n => $"{n.Ssid}:{n.IsConnected}")) +
            _legionReachable + (_legion != null) + _legion?.WinKeyLocked + _legion?.TouchpadLocked +
            _keepAwakeMode?.Invoke() + _phone?.Invoke()?.State + "|" + (s.Laptop != null);
        if (key != _systemDrawerKey) { _systemDrawerKey = key; BuildSystemDrawer(s); }
        if (s.Stats is { } stats)
        {
            _systemCpu!.Text = PercentAndTemp(stats.CpuPercent, _legion?.CpuTemp);
            _systemGpu!.Text = PercentAndTemp(stats.GpuPercent, _legion?.GpuTemp);
            _systemRam!.Text = $"{stats.RamUsedGb:0.#} von {stats.RamTotalGb:0.#} GB";
            _systemTraffic!.Text = $"↓ {FormatRate(stats.DownBytesPerSecond)}  ↑ {FormatRate(stats.UpBytesPerSecond)}";
            if (IsVisible) SetCompactStatus(_loadWarning.Update(DateTime.UtcNow, stats.CpuPercent, stats.GpuPercent, _legion?.CpuTemp, _legion?.GpuTemp));
        }
        if (s.Apps != null)
            foreach (var app in s.Apps)
                if (_systemAppBars.TryGetValue(app.ProcessId, out var bar)) PaintCompactBar(bar, app.Volume, app.IsMuted);
        UpdateTopList(s.Top);
    }

    private static string PercentAndTemp(double? percent, int? temp) =>
        (percent is { } p ? $"{p:0} %" : "–") + (temp is > 0 ? $", {temp} °C" : "");

    private async void ToggleWaveXlr()
    {
        if (_waveXlr is not { } state || _faceMic is not { } mic) return;
        ((FrameworkElement)mic.Disc.Parent).IsEnabled = false;
        bool muted = !state.Muted;
        bool changed = await WaveXlrControl.SetMutedAsync(state, muted);
        if (_disposed) return;
        if (changed) _waveXlr = state with { Muted = muted };
        PaintFaceRow(_faceMic, _waveXlr == null ? null : !_waveXlr.Muted, "Wave XLR", "Stumm", MicSand);
    }

    internal static string ShortOutputName(OutputDevice output)
    {
        if (output.FriendlyName.Contains("Wave", StringComparison.OrdinalIgnoreCase) || output.FriendlyName.Contains("Elgato Virtual Audio", StringComparison.OrdinalIgnoreCase)) return "Wave";
        if (output.FriendlyName.Contains("AirPods", StringComparison.OrdinalIgnoreCase)) return "AirPods";
        return output.Name.Length > 12 ? output.Name[..12] : output.Name;
    }

    internal static int OutputGlyph(OutputDevice? output) => output != null &&
        (output.Bluetooth || output.FriendlyName.Contains("Headphone", StringComparison.OrdinalIgnoreCase) ||
            output.FriendlyName.Contains("Kopfh", StringComparison.OrdinalIgnoreCase) ||
            output.FriendlyName.Contains("AirPods", StringComparison.OrdinalIgnoreCase)) ? 0xE7F6 : 0xE7F5;

    private void SelectCompactOutput(OutputDevice output)
    {
        if (output.IsDefault) return;
        _outputs = _outputs.Select(o => o with { IsDefault = o.Id == output.Id }).ToList();
        ShowFaceOutput();
        if (_snapshot is { } snapshot)
        {
            snapshot.Outputs = _outputs;
            BuildCompactSystem(snapshot);
        }
        Task.Run(() => AudioMixerService.SwitchDefaultOutputDevice(output.Id));
        RefreshSoon(350);
    }

    private Grid CompactVolumeBar(double level, Action<double> set, bool master = false, uint? app = null)
    {
        var value = CompactText($"{level * 100:0} %", 11, CompactLabel); value.Width = 34; value.TextAlignment = TextAlignment.Right;
        var track = ThinBar(level, CompactInk, v => { value.Text = $"{v * 100:0} %"; set(v); });
        var fill = (ScaleTransform)track.Tag;
        var grid = new Grid { MinHeight = 27 };
        grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(track); Grid.SetColumn(value, 1); grid.Children.Add(value);
        track.PreviewMouseWheel += (_, e) => { e.Handled = true; double v = Math.Clamp(fill.ScaleX + Math.Sign(e.Delta) * WheelVolumeStep, 0, 1); set(v); Motion.Stop(fill, ScaleTransform.ScaleXProperty, v); value.Text = $"{v * 100:0} %"; };
        if (master) _systemMasterBars.Add((fill, value));
        if (app is { } id) _systemAppBars[id] = (fill, value);
        return grid;
    }

    private void ShowCompactVolume(float volume, bool muted)
    {
        foreach (var bar in _systemMasterBars) PaintCompactBar(bar, volume, muted);
        ShowCapsuleLevel(_volumeCapsule, muted ? 0 : volume);
    }

    private static void PaintCompactBar((ScaleTransform Fill, TextBlock Value) bar, double level, bool muted)
    {
        double target = muted ? 0 : Math.Clamp(level, 0, 1);
        bar.Value.Text = $"{target * 100:0} %";
        bar.Value.ToolTip = muted ? "Stumm" : null;
        if (bar.Value.IsVisible) Motion.To(bar.Fill, ScaleTransform.ScaleXProperty, target, Motion.Shift);
        else Motion.Stop(bar.Fill, ScaleTransform.ScaleXProperty, target);
    }

    private void BuildSystemDrawer(Snapshot s)
    {
        foreach (var part in new FrameworkElement[] { _systemSound, _systemReadouts, _systemGear, _systemDevices, _systemDisplays, _displaysSection, _topList, _laptopSection })
        {
            if (part.Parent is Panel parent) parent.Children.Remove(part);
            else if (part.Parent is Border border) border.Child = null;
        }
        _systemSound.Children.Clear(); _systemReadouts.Children.Clear(); _systemGear.Children.Clear(); _systemDevices.Children.Clear();
        while (_systemMasterBars.Count > 1) _systemMasterBars.RemoveAt(1);
        _systemAppBars.Clear();
        _systemSound.Children.Add(CompactText("Ton", 11, CompactLabel));
        foreach (var output in _outputs)
        {
            var row = CompactLine(OutputGlyph(output), ShortOutputName(output),
                output.IsDefault ? CompactGlyph(0xE73E, CompactInk) : null);
            var hit = new Border { Child = row, ToolTip = output.FriendlyName, Cursor = Cursors.Hand, Background = Brushes.Transparent };
            Pressable(hit, () => Brushes.Transparent, Frozen("#0AFFFFFF"), gentle: true);
            hit.MouseLeftButtonUp += (_, e) => { e.Handled = true; SelectCompactOutput(output); };
            _systemSound.Children.Add(hit);
        }
        var master = CompactVolumeBar(_volume?.Current.Volume ?? 0, v => _volume?.SetVolume((float)v), master: true);
        _systemSound.Children.Add(CompactLine(0xE767, "Gesamt", master)); master.Width = 130;
        foreach (var session in s.Apps ?? new())
        {
            var app = session;
            var bar = CompactVolumeBar(app.Volume, v =>
            {
                if (_mixer?.Invoke() is { } mixer) Task.Run(() => mixer.SetSessionVolume(app.ProcessId, (float)v));
            }, app: app.ProcessId);
            bar.Width = 130;
            _systemSound.Children.Add(CompactLine(0xE767, app.DisplayName, bar));
        }
        _systemSound.Children.Add(CompactText("Displays", 11, CompactLabel));
        _systemSound.Children.Add(_systemDisplays);
        var more = CompactPill("Anzeigeoptionen", () =>
        {
            _advancedDisplaysOpen = !_advancedDisplaysOpen;
            _expanded["displays"] = true;
            if (_advancedDisplaysOpen) BuildDisplaysSection();
            if (_advancedDisplays != null) FoldInline(_advancedDisplays, _advancedDisplaysOpen, true);
        });
        _systemSound.Children.Add(more);
        _systemReadouts.Children.Add(CompactText("Leistung gerade", 11, CompactLabel));
        _systemCpu = CompactText("–", 11, CompactLabel); _systemGpu = CompactText("–", 11, CompactLabel); _systemRam = CompactText("–", 11, CompactLabel);
        var cpu = CompactLine(0xE950, "CPU", _systemCpu); OpenOnClick(cpu, () => "taskmgr.exe");
        _systemReadouts.Children.Add(cpu); _systemReadouts.Children.Add(CompactLine(0xE7F4, "GPU", _systemGpu));
        _systemReadouts.Children.Add(CompactLine(0xE964, "RAM", _systemRam));
        var processes = CompactPill("Prozesse", () => ToggleTop(TopMode.Cpu)); _systemReadouts.Children.Add(processes);
        _systemGear.Children.Add(CompactText("Geräte", 11, CompactLabel));
        _systemGear.Children.Add(_systemDevices);
        foreach (var device in s.Devices)
        {
            var detail = new StackPanel { Orientation = Orientation.Horizontal };
            if (device.Battery >= 0)
            {
                var battery = CompactText($"{device.Battery} %", 11, CompactLabel);
                battery.VerticalAlignment = VerticalAlignment.Center;
                if (device.CanConnect) battery.Margin = new Thickness(0, 0, 8, 0);
                detail.Children.Add(battery);
            }
            if (device.CanConnect) detail.Children.Add(CompactPill(device.IsConnected ? "Trennen" : "Verbinden", () => SetBluetoothConnected(device, !device.IsConnected)));
            _systemDevices.Children.Add(CompactLine(device.Glyph[0], device.Name, detail));
        }
        AddSystemNetwork(s);
        if (_legionAttached)
        {
            if (_legion is { } state)
            {
                _systemDevices.Children.Add(CompactLine(0xE765, Loc.Get("legion.winKey"), Toggle(!state.WinKeyLocked, async on => ApplyLegion(await LegionService.SetWinKeyLockedAsync(!on)))));
                _systemDevices.Children.Add(CompactLine(0xEFA5, Loc.Get("legion.touchpad"), Toggle(!state.TouchpadLocked, async on => ApplyLegion(await LegionService.SetTouchpadLockedAsync(!on)))));
            }
            else _systemDevices.Children.Add(CompactPill(Loc.Get("legion.setup"), async () => { await LegionService.RunSetupAsync(); RefreshLegion(); }));
            var light = CompactLine(0xED39, "Tastaturlicht", CompactText("Fn + Leertaste", 10.5, CompactLabel));
            light.ToolTip = "Der installierte Lenovo-Helfer stellt die Tastaturbeleuchtung nicht bereit";
            _systemDevices.Children.Add(light);
        }
        if (_keepAwakeMode != null && _setKeepAwakeMode != null)
        {
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            var automatic = CompactPill("Auto", () => { _setKeepAwakeMode(KeepAwakeMode.Automatic); _systemDrawerKey = ""; if (_snapshot != null) BuildCompactSystem(_snapshot); });
            automatic.ToolTip = _keepAwakeLabel?.Invoke(); automatic.Margin = new Thickness(0, 0, 8, 0); actions.Children.Add(automatic);
            actions.Children.Add(Toggle(_keepAwakeMode() != KeepAwakeMode.Off, on => _setKeepAwakeMode(on ? KeepAwakeMode.Always : KeepAwakeMode.Off)));
            _systemDevices.Children.Add(CompactLine(0xE708, "Wach bleiben", actions));
        }
        AddPhoneRow(_systemDevices);
        var body = new StackPanel { Children = { BalancedSystemColumns() } };
        if (s.Laptop != null) body.Children.Add(_laptopSection);
        _advancedDisplays = new Border { Height = 0, ClipToBounds = true, Child = _displaysSection };
        body.Children.Add(_advancedDisplays); body.Children.Add(_topList);
        AddDrawer(body);
        if (_advancedDisplaysOpen) FoldInline(_advancedDisplays, true, false);
        if (IsVisible && _systemDisplays.Children.Count == 0) RefreshCompactDisplays();
        ContentHeightChanged?.Invoke();
    }

    /// <summary>
    /// Sound and displays sit left, devices right; the load readouts join whichever side keeps the drawer shorter,
    /// so one long column does not push the drawer past the notch's height while the other half stays empty.
    /// </summary>
    private Grid BalancedSystemColumns()
    {
        double column = Math.Max(120, ((ActualWidth > 0 ? ActualWidth : 568) - 24) / 2);
        double Height(FrameworkElement part)
        {
            part.Margin = new Thickness(0);
            part.Measure(new Size(column, double.PositiveInfinity));
            return part.DesiredSize.Height;
        }
        double sound = Height(_systemSound), load = Height(_systemReadouts), gear = Height(_systemGear);
        const double gap = 8;
        bool loadLeft = Math.Max(sound + gap + load, gear) < Math.Max(sound, load + gap + gear);
        _systemReadouts.Margin = new Thickness(0, loadLeft ? gap : 0, 0, 0);
        _systemGear.Margin = new Thickness(0, loadLeft ? 0 : gap, 0, 0);
        var left = new StackPanel { Children = { _systemSound } };
        var right = new StackPanel();
        if (loadLeft) left.Children.Add(_systemReadouts); else right.Children.Add(_systemReadouts);
        right.Children.Add(_systemGear);
        return CompactColumns(left, right);
    }

    private void AddSystemNetwork(Snapshot s)
    {
        Border? wifi = null;
        string name = s.Networks.FirstOrDefault(n => n.IsConnected)?.Ssid ?? Loc.Get("control.notConnected");
        wifi = CompactPill(name, () =>
        {
            if (s.WifiStatus == WifiListStatus.LocationBlocked) NetworkControl.OpenLocationSettings();
            else ShowWifiMenu(wifi!);
        });
        wifi.MaxWidth = 140;
        _systemDevices.Children.Add(CompactLine(0xE701, "WLAN", wifi));
        var wired = s.Wired.FirstOrDefault(w => w.IsUp) ?? s.Wired.FirstOrDefault();
        if (wired != null)
        {
            var row = CompactLine(0xE839, "Ethernet", CompactText(wired.IsUp ? Loc.Get("control.connected") : "–", 11, CompactLabel));
            row.ToolTip = wired.Description; _systemDevices.Children.Add(row);
        }
        _systemTraffic = CompactText("–", 10.5, CompactLabel);
        _systemDevices.Children.Add(_systemTraffic);
    }

    private async void RefreshCompactDisplays()
    {
        if (_readingCompactDisplays || _disposed || !IsVisible) return;
        _readingCompactDisplays = true;
        int revision = ++_compactDisplayRevision;
        try
        {
            var displays = await Task.Run(() => DisplayApi.List());
            if (_disposed || !IsVisible || revision != _compactDisplayRevision) return;
            _systemDisplays.Children.Clear();
            foreach (var display in displays.Where(d => d.Active))
            {
                int? level = DisplayApi is Win32DisplayApi native ? await native.GetBrightnessAsync(display.Id) : await DisplayBrightness.GetAsync(display.Id);
                if (_disposed || !IsVisible || revision != _compactDisplayRevision) return;
                if (level == null) continue;
                var bar = CompactVolumeBar(level.Value / 100d, value => DisplayBrightness.Set(display.Id, (int)Math.Round(value * 100)));
                bar.Width = 125;
                _systemDisplays.Children.Add(CompactLine(0xE706, display.Name, bar));
            }
            if (_drawerOpen && _drawerHeight != null) Motion.Stop(_drawerHeight, WeatherTween.ValueProperty, MeasureDrawer());
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        { RuntimeLog.Warn("CONTROL-CENTER", $"Displays not read: {ex.Message}"); }
        finally
        {
            _readingCompactDisplays = false;
            if (!_disposed && IsVisible && revision != _compactDisplayRevision) _ = Dispatcher.BeginInvoke(new Action(RefreshCompactDisplays));
        }
    }

    internal sealed class LoadWarningState
    {
        private DateTime? _highSince, _lowSince;
        private bool _active;
        public void Pause() { _highSince = _lowSince = null; }
        public string Update(DateTime now, double? cpu, double? gpu, int? cpuTemp, int? gpuTemp)
        {
            double load = Math.Max(cpu ?? 0, gpu ?? 0);
            int temp = Math.Max(cpuTemp ?? 0, gpuTemp ?? 0);
            if (load >= 90) _highSince ??= now; else _highSince = null;
            if (!_active && (temp >= 85 || _highSince is { } high && now - high >= TimeSpan.FromSeconds(10))) _active = true;
            if (_active && load < 80 && temp < 80)
            {
                _lowSince ??= now;
                if (now - _lowSince >= TimeSpan.FromSeconds(10)) _active = false;
            }
            else _lowSince = null;
            if (!_active) return "";
            bool graphics = temp >= 85 ? (gpuTemp ?? 0) >= (cpuTemp ?? 0) : (gpu ?? 0) >= (cpu ?? 0);
            return (graphics ? "GPU " : "CPU ") + PercentAndTemp(graphics ? gpu : cpu, graphics ? gpuTemp : cpuTemp);
        }
    }
}
