using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using VNotch.Controllers;
using VNotch.Models;
using VNotch.Services;

namespace VNotch.Presenters;

public sealed class NotchContentViewRefs
{
    public required FrameworkElement ExpandedContent { get; init; }
    public FrameworkElement? CameraContent { get; init; }
    public FrameworkElement? ClaudeContent { get; init; }
    public FrameworkElement? PiContent { get; init; }
    public FrameworkElement? SchoolContent { get; init; }
    public FrameworkElement? WeatherContent { get; init; }
    public FrameworkElement? TimerContent { get; init; }
    public FrameworkElement? AudioContent { get; init; }
    public FrameworkElement? AudioScrollViewer { get; init; }
    public FrameworkElement? SecondaryContent { get; init; }
    public Func<long, bool>? IsSessionCurrent { get; init; }
}

public sealed class NotchContentTransitionPresenter : IDisposable
{


    private readonly NotchContentViewRefs _refs;
    private long _activeSessionId;
    private long _animationVersion;
    private bool _disposed;
    private sealed record PageOffset(Transform Original, TransformGroup Wrapper, ScaleTransform Scale, TranslateTransform Offset);
    private readonly Dictionary<FrameworkElement, PageOffset> _pageOffsets = new();
    private readonly Dictionary<(DependencyObject Target, DependencyProperty Property), double> _canceledVelocities = new();

    public NotchContentTransitionPresenter(NotchContentViewRefs refs)
    {
        _refs = refs ?? throw new ArgumentNullException(nameof(refs));
    }

