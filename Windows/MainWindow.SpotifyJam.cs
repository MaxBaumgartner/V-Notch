using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

// Music tab: start a Spotify Jam with one click and put the invite link on the clipboard.
public partial class MainWindow
{
    private const string JamLogTag = "SPOTIFY-JAM";
    private const string JamIdleGlyph = "";  // People
    private const string JamDoneGlyph = "";  // CheckMark
    private const string JamErrorGlyph = ""; // Warning
    private static readonly Color JamSuccessColor = Color.FromRgb(74, 222, 128);
    private static readonly Color JamErrorColor = Color.FromRgb(248, 113, 113);
    private static readonly TimeSpan JamDoneHold = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan JamErrorHold = TimeSpan.FromSeconds(6);

    private SpotifyRemoteService? _spotifyRemote;
    private ToolTip? _jamToolTip;
    private bool _jamBusy;
    private bool _jamNeedsSignIn;
    private int _jamFeedbackVersion;

    /// <summary>The Jam button only makes sense while Spotify is the media source.</summary>
    private void UpdateSpotifyJamButtonVisibility()
    {
        if (SpotifyJamButton == null) return;
        var visibility = _currentMediaInfo?.Platform == MediaPlatform.Spotify ? Visibility.Visible : Visibility.Collapsed;
        if (SpotifyJamButton.Visibility == visibility) return;
        SpotifyJamButton.Visibility = visibility;
        if (visibility == Visibility.Visible && !_jamBusy) ResetJamFeedback();
    }

