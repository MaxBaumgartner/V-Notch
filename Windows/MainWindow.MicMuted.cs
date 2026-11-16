using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VNotch.Services;
using VNotch.Services.ControlCenter;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

public partial class MainWindow
{
    #region Muted mic in a call

    // Talking into a muted mic is the mistake worth catching: a struck-out red mic shows while the mic is muted
    // (at the mic itself: a Wave XLR's mute sets the Windows flag) and a call records, or while Discord has you
    // muted or deafened in a voice channel. In the notch it takes the privacy dot's place; when a fullscreen game
    // has hidden the notch, it shows as a small tab at the top edge, which no click lands on.
    //
    // "A call records" cannot come from the privacy dot: that one needs sound coming through, and a muted mic
    // sends silence. A call keeps its recording session running while muted, so: an active capture session of any
    // app but those that hold the mic all day.
    private static readonly TimeSpan MicMutePoll = TimeSpan.FromSeconds(1);
    private static readonly Regex AlwaysRecordingApps = new("wave ?link|elgato|voicemeeter|nvidia|nvcontainer|medal|audiodg", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private DiscordVoice? _discordVoice;
    private DispatcherTimer? _micMutePoll;
    private bool _micInCall;
    private bool _micMutedWindows;
    private bool _micMutedShown;
    private bool _micMutedGlyphShown;
    private int _micMutedGlyphToken;
    private bool _micMutedTabShown;

    private void InitializeMicMuted()
    {
        string tokenFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "V-Notch", "discord-voice.token");
        _discordVoice = new DiscordVoice(() => _settings?.DiscordClientId ?? "", () => _settings?.EnableLocalOnlyMode ?? true, tokenFile);
        _discordVoice.MutedChanged += _ => Dispatcher.BeginInvoke(DispatcherPriority.Background, UpdateMicMuted);
        _discordVoice.Start();
        // One cheap question a second (is the mic muted?); only while it is, who records.
        _micMutePoll = new DispatcherTimer(DispatcherPriority.Background) { Interval = MicMutePoll };
        _micMutePoll.Tick += (_, _) => PollMicMute();
        _micMutePoll.Start();
    }

    private void DisposeMicMuted()
    {
        _micMutePoll?.Stop();
        _discordVoice?.Dispose();
    }

    /// <summary>Recording all day without being a call: the audio mixer (Wave Link), NVIDIA's instant replay, Medal.</summary>
    internal static bool IsCallRecorder(string processName) =>
        processName.Length > 0 && !AlwaysRecordingApps.IsMatch(processName);

    private DateTime _micPollErrorLoggedUtc = DateTime.MinValue;

    private void PollMicMute()
    {
        bool muted, inCall;
        try
        {
            muted = MicrophoneControl.IsMuted() == true;
            inCall = muted && MicrophoneControl.ActiveRecorders().Select(ProcessName).Any(IsCallRecorder);
        }
        catch (Exception ex)
        {
            // This runs every second on the UI thread: an audio API hiccup must never take the notch down.
            if (DateTime.UtcNow - _micPollErrorLoggedUtc > TimeSpan.FromMinutes(1))
            {
                _micPollErrorLoggedUtc = DateTime.UtcNow;
                RuntimeLog.Warn("MIC-MUTED", $"Mic check failed: {ex.GetType().Name}: {ex.Message}");
            }
            return;
        }
        if (muted == _micMutedWindows && inCall == _micInCall) return;
        _micMutedWindows = muted;
        _micInCall = inCall;
        UpdateMicMuted();
    }

    private static string ProcessName(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return "";
        }
    }

    internal static bool ShouldShowMicMuted(bool micInCall, bool windowsMuted, bool discordMutedInVoice) =>
        (micInCall && windowsMuted) || discordMutedInVoice;

    private void UpdateMicMuted()
    {
        if (_cleanedUp) return;
        bool shown = ShouldShowMicMuted(_micInCall, _micMutedWindows, _discordVoice?.IsMuted == true);
        if (shown == _micMutedShown)
        {
            ApplyMicMutedPresentation();
            return;
        }
        _micMutedShown = shown;
        UpdateMicRestGlyph();
        RuntimeLog.Log("MIC-MUTED", $"Muted in a call: {shown} (call {_micInCall}, Windows {_micMutedWindows}, Discord {_discordVoice?.IsMuted == true})");
        // The dot gives way to the struck-out mic, or comes back.
        SyncPrivacyDotVisibilityForCurrentView();
    }

    /// <summary>The glyph where the dot sits while the notch shows and has room; the tab while a fullscreen app hides it.</summary>
    private void ApplyMicMutedPresentation()
    {
        bool notchShown = _fullscreenSlideVisible;
        SetMicMutedGlyph(_micMutedShown && notchShown && !IsPrivacySpotTaken);
        SetMicMutedTab(_micMutedShown && !notchShown);
    }

    /// <summary>The call warning pops in once and fades smaller when the mic is back.</summary>
    private void SetMicMutedGlyph(bool show)
    {
        if (show == _micMutedGlyphShown) return;
        _micMutedGlyphShown = show;
        int token = ++_micMutedGlyphToken;
        bool entering = MicMutedGlyph.Visibility != Visibility.Visible;
        if (show)
        {
            var dot = PrivacyIndicatorPanel.Margin;
            MicMutedGlyph.Margin = new Thickness(0, Math.Max(0, dot.Top - 2), Math.Max(0, dot.Right - 2), 0);
            MicMutedGlyph.Visibility = Visibility.Visible;
        }
        Motion.To(MicMutedGlyphScale, ScaleTransform.ScaleXProperty, show ? 1 : Motion.ExitToScale,
            show ? Motion.TileScale : Motion.Exit, from: show && entering ? Motion.TileFromScale : null);
        Motion.To(MicMutedGlyphScale, ScaleTransform.ScaleYProperty, show ? 1 : Motion.ExitToScale,
            show ? Motion.TileScale : Motion.Exit, from: show && entering ? Motion.TileFromScale : null);
        Motion.To(MicMutedGlyph, OpacityProperty, show ? 1 : 0, show ? Motion.Tile : Motion.Exit,
            from: show && entering ? 0 : null, done: () =>
            {
                if (token != _micMutedGlyphToken || _micMutedGlyphShown) return;
                Motion.Stop(MicMutedGlyphScale, ScaleTransform.ScaleXProperty);
                Motion.Stop(MicMutedGlyphScale, ScaleTransform.ScaleYProperty);
                MicMutedGlyph.Visibility = Visibility.Collapsed;
            });
    }

    private void SetMicMutedTab(bool show)
    {
        if (show == _micMutedTabShown) return;
        _micMutedTabShown = show;
        bool entering = MicMutedTab.Visibility != Visibility.Visible;
        if (show) MicMutedTab.Visibility = Visibility.Visible;
        Motion.To(MicMutedTabSlide, TranslateTransform.YProperty, show ? 0 : -22,
            show ? Motion.Pop : Motion.PopBack, from: show && entering ? -22 : null, done: () =>
            {
                if (!_micMutedTabShown) MicMutedTab.Visibility = Visibility.Collapsed;
            });
    }

    #endregion
}
