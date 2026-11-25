using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using VNotch.Models;
using VNotch.Services;

namespace VNotch.Presenters;

public sealed class NotchShellViewRefs
{
    public required Border NotchBorder { get; init; }
    public FrameworkElement? NotchContainer { get; init; }
    public Action<double, SpringMotion?>? AnimateCornerRadius { get; init; }
    public Func<double, CornerRadius>? CornerRadiusBuilder { get; init; }
    public Func<long, bool>? IsSessionCurrent { get; init; }
    internal NotchShellAttachment? Attachment { get; init; }
}

public sealed class NotchShellPresenter : IDisposable
{
    private readonly NotchShellViewRefs _refs;
    private readonly CornerValue _corner;
    private long _activeSessionId;
    private long _animationVersion;
    private bool _disposed;
    private double? _canceledWidthVelocity, _canceledHeightVelocity;
    private Action? _heightArrived;
    private double _heightTarget;
    private SpringMotion _heightSpring = Motion.Glide;
    private System.Windows.Threading.DispatcherTimer? _watch;

    public NotchShellPresenter(NotchShellViewRefs refs)
    {
        _refs = refs ?? throw new ArgumentNullException(nameof(refs));
        _corner = new CornerValue();
        _corner.SetValue(CornerValue.ValueProperty, refs.NotchBorder.CornerRadius.BottomLeft);
        // MainWindow's settings are loaded after its presenters are constructed.
        _corner.Apply = SetCornerRadius;
        _refs.NotchBorder.IsVisibleChanged += OnVisibilityChanged;
    }