    public void TransitionContent(TransitionPlan plan, Action<TransitionExecutionResult> onCompleted)
    {
        if (_disposed)
        {
            onCompleted(new TransitionExecutionResult(plan.SessionId, TransitionExecutionStatus.Canceled, "Presenter disposed"));
            return;
        }

        long sessionId = plan.SessionId;
        _activeSessionId = sessionId;
        long version = ++_animationVersion;
        if (!IsCurrent(sessionId, version)) return;

        var targetElement = GetElementForView(plan.TargetView);
        var allElements = GetAllElements();

        // Collapse all pages only after their exit has finished.
        if (plan.TargetView == NotchView.Compact || targetElement == null)
        {
            FadeOutAll(sessionId, version, plan.Motion.Duration, plan.Motion.Easing, plan.Motion.TargetFps, plan.Motion.ReduceMotion, () =>
            {
                if (IsCurrent(sessionId, version))
                {
                    onCompleted(new TransitionExecutionResult(sessionId, TransitionExecutionStatus.Completed));
                }
            });
            return;
        }

        // The audio scroll viewer belongs to the audio page and must be ready before its entrance.
        if (plan.TargetView == NotchView.AudioMixer && _refs.AudioScrollViewer != null &&
            !ReferenceEquals(_refs.AudioScrollViewer, targetElement) && _refs.AudioScrollViewer.Visibility != Visibility.Visible)
        {
            _refs.AudioScrollViewer.BeginAnimation(UIElement.OpacityProperty, null);
            _refs.AudioScrollViewer.Visibility = Visibility.Visible;
            _refs.AudioScrollViewer.Opacity = 1.0;
            ClearTemporaryAnimationTransforms(_refs.AudioScrollViewer);
        }

        // Reduced motion commits the complete page state immediately.
        if (plan.Motion.ReduceMotion || plan.Motion.Duration.TimeSpan <= TimeSpan.Zero)
        {
            foreach (var el in allElements)
            {
                if (ReferenceEquals(el, targetElement))
                {
                    el.BeginAnimation(UIElement.OpacityProperty, null);
                    el.Visibility = Visibility.Visible;
                    el.Opacity = 1.0;
                    ReleasePageOffset(el);
                    ClearTemporaryAnimationTransforms(el);
                    el.IsHitTestVisible = true;
                }
                else
                {
                    ResetContentVisualState(el);
                }
            }
            if (IsCurrent(sessionId, version) && plan.TargetView != NotchView.AudioMixer && _refs.AudioScrollViewer != null && !ReferenceEquals(_refs.AudioScrollViewer, _refs.AudioContent))
            {
                ResetElementVisualState(_refs.AudioScrollViewer);
            }
            onCompleted(new TransitionExecutionResult(sessionId, TransitionExecutionStatus.Completed));
            return;
        }

        int fps = plan.Motion.TargetFps > 0 ? plan.Motion.TargetFps : AnimationConfig.TargetFps;
        bool switchingExpandedView = plan.FromView != NotchView.Compact;
        bool targetWasVisible = targetElement.Visibility == Visibility.Visible;
        var targetLayer = GetPageOffset(targetElement,
            switchingExpandedView ? 22 * plan.NavigationDirection : 0,
            targetWasVisible ? 1 : Motion.TileFromScale);
        targetElement.Visibility = Visibility.Visible;
        targetElement.IsHitTestVisible = false;
        int arriving = 4;
        void Arrived()
        {
            if (--arriving != 0 || !IsCurrent(sessionId, version)) return;
            Motion.Stop(targetElement, UIElement.OpacityProperty, 1);
            ReleasePageOffset(targetElement);
            targetElement.IsHitTestVisible = true;
            if (plan.TargetView != NotchView.AudioMixer && _refs.AudioScrollViewer != null &&
                !ReferenceEquals(_refs.AudioScrollViewer, _refs.AudioContent))
                ResetElementVisualState(_refs.AudioScrollViewer);
            onCompleted(new TransitionExecutionResult(sessionId, TransitionExecutionStatus.Completed));
        }
        SpringTo(targetElement, UIElement.OpacityProperty, 1, Motion.Tile,
            from: targetWasVisible ? null : 0, fps: fps, done: Arrived);
        SpringTo(targetLayer.Offset, TranslateTransform.XProperty, 0, Motion.Glide, fps: fps, done: Arrived);
        SpringTo(targetLayer.Scale, ScaleTransform.ScaleXProperty, 1, Motion.Tile, fps: fps, done: Arrived);
        SpringTo(targetLayer.Scale, ScaleTransform.ScaleYProperty, 1, Motion.Tile, fps: fps, done: Arrived);

        foreach (var element in allElements)
        {
            if (ReferenceEquals(element, targetElement)) continue;
            if (element.Visibility != Visibility.Visible)
            {
                ResetContentVisualState(element);
                continue;
            }
            var layer = GetPageOffset(element, 0, 1);
            element.IsHitTestVisible = false;
            SpringTo(layer.Offset, TranslateTransform.XProperty,
                switchingExpandedView ? -14 * plan.NavigationDirection : 0, Motion.Exit, fps: fps);
            SpringTo(layer.Scale, ScaleTransform.ScaleXProperty, Motion.ExitToScale, Motion.Exit, fps: fps);
            SpringTo(layer.Scale, ScaleTransform.ScaleYProperty, Motion.ExitToScale, Motion.Exit, fps: fps);
            SpringTo(element, UIElement.OpacityProperty, 0, Motion.Exit, fps: fps, done: () =>
            {
                if (!IsCurrent(sessionId, version)) return;
                ResetContentVisualState(element);
                if (ReferenceEquals(element, _refs.AudioContent) && _refs.AudioScrollViewer != null &&
                    !ReferenceEquals(_refs.AudioScrollViewer, _refs.AudioContent))
                    ResetElementVisualState(_refs.AudioScrollViewer);
            });
        }
    }

    private PageOffset GetPageOffset(FrameworkElement element, double initialX, double initialScale)
    {
        if (_pageOffsets.TryGetValue(element, out var layer) && ReferenceEquals(element.RenderTransform, layer.Wrapper))
            return layer;
        var original = element.RenderTransform ?? Transform.Identity;
        var scale = new ScaleTransform(initialScale, initialScale, element.ActualWidth / 2, element.ActualHeight / 2);
        var offset = new TranslateTransform(initialX, 0);
        var wrapper = new TransformGroup();
        wrapper.Children.Add(original);
        wrapper.Children.Add(scale);
        wrapper.Children.Add(offset);
        layer = new PageOffset(original, wrapper, scale, offset);
        _pageOffsets[element] = layer;
        element.RenderTransform = wrapper;
        return layer;
    }
    private void ReleasePageOffset(FrameworkElement element)
    {
        if (!_pageOffsets.Remove(element, out var layer)) return;
        _canceledVelocities.Remove((layer.Offset, TranslateTransform.XProperty));
        _canceledVelocities.Remove((layer.Scale, ScaleTransform.ScaleXProperty));
        _canceledVelocities.Remove((layer.Scale, ScaleTransform.ScaleYProperty));
        Motion.Stop(layer.Offset, TranslateTransform.XProperty);
        Motion.Stop(layer.Scale, ScaleTransform.ScaleXProperty);
        Motion.Stop(layer.Scale, ScaleTransform.ScaleYProperty);
        if (ReferenceEquals(element.RenderTransform, layer.Wrapper)) element.RenderTransform = layer.Original;
    }

