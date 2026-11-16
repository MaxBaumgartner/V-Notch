using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using VNotch.Services;
using VNotch.Services.ControlCenter;

namespace VNotch;

/// <summary>
/// Sound output beyond the two-finger swipe:
/// - With the mouse: hold the right button on the closed notch and pull sideways = next / previous speaker
///   (a left-button drag is already the music's skip gesture).
/// - Bluetooth headphones that come in (the XM6 out of the case) take the sound; when they go, it returns to the
///   wired speaker that played before (the Rays or the desk speakers on the jack).
/// - The system tab's output tile and app volumes get the volume watcher and the mixer.
/// </summary>
public partial class MainWindow
{
    private const double SpeakerDragDistance = 36;

    private bool _speakerDragging;
    private bool _speakerDragSwitched;
    private Point _speakerDragStart;
    // The last speaker in use that was not Bluetooth: where the sound goes back when the headphones leave.
    private string? _lastWiredSpeakerId;
    private string? _autoBluetoothSpeakerId;
    private readonly object _outputAutoGate = new();

    private void InitializeOutputAuto()
    {
        NotchWrapper.PreviewMouseRightButtonDown += SpeakerDrag_Down;
        NotchWrapper.PreviewMouseMove += SpeakerDrag_Move;
        NotchWrapper.PreviewMouseRightButtonUp += SpeakerDrag_Up;

        if (_masterVolumeWatcher is not { } watcher) return;
        ControlCenter.AttachOutput(watcher, () => App.Services.GetService(typeof(AudioMixerService)) as AudioMixerService);
        watcher.EndpointStateChanged += OnOutputEndpointChanged;
        watcher.SpeakerSwitched += (_, _) => RememberWiredSpeaker();
        Task.Run(RememberWiredSpeaker);
    }

    #region Right-button drag

    private void SpeakerDrag_Down(object sender, MouseButtonEventArgs e)
    {
        _speakerDragSwitched = false;
        _speakerDragging = !_isExpanded && !_isAnimating && !_isGreetingActive && !_cleanedUp;
        if (_speakerDragging) _speakerDragStart = e.GetPosition(this);
    }

    private void SpeakerDrag_Move(object sender, MouseEventArgs e)
    {
        if (!_speakerDragging || _speakerDragSwitched) return;
        if (e.RightButton != MouseButtonState.Pressed)
        {
            _speakerDragging = false;
            return;
        }
        Vector moved = e.GetPosition(this) - _speakerDragStart;
        if (Math.Abs(moved.X) < SpeakerDragDistance || Math.Abs(moved.X) < Math.Abs(moved.Y) * 1.5) return;
        _speakerDragSwitched = true;
        SwitchSpeaker(Math.Sign(moved.X));
    }

    private void SpeakerDrag_Up(object sender, MouseButtonEventArgs e)
    {
        _speakerDragging = false;
        // A drag that switched is not also a right click (pin the cover, dismiss a pill).
        if (_speakerDragSwitched) e.Handled = true;
        _speakerDragSwitched = false;
    }

    /// <summary>The right-click handlers that ran before the drag's own one ask this.</summary>
    private bool IsSpeakerDragSwitch => _speakerDragSwitched;

    #endregion

    #region Bluetooth headphones take over

    private void RememberWiredSpeaker()
    {
        try
        {
            var current = OutputDevices.List(BluetoothControl.Paired().Select(d => d.Name)).FirstOrDefault(o => o.IsDefault);
            if (current is { Bluetooth: false })
                lock (_outputAutoGate) _lastWiredSpeakerId = current.Id;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            RuntimeLog.Warn("OUTPUT-AUTO", ex.Message);
        }
    }

    /// <summary>Runs on a worker thread.</summary>
    private void OnOutputEndpointChanged(string endpointId, bool active)
    {
        if (_cleanedUp) return;
        try
        {
            var outputs = OutputDevices.List(BluetoothControl.Paired().Select(d => d.Name));
            if (active)
            {
                var device = outputs.FirstOrDefault(o => o.Id == endpointId);
                if (device is not { Bluetooth: true }) return;
                lock (_outputAutoGate) _autoBluetoothSpeakerId = device.Id;
                if (device.IsDefault) return;
                RuntimeLog.Info("OUTPUT-AUTO", $"{device.Name} came in, sound goes there");
                AudioMixerService.SwitchDefaultOutputDevice(device.Id);
                return;
            }

            string? back;
            lock (_outputAutoGate)
            {
                if (endpointId != _autoBluetoothSpeakerId) return;
                _autoBluetoothSpeakerId = null;
                back = _lastWiredSpeakerId;
            }
            if (back == null || outputs.FirstOrDefault(o => o.Id == back) is not { } wired || wired.IsDefault) return;
            RuntimeLog.Info("OUTPUT-AUTO", $"Headphones gone, back to {wired.Name}");
            AudioMixerService.SwitchDefaultOutputDevice(wired.Id);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            RuntimeLog.Warn("OUTPUT-AUTO", ex.Message);
        }
    }

    #endregion
}
