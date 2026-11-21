using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;

namespace VNotch.Controllers;

public sealed partial class GestureController
{
    private bool _isTracking;
    private Point _startPoint;
    private double _accumulatedX;
    private double _accumulatedY;
    private DateTime _lastSwipeTime = DateTime.MinValue;
    private DateTime _lastTapTime = DateTime.MinValue;
    private bool _gestureTriggered;
    private bool _allowShellDrag, _expanded, _allowMediaGestures;
    private readonly Queue<(DateTime At, double Y)> _verticalSamples = new();
    private readonly Queue<(DateTime At, double X)> _horizontalSamples = new();
    private int _axis;
    public bool IsVerticalDrag => _axis == 2;
    public bool HasDragAxis => _axis != 0;
    public double VerticalVelocity { get; private set; }
    public double HorizontalVelocity { get; private set; }

    public bool ShouldSkipTrack => _expanded && _axis == 1 && _allowMediaGestures &&
        (Math.Abs(_accumulatedX) > 55 || Math.Abs(HorizontalVelocity) > 450 &&
            Math.Sign(HorizontalVelocity) == Math.Sign(_accumulatedX));

    public event Action? SwipeLeft;
    public event Action? SwipeRight;
    public event Action? SwipeDown;
    public event Action? DoubleTap;

    public void BeginTracking(Point position, bool allowShellDrag = false, bool expanded = false,
        DateTime? timestamp = null, bool allowMediaGestures = true)
    {
        _isTracking = true;
        _startPoint = position;
        _accumulatedX = 0;
        _accumulatedY = 0;
        _gestureTriggered = false;
        _allowShellDrag = allowShellDrag;
        _expanded = expanded;
        _allowMediaGestures = allowMediaGestures;
        _axis = 0;
        VerticalVelocity = 0;
        HorizontalVelocity = 0;
        _horizontalSamples.Clear();
        _horizontalSamples.Enqueue((timestamp ?? DateTime.UtcNow, position.X));
        _verticalSamples.Clear();
        _verticalSamples.Enqueue((timestamp ?? DateTime.UtcNow, position.Y));
    }

    public bool UpdateTracking(Point currentPosition, DateTime? timestamp = null)
    {
        if (!_isTracking || _gestureTriggered) return false;

        if (_allowShellDrag)
        {
            _accumulatedX = currentPosition.X - _startPoint.X;
            _accumulatedY = currentPosition.Y - _startPoint.Y;
            if (_axis == 0 && Math.Sqrt(_accumulatedX * _accumulatedX + _accumulatedY * _accumulatedY) >= 6)
                _axis = Math.Abs(_accumulatedY) > Math.Abs(_accumulatedX) && (_expanded || _accumulatedY > 0) ? 2 : 1;
            var now = timestamp ?? DateTime.UtcNow;
            _verticalSamples.Enqueue((now, currentPosition.Y));
            while (_verticalSamples.Count > 2 && (now - _verticalSamples.Peek().At).TotalMilliseconds > 90)
                _verticalSamples.Dequeue();
            var first = _verticalSamples.Peek();
            double seconds = (now - first.At).TotalSeconds;
            VerticalVelocity = seconds > 0 ? (currentPosition.Y - first.Y) / seconds : 0;
            _horizontalSamples.Enqueue((now, currentPosition.X));
            while (_horizontalSamples.Count > 2 && (now - _horizontalSamples.Peek().At).TotalMilliseconds > 90)
                _horizontalSamples.Dequeue();
            var horizontalFirst = _horizontalSamples.Peek();
            double horizontalSeconds = (now - horizontalFirst.At).TotalSeconds;
            HorizontalVelocity = horizontalSeconds > 0 ? (currentPosition.X - horizontalFirst.X) / horizontalSeconds : 0;
            if (IsVerticalDrag || _expanded || !_allowMediaGestures) return false;
        }

        double elapsedMs = (DateTime.UtcNow - _lastSwipeTime).TotalMilliseconds;
        vn_gesture_evaluate_move(
            _startPoint.X, _startPoint.Y,
            currentPosition.X, currentPosition.Y,
            elapsedMs,
            out _accumulatedX, out _accumulatedY,
            out int gestureKind);

        if (gestureKind == 0 || (_allowShellDrag && gestureKind == 3)) return false;

        _gestureTriggered = true;
        _lastSwipeTime = DateTime.UtcNow;

        switch (gestureKind)
        {
            case 1:
                SwipeLeft?.Invoke();
                break;
            case 2:
                SwipeRight?.Invoke();
                break;
            case 3:
                SwipeDown?.Invoke();
                break;
        }
        return true;
    }

    public bool EndTracking(Point position)
    {
        if (!_isTracking) return false;
        _isTracking = false;

        if (IsVerticalDrag || !_allowMediaGestures) return false;

        if (_gestureTriggered) return false;

        var now = DateTime.UtcNow;
        double elapsedTapMs = (now - _lastTapTime).TotalMilliseconds;
        vn_gesture_evaluate_end(
            _startPoint.X, _startPoint.Y,
            position.X, position.Y,
            elapsedTapMs,
            out byte isTap, out byte isDoubleTap);

        if (isTap != 0)
        {
            if (isDoubleTap != 0)
            {
                _lastTapTime = DateTime.MinValue;
                DoubleTap?.Invoke();
                return true;
            }
            _lastTapTime = now;
        }

        return false;
    }

    public void CancelTracking()
    {
        _isTracking = false;
        _gestureTriggered = false;
    }

    public bool IsTracking => _isTracking;

    public bool IsGestureActive { get; set; }

    public bool GestureTriggered => _gestureTriggered;

    public double AccumulatedX => _accumulatedX;

    public double AccumulatedY => _accumulatedY;

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_gesture_evaluate_move(
        double start_x,
        double start_y,
        double current_x,
        double current_y,
        double elapsed_since_last_swipe_ms,
        out double out_delta_x,
        out double out_delta_y,
        out int out_gesture_kind);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_gesture_evaluate_end(
        double start_x,
        double start_y,
        double end_x,
        double end_y,
        double elapsed_since_last_tap_ms,
        out byte out_is_tap,
        out byte out_is_double_tap);
}