    private void ResetContentVisualState(FrameworkElement element)
    {
        ReleasePageOffset(element);
        _canceledVelocities.Remove((element, UIElement.OpacityProperty));
        ResetElementVisualState(element);
        element.IsHitTestVisible = true;
    }

    public long InvalidateSession()
    {
        ++_animationVersion;
        return ++_activeSessionId;
    }

    public void CancelActiveTransition()
    {
        ++_animationVersion;
        _activeSessionId++;
        foreach (var el in GetAllElements())
        {
            HoldSpring(el, UIElement.OpacityProperty);
            if (_pageOffsets.TryGetValue(el, out var layer))
            {
                HoldSpring(layer.Offset, TranslateTransform.XProperty);
                HoldSpring(layer.Scale, ScaleTransform.ScaleXProperty);
                HoldSpring(layer.Scale, ScaleTransform.ScaleYProperty);
            }
            HoldCurrentTransformAndEffect(el);
        }
        if (_refs.AudioScrollViewer != null && !ReferenceEquals(_refs.AudioScrollViewer, _refs.AudioContent))
        {
            double currentScrollOpacity = _refs.AudioScrollViewer.Visibility == Visibility.Visible ? _refs.AudioScrollViewer.Opacity : 0.0;
            _refs.AudioScrollViewer.BeginAnimation(UIElement.OpacityProperty, null);
            _refs.AudioScrollViewer.Opacity = currentScrollOpacity;
            HoldCurrentTransformAndEffect(_refs.AudioScrollViewer);
        }
    }

    private void HoldSpring(DependencyObject target, DependencyProperty property)
    {
        // Preserve speed at the held frame, even if the next request waits for another dispatcher turn.
        if (DependencyPropertyHelper.GetValueSource(target, property).IsAnimated)
            _canceledVelocities[(target, property)] = Motion.VelocityOf(target, property);
        Motion.Stop(target, property);
    }

    private void SpringTo(DependencyObject target, DependencyProperty property, double to, SpringMotion spring,
        double? from = null, int? fps = null, Action? done = null)
    {
        double? velocity = _canceledVelocities.Remove((target, property), out var held) ? held : null;
        Motion.To(target, property, to, spring, from: from, fps: fps, done: done, velocity: velocity);
    }

    private static void HoldCurrentTransformAndEffect(FrameworkElement element)
    {
        // Keep named transform/effect instances: other entrance animations can
        // still reference them. Only detach clocks, retaining the frame on screen.
        HoldTransform(element.RenderTransform);
        if (element.Effect is BlurEffect { IsFrozen: false } blur)
            HoldAnimatedValue(blur, BlurEffect.RadiusProperty);
    }

