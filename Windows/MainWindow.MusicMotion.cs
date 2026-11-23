using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using VNotch.Controls;
using VNotch.Models;
using VNotch.Services;

namespace VNotch;

public partial class MainWindow
{
    // These covers are disposable visual layers. Only the existing thumbnail pipeline owns artwork state.
    private sealed class MusicCoverLayer
    {
        internal readonly TranslateTransform Translate = new();
        internal readonly ScaleTransform Scale = new();
        internal readonly Border View;
        internal MusicCoverLayer()
        {
            var transforms = new TransformGroup();
            transforms.Children.Add(Scale);
            transforms.Children.Add(Translate);
            View = new Border { Width = 102, Height = 102, CornerRadius = new CornerRadius(14),
                RenderTransform = transforms, RenderTransformOrigin = new Point(.5, .5), IsHitTestVisible = false };
        }
        internal void Paint(ImageSource? image, Color accent) => View.Background = image == null
            ? new SolidColorBrush(Color.FromArgb(35, accent.R, accent.G, accent.B))
            : new ImageBrush(image) { Stretch = Stretch.UniformToFill };
    }

    private Grid? _musicCoverHost;
    private UIElement? _musicCanonicalCover;
    private MusicCoverLayer? _musicCover, _musicNeighbour;
    private readonly TranslateTransform _musicTextDrag = new();
    private bool _musicCoverActive, _musicCoverMoving, _musicDragging, _musicAwaitingTrack;
    private bool _musicShellFeedback;
    private double _musicDragOrigin;
    private double _musicReleaseVelocity;
    private int _musicDirection = 1, _musicMotionGeneration;
    private DateTime _musicRequestAt;
    private DispatcherTimer? _musicWheelEnd;
    private DispatcherTimer? _musicRequestTimeout;
    private Win32Interop.LowLevelKeyboardHook? _musicMediaKeys;
    private double _musicWheelX;
    private bool _musicCoverPressed;

    private bool IsMusicCoverSource(DependencyObject? source)
    {
        while (source != null && source is not Window)
        {
            if (ReferenceEquals(source, ThumbnailBorder)) return true;
            source = ShellInputParent(source);
        }
        return false;
    }

    private bool CanSwipeMusic => _isExpanded && MediaPlayback.IsVisible && _isNotchVisible && !_isHiddenByFullscreen &&
        _transitionCoordinator.TargetView == NotchView.Media && _currentMediaInfo?.IsAnyMediaPlaying == true;

    private void InitializeMusicMotion()
    {
        _viewModel.Media.NextTrackTriggered += MusicNextRequested;
        _viewModel.Media.PreviousTrackTriggered += MusicPreviousRequested;
        MediaPlayback.IsVisibleChanged += (_, _) => RefreshMusicMotionVisibility();
        InlinePlayPauseButton.IsVisibleChanged += (_, _) =>
        {
            if (!InlinePlayPauseButton.IsVisible) Motion.Stop(this, InlinePlaybackMorphProperty, _isPlaying ? 0 : 1);
        };
        SpotifyJamButton.IsVisibleChanged += (_, _) =>
        {
            if (SpotifyJamButton.IsVisible) return;
            Motion.Stop(SpotifyJamButtonScale, ScaleTransform.ScaleXProperty, 1);
            Motion.Stop(SpotifyJamButtonScale, ScaleTransform.ScaleYProperty, 1);
        };
    }

