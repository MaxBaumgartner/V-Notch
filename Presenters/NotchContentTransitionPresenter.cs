using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
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
    public FrameworkElement? RobloxContent { get; init; }
    public FrameworkElement? AiUsageContent { get; init; }
    public FrameworkElement? TimerContent { get; init; }
    public FrameworkElement? AudioContent { get; init; }
    public FrameworkElement? AudioScrollViewer { get; init; }
    public FrameworkElement? SecondaryContent { get; init; }
    public Func<long, bool>? IsSessionCurrent { get; init; }
    internal NotchShellAttachment? ShellAttachment { get; init; }
}

public sealed class NotchContentTransitionPresenter : IDisposable
{


    private readonly NotchContentViewRefs _refs;
    private long _activeSessionId;
    private long _animationVersion;
    private bool _disposed;
    private sealed record PageOffset(Transform Original, TransformGroup Wrapper, ScaleTransform Scale,
        TranslateTransform Offset, Effect? OriginalEffect, BlurEffect Blur);
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

        if (_refs.ShellAttachment != null && (plan.FromView == NotchView.Compact || plan.TargetView == NotchView.Compact))
        {
            // Shell progress owns opacity and scale until both dimensions rest. Closing keeps the page laid out.
            if (plan.TargetView != NotchView.Compact) SnapToView(plan.TargetView);
            onCompleted(new TransitionExecutionResult(sessionId, TransitionExecutionStatus.Completed));
            return;
        }

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
            ShowAudioScrollViewer();
        }

        // Reduced motion commits the complete page state immediately.
        if (plan.Motion.ReduceMotion || plan.Motion.Duration.TimeSpan <= TimeSpan.Zero)
        {
            ApplyImmediateExpandedTransition(plan, targetElement, allElements, sessionId, version);
            onCompleted(new TransitionExecutionResult(sessionId, TransitionExecutionStatus.Completed));
            return;
        }

        int fps = plan.Motion.TargetFps > 0 ? plan.Motion.TargetFps : AnimationConfig.TargetFps;
        bool switchingExpandedView = plan.FromView != NotchView.Compact;
        bool aiDetailTransition = (plan.FromView == NotchView.Media && plan.TargetView == NotchView.AiUsage)
            || (plan.FromView == NotchView.AiUsage && plan.TargetView == NotchView.Media);
        double targetOpacity = targetElement.Visibility == Visibility.Visible ? targetElement.Opacity : 0;
        bool targetWasVisible = targetOpacity > 0.01;
        var targetLayer = GetPageOffset(targetElement,
            switchingExpandedView && !targetWasVisible ? 8 * plan.NavigationDirection : 0,
            aiDetailTransition && !targetWasVisible ? 0.97 : 1);
        targetElement.Visibility = Visibility.Visible;
        targetElement.IsHitTestVisible = false;
        int arriving = 5;
        void Arrived()
        {
            if (--arriving != 0 || !IsCurrent(sessionId, version)) return;
            Motion.Stop(targetElement, UIElement.OpacityProperty, 1);
            ReleasePageOffset(targetElement);
            targetElement.IsHitTestVisible = true;
            if (plan.TargetView != NotchView.AudioMixer)
                ResetDetachedAudioScrollViewer();
            onCompleted(new TransitionExecutionResult(sessionId, TransitionExecutionStatus.Completed));
        }
        // One motion instead of two pages on top of each other: the old page is gone within 90 ms, the new one
        // follows a beat later. When the shell shrinks it waits longer, so it lands with the shell instead of
        // sitting over a tall black gap. A page that is still on screen (a quick switch back) returns at once.
        bool shrinking = double.IsFinite(plan.FromHeight) && plan.FromHeight - plan.TargetHeight > 40;
        int beat = !switchingExpandedView || targetWasVisible ? 0 : shrinking ? ShrinkBeatMs : SwitchBeatMs;
        EaseTo(targetElement, UIElement.OpacityProperty, 1, ArriveMs,
            from: targetOpacity, fps: fps, done: Arrived, delayMs: beat);
        EaseTo(targetLayer.Offset, TranslateTransform.XProperty, 0, ArriveMs, fps: fps, done: Arrived, delayMs: beat);
        EaseTo(targetLayer.Blur, BlurEffect.RadiusProperty, 0, ArriveMs, fps: fps, done: Arrived, delayMs: beat);
        EaseTo(targetLayer.Scale, ScaleTransform.ScaleXProperty, 1, ArriveMs, fps: fps, done: Arrived, delayMs: beat);
        EaseTo(targetLayer.Scale, ScaleTransform.ScaleYProperty, 1, ArriveMs, fps: fps, done: Arrived, delayMs: beat);

        foreach (var element in allElements)
        {
            if (ReferenceEquals(element, targetElement)) continue;
            if (element.Visibility != Visibility.Visible || element.Opacity <= 0.01)
            {
                ResetContentVisualState(element);
                continue;
            }
            var layer = GetPageOffset(element, 0, 1);
            element.IsHitTestVisible = false;
            EaseTo(layer.Offset, TranslateTransform.XProperty,
                switchingExpandedView ? -6 * plan.NavigationDirection : 0, LeaveMs, fps: fps);
            double exitScale = aiDetailTransition ? 0.97 : 1;
            EaseTo(layer.Scale, ScaleTransform.ScaleXProperty, exitScale, LeaveMs, fps: fps);
            EaseTo(layer.Scale, ScaleTransform.ScaleYProperty, exitScale, LeaveMs, fps: fps);
            EaseTo(element, UIElement.OpacityProperty, 0, LeaveMs, fps: fps, done: () =>
            {
                if (!IsCurrent(sessionId, version)) return;
                ResetContentVisualState(element);
                if (ReferenceEquals(element, _refs.AudioContent))
                    ResetDetachedAudioScrollViewer();
            });
        }
    }

    private PageOffset GetPageOffset(FrameworkElement element, double initialX, double initialScale)
    {
        if (_pageOffsets.TryGetValue(element, out var layer) && ReferenceEquals(element.RenderTransform, layer.Wrapper))
            return layer;
        var original = element.RenderTransform ?? Transform.Identity;
        // Account for the element's existing origin when scaling around its centre.
        double width = double.IsFinite(element.Width) ? element.Width : element.ActualWidth;
        double height = double.IsFinite(element.Height) ? element.Height : element.ActualHeight;
        var scale = new ScaleTransform(initialScale, initialScale,
            (0.5 - element.RenderTransformOrigin.X) * width,
            (0.5 - element.RenderTransformOrigin.Y) * height);
        var offset = new TranslateTransform(initialX, 0);
        var wrapper = new TransformGroup();
        wrapper.Children.Add(original);
        wrapper.Children.Add(scale);
        wrapper.Children.Add(offset);
        var blur = new BlurEffect { Radius = initialX == 0 ? 0 : 2, RenderingBias = RenderingBias.Performance };
        layer = new PageOffset(original, wrapper, scale, offset, element.Effect, blur);
        _pageOffsets[element] = layer;
        element.RenderTransform = wrapper;
        element.Effect = blur;
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
        Motion.Stop(layer.Blur, BlurEffect.RadiusProperty, 0);
        if (ReferenceEquals(element.Effect, layer.Blur)) element.Effect = layer.OriginalEffect;
    }

    private void ResetContentVisualState(FrameworkElement element)
    {
        ReleasePageOffset(element);
        _canceledVelocities.Remove((element, UIElement.OpacityProperty));
        ResetElementVisualState(element);
        element.IsHitTestVisible = true;
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
            el.IsHitTestVisible = true;
            HoldCurrentTransformAndEffect(el);
        }
        if (_refs.AudioScrollViewer != null && !ReferenceEquals(_refs.AudioScrollViewer, _refs.AudioContent))
        {
            double currentScrollOpacity = _refs.AudioScrollViewer.Visibility == Visibility.Visible ? _refs.AudioScrollViewer.Opacity : 0.0;
            _refs.AudioScrollViewer.BeginAnimation(UIElement.OpacityProperty, null);
            _refs.AudioScrollViewer.Opacity = currentScrollOpacity;
            _refs.AudioScrollViewer.IsHitTestVisible = true;
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

    // Tab switch timing: the old page leaves, a beat of shell alone, the new page arrives.
    private const int LeaveMs = 90;
    private const int SwitchBeatMs = 60;
    private const int ShrinkBeatMs = 140;
    private const int ArriveMs = 200;

    private static void EaseTo(DependencyObject target, DependencyProperty property, double to, int milliseconds,
        double? from = null, int? fps = null, Action? done = null, int delayMs = 0)
    {
        double start = from ?? (double)target.GetValue(property);
        Motion.Stop(target, property, start);
        if (AnimationConfig.ReduceMotion || Math.Abs(start - to) < .0001)
        {
            target.SetValue(property, to);
            done?.Invoke();
            return;
        }
        var animation = new DoubleAnimation(start, to, TimeSpan.FromMilliseconds(milliseconds))
        { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
        // Waiting at its start value (set just above), so the page never flashes in before its turn.
        if (delayMs > 0) animation.BeginTime = TimeSpan.FromMilliseconds(delayMs);
        Timeline.SetDesiredFrameRate(animation, fps ?? AnimationConfig.TargetFps);
        animation.Completed += (_, _) => done?.Invoke();
        ((IAnimatable)target).BeginAnimation(property, animation);
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

    private void ApplyImmediateExpandedTransition(
        TransitionPlan plan, FrameworkElement targetElement, List<FrameworkElement> allElements,
        long sessionId, long version)
    {
        foreach (var element in allElements)
        {
            if (ReferenceEquals(element, targetElement))
            {
                Motion.Stop(element, UIElement.OpacityProperty, 1);
                element.Visibility = Visibility.Visible;
                ReleasePageOffset(element);
                ClearTemporaryAnimationTransforms(element);
                element.IsHitTestVisible = true;
            }
            else
            {
                ResetContentVisualState(element);
            }
        }
        if (IsCurrent(sessionId, version) && plan.TargetView != NotchView.AudioMixer)
            ResetDetachedAudioScrollViewer();
    }

    private void ShowAudioScrollViewer()
    {
        if (_refs.AudioScrollViewer is not { } scrollViewer) return;
        Motion.Stop(scrollViewer, UIElement.OpacityProperty, 1);
        scrollViewer.Visibility = Visibility.Visible;
        ClearTemporaryAnimationTransforms(scrollViewer);
    }

    private void ResetDetachedAudioScrollViewer()
    {
        if (_refs.AudioScrollViewer != null && !ReferenceEquals(_refs.AudioScrollViewer, _refs.AudioContent))
            ResetElementVisualState(_refs.AudioScrollViewer);
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
            if (IsCurrent(sessionId, version))
            {
                ResetDetachedAudioScrollViewer();
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
            ResetDetachedAudioScrollViewer();
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
            NotchView.Roblox => _refs.RobloxContent,
            NotchView.AiUsage => _refs.AiUsageContent,
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
        if (_refs.RobloxContent != null) list.Add(_refs.RobloxContent);
        if (_refs.AiUsageContent != null) list.Add(_refs.AiUsageContent);
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