    private static void HoldTransform(Transform? transform)
    {
        if (transform == null || transform.IsFrozen) return;
        if (transform is TransformGroup group)
        {
            foreach (var child in group.Children) HoldTransform(child);
            return;
        }
        if (!transform.HasAnimatedProperties) return;
        switch (transform)
        {
            case TranslateTransform:
                HoldAnimatedValue(transform, TranslateTransform.XProperty);
                HoldAnimatedValue(transform, TranslateTransform.YProperty);
                break;
            case ScaleTransform:
                HoldAnimatedValue(transform, ScaleTransform.ScaleXProperty);
                HoldAnimatedValue(transform, ScaleTransform.ScaleYProperty);
                HoldAnimatedValue(transform, ScaleTransform.CenterXProperty);
                HoldAnimatedValue(transform, ScaleTransform.CenterYProperty);
                break;
            case RotateTransform:
                HoldAnimatedValue(transform, RotateTransform.AngleProperty);
                HoldAnimatedValue(transform, RotateTransform.CenterXProperty);
                HoldAnimatedValue(transform, RotateTransform.CenterYProperty);
                break;
            case SkewTransform:
                HoldAnimatedValue(transform, SkewTransform.AngleXProperty);
                HoldAnimatedValue(transform, SkewTransform.AngleYProperty);
                HoldAnimatedValue(transform, SkewTransform.CenterXProperty);
                HoldAnimatedValue(transform, SkewTransform.CenterYProperty);
                break;
            case MatrixTransform:
                HoldAnimatedValue(transform, MatrixTransform.MatrixProperty);
                break;
        }
    }

    private static void HoldAnimatedValue(Animatable target, DependencyProperty property)
    {
        if (!DependencyPropertyHelper.GetValueSource(target, property).IsAnimated) return;
        object value = target.GetValue(property);
        target.BeginAnimation(property, null);
        target.SetValue(property, value);
    }

    public void SnapToView(NotchView view)
    {
        if (_disposed) return;
        ++_animationVersion;
        _activeSessionId++;
        _canceledVelocities.Clear();
        var target = GetElementForView(view);
        if (view == NotchView.AudioMixer && _refs.AudioScrollViewer != null)
        {
            _refs.AudioScrollViewer.BeginAnimation(UIElement.OpacityProperty, null);
            _refs.AudioScrollViewer.Visibility = Visibility.Visible;
            _refs.AudioScrollViewer.Opacity = 1.0;
            ClearTemporaryAnimationTransforms(_refs.AudioScrollViewer);
        }
        else if (_refs.AudioScrollViewer != null && !ReferenceEquals(_refs.AudioScrollViewer, _refs.AudioContent))
        {
            ResetElementVisualState(_refs.AudioScrollViewer);
        }

        foreach (var el in GetAllElements())
        {
            if (target != null && ReferenceEquals(el, target))
            {
                el.BeginAnimation(UIElement.OpacityProperty, null);
                el.Visibility = Visibility.Visible;
                el.Opacity = 1.0;
                ReleasePageOffset(el);
                el.IsHitTestVisible = true;
                ClearTemporaryAnimationTransforms(el);
            }
            else
            {
                ResetContentVisualState(el);
            }
        }
    }

    public static void ResetElementVisualState(FrameworkElement el)
    {
        if (el == null) return;
        Motion.Stop(el, UIElement.OpacityProperty, 0);
        el.Visibility = Visibility.Collapsed;
        ClearTemporaryAnimationTransforms(el);
    }

    public static void ClearTransformAndEffects(FrameworkElement el) => ClearTemporaryAnimationTransforms(el);

    public static void ClearTemporaryAnimationTransforms(FrameworkElement el)
    {
        if (el == null) return;

        // Keep the media layout transforms; other code retains references to those named objects.
        if (el.Name == "ExpandedContent")
        {
            if (el.RenderTransform is TransformGroup tg)
            {
                foreach (var child in tg.Children)
                {
                    if (child is TranslateTransform tt)
                    {
                        tt.BeginAnimation(TranslateTransform.YProperty, null);
                        tt.BeginAnimation(TranslateTransform.XProperty, null);
                    }
                    else if (child is ScaleTransform st)
                    {
                        st.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                        st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                        st.ScaleX = 1.0;
                        st.ScaleY = 1.0;
                    }
                }
            }
            else if (el.RenderTransform is TranslateTransform tt)
            {
                tt.BeginAnimation(TranslateTransform.YProperty, null);
                tt.BeginAnimation(TranslateTransform.XProperty, null);
            }

            if (el.Effect is BlurEffect blur)
            {
                blur.BeginAnimation(BlurEffect.RadiusProperty, null);
                blur.Radius = 0;
            }
            if (Controls.ActiveBlur.GetBlur(el) == null)
            {
                el.Effect = null;
            }
            return;
        }

        // Other pages have no persistent layout transforms.
        // Release their temporary transforms and effects once hidden.
        el.RenderTransform = null;
        if (el.Effect is BlurEffect b)
        {
            b.BeginAnimation(BlurEffect.RadiusProperty, null);
            b.Radius = 0;
        }
        el.Effect = null;
    }

