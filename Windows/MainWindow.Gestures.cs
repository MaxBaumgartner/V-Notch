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

public partial class MainWindow
{
    private const string GestureLogTag = "GESTURE";
    private GestureController _gestureController = null!;

    private bool _isGestureActive
    {
        get => _gestureController?.IsGestureActive ?? false;
        set
        {
            if (_gestureController == null || _gestureController.IsGestureActive == value) return;

            _gestureController.IsGestureActive = value;
            UpdateGlassMotionState();
        }
    }

    private DateTime _gestureSampleAt;
    private double _gestureSampleX;
    private double _gestureReleaseVelocity;
    private TranslateTransform? _gestureTranslate;
    private TranslateTransform? _gestureShadowTranslate;

    // Synchronize translate transforms on notch wrapper and shadow wrapper so gesture
    // drags move the entire notch (including left/right ears) and its shadow together
    // without detaching curves or leaving shadow artifacts behind.
    private void EnsureGestureTransforms()
    {
        if (_gestureTranslate is not TranslateTransform)
        {
            _gestureTranslate = NotchGestureTranslate ?? FindTranslateTransform(NotchWrapper.RenderTransform);
            if (_gestureTranslate == null)
            {
                _gestureTranslate = new TranslateTransform(0, 0);
                if (NotchWrapper.RenderTransform is TransformGroup tg)
                {
                    tg.Children.Add(_gestureTranslate);
                }
                else
                {
                    var group = new TransformGroup();
                    group.Children.Add(NotchScale);
                    group.Children.Add(_gestureTranslate);
                    NotchWrapper.RenderTransform = group;
                }
            }

            if (NotchBorder.RenderTransform is TranslateTransform)
            {
                NotchBorder.RenderTransform = null;
            }
        }

        if (_gestureShadowTranslate is not TranslateTransform)
        {
            _gestureShadowTranslate = NotchShadowGestureTranslate ?? FindTranslateTransform(NotchShadowWrapper.RenderTransform);
            if (_gestureShadowTranslate == null)
            {
                _gestureShadowTranslate = new TranslateTransform(0, 0);
                if (NotchShadowWrapper.RenderTransform is TransformGroup tg)
                {
                    tg.Children.Add(_gestureShadowTranslate);
                }
                else
                {
                    var group = new TransformGroup();
                    group.Children.Add(NotchShadowScale);
                    group.Children.Add(_gestureShadowTranslate);
                    NotchShadowWrapper.RenderTransform = group;
                }
            }

            if (NotchBorderShadow.RenderTransform is TranslateTransform)
            {
                NotchBorderShadow.RenderTransform = null;
            }
        }
    }

    private static TranslateTransform? FindTranslateTransform(Transform transform)
    {
        if (transform is TranslateTransform tt) return tt;
        if (transform is TransformGroup tg)
        {
            foreach (var child in tg.Children)
            {
                if (child is TranslateTransform childTt) return childTt;
            }
        }
        return null;
    }

    private void InitializeGestureController()
    {
        _gestureController = new GestureController();
        _gestureController.SwipeLeft += OnGestureSwipeLeft;
        _gestureController.SwipeRight += OnGestureSwipeRight;
        _gestureController.SwipeDown += OnGestureSwipeDown;
        _gestureController.DoubleTap += OnGestureDoubleTap;
    }

