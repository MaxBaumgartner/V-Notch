using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using VNotch.Services;
using VNotch.Services.ControlCenter;

namespace VNotch.Controls;

/// <summary>
/// WLAN and Bluetooth open in place, like Windows' quick settings: the round button switches the radio, the name
/// opens a page with an on/off switch and the networks or devices to pick. Back (or closing the notch) returns.
/// </summary>
public partial class ControlCenterView
{
    private enum QuickPage { None, Wifi, Bluetooth }

    private const int QuickNetworkLimit = 6;
    private Grid? _faceGrid;
    private StackPanel? _quickPanel;
    private QuickPage _quickPage;
    private List<WifiNetwork>? _quickNetworks;
    private readonly TranslateTransform _faceShift = new(), _quickShift = new();
    private string _quickKey = "";
    private bool _drawerBeforeQuick;

    /// <summary>The open page follows the regular refresh, but only rebuilds when what it lists has changed.</summary>
    private void RefreshQuickPage(Snapshot s)
    {
        if (_quickPage == QuickPage.None) return;
        string key = _quickPage + "|" + s.WifiOn + s.BluetoothOn + "|" +
            string.Join(";", (_quickNetworks ?? s.Networks).Select(n => n.Ssid + n.IsConnected + n.Signal / 25)) + "|" +
            string.Join(";", s.Devices.Select(d => d.Address + d.IsConnected + d.Battery));
        if (key == _quickKey) return;
        _quickKey = key;
        FillQuickPanel(s);
    }

    private void OpenQuickPage(QuickPage page)
    {
        if (_faceGrid == null || _snapshot is not { } snapshot) return;
        // The page takes the face's place; the drawer folds away meanwhile so the notch stays short, and comes back after.
        if (_quickPage == QuickPage.None) _drawerBeforeQuick = _drawerOpen;
        if (_drawerOpen) SetDrawerOpen(false);
        _quickPage = page;
        _quickNetworks = null;
        EnsureQuickPanel();
        _quickKey = "";
        RefreshQuickPage(snapshot);
        SwapQuickPage(toPanel: true);
        if (page == QuickPage.Wifi) _ = RescanQuickNetworks();
    }

    private void CloseQuickPage(bool animate = true)
    {
        if (_quickPage == QuickPage.None) return;
        _quickPage = QuickPage.None;
        SwapQuickPage(toPanel: false, animate);
        if (_drawerBeforeQuick && animate && IsVisible) SetDrawerOpen(true);
        _drawerBeforeQuick = false;
    }

    private void EnsureQuickPanel()
    {
        if (_quickPanel != null) return;
        _quickPanel = new StackPanel { Visibility = Visibility.Collapsed, RenderTransform = _quickShift };
        _compactPage.Children.Insert(_compactPage.Children.IndexOf(_faceGrid!) + 1, _quickPanel);
        _faceGrid!.RenderTransform = _faceShift;
        IsVisibleChanged += (_, _) => { if (!IsVisible) CloseQuickPage(animate: false); };
    }

    /// <summary>The face slides out to the left and the page in from the right (and back), then the notch fits.</summary>
    private void SwapQuickPage(bool toPanel, bool animate = true)
    {
        if (_faceGrid == null || _quickPanel == null) return;
        FrameworkElement leaving = toPanel ? _faceGrid : _quickPanel, arriving = toPanel ? _quickPanel : _faceGrid;
        TranslateTransform leaveShift = toPanel ? _faceShift : _quickShift, arriveShift = toPanel ? _quickShift : _faceShift;
        double direction = toPanel ? 1 : -1;
        leaving.Visibility = Visibility.Collapsed;
        arriving.Visibility = Visibility.Visible;
        Motion.Stop(leaveShift, TranslateTransform.XProperty, 0);
        Motion.Stop(leaving, OpacityProperty, 1);
        if (!animate || !IsVisible || AnimationConfig.ReduceMotion)
        {
            Motion.Stop(arriveShift, TranslateTransform.XProperty, 0);
            Motion.Stop(arriving, OpacityProperty, 1);
        }
        else
        {
            Motion.To(arriveShift, TranslateTransform.XProperty, 0, Motion.Glide, from: 18 * direction);
            Motion.To(arriving, OpacityProperty, 1, Motion.Tile, from: 0);
        }
        ContentHeightChanged?.Invoke();
    }