    private void FadeOutAll(long sessionId, long version, Duration duration, IEasingFunction easing, int fps, bool reduceMotion, Action onCompleted)
    {
        var elements = GetAllElements();
        if (reduceMotion || duration.TimeSpan <= TimeSpan.Zero)
        {
            foreach (var el in elements)
            {
                ResetContentVisualState(el);
            }
            if (IsCurrent(sessionId, version) && _refs.AudioScrollViewer != null && !ReferenceEquals(_refs.AudioScrollViewer, _refs.AudioContent))
            {
                ResetElementVisualState(_refs.AudioScrollViewer);
            }
            onCompleted();
            return;
        }

        var leaving = elements.FindAll(element => element.Visibility == Visibility.Visible && element.Opacity > 0.01);
        foreach (var element in elements)
            if (!leaving.Contains(element)) ResetContentVisualState(element);
        int pendingCount = leaving.Count;
        void Finished()
        {
            if (_refs.AudioScrollViewer != null && !ReferenceEquals(_refs.AudioScrollViewer, _refs.AudioContent))
                ResetElementVisualState(_refs.AudioScrollViewer);
            onCompleted();
        }
        if (pendingCount == 0)
        {
            Finished();
            return;
        }
        foreach (var element in leaving)
        {
            var layer = GetPageOffset(element, 0, 1);
            element.IsHitTestVisible = false;
            SpringTo(layer.Offset, TranslateTransform.XProperty, 0, Motion.Exit, fps: fps);
            SpringTo(layer.Scale, ScaleTransform.ScaleXProperty, Motion.ExitToScale, Motion.Exit, fps: fps);
            SpringTo(layer.Scale, ScaleTransform.ScaleYProperty, Motion.ExitToScale, Motion.Exit, fps: fps);
            SpringTo(element, UIElement.OpacityProperty, 0, Motion.Exit, fps: fps, done: () =>
            {
                if (!IsCurrent(sessionId, version)) return;
                ResetContentVisualState(element);
                if (--pendingCount == 0) Finished();
            });
        }
    }
    private FrameworkElement? GetElementForView(NotchView view)
    {
        return view switch
        {
            NotchView.Media => _refs.ExpandedContent,
            NotchView.Timer => _refs.TimerContent,
            NotchView.AudioMixer => _refs.AudioContent ?? _refs.AudioScrollViewer,
            NotchView.Camera => _refs.CameraContent,
            NotchView.Claude => _refs.ClaudeContent,
            NotchView.Pi => _refs.PiContent,
            NotchView.School => _refs.SchoolContent,
            NotchView.Weather => _refs.WeatherContent,
            NotchView.Secondary => _refs.SecondaryContent,
            _ => null
        };
    }

    private List<FrameworkElement> GetAllElements()
    {
        var list = new List<FrameworkElement> { _refs.ExpandedContent };
        if (_refs.CameraContent != null) list.Add(_refs.CameraContent);
        if (_refs.ClaudeContent != null) list.Add(_refs.ClaudeContent);
        if (_refs.PiContent != null) list.Add(_refs.PiContent);
        if (_refs.SchoolContent != null) list.Add(_refs.SchoolContent);
        if (_refs.WeatherContent != null) list.Add(_refs.WeatherContent);
        if (_refs.TimerContent != null) list.Add(_refs.TimerContent);
        if (_refs.AudioContent != null) list.Add(_refs.AudioContent);
        else if (_refs.AudioScrollViewer != null) list.Add(_refs.AudioScrollViewer);
        if (_refs.SecondaryContent != null) list.Add(_refs.SecondaryContent);
        return list;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelActiveTransition();
        foreach (var element in GetAllElements()) ReleasePageOffset(element);
        _canceledVelocities.Clear();
    }

    private bool IsCurrent(long sessionId, long version) =>
        !_disposed && version == _animationVersion && sessionId == _activeSessionId &&
        (_refs.IsSessionCurrent?.Invoke(sessionId) ?? true);
}