    private void RefreshMusicMotionVisibility()
    {
        if (!MediaPlayback.IsVisible || !_isNotchVisible || _isHiddenByFullscreen || _cleanedUp)
        {
            Motion.Stop(MediaPlayback, MediaPlaybackView.PlaybackMorphProperty, _isPlaying ? 0 : 1);
            Motion.Stop(this, InlinePlaybackMorphProperty, _isPlaying ? 0 : 1);
            _musicMediaKeys?.Dispose();
            _musicMediaKeys = null;
            ResetMusicMotion();
            return;
        }
        if (_musicMediaKeys != null || MotionLabNoMedia) return;
        _musicMediaKeys = new Win32Interop.LowLevelKeyboardHook((key, message) =>
        {
            // Observe physical media keys; the OS still delivers them to the player.
            if ((message == 0x0100 || message == 0x0104) && (key.flags & 0x10) == 0)
            {
                if (key.vkCode == Win32Interop.VK_MEDIA_NEXT_TRACK) MusicTransportRequested(1);
                else if (key.vkCode == Win32Interop.VK_MEDIA_PREV_TRACK) MusicTransportRequested(-1);
            }
            return false;
        });
        _musicMediaKeys.Install();
    }

    private void MusicNextRequested(object? sender, EventArgs e) => MusicTransportRequested(1);
    private void MusicPreviousRequested(object? sender, EventArgs e) => MusicTransportRequested(-1);

