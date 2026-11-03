using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Controllers;
using VNotch.Models;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

// Vertical slider in the music tab for the volume of the app that plays the current
// media, never the system volume. For Spotify (when signed in) it is Spotify's own
// volume, the slider inside the Spotify app, so both move together; for other sources
// (a browser, ...) it is the app's Windows mixer session. The wheel over the music
// card and over the compact notch move the same volume.
public partial class MainWindow
{
    private static readonly TimeSpan MediaVolumePollInterval = TimeSpan.FromSeconds(1);
    // Spotify pushes its volume over an open socket, so a poll only reads the cache; not
    // right after a change though (the pushes lag a moment behind a volume write).
    private static readonly TimeSpan SpotifyVolumePollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SpotifyVolumeReadHold = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SpotifyVolumeWriteSpacing = TimeSpan.FromMilliseconds(300);
    private const string MediaVolumeLogTag = "MEDIA-VOLUME";

    private readonly record struct AppVolumeTarget(SpotifyRemoteService? Spotify, string? SpotifySpDc)
    {
        public bool IsSpotify => Spotify != null;
    }

    private DateTime _mediaVolumeReadHoldUntilUtc = DateTime.MinValue;
    private DateTime _lastSpotifyVolumeWriteUtc = DateTime.MinValue; // audio queue thread only
    private float _spotifyVolumeBeforeMute = 0.5f;

    /// <summary>Picks the volume to move now (UI thread); the result is handed to the audio queue.</summary>
    private AppVolumeTarget CurrentAppVolumeTarget()
    {
        bool spotify = !_cleanedUp && _currentMediaInfo?.Platform == MediaPlatform.Spotify &&
            !string.IsNullOrWhiteSpace(_settings.SpotifySpDc);
        return spotify ? new AppVolumeTarget(_spotifyRemote ??= new SpotifyRemoteService(), _settings.SpotifySpDc) : default;
    }

    // Audio queue thread only (blocking calls).
    private bool TryReadAppVolume(AppVolumeTarget target, out float volume, out bool muted)
    {
        if (!target.IsSpotify) return _mediaService.TryGetCurrentSessionVolume(out volume, out muted);
        muted = false;
        volume = 0f;
        try
        {
            int? percent = target.Spotify!.GetVolumePercentAsync(target.SpotifySpDc).GetAwaiter().GetResult();
            volume = percent.GetValueOrDefault() / 100f;
            return percent.HasValue;
        }
        catch (Exception ex) // e.g. disposed during shutdown; callers must always get an answer
        {
            RuntimeLog.Debug(MediaVolumeLogTag, () => $"Spotify volume read failed: {ex.Message}");
            return false;
        }
    }

    // Audio queue thread only (blocking calls).
    private bool TryWriteAppVolume(AppVolumeTarget target, float volume)
    {
        if (!target.IsSpotify) return _mediaService.TrySetCurrentSessionVolume(volume);
        // Space the requests; while one waits, the queue keeps only the newest value.
        TimeSpan wait = _lastSpotifyVolumeWriteUtc + SpotifyVolumeWriteSpacing - DateTime.UtcNow;
        if (wait > TimeSpan.Zero) System.Threading.Thread.Sleep(wait);
        _lastSpotifyVolumeWriteUtc = DateTime.UtcNow;
        int percent = (int)Math.Round(Math.Clamp(volume, 0f, 1f) * 100);
        try
        {
            return target.Spotify!.SetVolumePercentAsync(target.SpotifySpDc, percent).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            RuntimeLog.Debug(MediaVolumeLogTag, () => $"Spotify volume write failed: {ex.Message}");
            return false;
        }
    }

    private float _mediaVolume = 0.5f;
    private bool _mediaVolumeMuted;
    private bool _mediaVolumeKnown;
    private bool _mediaVolumeAvailable = true;
    private bool _isDraggingMediaVolume;
    private bool _mediaVolumeReadPending;
    private bool _mediaVolumeWasLive;
    private int _mediaVolumeVersion;
    private DateTime _mediaVolumeNextPollUtc = DateTime.MinValue;