    private async void SpotifyJamButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_jamBusy || _cleanedUp) return;
        _jamBusy = true;
        int version = ++_jamFeedbackVersion;
        TimeSpan? hold = null;
        try
        {
            if ((_jamNeedsSignIn || string.IsNullOrWhiteSpace(_settings.SpotifySpDc)) && !SignInToSpotifyForJam())
            {
                ResetJamFeedback();
                return;
            }

            ShowJamFeedback(JamIdleGlyph, null, "media.jam.starting", pulse: true);
            _spotifyRemote ??= new SpotifyRemoteService();
            var result = await _spotifyRemote.StartJamAsync(_settings.SpotifySpDc);
            if (_cleanedUp) return;
            RuntimeLog.Log(JamLogTag, $"Jam start: {result.Status} (HTTP {result.HttpStatus})");
            bool copied = result.Status == SpotifyJamStatus.Started && result.Link != null &&
                await TryCopyJamLinkAsync(result.Link);

            switch (result.Status)
            {
                case SpotifyJamStatus.Started when copied:
                    ShowJamFeedback(JamDoneGlyph, JamSuccessColor, "media.jam.copied");
                    hold = JamDoneHold;
                    break;
                case SpotifyJamStatus.SessionExpired:
                case SpotifyJamStatus.NotConnected:
                    _jamNeedsSignIn = true;
                    ShowJamFeedback(JamErrorGlyph, JamErrorColor, "media.jam.expired");
                    hold = JamErrorHold;
                    break;
                case SpotifyJamStatus.Rejected:
                    ShowJamFeedback(JamErrorGlyph, JamErrorColor, "media.jam.rejected");
                    hold = JamErrorHold;
                    break;
                default:
                    ShowJamFeedback(JamErrorGlyph, JamErrorColor, "media.jam.failed");
                    hold = JamErrorHold;
                    break;
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Error(JamLogTag, ex, "Jam start failed");
            if (!_cleanedUp)
            {
                ShowJamFeedback(JamErrorGlyph, JamErrorColor, "media.jam.failed");
                hold = JamErrorHold;
            }
        }
        finally
        {
            _jamBusy = false;
        }

        if (hold is not { } delay) return;
        await Task.Delay(delay);
        if (!_cleanedUp && !_jamBusy && version == _jamFeedbackVersion) ResetJamFeedback();
    }

    /// <summary>Opens V-Notch's Spotify login once; the sp_dc cookie is stored encrypted in the settings.</summary>
    private bool SignInToSpotifyForJam()
    {
        var login = new SpotifyLoginWindow
        {
            PrivacyFeature = NetworkFeature.SpotifyRemote,
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        if (login.ShowDialog() != true || string.IsNullOrWhiteSpace(login.SpotifySpDc) || _cleanedUp) return false;

        _settings.SpotifySpDc = login.SpotifySpDc;
        _jamNeedsSignIn = false;
        _settingsService.SaveAsync(_settings).SafeFireAndForget("SETTINGS-SAVE");
        return true;
    }

    private static async Task<bool> TryCopyJamLinkAsync(string link)
    {
        // The clipboard can be held briefly by another app (or our own clipboard history).
        for (int attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(link, true);
                return true;
            }
            catch (ExternalException)
            {
                await Task.Delay(60);
            }
        }
        RuntimeLog.Warn(JamLogTag, "Could not put the Jam link on the clipboard.");
        return false;
    }

    private void ShowJamFeedback(string glyph, Color? color, string toolTipKey, bool pulse = false)
    {
        SpotifyJamIcon.Text = glyph;
        SpotifyJamIcon.Foreground = color is { } c ? new SolidColorBrush(c) : UiPalette.IconBrush;
        SetJamToolTip(toolTipKey);

        SpotifyJamIcon.BeginAnimation(OpacityProperty, null);
        SpotifyJamIcon.Opacity = 1;
        if (pulse && !AnimationConfig.ReduceMotion)
        {
            // Runs only for the few hundred ms the request takes.
            var breathe = MakeAnim(1.0, 0.35, new Duration(TimeSpan.FromMilliseconds(450)), _easeQuadOut);
            breathe.AutoReverse = true;
            breathe.RepeatBehavior = RepeatBehavior.Forever;
            SpotifyJamIcon.BeginAnimation(OpacityProperty, breathe);
        }
        else if (!pulse && !AnimationConfig.ReduceMotion)
        {
            var pop = MakeAnim(1.25, 1.0, new Duration(TimeSpan.FromMilliseconds(260)), _easeAppleOut);
            SpotifyJamButtonScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            SpotifyJamButtonScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        }
    }

    private void ResetJamFeedback()
    {
        if (SpotifyJamIcon == null) return;
        SpotifyJamIcon.BeginAnimation(OpacityProperty, null);
        SpotifyJamIcon.Opacity = 1;
        SpotifyJamIcon.Text = JamIdleGlyph;
        SpotifyJamIcon.Foreground = UiPalette.IconBrush;
        SetJamToolTip(_jamNeedsSignIn ? "media.jam.expired" : "media.jam.start");
    }

    private void SetJamToolTip(string key)
    {
        string text = Loc.Get(key);
        _jamToolTip ??= new ToolTip();
        _jamToolTip.Content = text;
        SpotifyJamButton.ToolTip = _jamToolTip;
        System.Windows.Automation.AutomationProperties.SetName(SpotifyJamButton, text);
    }

    private void SpotifyJamButton_MouseEnter(object sender, MouseEventArgs e) => AnimateJamButtonHover(1.15);

    private void SpotifyJamButton_MouseLeave(object sender, MouseEventArgs e) => AnimateJamButtonHover(1.0);

    private void AnimateJamButtonHover(double scale)
    {
        if (AnimationConfig.ReduceMotion) return;
        var anim = MakeAnim(scale, _dur150, _easeQuadOut, AnimationConfig.TargetFps);
        SpotifyJamButtonScale.BeginAnimation(ScaleTransform.ScaleXProperty, anim, HandoffBehavior.SnapshotAndReplace);
        SpotifyJamButtonScale.BeginAnimation(ScaleTransform.ScaleYProperty, anim, HandoffBehavior.SnapshotAndReplace);
    }
}