    public void AnimateShell(TransitionPlan plan, Action<TransitionExecutionResult> onCompleted)
    {
        if (_disposed)
        {
            onCompleted(new TransitionExecutionResult(plan.SessionId, TransitionExecutionStatus.Canceled, "Presenter disposed"));
            return;
        }

        long sessionId = _activeSessionId = plan.SessionId;
        StopWatch();
        long version = ++_animationVersion;
        _heightArrived = null;
        _heightTarget = plan.TargetHeight;
        if (!IsCurrent(sessionId, version)) return;
        var border = _refs.NotchBorder;
        bool opening = plan.FromView == NotchView.Compact && plan.TargetView != NotchView.Compact;
        bool closing = plan.TargetView == NotchView.Compact;
        var widthSpring = plan.Motion.Spring == Motion.Shift ? Motion.Shift :
            opening ? Motion.ShellOpenWidth : closing ? Motion.ShellCloseWidth : Motion.Shift;
        var heightSpring = _heightSpring = plan.Motion.Spring == Motion.Shift ? Motion.Shift :
            opening ? Motion.ShellOpenHeight : closing ? Motion.ShellCloseHeight : Motion.Shift;
        if (opening) _refs.Attachment?.Attach(plan.TargetWidth, plan.TargetHeight);
        else if (closing) _refs.Attachment?.Attach(border.Width, border.Height);
        else _refs.Attachment?.Detach(true);
        int fps = plan.Motion.TargetFps > 0 ? plan.Motion.TargetFps : AnimationConfig.TargetFps;
        double? widthVelocity = _canceledWidthVelocity, heightVelocity = _canceledHeightVelocity;
        _canceledWidthVelocity = _canceledHeightVelocity = null;

        if (plan.Motion.ReduceMotion || plan.Motion.Duration.TimeSpan <= TimeSpan.Zero)
        {
            Motion.Stop(border, FrameworkElement.WidthProperty, plan.TargetWidth);
            Motion.Stop(border, FrameworkElement.HeightProperty, plan.TargetHeight);
            Motion.Stop(_corner, CornerValue.ValueProperty, plan.TargetCornerRadius);
            if (_refs.AnimateCornerRadius is { } snapCorner) snapCorner(plan.TargetCornerRadius, null);
            else SetCornerRadius(plan.TargetCornerRadius);
            _refs.Attachment?.Detach(!closing);
            onCompleted(new TransitionExecutionResult(sessionId, TransitionExecutionStatus.Completed));
            return;
        }

        // Width leads and height follows its own spring; the corner shares the height's momentum model.
        if (_refs.AnimateCornerRadius is { } animateCorner)
            animateCorner(plan.TargetCornerRadius, heightSpring);
        else
            Motion.To(_corner, CornerValue.ValueProperty, plan.TargetCornerRadius, heightSpring, fps: fps);

        // Hover and media-width springs may take the size over mid-session; the session still ends when it rests.
        bool widthArrived = false, heightArrived = false, reported = false;
        void Arrived()
        {
            if (!widthArrived || !heightArrived || reported || !IsCurrent(sessionId, version)) return;
            reported = true;
            StopWatch();
            _refs.Attachment?.Detach(!closing);
            onCompleted(new TransitionExecutionResult(sessionId, TransitionExecutionStatus.Completed));
        }
        if (!double.IsFinite(border.Width)) border.Width = ReadExtent(border.Width, border.ActualWidth, plan.TargetWidth);
        if (!double.IsFinite(border.Height)) border.Height = ReadExtent(border.Height, border.ActualHeight, plan.TargetHeight);
        Motion.To(border, FrameworkElement.WidthProperty, plan.TargetWidth, widthSpring, fps: fps, done: () =>
        {
            if (!IsCurrent(sessionId, version)) return;
            // Rest exactly on the target, unless something else took the width somewhere of its own meanwhile.
            if (Math.Abs(border.Width - plan.TargetWidth) < 1) Motion.Stop(border, FrameworkElement.WidthProperty, plan.TargetWidth);
            widthArrived = true;
            Arrived();
        }, velocity: widthVelocity, keep: true, acceleration: closing ? .25 : 0);
        _heightArrived = () =>
        {
            if (!IsCurrent(sessionId, version)) return;
            if (Math.Abs(border.Height - _heightTarget) < 1) Motion.Stop(border, FrameworkElement.HeightProperty, _heightTarget);
            _heightArrived = null;
            heightArrived = true;
            Arrived();
        };
        Motion.To(border, FrameworkElement.HeightProperty, plan.TargetHeight, heightSpring, fps: fps, done: _heightArrived, velocity: heightVelocity, keep: true, acceleration: closing ? .25 : 0);

        // Pills, banners and the timer still move the notch with plain animations. One of those cuts a spring off
        // without telling it, and the session would wait for an ending that never comes.
        var watch = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
        { Interval = TimeSpan.FromMilliseconds(100) };
        watch.Tick += (_, _) =>
        {
            if (_disposed || reported || !IsCurrent(sessionId, version)) { watch.Stop(); return; }
            if (Motion.IsRunning(border, FrameworkElement.WidthProperty) ||
                Motion.IsRunning(border, FrameworkElement.HeightProperty)) return;
            watch.Stop();
            // Whoever took the size over owns it now; only the session ends.
            widthArrived = heightArrived = true;
            _heightArrived = null;
            Arrived();
        };
        if (!reported)
        {
            _watch = watch;
            if (border.IsVisible) watch.Start();
        }
    }

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_refs.NotchBorder.IsVisible) _watch?.Start();
        else _watch?.Stop();
    }

    private void StopWatch()
    {
        _watch?.Stop();
        _watch = null;
    }

    internal bool RetargetHeight(double height, Action done)
    {
        if (_disposed || _heightArrived == null) return false;
        _heightTarget = height;
        // A content measurement replaces the height spring; the session's kept ending rides along.
        Motion.To(_refs.NotchBorder, FrameworkElement.HeightProperty, height, _heightSpring, done: done);
        return true;
    }

    internal void SetReleaseVelocity(double heightVelocity, double widthPerHeight)
    {
        _canceledHeightVelocity = heightVelocity;
        _canceledWidthVelocity = heightVelocity * widthPerHeight;
    }

    public void CancelCurrentAnimation()
    {
        bool ownsSize = _heightArrived != null || _watch != null;
        StopWatch();
        ++_animationVersion;
        ++_activeSessionId;
        _heightArrived = null;
        var border = _refs.NotchBorder;
        // Coordinators cancel before retargeting. Hold the rendered frame without losing its release speed.
        _canceledWidthVelocity = Motion.VelocityOf(border, FrameworkElement.WidthProperty);
        _canceledHeightVelocity = Motion.VelocityOf(border, FrameworkElement.HeightProperty);
        // A compact face can own these springs now; leave its velocity intact for its next retarget.
        if (ownsSize)
        {
            Motion.Stop(border, FrameworkElement.WidthProperty);
            Motion.Stop(border, FrameworkElement.HeightProperty);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _refs.NotchBorder.IsVisibleChanged -= OnVisibilityChanged;
        CancelCurrentAnimation();
        Motion.Stop(_corner, CornerValue.ValueProperty);
        _refs.Attachment?.Dispose();
    }

    private void SetCornerRadius(double radius) =>
        _refs.NotchBorder.CornerRadius = _refs.CornerRadiusBuilder?.Invoke(radius) ?? new CornerRadius(radius);

    private bool IsCurrent(long sessionId, long version) =>
        !_disposed && version == _animationVersion && sessionId == _activeSessionId &&
        (_refs.IsSessionCurrent?.Invoke(sessionId) ?? true);

    private static double ReadExtent(double effective, double actual, double fallback) =>
        double.IsFinite(effective) && effective >= 0 ? effective : double.IsFinite(actual) && actual > 0 ? actual : fallback;

    private sealed class CornerValue : Animatable
    {
        public Action<double>? Apply { get; set; }
        public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
            "Value", typeof(double), typeof(CornerValue), new PropertyMetadata(0d,
                (target, args) => ((CornerValue)target).Apply?.Invoke((double)args.NewValue)));
        protected override Freezable CreateInstanceCore() => new CornerValue();
    }
}