    private void NotchWrapper_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && _isExpanded &&
            !IsShellDragControl(e.OriginalSource as DependencyObject) && FindShellScrollCandidate(e.OriginalSource as DependencyObject) is { } scroll)
        {
            if (TryBeginGesture(e, capture: false))
            {
                _shellPendingScroll = scroll;
                _shellPendingScrollOrigin = e.GetPosition(this);
            }
            return;
        }
        if (e.ChangedButton == MouseButton.Left &&
            (_isExpanded || (_isAnimating && _shellAttachment.IsAttached)) &&
            !IsShellDragControl(e.OriginalSource as DependencyObject) && TryBeginGesture(e))
        {
            e.Handled = true;
            return;
        }

        if (e.ChangedButton != MouseButton.Middle) return;
        if (!_settings.EnableGestureControls) return;
        if (_isDraggingVolumeIndicator || _isDraggingNotch) return;
        if (_spotlightMorphSessionActive || _spotlightMorphOwnsNotchVisibility) return;

        e.Handled = true;
        TogglePlayPauseInstantAsync().SafeFireAndForget(GestureLogTag);
    }

    private void NotchWrapper_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) CancelScrollShellCandidate();

        if (e.ChangedButton == MouseButton.Middle)
        {
            e.Handled = true;
        }
    }

    private void NotchWrapper_MouseMove(object sender, MouseEventArgs e)
    {
        if (_isDraggingNotch && e.LeftButton == MouseButtonState.Pressed)
        {
            UpdateNotchDrag(NotchWrapper.PointToScreen(e.GetPosition(NotchWrapper)));

            e.Handled = true;
            return;
        }

        if (HandleAskGestureMove(e) || HandleCopiedGestureMove(e))
        {
            e.Handled = true;
            return;
        }

        NotchBorder_GestureMouseMove(sender, e);
    }

    private bool TryBeginNotchDrag(MouseButtonEventArgs e)
    {
        if (IsInteractiveControl(e.OriginalSource as DependencyObject) || !NotchWrapper.CaptureMouse())
            return false;

        _isDraggingNotch = true;
        _hasActuallyDragged = false;
        _notchDragStartMouse = NotchWrapper.PointToScreen(e.GetPosition(NotchWrapper));
        _notchDragStartFixedX = _fixedX;
        _notchDragStartFixedY = _fixedY;
        return true;
    }

    internal void UpdateNotchDrag(Point screenPoint)
    {
        if (!_isDraggingNotch) return;
        int dx = (int)Math.Round(screenPoint.X - _notchDragStartMouse.X);
        int dy = (int)Math.Round(screenPoint.Y - _notchDragStartMouse.Y);
        if (!_hasActuallyDragged && Math.Abs(dx) <= 3 && Math.Abs(dy) <= 3) return;
        _hasActuallyDragged = true;
        _overlayWindow.MoveFixedPosition(_notchDragStartFixedX + dx, _notchDragStartFixedY + dy);
        _notchManager.RefreshAnchor();
        _liquidGlass?.SetLiveRegion(GetGlassCaptureRegion());
    }

    private void NotchWrapper_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDraggingNotch)
        {
            _isDraggingNotch = false;
            NotchWrapper.ReleaseMouseCapture();

            if (_hasActuallyDragged && _settings.AllowNotchDragging)
            {
                var anchor = NotchAnchor;
                var screen = System.Windows.Forms.Screen.FromPoint(anchor);
                var monitor = MonitorSelection.GetChoices().FirstOrDefault(choice => choice.Screen?.DeviceName == screen.DeviceName);
                StoreNotchPosition(NotchPosition.FromAnchor(screen.Bounds, anchor), monitor);
            }
            else if (!_hasActuallyDragged)
            {
                // User just clicked without dragging -> toggle/open notch normally
                ToggleNotchFromClick(e.ClickCount);
            }

            e.Handled = true;
            return;
        }

        if (HandleAskGestureUp(e) || HandleCopiedGestureUp(e))
        {
            e.Handled = true;
            return;
        }

        NotchBorder_GestureMouseUp(sender, e);
    }

    private System.Drawing.Point NotchAnchor => new(_fixedX + _windowWidth / 2, _fixedY);

    private Size VisibleNotchSize => new(
        NotchBorder.ActualWidth > 0 ? NotchBorder.ActualWidth : _collapsedWidth,
        (NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight : _collapsedHeight)
            + (_settings.EnableDynamicIslandMode ? DynamicIslandTopMargin : 0));

    private void NotchBorder_PositionSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_cleanedUp && !_isDraggingNotch && _settings.CustomNotchPosition is { } position &&
            _overlayWindow.FitCustomPosition(position, VisibleNotchSize))
        {
            _notchManager.RefreshAnchor();
            _liquidGlass?.SetLiveRegion(GetGlassCaptureRegion());
        }
    }

    private void NotchWrapper_LostMoveCapture(object sender, MouseEventArgs e) => CancelUncapturedNotchDrag();

    private void CancelUncapturedNotchDrag()
    {
        if (!_isDraggingNotch || NotchWrapper.IsMouseCaptured) return;
        _isDraggingNotch = false;
        if (!_cleanedUp && _hasActuallyDragged && _settings.AllowNotchDragging) ApplyNotchPlacement();
    }

    private void StoreNotchPosition(NotchPosition? position, MonitorSelection.Choice? monitor)
    {
        if (SettingsPreparation.Active is { } editor)
        {
            editor.PreviewNotchPosition(position, monitor);
        }
        else
        {
            _settings.CustomNotchPosition = position;
            if (monitor != null)
            {
                _settings.MonitorIndex = monitor.Index;
                _settings.MonitorDeviceId = monitor.Id;
            }
            ApplyNotchPlacement();
            _settingsService.SaveAsync(_settings).SafeFireAndForget("SETTINGS-SAVE");
        }
        _liquidGlass?.SetLiveRegion(GetGlassCaptureRegion());
    }

    private bool TryBeginGesture(MouseButtonEventArgs e, bool capture = true)
    {
        if (!_settings.EnableGestureControls)
        {
            RuntimeLog.Log(GestureLogTag, "blocked: EnableGestureControls=false");
            return false;
        }
        if (_isDraggingVolumeIndicator || _isDraggingNotch || _isGreetingActive ||
            _spotlightMorphSessionActive || _spotlightMorphOwnsNotchVisibility) return false;
        if (_isDebugDraggable || (_isAnimating && !_shellAttachment.IsAttached)) return false;

        var pos = _isExpanded ? e.GetPosition(this) : e.GetPosition(NotchBorder);
        _musicCoverPressed = IsMusicCoverSource(e.OriginalSource as DependencyObject);
        _gestureSampleAt = DateTime.UtcNow;
        _gestureSampleX = pos.X;
        _gestureReleaseVelocity = 0;
        BeginShellGestureTracking(pos, DateTime.UtcNow);
        _isGestureActive = true;

        if (capture) NotchWrapper.CaptureMouse();

        return true;
    }

    private void NotchBorder_GestureMouseMove(object sender, MouseEventArgs e)
    {
        if (_shellPendingScroll != null) return;
        if (!_isGestureActive || !_gestureController.IsTracking) return;

        var pos = _shellGestureWasOpen ? e.GetPosition(this) : e.GetPosition(NotchBorder);
        var now = DateTime.UtcNow;
        double seconds = (now - _gestureSampleAt).TotalSeconds;
        if (seconds > 0) _gestureReleaseVelocity = Math.Clamp((pos.X - _gestureSampleX) * 0.3 / seconds, -1200, 1200);
        _gestureSampleAt = now;
        _gestureSampleX = pos.X;
        bool triggered = UpdateGestureTracking(pos, now);

        if (_gestureController.IsVerticalDrag) { e.Handled = true; return; }
        if (_shellGestureWasOpen) { e.Handled = _gestureController.HasDragAxis; return; }

        if (!triggered && !_gestureController.GestureTriggered)
        {
            ApplyGestureDragFeedback(_gestureController.AccumulatedX);
        }
    }

    private void NotchBorder_GestureMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isGestureActive) return;

        if (_musicDragging)
        {
            FinishMusicSwipe(false, DateTime.UtcNow);
            _gestureController.CancelTracking();
            _isGestureActive = false;
            NotchWrapper.ReleaseMouseCapture();
            e.Handled = true;
            return;
        }

        if (_shellDragActive)
        {
            FinishShellDrag(canceled: false);
            _gestureController.CancelTracking();
            _isGestureActive = false;
            NotchWrapper.ReleaseMouseCapture();
            e.Handled = true;
            return;
        }

        _isGestureActive = false;
        NotchWrapper.ReleaseMouseCapture();

        if (_shellGestureWasOpen)
        {
            _gestureController.CancelTracking();
            if (!_gestureController.HasDragAxis && !_isAnimating)
            {
                if (_musicCoverPressed) OpenCurrentMediaSourceFromThumbnail();
                else ToggleNotchFromClick(e.ClickCount);
            }
            return;
        }

        var pos = e.GetPosition(NotchBorder);
        bool wasTap = _gestureController.EndTracking(pos);

        if (!_gestureController.GestureTriggered)
        {
            AnimateGestureSnapBack();
            if (!wasTap)
            {
                ToggleNotchFromClick(e.ClickCount);
            }
        }
        else
        {
            AnimateGestureSnapBack();
        }

        if (!NotchWrapper.IsMouseOver)
        {
            AnimateNotchHover(false);
        }

        if (_isMusicCompactMode && _isCompactThumbnailHovered)
        {
            if (!IsCursorInsideCompactThumbnailExitZone())
            {
                SetCompactThumbnailHover(false);
            }
            else if (!CompactThumbnailBorder.IsMouseOver)
            {
                _compactThumbnailHoverLeaveTimer.Stop();
                _compactThumbnailHoverLeaveTimer.Start();
            }
        }
    }

    private void OnGestureSwipeLeft() => ExecuteGestureSwipeLeftAsync().SafeFireAndForget(GestureLogTag);

    private async Task ExecuteGestureSwipeLeftAsync()
    {
        if ((DateTime.UtcNow - _lastMediaActionTime).TotalMilliseconds < 500) return;
        _lastMediaActionTime = DateTime.UtcNow;

        await Dispatcher.InvokeAsync(() =>
        {
            PlayGestureSwipeFeedback(isLeft: true);
            PlayNextSkipAnimation();
            OptimisticPrepareForNextTrack();
        });

        try
        {
            await _mediaService.NextTrackAsync();
        }
        catch (Exception ex)
        {
            RuntimeLog.Error(GestureLogTag, ex, "SwipeLeft/Next failed");
        }
    }

    private void OnGestureSwipeRight() => ExecuteGestureSwipeRightAsync().SafeFireAndForget(GestureLogTag);

    private async Task ExecuteGestureSwipeRightAsync()
    {
        if ((DateTime.UtcNow - _lastMediaActionTime).TotalMilliseconds < 500) return;
        _lastMediaActionTime = DateTime.UtcNow;

        await Dispatcher.InvokeAsync(() =>
        {
            PlayGestureSwipeFeedback(isLeft: false);
            PlayPrevSkipAnimation();
        });

        try
        {
            PrepareForPreviousTrackRequest();
            await _mediaService.PreviousTrackAsync();
        }
        catch (Exception ex)
        {
            RuntimeLog.Error(GestureLogTag, ex, "SwipeRight/Prev failed");
        }
    }

    private void OnGestureSwipeDown()
    {
        Dispatcher.Invoke(() =>
        {
            PlayGestureSwipeDownFeedback();

            if (!_isExpanded && !_isAnimating)
            {
                _transitionCoordinator.RequestView(VNotch.Models.NotchView.Secondary, "GestureSwipeDown");
            }
            else if (_isExpanded && !_isSecondaryView && !_isAnimating)
            {
                SwitchToSecondaryView();
            }
        });
    }

    private void OnGestureDoubleTap() => ExecuteGestureDoubleTapAsync().SafeFireAndForget(GestureLogTag);

    private async Task ExecuteGestureDoubleTapAsync()
    {
        await Dispatcher.InvokeAsync(() =>
        {
            PlayGestureDoubleTapFeedback();
        });

        try
        {
            if ((DateTime.UtcNow - _lastMediaActionTime).TotalMilliseconds < 500) return;
            _lastMediaActionTime = DateTime.UtcNow;

            _isPlaying = !_isPlaying;
            await Dispatcher.InvokeAsync(() => UpdatePlayPauseIcon());

            _progressEngine.NotifyUserPlayPause(_isPlaying);
            await _mediaService.PlayPauseAsync();
        }
        catch (Exception ex)
        {
            RuntimeLog.Error(GestureLogTag, ex, "DoubleTap/PlayPause failed");
        }
    }

    private void ApplyGestureDragFeedback(double deltaX)
    {
        double dampened = deltaX * 0.3;
        double clamped = Math.Clamp(dampened, -20, 20);

        EnsureGestureTransforms();

        Motion.Stop(_gestureTranslate!, TranslateTransform.XProperty, clamped);
        Motion.Stop(_gestureShadowTranslate!, TranslateTransform.XProperty, clamped);
    }

    private void AnimateGestureSnapBack()
    {
        EnsureGestureTransforms();
        if (_gestureTranslate == null) return;

        double velocity = (DateTime.UtcNow - _gestureSampleAt).TotalMilliseconds <= 100 ? _gestureReleaseVelocity : 0;
        int generation = ++_glassGestureSnapBackGen;
        _glassGestureSnapBackMotion = !AnimationConfig.ReduceMotion;
        UpdateGlassMotionState();
        Motion.To(_gestureShadowTranslate!, TranslateTransform.XProperty, 0, Motion.Glide, velocity: velocity,
            done: () => Motion.Stop(_gestureShadowTranslate!, TranslateTransform.XProperty, 0));
        Motion.To(_gestureTranslate, TranslateTransform.XProperty, 0, Motion.Glide, velocity: velocity, done: () =>
        {
            Motion.Stop(_gestureTranslate, TranslateTransform.XProperty, 0);
            if (generation != _glassGestureSnapBackGen) return;
            _glassGestureSnapBackMotion = false;
            UpdateGlassMotionState();
        });
    }

    private void PlayGestureSwipeFeedback(bool isLeft)
    {
        EnsureGestureTransforms();
        double velocity = _gestureReleaseVelocity;
        if (Math.Abs(velocity) < 1) velocity = isLeft ? -180 : 180;
        Motion.To(_gestureTranslate!, TranslateTransform.XProperty, 0, Motion.Pop, velocity: velocity);
        Motion.To(_gestureShadowTranslate!, TranslateTransform.XProperty, 0, Motion.Pop, velocity: velocity);
        SpringGestureScale(-0.8, -0.8);
    }

    private void PlayGestureSwipeDownFeedback() => SpringGestureScale(0, 1.8);

    private void PlayGestureDoubleTapFeedback() => SpringGestureScale(-1.8, -1.8);

    private void PlayGestureMiddleClickFeedback() => SpringGestureScale(0.6, -1.4);

    private void SpringGestureScale(double velocityX, double velocityY)
    {
        Motion.To(NotchScale, ScaleTransform.ScaleXProperty, 1, Motion.Pop, velocity: velocityX);
        Motion.To(NotchScale, ScaleTransform.ScaleYProperty, 1, Motion.Pop, velocity: velocityY);
        Motion.To(NotchShadowScale, ScaleTransform.ScaleXProperty, 1, Motion.Pop, velocity: velocityX);
        Motion.To(NotchShadowScale, ScaleTransform.ScaleYProperty, 1, Motion.Pop, velocity: velocityY);
    }

    private void DisposeGestureController()
    {
        if (_gestureController != null)
        {
            _gestureController.SwipeLeft -= OnGestureSwipeLeft;
            _gestureController.SwipeRight -= OnGestureSwipeRight;
            _gestureController.SwipeDown -= OnGestureSwipeDown;
            _gestureController.DoubleTap -= OnGestureDoubleTap;
        }
    }
}