    /// <summary>The music tab is open (or opening), so the slider is on screen.</summary>
    private bool IsMediaVolumeSliderLive
    {
        get
        {
            if (_cleanedUp) return false;
            var s = _transitionCoordinator.Snapshot;
            return s.TargetView == NotchView.Media &&
                s.ShapeState is NotchShapeState.Expanding or NotchShapeState.Expanded;
        }
    }

    private bool ShouldRunVolumeSyncTimer => (_isExpanded && _isMusicExpanded) || IsMediaVolumeSliderLive;

    /// <summary>Called on every coordinator state change: starts/stops polling and reads once on open.</summary>
    private void SyncMediaVolumeSliderState()
    {
        if (_volumeSyncTimer is null) return; // state events can arrive before the constructor made it
        bool live = IsMediaVolumeSliderLive;
        SetTimerEnabled(_volumeSyncTimer, ShouldRunVolumeSyncTimer);
        if (live && !_mediaVolumeWasLive)
        {
            UpdateSpotifyJamButtonVisibility();
            _mediaVolumeNextPollUtc = DateTime.UtcNow + MediaVolumePollInterval;
            RequestMediaVolumeRead();
        }
        else if (!live && _mediaVolumeWasLive)
        {
            // The source may change while the tab is closed; never wheel from a stale baseline.
            _mediaVolumeKnown = false;
            EndMediaVolumeDrag();
        }
        _mediaVolumeWasLive = live;
    }

    private void PollMediaVolumeIfDue()
    {
        if (!IsMediaVolumeSliderLive) return;
        var now = DateTime.UtcNow;
        if (now < _mediaVolumeNextPollUtc) return;
        var target = CurrentAppVolumeTarget();
        _mediaVolumeNextPollUtc = now + (target.IsSpotify ? SpotifyVolumePollInterval : MediaVolumePollInterval);
        UpdateSpotifyJamButtonVisibility(); // the source can switch while the tab is open
        if (!_isDraggingMediaVolume && now >= _mediaVolumeReadHoldUntilUtc) RequestMediaVolumeRead();
    }

    private void RequestMediaVolumeRead()
    {
        if (_mediaVolumeReadPending || _isDraggingMediaVolume) return;
        _mediaVolumeReadPending = true;
        int version = _mediaVolumeVersion;
        var target = CurrentAppVolumeTarget();
        // Same serialized queue as the writes, so a read never overtakes a pending write.
        _mediaVolumeWrites.Post("media-volume-read", () =>
        {
            bool ok = false;
            float volume = 0f;
            bool muted = false;
            try
            {
                ok = TryReadAppVolume(target, out volume, out muted);
            }
            catch (Exception ex)
            {
                RuntimeLog.Error(MediaVolumeLogTag, ex, "Read app volume failed");
            }
            Dispatcher.BeginInvoke(() =>
            {
                _mediaVolumeReadPending = false;
                if (_cleanedUp || _isDraggingMediaVolume || version != _mediaVolumeVersion) return;
                _mediaVolumeAvailable = ok;
                if (ok)
                {
                    _mediaVolume = Math.Clamp(volume, 0f, 1f);
                    _mediaVolumeMuted = muted;
                    _mediaVolumeKnown = true;
                }
                RenderMediaVolume(animate: true);
            });
        });
    }

    private void SetMediaVolumeFromUser(float volume, bool glide = false)
    {
        _mediaVolumeVersion++;
        _mediaVolume = Math.Clamp(volume, 0f, 1f);
        _mediaVolumeKnown = true;
        // A write above zero also unmutes the session (MediaSessionVolumeService.TrySetVolume).
        if (_mediaVolume > 0.001f) _mediaVolumeMuted = false;
        // The compact-notch wheel keeps its own baseline; make it re-read after this change.
        _volumeSynced = false;
        RenderMediaVolume(animate: glide);

        float level = _mediaVolume;
        int version = _mediaVolumeVersion;
        var target = CurrentAppVolumeTarget();
        if (target.IsSpotify) _mediaVolumeReadHoldUntilUtc = DateTime.UtcNow + SpotifyVolumeReadHold;
        _mediaVolumeWrites.Post("volume", () =>
        {
            if (TryWriteAppVolume(target, level)) return;
            Dispatcher.BeginInvoke(() =>
            {
                if (_cleanedUp || version != _mediaVolumeVersion) return;
                RuntimeLog.Log(MediaVolumeLogTag, "App volume write failed; re-reading.");
                RequestMediaVolumeRead();
            });
        });
    }

