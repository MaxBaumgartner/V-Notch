using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VNotch.Controllers;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

public partial class MainWindow
{
    #region Bluetooth Notification

    private bool _isBluetoothNotificationVisible = false;
    private bool _currentBluetoothConnected = true;
    private int _bluetoothNotificationToken = 0;

    private void InitializeBluetoothNotificationController()
    {
        _bluetoothController.ShowRequested += BluetoothController_ShowRequested;
        _bluetoothController.DismissRequested += BluetoothController_DismissRequested;
    }

    private void BluetoothModule_DeviceConnected(object? sender, BluetoothDeviceInfo info)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _bluetoothController.TryShow(info, connected: true,
                _isNotchVisible, _isAnimating, _isExpanded, _isMusicExpanded);
        });
    }

    private void BluetoothModule_DeviceDisconnected(object? sender, BluetoothDeviceInfo info)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _bluetoothController.TryShow(info, connected: false,
                _isNotchVisible, _isAnimating, _isExpanded, _isMusicExpanded);
        });
    }

    private void BluetoothController_ShowRequested(BluetoothDeviceInfo device, bool connected)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _currentBluetoothConnected = connected;

            if (connected)
            {
                BluetoothDeviceName.Text = device.Name;
                BluetoothIcon.Data = BluetoothNotificationController.GetIconGeometry(device.DeviceType);
                BluetoothStatusText.Text = Loc.Get("bluetooth.connected");
            }
            else
            {
                BluetoothDisconnectDeviceName.Text = device.Name;
                BluetoothDisconnectIcon.Data = BluetoothNotificationController.GetIconGeometry(device.DeviceType);
                BluetoothDisconnectStatusText.Text = Loc.Get("bluetooth.disconnected");
            }

            AnimateBluetoothNotificationIn(connected);
        });
    }

    private void BluetoothController_DismissRequested()
    {
        Dispatcher.BeginInvoke(() =>
        {
            AnimateBluetoothNotificationOut();
        });
    }

    private Grid ActiveBluetoothGrid => _currentBluetoothConnected ? BluetoothNotification : BluetoothDisconnectNotification;
    private TranslateTransform ActiveBluetoothTranslate => _currentBluetoothConnected ? BluetoothNotificationTranslate : BluetoothDisconnectNotificationTranslate;
    private ScaleTransform ActiveBluetoothIconScale => _currentBluetoothConnected ? BluetoothIconScale : BluetoothDisconnectIconScale;

    private void AnimateBluetoothNotificationIn(bool connected)
    {
        if (!TryAcquireCompactSlot(VNotch.Controllers.CompactPillSlot.Bluetooth, out int token))
        {
            return;
        }

        _bluetoothNotificationToken = token;
        _isBluetoothNotificationVisible = true;

        SuppressPrivacyDot();

        var otherGrid = connected ? BluetoothDisconnectNotification : BluetoothNotification;
        otherGrid.Visibility = Visibility.Collapsed;
        otherGrid.Opacity = 0;

        var activeGrid = ActiveBluetoothGrid;
        var activeTranslate = ActiveBluetoothTranslate;
        var activeIconScale = ActiveBluetoothIconScale;

        activeGrid.Visibility = Visibility.Visible;
        activeGrid.Opacity = 0;

        PlayBluetoothBounce();

        HideCompactSurface(CollapsedContent);
        HideCompactSurface(MusicCompactContent);

        var fadeIn = MakeCompactNotificationEntrance(0, 1);
        activeGrid.BeginAnimation(OpacityProperty, fadeIn);

        Motion.To(activeTranslate, TranslateTransform.YProperty, 0, Motion.Tile, from: 6);

        Motion.To(activeIconScale, ScaleTransform.ScaleXProperty, 1, Motion.TileScale, from: Motion.TileFromScale);
        Motion.To(activeIconScale, ScaleTransform.ScaleYProperty, 1, Motion.TileScale, from: Motion.TileFromScale);
    }

    private void AnimateBluetoothNotificationOut()
    {
        var activeGrid = ActiveBluetoothGrid;
        var activeTranslate = ActiveBluetoothTranslate;
        int token = _bluetoothNotificationToken;

        var fadeOut = MakeCompactNotificationExit(1, 0);
        fadeOut.Completed += (s, e) =>
        {
            if (token != _bluetoothNotificationToken) return;
            if (IsCompactSlotStale(token)) return;

            activeGrid.Visibility = Visibility.Collapsed;
            _isBluetoothNotificationVisible = false;
            _bluetoothController.MarkDismissed();
            _compactPillArbiter.Release(token);
            RestoreCompactMediaPresentation();
            _bluetoothNotificationToken = 0;

            RestorePrivacyDotVisibility();

            if (_isMusicCompactMode && _currentMediaInfo != null)
            {
                MusicCompactContent.Visibility = Visibility.Visible;
                var fadeInMusic = MakeCompactContentRestore();
                MusicCompactContent.BeginAnimation(OpacityProperty, fadeInMusic);
            }
            else
            {
                CollapsedContent.Visibility = Visibility.Visible;
                var fadeInCollapsed = MakeCompactContentRestore();
                CollapsedContent.BeginAnimation(OpacityProperty, fadeInCollapsed);
            }
        };

        activeGrid.BeginAnimation(OpacityProperty, fadeOut);

        Motion.To(activeTranslate, TranslateTransform.YProperty, -4, Motion.Exit);
        Motion.To(ActiveBluetoothIconScale, ScaleTransform.ScaleXProperty, Motion.ExitToScale, Motion.Exit);
        Motion.To(ActiveBluetoothIconScale, ScaleTransform.ScaleYProperty, Motion.ExitToScale, Motion.Exit);
    }

    private void CancelBluetoothNotificationImmediate()
    {
        if (!_isBluetoothNotificationVisible) return;

        _isBluetoothNotificationVisible = false;
        _bluetoothNotificationToken = 0;
        _bluetoothController.MarkDismissed();

        Motion.Stop(BluetoothNotification, OpacityProperty, 0);
        Motion.Stop(BluetoothNotificationTranslate, TranslateTransform.YProperty, 0);
        Motion.Stop(BluetoothIconScale, ScaleTransform.ScaleXProperty, 1);
        Motion.Stop(BluetoothIconScale, ScaleTransform.ScaleYProperty, 1);
        BluetoothNotification.Visibility = Visibility.Collapsed;

        Motion.Stop(BluetoothDisconnectNotification, OpacityProperty, 0);
        Motion.Stop(BluetoothDisconnectNotificationTranslate, TranslateTransform.YProperty, 0);
        Motion.Stop(BluetoothDisconnectIconScale, ScaleTransform.ScaleXProperty, 1);
        Motion.Stop(BluetoothDisconnectIconScale, ScaleTransform.ScaleYProperty, 1);
        BluetoothDisconnectNotification.Visibility = Visibility.Collapsed;
    }

    private void PlayBluetoothBounce()
    {
        if (_isExpanded || _isAnimating || AnimationConfig.ReduceMotion) return;
        foreach (var scale in new[] { NotchScale, NotchShadowScale })
        {
            Motion.To(scale, ScaleTransform.ScaleXProperty, 1, Motion.Press, velocity: 1.2);
            Motion.To(scale, ScaleTransform.ScaleYProperty, 1, Motion.Press, velocity: -0.9);
        }
    }

    #endregion
}