    private void FillQuickPanel(Snapshot s)
    {
        if (_quickPanel == null || _quickPage == QuickPage.None) return;
        _quickPanel.Children.Clear();
        bool wifi = _quickPage == QuickPage.Wifi;
        bool? on = wifi ? s.WifiOn : s.BluetoothOn;

        // Header: back, title, on/off.
        var back = CompactGlyph(0xE76B);
        back.FontSize = 12;
        var backButton = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Background = Frozen("#1AFFFFFF"),
            Cursor = Cursors.Hand, Child = back, ToolTip = Say("Zurück", "Back") };
        Squish(backButton);
        backButton.MouseLeftButtonUp += (_, e) => { e.Handled = true; CloseQuickPage(); };
        var title = CompactText(wifi ? Say("WLAN", "Wi-Fi") : "Bluetooth", 15, CompactInk);
        title.FontWeight = FontWeights.SemiBold;
        title.Margin = new Thickness(12, 0, 0, 0);
        title.VerticalAlignment = VerticalAlignment.Center;
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8), LastChildFill = false };
        header.Children.Add(backButton);
        header.Children.Add(title);
        if (on != null && (wifi ? _wifiRadio : _bluetoothRadio) != null)
        {
            var toggle = Toggle(on == true, value =>
            {
                if (wifi && _wifiRadio != null) SetRadio(_wifiRadio, value);
                else if (!wifi && _bluetoothRadio != null) SetRadio(_bluetoothRadio, value);
            });
            toggle.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(toggle, Dock.Right);
            header.Children.Add(toggle);
        }
        _quickPanel.Children.Add(header);

        if (on != true)
        {
            _quickPanel.Children.Add(QuickNote(wifi ? Say("WLAN ist aus", "Wi-Fi is off") : Say("Bluetooth ist aus", "Bluetooth is off")));
        }
        else if (wifi)
        {
            var networks = (_quickNetworks ?? s.Networks)
                .Where(n => n.Ssid.Length > 0)
                .OrderByDescending(n => n.IsConnected).ThenByDescending(n => n.HasProfile).ThenByDescending(n => n.Signal)
                .Take(QuickNetworkLimit).ToList();
            if (networks.Count == 0) _quickPanel.Children.Add(QuickNote(Loc.Get("control.noNetworks")));
            foreach (var network in networks)
            {
                var chosen = network;
                int glyph = network.Signal >= 75 ? 0xE701 : network.Signal >= 50 ? 0xE874 : network.Signal >= 25 ? 0xE873 : 0xE872;
                _quickPanel.Children.Add(QuickRow(glyph, network.Ssid, network.IsConnected ? Loc.Get("control.connected") : network.Secured ? "" : "",
                    network.IsConnected, network.IsConnected ? null : () => ConnectWifi(chosen)));
            }
        }
        else
        {
            var devices = s.Devices.OrderByDescending(d => d.IsConnected).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
                .Take(QuickNetworkLimit).ToList();
            if (devices.Count == 0) _quickPanel.Children.Add(QuickNote(Say("Keine gekoppelten Geräte", "No paired devices")));
            foreach (var device in devices)
            {
                var chosen = device;
                string state = device.IsConnected ? (device.Battery >= 0 ? $"{device.Battery} %" : Loc.Get("control.connected")) : "";
                _quickPanel.Children.Add(QuickRow(device.CanConnect ? 0xE7F6 : 0xE702, device.Name, state, device.IsConnected,
                    device.CanConnect ? () => SetBluetoothConnected(chosen, !chosen.IsConnected) : null));
            }
        }

        var more = CompactText(wifi ? Loc.Get("control.moreNetworks") : Say("Bluetooth-Einstellungen", "Bluetooth settings"), 11, CompactLabel);
        var moreButton = new Border { Background = Brushes.Transparent, Cursor = Cursors.Hand, Padding = new Thickness(0, 8, 0, 2), Child = more,
            HorizontalAlignment = HorizontalAlignment.Left };
        moreButton.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (wifi) NetworkControl.OpenNetworkFlyout();
            else try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true }); } catch { }
        };
        _quickPanel.Children.Add(moreButton);
        ContentHeightChanged?.Invoke();
    }

    private TextBlock QuickNote(string text)
    {
        var note = CompactText(text, 12, CompactLabel);
        note.Margin = new Thickness(2, 6, 0, 6);
        return note;
    }

    /// <summary>One network or device: icon, name, and on the right its state (a lock, "Verbunden", the battery).</summary>
    private Border QuickRow(int glyph, string name, string trailing, bool active, Action? click)
    {
        var icon = CompactGlyph(glyph);
        icon.FontSize = 14;
        icon.Width = 22;
        icon.Foreground = active ? CompactInk : CompactLabel;
        var label = CompactText(name, 12.5, active ? CompactInk : Frozen("#D9FFFFFF"));
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        if (active) label.FontWeight = FontWeights.SemiBold;
        var line = new DockPanel { Height = 34, LastChildFill = true };
        line.Children.Add(icon);
        if (trailing.Length > 0)
        {
            var right = trailing.Length == 1 ? CompactGlyph(trailing[0]) : CompactText(trailing, 11, CompactLabel);
            if (trailing.Length == 1) right.FontSize = 11;
            right.Margin = new Thickness(10, 0, 0, 0);
            right.VerticalAlignment = VerticalAlignment.Center;
            if (active)
            {
                var dot = new Ellipse { Width = 6, Height = 6, Fill = new SolidColorBrush(ConnectionBlue), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                var group = new StackPanel { Orientation = Orientation.Horizontal, Children = { dot, right } };
                right.Margin = new Thickness(5, 0, 0, 0);
                DockPanel.SetDock(group, Dock.Right);
                line.Children.Add(group);
            }
            else
            {
                DockPanel.SetDock(right, Dock.Right);
                line.Children.Add(right);
            }
        }
        label.VerticalAlignment = VerticalAlignment.Center;
        line.Children.Add(label);
        var row = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 0, 10, 0), Background = Brushes.Transparent, Child = line };
        if (click != null)
        {
            row.Cursor = Cursors.Hand;
            Pressable(row, () => Brushes.Transparent, CardHover);
            row.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
        }
        return row;
    }

    private async Task RescanQuickNetworks()
    {
        var networks = await Task.Run(() =>
        {
            var list = new List<WifiNetwork>();
            NetworkControl.GetNetworks(list, rescan: true);
            return list;
        });
        if (_disposed || _quickPage != QuickPage.Wifi || _snapshot is not { } snapshot) return;
        _quickNetworks = networks;
        RefreshQuickPage(snapshot);
    }
}
