using System.Windows;

namespace VNotch;

public partial class MainWindow
{
    // Sliding the notch away for a fullscreen game only empties the window: it stays a shown, see-through,
    // top-most window over the game. That alone makes Windows compose every game frame on the desktop instead
    // of handing it straight to the display (more input lag). Measured with a borderless D3D flip-model app on
    // the 360 Hz monitor: every frame composed with the notch up, every frame direct without it.
    // So once the notch has slid away for a fullscreen app and nothing of it shows, the window leaves the
    // screen, and it comes back before anything has to show again.
    private void UpdateFullscreenParking()
    {
        bool park = !_cleanedUp && _isHiddenByFullscreen && !_fullscreenSlideVisible && !_isFullscreenSlideAnimating
            && !_micMutedTabShown && MicMutedTab.Visibility != Visibility.Visible;
        _overlayWindow.SetParked(park);
        _spotlightController.SetFullscreenAppInFront(!_cleanedUp && _isHiddenByFullscreen);
    }
}