    private void MusicTransportRequested(int direction)
    {
        if (!CanSwipeMusic) return;
        _musicAwaitingTrack = true;
        _musicRequestAt = DateTime.UtcNow;
        BeginMusicSkip(direction, _musicDragging ? _musicReleaseVelocity : 0);
        _musicRequestTimeout ??= new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromSeconds(2) };
        _musicRequestTimeout.Stop();
        _musicRequestTimeout.Tick -= ExpireMusicRequest;
        _musicRequestTimeout.Tick += ExpireMusicRequest;
        _musicRequestTimeout.Start();
        if (direction > 0) ButtonFeedback.PlayNextSkipAnimation(MediaPlayback.NextArrow0, MediaPlayback.NextArrow1);
        else ButtonFeedback.PlayPrevSkipAnimation(MediaPlayback.PrevArrow0, MediaPlayback.PrevArrow2);
    }

    private void ExpireMusicRequest(object? sender, EventArgs e)
    {
        _musicRequestTimeout!.Stop();
        if (!_musicAwaitingTrack) return;
        _musicAwaitingTrack = false;
        // Previous can restart the current song; a failed command also leaves its cover in place.
        if (_musicCoverActive) _musicCover!.Paint(_currentMediaInfo?.Thumbnail, MusicCoverAccent);
        RestoreMusicCanonicalCover();
    }

    private void EnsureMusicCoverLayers()
    {
        if (_musicCoverHost != null) return;
        _musicCanonicalCover = ThumbnailBorder.Child;
        ThumbnailBorder.Child = null;
        _musicCoverHost = new Grid { ClipToBounds = false };
        _musicCoverHost.Children.Add(_musicCanonicalCover);
        _musicNeighbour = new MusicCoverLayer();
        _musicCover = new MusicCoverLayer();
        _musicCoverHost.Children.Add(_musicNeighbour.View);
        _musicCoverHost.Children.Add(_musicCover.View);
        ThumbnailBorder.Child = _musicCoverHost;
        _musicCover.View.Visibility = _musicNeighbour.View.Visibility = Visibility.Collapsed;
        MediaPlayback.TrackTextBlock.RenderTransform = _musicTextDrag;
    }

    private Color MusicCoverAccent => _lastDominantColor == Colors.Transparent ? UiPalette.PrimaryColor : _lastDominantColor;

    private void ActivateMusicCover()
    {
        EnsureMusicCoverLayers();
        if (_musicCoverActive) return;
        _musicCoverActive = true;
        _musicCover!.Paint(ThumbnailImageNext.Source ?? ThumbnailImage.Source, MusicCoverAccent);
        _musicCover.View.Visibility = _musicNeighbour!.View.Visibility = Visibility.Visible;
        Motion.Stop(_musicCover.Translate, TranslateTransform.XProperty, 0);
        Motion.Stop(_musicCover.Scale, ScaleTransform.ScaleXProperty, 1);
        Motion.Stop(_musicCover.Scale, ScaleTransform.ScaleYProperty, 1);
        Motion.Stop(_musicCover.View, OpacityProperty, 1);
        Motion.Stop(_musicNeighbour.View, OpacityProperty, 0);
        _musicCanonicalCover!.Opacity = 0;
    }

    internal void MoveMusicSwipe(double distance)
    {
        if (!_musicDragging)
        {
            ActivateMusicCover();
            _musicDragging = true;
            _musicReleaseVelocity = 0;
            _musicMotionGeneration++;
            _musicDragOrigin = _musicCover!.Translate.X + (_gestureTranslate?.X ?? 0);
        }
        double magnitude = Math.Abs(distance);
        double x = _musicDragOrigin + Math.Sign(distance) * (magnitude <= 60 ? magnitude : 60 + ShellRubber(magnitude - 60, 46));
        ApplyGestureDragFeedback(distance);
        _musicShellFeedback = true;
        double shellX = _gestureTranslate!.X;
        _musicDirection = distance < 0 ? 1 : -1;
        _musicNeighbour!.Paint(null, MusicCoverAccent);
        Panel.SetZIndex(_musicCover!.View, 2);
        Panel.SetZIndex(_musicNeighbour.View, 1);
        Motion.Stop(_musicCover.Translate, TranslateTransform.XProperty, x - shellX);
        double scale = 1 - Math.Min(.2, Math.Abs(x) * .00175);
        Motion.Stop(_musicCover.Scale, ScaleTransform.ScaleXProperty, scale);
        Motion.Stop(_musicCover.Scale, ScaleTransform.ScaleYProperty, scale);
        Motion.Stop(_musicNeighbour.Translate, TranslateTransform.XProperty, _musicDirection * 61 + x * .3 - shellX);
        Motion.Stop(_musicNeighbour.Scale, ScaleTransform.ScaleXProperty, .75);
        Motion.Stop(_musicNeighbour.Scale, ScaleTransform.ScaleYProperty, .75);
        Motion.Stop(_musicNeighbour.View, OpacityProperty, Math.Min(.75, Math.Abs(x) / 90));
        Motion.Stop(_musicTextDrag, TranslateTransform.XProperty, x * .45 - shellX);
        Motion.Stop(MediaPlayback.TrackTextBlock, OpacityProperty, 1 - Math.Min(.65, Math.Abs(x) / 130));
    }

    internal void FinishMusicSwipe(bool canceled, DateTime timestamp)
    {
        if (!_musicDragging) return;
        double velocity = (timestamp - _shellLastSampleAt).TotalMilliseconds < 60 ? _gestureController.HorizontalVelocity : 0;
        _musicReleaseVelocity = velocity;
        if (!canceled && _gestureController.ShouldSkipTrack &&
            (Math.Abs(_gestureController.AccumulatedX) > 55 || Math.Abs(velocity) > 450))
        {
            var command = _musicDirection > 0 ? _viewModel.NextTrackCommand : _viewModel.PreviousTrackCommand;
            command.ExecuteAsync(null).SafeFireAndForget("MUSIC-SWIPE");
            // A throttled command emits no transport event and must return the grabbed cover.
            if (_musicDragging) ReturnMusicSwipe(velocity);
        }
        else ReturnMusicSwipe(velocity);
    }

    private void ReturnMusicSwipe(double velocity)
    {
        _musicDragging = false;
        _musicCoverMoving = true;
        int generation = ++_musicMotionGeneration;
        double shellVelocity = Math.Clamp(velocity * .3, -1200, 1200);
        Motion.To(_gestureTranslate!, TranslateTransform.XProperty, 0, Motion.Pop, velocity: shellVelocity);
        Motion.To(_gestureShadowTranslate!, TranslateTransform.XProperty, 0, Motion.Pop, velocity: shellVelocity);
        Motion.To(_musicCover!.Translate, TranslateTransform.XProperty, 0, Motion.Pop, velocity: velocity - shellVelocity,
            done: () => FinishMusicCoverMotion(generation));
        Motion.To(_musicCover.Scale, ScaleTransform.ScaleXProperty, 1, Motion.Pop);
        Motion.To(_musicCover.Scale, ScaleTransform.ScaleYProperty, 1, Motion.Pop);
        Motion.To(_musicNeighbour!.View, OpacityProperty, 0, Motion.Exit);
        ReturnMusicText(velocity);
        Motion.To(_musicCover.View, OpacityProperty, 1, Motion.Pop);
    }

    private void ReturnMusicText(double? velocity)
    {
        double shellVelocity = _gestureTranslate == null ? 0 : Motion.VelocityOf(_gestureTranslate, TranslateTransform.XProperty);
        Motion.To(_musicTextDrag, TranslateTransform.XProperty, 0, Motion.Pop,
            velocity: velocity.HasValue ? velocity.Value * .45 - shellVelocity : null);
        Motion.To(MediaPlayback.TrackTextBlock, OpacityProperty, 1, Motion.Tile);
    }

    internal void BeginMusicSkip(int direction, double velocity)
    {
        ActivateMusicCover();
        _musicDirection = direction;
        bool dragged = _musicDragging;
        if (dragged)
        {
            _gestureReleaseVelocity = Math.Clamp(velocity * .3, -1200, 1200);
            PlayGestureSwipeFeedback(direction > 0);
        }
        double shellVelocity = _gestureTranslate == null ? 0 : Motion.VelocityOf(_gestureTranslate, TranslateTransform.XProperty);
        _musicDragging = false;
        _musicCoverMoving = true;
        int generation = ++_musicMotionGeneration;
        var outgoing = _musicCover!;
        var incoming = _musicNeighbour!;
        incoming.Paint(null, MusicCoverAccent);
        Panel.SetZIndex(outgoing.View, 1);
        Panel.SetZIndex(incoming.View, 2);
        Motion.To(outgoing.Translate, TranslateTransform.XProperty, -direction * 115, Motion.Exit,
            velocity: dragged ? velocity - shellVelocity : null);
        Motion.To(outgoing.Scale, ScaleTransform.ScaleXProperty, .68, Motion.Exit);
        Motion.To(outgoing.Scale, ScaleTransform.ScaleYProperty, .68, Motion.Exit);
        Motion.To(outgoing.View, OpacityProperty, 0, Motion.Exit);
        if (incoming.View.Opacity < .05)
        {
            Motion.Stop(incoming.Translate, TranslateTransform.XProperty, direction * 61);
            Motion.Stop(incoming.Scale, ScaleTransform.ScaleXProperty, .75);
            Motion.Stop(incoming.Scale, ScaleTransform.ScaleYProperty, .75);
        }
        _musicCover = incoming;
        _musicNeighbour = outgoing;
        ReturnMusicText(dragged ? velocity : null);
        Motion.To(incoming.Scale, ScaleTransform.ScaleXProperty, 1, Motion.Pop);
        Motion.To(incoming.Scale, ScaleTransform.ScaleYProperty, 1, Motion.Pop);
        Motion.To(incoming.View, OpacityProperty, 1, Motion.Tile);
        Motion.To(incoming.Translate, TranslateTransform.XProperty, 0, Motion.Flick,
            velocity: dragged ? velocity * .5 - shellVelocity : null,
            done: () => FinishMusicCoverMotion(generation));
    }

    private void FinishMusicCoverMotion(int generation)
    {
        if (generation != _musicMotionGeneration) return;
        _musicCoverMoving = false;
        RestoreMusicCanonicalCover();
    }

    private void PresentMusicTrack(MediaInfo info, bool newTrack)
    {
        if (!CanSwipeMusic || info.IsMetadataPending) return;
        if (newTrack)
        {
            bool recentRequest = DateTime.UtcNow - _musicRequestAt < TimeSpan.FromSeconds(2);
            if (!_musicAwaitingTrack || !recentRequest) BeginMusicSkip(recentRequest ? _musicDirection : 1, 0);
            _musicAwaitingTrack = false;
            _musicRequestTimeout?.Stop();
        }
        if (_musicCoverActive) _musicCover!.Paint(info.Thumbnail, MusicCoverAccent);
    }

    private void RestoreMusicCanonicalCover()
    {
        if (!_musicCoverActive || _musicDragging || _musicCoverMoving || _musicAwaitingTrack ||
            _isThumbnailSwitchActive || _pendingFlipThumbnail != null ||
            _currentMediaInfo?.Thumbnail == null || !ReferenceEquals(ThumbnailImage.Source, _currentMediaInfo.Thumbnail)) return;
        ResetMusicMotion();
    }

    private void ResetMusicMotion()
    {
        if (!_musicCoverActive && !_musicDragging && !_musicCoverMoving && !_musicAwaitingTrack &&
            _musicWheelEnd?.IsEnabled != true) return;
        _musicWheelEnd?.Stop();
        _musicRequestTimeout?.Stop();
        _musicMotionGeneration++;
        _musicCoverActive = _musicCoverMoving = _musicDragging = _musicAwaitingTrack = false;
        if (_musicShellFeedback)
        {
            Motion.Stop(_gestureTranslate!, TranslateTransform.XProperty, 0);
            Motion.Stop(_gestureShadowTranslate!, TranslateTransform.XProperty, 0);
            Motion.Stop(NotchScale, ScaleTransform.ScaleXProperty, 1);
            Motion.Stop(NotchScale, ScaleTransform.ScaleYProperty, 1);
            Motion.Stop(NotchShadowScale, ScaleTransform.ScaleXProperty, 1);
            Motion.Stop(NotchShadowScale, ScaleTransform.ScaleYProperty, 1);
            _musicShellFeedback = false;
        }
        if (_musicCanonicalCover != null) _musicCanonicalCover.Opacity = 1;
        foreach (var layer in new[] { _musicCover, _musicNeighbour })
        {
            if (layer == null) continue;
            Motion.Stop(layer.Translate, TranslateTransform.XProperty, 0);
            Motion.Stop(layer.Scale, ScaleTransform.ScaleXProperty, 1);
            Motion.Stop(layer.Scale, ScaleTransform.ScaleYProperty, 1);
            Motion.Stop(layer.View, OpacityProperty, 0);
            layer.View.Visibility = Visibility.Collapsed;
            layer.View.Background = null;
        }
        Motion.Stop(_musicTextDrag, TranslateTransform.XProperty, 0);
        Motion.Stop(MediaPlayback.TrackTextBlock, OpacityProperty, 1);
    }

    private bool MusicWheelInput(int delta)
    {
        if (!CanSwipeMusic || !_settings.EnableGestureControls || _isGestureActive || _isAnimating) return false;
        var now = DateTime.UtcNow;
        if (_musicWheelEnd?.IsEnabled != true)
        {
            _musicWheelX = 0;
            _gestureController.BeginTracking(new Point(), allowShellDrag: true, expanded: true, timestamp: now);
        }
        // WM_MOUSEHWHEEL is positive to the right; retain its continuous deltas through the shared tracker.
        _musicWheelX += delta * .5;
        _gestureController.UpdateTracking(new Point(_musicWheelX, 0), now);
        _shellLastSampleAt = now;
        MoveMusicSwipe(_gestureController.AccumulatedX);
        _musicWheelEnd ??= new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(85) };
        _musicWheelEnd.Stop();
        _musicWheelEnd.Tick -= FinishMusicWheel;
        _musicWheelEnd.Tick += FinishMusicWheel;
        _musicWheelEnd.Start();
        return true;
    }

    private void FinishMusicWheel(object? sender, EventArgs e)
    {
        _musicWheelEnd!.Stop();
        if (CanSwipeMusic) FinishMusicSwipe(false, _shellLastSampleAt);
        else ResetMusicMotion();
        _gestureController.CancelTracking();
    }
}