    private void RenderMediaVolume(bool animate)
    {
        if (MediaVolumeFillScale == null) return;

        double target = Math.Clamp(_mediaVolume, 0, 1);
        var scale = MediaVolumeFillScale;
        double current = scale.ScaleY;
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        scale.ScaleY = target;
        if (animate && !AnimationConfig.ReduceMotion && Math.Abs(current - target) > 0.0001)
        {
            var animation = MakeAnim(current, target, MediaVolumeGlide, _easeAppleOut);
            animation.FillBehavior = FillBehavior.Stop;
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation, HandoffBehavior.SnapshotAndReplace);
        }

        MediaVolumeFill.Opacity = _mediaVolumeMuted ? 0.35 : 1.0;
        string glyph = MediaVolumeGlyph(_mediaVolume, _mediaVolumeMuted);
        if (MediaVolumeIcon.Text != glyph)
        {
            // The speaker hops when it gains or loses a wave or mutes: up for louder, a dip for quieter.
            int before = MediaVolumeLevel(MediaVolumeIcon.Text), after = MediaVolumeLevel(glyph);
            MediaVolumeIcon.Text = glyph;
            if (before >= 0) HopMediaVolumeIcon(after > before ? 1.3 : 0.75);
        }
        MediaVolumeColumn.Opacity = _mediaVolumeAvailable ? 1.0 : 0.35;
        MediaVolumeBar.SetCurrentValue(VNotch.Controls.AccessibleVolumePanel.ValueProperty, target);
    }

    // Same thresholds as the native audio-mixer icon (vn_audio_mixer_select_icon_glyph).
    private static string MediaVolumeGlyph(float volume, bool muted) =>
        muted || volume <= 0.01f ? ""
        : volume < 0.33f ? ""
        : volume < 0.66f ? ""
        : "";

    private void MediaWidget_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!IsMediaVolumeSliderLive || e.Delta == 0) return;
        e.Handled = true;
        if (_isAnimating || _isDraggingMediaVolume) return;
        if (!_mediaVolumeKnown)
        {
            // No baseline yet: a step from the 50 % placeholder would jump the app's volume.
            RequestMediaVolumeRead();
            return;
        }
        if ((e.Delta > 0 && _mediaVolume >= 0.999f) || (e.Delta < 0 && _mediaVolume <= 0.001f))
        {
            BumpMediaVolume(up: e.Delta > 0);
            return;
        }
        SetMediaVolumeFromUser(_mediaVolume + (e.Delta / 120f) * VolumeScrollStep, glide: true);
    }

    private void MediaVolumeBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _isDraggingMediaVolume = true;
        MediaVolumeBar.CaptureMouse();
        SetMediaVolumeFromPointer(e);
    }

    private void MediaVolumeBar_MouseMove(object sender, MouseEventArgs e)
    {
        if (_isDraggingMediaVolume && e.LeftButton == MouseButtonState.Pressed)
            SetMediaVolumeFromPointer(e);
    }

    private void MediaVolumeBar_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDraggingMediaVolume) return;
        e.Handled = true;
        EndMediaVolumeDrag();
    }

    private void MediaVolumeBar_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (!_isDraggingMediaVolume) return;
        _isDraggingMediaVolume = false;
        SnapBackMediaVolume();
        if (!MediaVolumeBar.IsMouseOver) AnimateMediaVolumeTrackWidth(1.0);
    }

    private void EndMediaVolumeDrag()
    {
        if (!_isDraggingMediaVolume) return;
        _isDraggingMediaVolume = false;
        SnapBackMediaVolume();
        if (MediaVolumeBar.IsMouseCaptured) MediaVolumeBar.ReleaseMouseCapture();
        if (!MediaVolumeBar.IsMouseOver) AnimateMediaVolumeTrackWidth(1.0);
    }

    private void SetMediaVolumeFromPointer(MouseEventArgs e)
    {
        double height = MediaVolumeTrack.ActualHeight;
        if (height <= 0) return;
        double y = e.GetPosition(MediaVolumeTrack).Y;
        SetMediaVolumeFromUser((float)Math.Clamp(1.0 - y / height, 0.0, 1.0));
        double past = y < 0 ? -y : y > height ? y - height : 0;
        StretchMediaVolume(RubberBand(past, height) / height, up: y < 0);
    }

    private void MediaVolumeBar_ValueRequested(object sender, RoutedPropertyChangedEventArgs<double> e)
        => SetMediaVolumeFromUser((float)e.NewValue);

    private void MediaVolumeMuteButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (CurrentAppVolumeTarget().IsSpotify)
        {
            // Spotify has no mute: go to 0 and come back to the level before.
            if (!_mediaVolumeKnown) return;
            if (_mediaVolume > 0.01f)
            {
                _spotifyVolumeBeforeMute = _mediaVolume;
                SetMediaVolumeFromUser(0f);
            }
            else SetMediaVolumeFromUser(_spotifyVolumeBeforeMute > 0.01f ? _spotifyVolumeBeforeMute : 0.5f);
            return;
        }

        int version = ++_mediaVolumeVersion;
        // Every click is a toggle, so keep each one (unique key) instead of coalescing.
        _mediaVolumeWrites.Post($"media-mute:{version}", () =>
        {
            bool ok = _mediaService.TryToggleCurrentSessionMute();
            Dispatcher.BeginInvoke(() =>
            {
                if (_cleanedUp) return;
                if (!ok) RuntimeLog.Log(MediaVolumeLogTag, "App mute toggle failed.");
                RequestMediaVolumeRead();
            });
        });
    }

    private void MediaVolumeBar_MouseEnter(object sender, MouseEventArgs e) => AnimateMediaVolumeTrackWidth(1.5);

    private void MediaVolumeBar_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_isDraggingMediaVolume) AnimateMediaVolumeTrackWidth(1.0);
    }

    private void AnimateMediaVolumeTrackWidth(double scaleX)
    {
        var scale = MediaVolumeTrackScale;
        if (AnimationConfig.ReduceMotion)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.ScaleX = scaleX;
            return;
        }
        // Widens with a little overshoot, like a finger landing on it.
        var widen = new DoubleAnimation(scaleX, TimeSpan.FromMilliseconds(scaleX > 1 ? 380 : 220))
        {
            EasingFunction = scaleX > 1 ? MediaVolumeSpringOut : _easeQuadOut
        };
        Timeline.SetDesiredFrameRate(widen, AnimationConfig.TargetFps);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, widen, HandoffBehavior.SnapshotAndReplace);
    }

    #region Rubber band

    private static readonly Duration MediaVolumeGlide = new(TimeSpan.FromMilliseconds(220));
    private static readonly IEasingFunction MediaVolumeSpringOut = FreezeVolumeEase(new BackEase { Amplitude = 0.6, EasingMode = EasingMode.EaseOut });
    private static readonly IEasingFunction MediaVolumeSnapBack = FreezeVolumeEase(new ElasticEase { Oscillations = 2, Springiness = 6, EasingMode = EasingMode.EaseOut });

    private static IEasingFunction FreezeVolumeEase(EasingFunctionBase ease)
    {
        ease.Freeze();
        return ease;
    }

    // 0 muted .. 3 loud, from the speaker glyph; -1 before the first glyph.
    private static int MediaVolumeLevel(string glyph) =>
        glyph == MediaVolumeGlyph(0, true) ? 0 : glyph == MediaVolumeGlyph(0.2f, false) ? 1 : glyph == MediaVolumeGlyph(0.5f, false) ? 2 : glyph == MediaVolumeGlyph(1, false) ? 3 : -1;

    /// <summary>
    /// How far the bar gives when pulled <paramref name="past"/> its end: the rubber-band curve of iOS
    /// scroll views, easy at first and stiffer the further it goes, at most half the bar.
    /// </summary>
    internal static double RubberBand(double past, double length)
    {
        if (past <= 0 || length <= 0) return 0;
        const double Stiffness = 0.55;
        return (1 - 1 / (past * Stiffness / length + 1)) * length * 0.5;
    }

    /// <summary>Pulled past an end: the bar stretches that way, anchored at the other end.</summary>
    private void StretchMediaVolume(double stretch, bool up)
    {
        if (AnimationConfig.ReduceMotion) return;
        var scale = MediaVolumeTrackScale;
        if (stretch <= 0)
        {
            if (Math.Abs(scale.ScaleY - 1) > 0.001) SnapBackMediaVolume();
            return;
        }
        MediaVolumeTrack.RenderTransformOrigin = new Point(0.5, up ? 1 : 0);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        scale.ScaleY = 1 + stretch;
    }

    /// <summary>Let go: the bar springs back to its length with a wobble.</summary>
    private void SnapBackMediaVolume()
    {
        var scale = MediaVolumeTrackScale;
        double from = scale.ScaleY;
        if (Math.Abs(from - 1) < 0.001 || AnimationConfig.ReduceMotion)
        {
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            scale.ScaleY = 1;
            return;
        }
        var back = new DoubleAnimation(from, 1, TimeSpan.FromMilliseconds(650)) { EasingFunction = MediaVolumeSnapBack };
        Timeline.SetDesiredFrameRate(back, AnimationConfig.TargetFps);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, back, HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>The wheel pushes against an end: the bar bumps out that way and wobbles back.</summary>
    private void BumpMediaVolume(bool up)
    {
        if (AnimationConfig.ReduceMotion) return;
        MediaVolumeTrack.RenderTransformOrigin = new Point(0.5, up ? 1 : 0);
        var bump = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(620) };
        bump.KeyFrames.Add(new EasingDoubleKeyFrame(1.09, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(90)), _easeQuadOut));
        bump.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(620)), MediaVolumeSnapBack));
        Timeline.SetDesiredFrameRate(bump, AnimationConfig.TargetFps);
        MediaVolumeTrackScale.BeginAnimation(ScaleTransform.ScaleYProperty, bump, HandoffBehavior.SnapshotAndReplace);
        HopMediaVolumeIcon(up ? 1.25 : 0.8);
    }

    private void HopMediaVolumeIcon(double peak)
    {
        if (AnimationConfig.ReduceMotion || !_isExpanded) return;
        var scale = MediaVolumeIcon.RenderTransform as ScaleTransform ?? new ScaleTransform(1, 1);
        MediaVolumeIcon.RenderTransform = scale;
        MediaVolumeIcon.RenderTransformOrigin = new Point(0.5, 0.5);
        var hop = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(520) };
        hop.KeyFrames.Add(new EasingDoubleKeyFrame(peak, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(110)), _easeQuadOut));
        hop.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(520)), MediaVolumeSnapBack));
        Timeline.SetDesiredFrameRate(hop, AnimationConfig.TargetFps);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, hop);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, hop);
    }

    #endregion

    private void MediaVolumeTrack_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        MediaVolumeTrackClip.Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height);
        double radius = e.NewSize.Width / 2.0;
        MediaVolumeTrackClip.RadiusX = radius;
        MediaVolumeTrackClip.RadiusY = radius;
    }

    /// <summary>Tints the fill like the progress bar (album colour, or the default palette).</summary>
    private void TintMediaVolumeFill(AnimationTimeline startColor, AnimationTimeline endColor)
    {
        MediaVolumeGradientStart.BeginAnimation(GradientStop.ColorProperty, startColor);
        MediaVolumeGradientEnd.BeginAnimation(GradientStop.ColorProperty, endColor);
    }
}
