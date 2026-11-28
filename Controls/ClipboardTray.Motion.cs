using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Services;

namespace VNotch.Controls;

public sealed partial class ClipboardTray
{
    // Shared duration scale: input, state changes, and entrance travel.
    private const int MotionFast = 120;
    private const int MotionStandard = 180;
    private static readonly CubicEase TrayEase = CreateTrayEase();
    private static CubicEase CreateTrayEase()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ease.Freeze();
        return ease;
    }

    [LibraryImport("VNotch.Native")]
    private static partial int vn_clipboard_motion_compute_reflow_offset(
        double originX,
        double renderedOffsetX,
        double scrollOffset,
        double previousX,
        byte hasPreviousLayout,
        double previousLayoutX,
        byte sameTransform,
        out double outLayoutX,
        out double outOffsetX,
        out byte outShouldAnimate);

    internal bool AnimateResults { get => _animateResults; set => _animateResults = value; }
    private bool _animateResults = true;
    internal bool IsLeaving => _trayLeaving;
    private bool _trayLeaving;
    private bool _entranceWasHidden = true;
    private readonly Dictionary<FrameworkElement, object> _dismissingLayers = [];
    private bool _trayDropBorderVisible = true;
    private int _trayDropBorderMotionVersion;

    private void InitializeComponentMotion()
    {
        foreach (var panel in new FrameworkElement[] { LockPanel, ApprovalPanel, DetailPanel, EmptyText, EmptyDropSilhouette, StatusPanel, DeleteConfirmationPanel, LabelEditorPanel })
            panel.IsVisibleChanged += (_, _) =>
            {
                if (panel.IsVisible && !_disposed && !_trayLeaving) AnimateLayer(panel, true, 0, true);
                if (panel == LockPanel && panel.IsVisible) QueuePasscodeFocus();
            };
    }

    private void QueuePasscodeFocus()
    {
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
        {
            if (_disposed || _trayLeaving || !IsLoaded || !IsVisible || _category != "Personal" ||
                _controller?.Store.IsPersonalUnlocked == true || !LockPanel.IsHitTestVisible ||
                !PasscodeBox.IsVisible || DetailPanel.IsVisible || ApprovalPanel.IsVisible) return;
            Window.GetWindow(this)?.Activate();
            PasscodeBox.Focus();
            Keyboard.Focus(PasscodeBox);
        }));
    }

    private bool _searchHasText;

    private void UpdateSearchMotion()
    {
        bool hasText = SearchBox.Text.Length != 0;
        bool changed = hasText != _searchHasText;
        _searchHasText = hasText;
        if (hasText) ShowTransientLayer(ClearSearchButton);
        else DismissTransientLayer(ClearSearchButton);
        if (!changed || !IsLoaded || !IsVisible || _trayLeaving) return;
        // Keep the input and caret steady when a query starts or clears.
        if (hasText) AnimateLayer(ClearSearchButton, true, 0, true, MotionStandard, 3);
    }

    private void UpdateEmptyMessage(bool visible, string message)
    {
        if (!visible)
        {
            DismissTransientLayer(EmptyText);
            return;
        }
        bool changed = EmptyText.Text != message;
        bool alreadyVisible = EmptyText.IsVisible;
        EmptyText.Text = message;
        ShowTransientLayer(EmptyText);
        // IsVisibleChanged handles first appearance; changing the query while
        // results stay empty needs its own transition.
        if (changed && alreadyVisible && !_trayLeaving && !_disposed)
            AnimateLayer(EmptyText, true, 0, true, MotionStandard, 4);
    }

    public void BeginEntrance(bool animate = true) => BeginEntrance(null, animate);

    internal void BeginEntrance(Action? searchEntranceCompleted, bool animate = true)
    {
        bool reversing = _trayLeaving && !_entranceWasHidden && IsVisible && Opacity > 0.01;
        _entranceWasHidden = false;
        _trayLeaving = false;
        DeferEntranceWork();
        // The view entrance already owns the row fade; do not restart it when
        // the initial asynchronous search finishes.
        _animateResults = false;

        if (!animate || !IsVisible || AnimationConfig.ReduceMotion)
        {
            ResetShelfMotion();
            searchEntranceCompleted?.Invoke();
            return;
        }

        AnimateShelfLayer(SearchSurface, entering: true, fresh: !reversing, completed: searchEntranceCompleted);
        foreach (var layer in new FrameworkElement[] { ToolbarSurface, CategoryScroll, Cards, LockPanel,
            ApprovalPanel, DetailPanel, EmptyText, EmptyDropSilhouette, StatusPanel, DeleteConfirmationPanel, LabelEditorPanel })
            if (layer.IsVisible) AnimateShelfLayer(layer, entering: true, fresh: !reversing);
        if (LockPanel.IsVisible) QueuePasscodeFocus();
        if (_trayDropBorderVisible) AnimateTrayDropBorder(true, !reversing);
    }

    public void BeginExit(bool animate = true)
    {
        CancelCategoryReorder();
        CloseLabelEditor(immediately: true);
        CancelPendingPersonalDrop(); ResolveDeleteConfirmation(false, immediately: true); ClearStatus(immediately: true);
        CancelCardPositionAnimation();
        _trayLeaving = true;
        UpdatePinCaret();
        StopEntranceWork();
        CancelThumbnailLoads();
        CancelCardArrivals();
        if (!animate || !IsVisible || AnimationConfig.ReduceMotion) { ResetCardMotion(Cards); return; }
        AnimateTrayDropBorder(false);
        foreach (var layer in new FrameworkElement[] { Cards, CategoryScroll, SearchSurface, ToolbarSurface,
            LockPanel, ApprovalPanel, DetailPanel, EmptyText, EmptyDropSilhouette })
            if (layer.IsVisible) AnimateShelfLayer(layer, entering: false);
    }

    private void ShowTransientLayer(FrameworkElement element)
    {
        bool reversing = _dismissingLayers.Remove(element);
        element.IsHitTestVisible = true;
        if (element == LockPanel) element.IsEnabled = true;
        if (element.Visibility != Visibility.Visible) element.Visibility = Visibility.Visible;
        else if (reversing) AnimateLayer(element, true, 0);
        if (element == LockPanel && reversing) QueuePasscodeFocus();
    }

    private void DismissTransientLayer(FrameworkElement element, Action? completed = null, bool immediately = false)
    {
        if (immediately || _disposed || !element.IsVisible || _trayLeaving)
        {
            _dismissingLayers.Remove(element);
            element.Visibility = Visibility.Collapsed;
            ResetCardMotion(element);
            completed?.Invoke();
            return;
        }
        if (_dismissingLayers.ContainsKey(element)) return;
        var dismissal = new object();
        _dismissingLayers[element] = dismissal;
        element.IsHitTestVisible = false;
        if (element == LockPanel) element.IsEnabled = false;
        AnimateLayer(element, false, 0, completed: () =>
        {
            // A newer message or confirmation may have reversed this fade.
            if (!_dismissingLayers.TryGetValue(element, out var current) || !ReferenceEquals(current, dismissal)) return;
            _dismissingLayers.Remove(element);
            element.Visibility = Visibility.Collapsed;
            ResetCardMotion(element);
            completed?.Invoke();
        });
    }

    internal static void AnimateLayer(FrameworkElement element, bool entering, int delay, bool fresh = false, int customDuration = 0, double driftY = 6, Action? completed = null)
    {
        if (element.Name == "LockPanel") { delay = 0; driftY = 6; }
        var (scale, translate) = LayerTransforms(element);
        double opacity = fresh ? 0 : element.Opacity;
        // Leave style and binding values, such as the disabled search opacity, in control.
        bool ownsOpacity = element.ReadLocalValue(OpacityProperty) is double;
        double restingOpacity = ownsOpacity ? 1 : (double)element.GetAnimationBaseValue(OpacityProperty);
        double targetOpacity = entering ? restingOpacity : 0;
        element.BeginAnimation(OpacityProperty, null);
        if (ownsOpacity) element.Opacity = targetOpacity;
        if (AnimationConfig.ReduceMotion)
        {
            if (!entering && !ownsOpacity)
                element.BeginAnimation(OpacityProperty,
                    new DoubleAnimation(0, new Duration(TimeSpan.Zero)) { FillBehavior = FillBehavior.HoldEnd });
            Motion.Stop(scale, ScaleTransform.ScaleXProperty, 1);
            Motion.Stop(scale, ScaleTransform.ScaleYProperty, 1);
            Motion.Stop(translate, TranslateTransform.XProperty, 0);
            Motion.Stop(translate, TranslateTransform.YProperty, 0);
            completed?.Invoke();
            return;
        }
        var wait = TimeSpan.FromMilliseconds(Math.Min(Math.Max(delay, 0), Motion.StaggerCapMs));
        var spring = entering ? Motion.Tile : Motion.Exit;
        Motion.To(scale, ScaleTransform.ScaleXProperty, entering ? 1 : Motion.ExitToScale, entering ? Motion.TileScale : spring,
            from: fresh ? Motion.TileFromScale : null, delay: wait);
        Motion.To(scale, ScaleTransform.ScaleYProperty, entering ? 1 : Motion.ExitToScale, entering ? Motion.TileScale : spring,
            from: fresh ? Motion.TileFromScale : null, delay: wait);
        Motion.To(translate, TranslateTransform.XProperty, 0, spring, from: fresh ? 0 : null, delay: wait);
        Motion.To(translate, TranslateTransform.YProperty, entering ? 0 : driftY, spring,
            from: fresh ? driftY : null, delay: wait);
        var fade = CreateLayerAnimation(opacity, targetOpacity, (int)wait.TotalMilliseconds, entering ? 180 : 140);
        if (!entering && !ownsOpacity) fade.FillBehavior = FillBehavior.HoldEnd;
        if (completed != null) fade.Completed += (_, _) => completed();
        element.BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
    }

    private static (ScaleTransform Scale, TranslateTransform Translate) LayerTransforms(FrameworkElement element)
    {
        if (element.RenderTransform is TransformGroup group && !group.IsFrozen &&
            FindTransform<ScaleTransform>(group.Children) is { } existingScale &&
            FindTransform<TranslateTransform>(group.Children) is { } existingTranslate)
            return (existingScale, existingTranslate);
        var rendered = element.RenderTransform.Value;
        var scale = new ScaleTransform(rendered.M11, rendered.M22);
        var translate = new TranslateTransform(rendered.OffsetX, rendered.OffsetY);
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.RenderTransform = new TransformGroup { Children = { scale, translate } };
        return (scale, translate);
    }

    private static DoubleAnimationUsingKeyFrames CreateLayerAnimation(double from, double to, int delay, int duration)
    {
        var animation = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        if (delay > 0)
            animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay))));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay + duration)),
            TrayEase));
        Timeline.SetDesiredFrameRate(animation, AnimationConfig.TargetFps);
        return animation;
    }

    private static void AnimateValue(Animatable target, DependencyProperty property, double value, int milliseconds)
    {
        Motion.To(target, property, value, milliseconds == MotionFast ? Motion.PressDown : Motion.Press);
    }

    private void Button_PointerDown(object sender, MouseButtonEventArgs e) => SetButtonScale(sender, 0.96, MotionFast);
    private void Button_PointerUp(object sender, MouseButtonEventArgs e) => SetButtonScale(sender, 1, MotionStandard);
    private void Button_PointerLeave(object sender, MouseEventArgs e) => SetButtonScale(sender, 1, MotionStandard);
    private void Button_KeyDown(object sender, KeyEventArgs e)
    {
        if (!e.IsRepeat && e.Key is Key.Space or Key.Enter) SetButtonScale(sender, 0.96, MotionFast);
    }
    private void Button_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Space or Key.Enter) SetButtonScale(sender, 1, MotionStandard);
    }
    private void Button_Reset(object sender, RoutedEventArgs e) => SetButtonScale(sender, 1, MotionStandard);
    private void GroupChoice_Motion(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { IsLoaded: true } element) return;
        AnimateCardPress(element, false);
        if (!AnimationConfig.ReduceMotion && element.RenderTransform is TransformGroup group && FindTransform<ScaleTransform>(group.Children) is { } scale)
        {
            Motion.To(scale, ScaleTransform.ScaleXProperty, 0.96, Motion.PressDown,
                done: () => Motion.To(scale, ScaleTransform.ScaleXProperty, 1, Motion.Press));
            Motion.To(scale, ScaleTransform.ScaleYProperty, 0.96, Motion.PressDown,
                done: () => Motion.To(scale, ScaleTransform.ScaleYProperty, 1, Motion.Press));
        }
    }
    private static void SetButtonScale(object sender, double value, int duration)
    {
        if (sender is not Button button) return;
        if (AnimationConfig.ReduceMotion) value = 1;
        ScaleTransform scale;
        if (button.RenderTransform is ScaleTransform single)
        {
            scale = single.IsFrozen ? single.CloneCurrentValue() : single;
            if (!ReferenceEquals(scale, single)) button.RenderTransform = scale;
        }
        else if (button.RenderTransform is TransformGroup group)
        {
            if (group.IsFrozen) button.RenderTransform = group = group.CloneCurrentValue();
            ScaleTransform? foundScale = null;
            for (int i = 0; i < group.Children.Count; i++)
            {
                if (group.Children[i] is ScaleTransform candidate)
                {
                    foundScale = candidate;
                    break;
                }
            }
            scale = foundScale ?? new ScaleTransform(1, 1);
            if (!group.Children.Contains(scale)) group.Children.Insert(0, scale);
            else if (scale.IsFrozen)
            {
                int index = group.Children.IndexOf(scale);
                group.Children[index] = scale = scale.CloneCurrentValue();
            }
        }
        else
        {
            // Header/clear buttons can still be translating in from their entrance.
            var existing = button.RenderTransform;
            scale = new ScaleTransform(1, 1);
            var transforms = new TransformGroup { Children = { scale } };
            if (existing != null && !ReferenceEquals(existing, Transform.Identity)) transforms.Children.Add(existing);
            button.RenderTransform = transforms;
            button.RenderTransformOrigin = new Point(0.5, 0.5);
        }
        if (!button.IsLoaded || !button.IsVisible || !button.IsEnabled || !SystemParameters.ClientAreaAnimation)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            scale.ScaleX = scale.ScaleY = 1;
            return;
        }
        AnimateValue(scale, ScaleTransform.ScaleXProperty, value, duration);
        AnimateValue(scale, ScaleTransform.ScaleYProperty, value, duration);
    }

    private readonly HashSet<Guid> _arrivingCards = [];
    internal Dictionary<Guid, double>? PendingCardPositions => _pendingCardPositions;
    private Dictionary<Guid, double>? _pendingCardPositions;
    private Dictionary<Guid, (double X, Transform Motion)>? _capturedCardLayouts;
    private Dictionary<Guid, (double X, Transform Motion)>? _pendingCardLayouts;

    // Recycled containers can be detached from Cards or from any
    // PresentationSource; TranslatePoint throws in that state.
    private bool TryGetPositionInCards(UIElement element, out Point point)
    {
        point = default;
        if (!Cards.IsAncestorOf(element) || PresentationSource.FromVisual(element) == null) return false;
        try { point = element.TranslatePoint(new Point(), Cards); return true; }
        catch (InvalidOperationException) { return false; }
    }

    internal Dictionary<Guid, double> CaptureCardPositions()
    {
        var positions = new Dictionary<Guid, double>();
        _capturedCardLayouts = null;
        if (_animateResults || AnimationConfig.ReduceMotion || !Cards.IsLoaded) return positions;
        _capturedCardLayouts = [];
        double scrollOffset = FindScrollViewer(Cards)?.HorizontalOffset ?? 0;
        foreach (var item in _selectionContainers)
            if (item.IsVisible && item.DataContext is VNotch.ViewModels.ClipboardCardViewModel card &&
                TryGetPositionInCards(item, out var origin))
            {
                positions[card.Entry.Id] = origin.X + scrollOffset;
                _capturedCardLayouts[card.Entry.Id] =
                    (positions[card.Entry.Id] - item.RenderTransform.Value.OffsetX, item.RenderTransform);
            }
        return positions;
    }

    internal void AnimateCardPositions(Dictionary<Guid, double> positions)
    {
        var layouts = _capturedCardLayouts;
        CancelCardPositionAnimation();
        if (positions.Count == 0 || _disposed || _trayLeaving || !IsLoaded) return;
        _pendingCardPositions = positions;
        _pendingCardLayouts = layouts;
        Cards.LayoutUpdated += ApplyCardPositionsAfterLayout;
    }

    private void ApplyCardPositionsAfterLayout(object? sender, EventArgs e)
    {
        if (!Cards.IsMeasureValid || !Cards.IsArrangeValid) return;
        // The ListBox can be valid while its virtualizing items host still has
        // the old slot positions. Wait for that host before measuring the move.
        foreach (var item in _selectionContainers)
            if (item.IsVisible && VisualTreeHelper.GetParent(item) is UIElement host &&
                (!host.IsMeasureValid || !host.IsArrangeValid)) return;
        var positions = _pendingCardPositions;
        var layouts = _pendingCardLayouts;
        CancelCardPositionAnimation();
        if (positions == null || _disposed || _trayLeaving || !IsVisible || AnimationConfig.ReduceMotion) return;
        double scrollOffset = FindScrollViewer(Cards)?.HorizontalOffset ?? 0;
        foreach (var item in _selectionContainers)
        {
            if (!item.IsVisible || item.DataContext is not VNotch.ViewModels.ClipboardCardViewModel card ||
                !positions.TryGetValue(card.Entry.Id, out double previous) ||
                !TryGetPositionInCards(item, out var origin)) continue;
            var rendered = item.RenderTransform.Value;
            (double X, Transform Motion) previousLayout = default;
            bool hasPrevLayout = layouts != null && layouts.TryGetValue(card.Entry.Id, out previousLayout);
            byte sameTransform = hasPrevLayout && ReferenceEquals(item.RenderTransform, previousLayout.Motion) ? (byte)1 : (byte)0;

            vn_clipboard_motion_compute_reflow_offset(
                origin.X,
                rendered.OffsetX,
                scrollOffset,
                previous,
                hasPrevLayout ? (byte)1 : (byte)0,
                hasPrevLayout ? previousLayout.X : 0.0,
                sameTransform,
                out _,
                out double offset,
                out byte shouldAnimate);

            // Metadata-only refreshes must not restart an in-flight slide.
            if (shouldAnimate == 0) continue;

            var (_, translate) = LayerTransforms(item);
            double speedX = Motion.VelocityOf(translate, TranslateTransform.XProperty);
            translate.SetValue(TranslateTransform.XProperty, 0d);
            Motion.To(translate, TranslateTransform.XProperty, 0, Motion.Glide, from: offset, velocity: speedX);
            Motion.To(translate, TranslateTransform.YProperty, 0, Motion.Glide);
        }
    }

    private void CancelCardPositionAnimation()
    {
        Cards.LayoutUpdated -= ApplyCardPositionsAfterLayout;
        _pendingCardPositions = null;
        _pendingCardLayouts = null;
        _capturedCardLayouts = null;
    }

    private void RecordArrivingCards(IReadOnlyList<VNotch.Models.ClipboardEntry> entries)
    {
        var existing = _cards.ById;
        Span<Guid> arriving = stackalloc Guid[33];
        int arrivingCount = 0;
        for (int i = 0; i < entries.Count; i++)
        {
            var id = entries[i].Id;
            if (!existing.ContainsKey(id))
            {
                arriving[arrivingCount++] = id;
                if (arrivingCount > 32) break;
            }
        }
        if (arrivingCount <= 32)
        {
            for (int i = 0; i < arrivingCount; i++)
                _arrivingCards.Add(arriving[i]);
        }
    }

    private async Task AnimateCollectionChangesAsync(IReadOnlyList<VNotch.Models.ClipboardEntry> entries, CancellationToken cancellation)
    {
        if (_animateResults || _trayLeaving || !IsVisible || AnimationConfig.ReduceMotion) return;
        List<(ListBoxItem Item, Guid EntryId)>? removing = null;
        foreach (var item in _selectionContainers)
        {
            if (!item.IsVisible || item.DataContext is not VNotch.ViewModels.ClipboardCardViewModel card || ContainsEntryId(entries, card.Entry.Id)) continue;
            AnimateCardLifecycle(item, entering: false);
            removing ??= new List<(ListBoxItem Item, Guid EntryId)>();
            removing.Add((item, card.Entry.Id));
        }
        if (removing is { Count: > 0 })
        {
            try
            {
                // Survivors move once, after reconciliation, from their last
                // rendered positions. Moving them before layout creates a second
                // slide when the actual item slots change.
                await Task.Delay(160, cancellation);
                cancellation.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                foreach (var (item, entryId) in removing)
                    if (!_trayLeaving && item.IsVisible && item.DataContext is VNotch.ViewModels.ClipboardCardViewModel card && card.Entry.Id == entryId)
                    {
                        // Reverse from the rendered state instead of flashing back to opaque.
                        AnimateCardLifecycle(item, entering: true);
                    }
                throw;
            }
            // Keep completed exits hidden until reconciliation removes them.
            // Container cleanup resets motion before a recycled card is reused.
        }
    }

    private static void AnimateCardLifecycle(FrameworkElement element, bool entering, bool fresh = false) =>
        AnimateShelfLayer(element, entering, fresh);

    private static void AnimateShelfLayer(FrameworkElement element, bool entering, bool fresh = false, Action? completed = null)
    {
        if (!element.IsVisible || AnimationConfig.ReduceMotion)
        {
            ResetCardMotion(element);
            completed?.Invoke();
            return;
        }
        var (_, translate) = LayerTransforms(element);
        Motion.To(translate, TranslateTransform.YProperty,
            entering ? 0 : -6, entering ? Motion.Shift : Motion.Exit,
            from: fresh ? -6 : null);
        // Keep the template's resting opacity available when a container is recycled.
        double restingOpacity = (double)element.GetAnimationBaseValue(OpacityProperty);
        if (entering || element.Opacity > 0)
            Motion.To(element, OpacityProperty, entering ? restingOpacity : 0, entering ? Motion.Exit : Motion.PressDown,
                from: fresh ? restingOpacity * 0.72 : null, done: completed, restoreBaseValue: entering);
        else
            completed?.Invoke();
    }

    private void ResetShelfMotion()
    {
        foreach (var layer in new FrameworkElement[] { SearchSurface, ToolbarSurface, CategoryScroll, Cards,
            LockPanel, ApprovalPanel, DetailPanel, EmptyText, EmptyDropSilhouette, StatusPanel,
            DeleteConfirmationPanel, LabelEditorPanel })
            ResetCardMotion(layer);
    }

    private void CancelCardArrivals()
    {
        _arrivingCards.Clear();
        foreach (var item in _selectionContainers) ResetCardMotion(item);
    }

    private void ConsumeCardArrival(ListBoxItem item, Guid entryId)
    {
        if (_arrivingCards.Remove(entryId) && item.IsVisible && !_trayLeaving && !_disposed &&
            !AnimationConfig.ReduceMotion)
            AnimateCardLifecycle(item, entering: true, fresh: true);
    }

    private void CardContainer_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ListBoxItem item) return;
        RegisterSelectionContainer(item);
        ResetCardMotion(item);
        if (item.DataContext is VNotch.ViewModels.ClipboardCardViewModel card)
            ConsumeCardArrival(item, card.Entry.Id);
    }

    private void CardContainer_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ListBoxItem item) return;
        UnregisterSelectionContainer(item);
        ResetCardMotion(item);
    }

    private void RevealResults()
    {
        if (!_animateResults || _trayLeaving) return;
        _animateResults = false;
        if (Cards.IsVisible && !AnimationConfig.ReduceMotion)
            AnimateCardLifecycle(Cards, entering: true, fresh: !Motion.IsRunning(Cards, OpacityProperty));
    }

    private static void ResetCardMotion(FrameworkElement element)
    {
        if (element.RenderTransform is TransformGroup group && !group.IsFrozen)
        {
            if (FindTransform<ScaleTransform>(group.Children) is { } scale)
            {
                Motion.Stop(scale, ScaleTransform.ScaleXProperty, 1);
                Motion.Stop(scale, ScaleTransform.ScaleYProperty, 1);
            }
            if (FindTransform<TranslateTransform>(group.Children) is { } translate)
            {
                Motion.Stop(translate, TranslateTransform.XProperty, 0);
                Motion.Stop(translate, TranslateTransform.YProperty, 0);
            }
        }
        element.RenderTransform = Transform.Identity;
        Motion.Forget(element, OpacityProperty);
        element.BeginAnimation(OpacityProperty, null);
        if (element.ReadLocalValue(OpacityProperty) is double) element.Opacity = 1;
    }

    private static void AnimateCardPress(FrameworkElement element, bool pressed)
    {
        if (AnimationConfig.ReduceMotion) pressed = false;
        var (scale, _) = LayerTransforms(element);
        AnimateValue(scale, ScaleTransform.ScaleXProperty, pressed ? 0.95 : 1, pressed ? MotionFast : MotionStandard);
        AnimateValue(scale, ScaleTransform.ScaleYProperty, pressed ? 0.95 : 1, pressed ? MotionFast : MotionStandard);
    }

    private void Card_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement element) AnimateCardPress(element, false);
    }

    private void SetTrayDropBorderVisible(bool visible)
    {
        if (_trayDropBorderVisible == visible) return;
        _trayDropBorderVisible = visible;
        if (!IsLoaded || !IsVisible || _trayLeaving || _disposed)
        {
            ++_trayDropBorderMotionVersion;
            TrayDropBorder.BeginAnimation(OpacityProperty, null);
            TrayDropBorder.Opacity = visible ? 1 : 0;
            TrayDropBorder.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            return;
        }
        AnimateTrayDropBorder(visible, collapseOnExit: !visible);
    }

    private void AnimateTrayDropBorder(bool entering, bool fresh = false, bool collapseOnExit = false)
    {
        if (TrayDropBorder == null || _disposed) return;
        int version = ++_trayDropBorderMotionVersion;
        // Capture the rendered value before removing the clock so rapid reversals
        // continue from the current fade rather than jumping to its old target.
        double fromOpacity = fresh || TrayDropBorder.Visibility != Visibility.Visible ? 0 : TrayDropBorder.Opacity;
        double targetOpacity = entering ? 1 : 0;
        TrayDropBorder.BeginAnimation(OpacityProperty, null);
        TrayDropBorder.Opacity = targetOpacity;
        if (entering) TrayDropBorder.Visibility = Visibility.Visible;

        if (Math.Abs(fromOpacity - targetOpacity) < 1e-6)
        {
            if (collapseOnExit) TrayDropBorder.Visibility = Visibility.Collapsed;
            return;
        }

        var fade = new DoubleAnimation(fromOpacity, targetOpacity,
            TimeSpan.FromMilliseconds(AnimationConfig.ReduceMotion ? 80 : MotionStandard))
        {
            EasingFunction = TrayMotion.EaseOut,
            FillBehavior = FillBehavior.Stop
        };
        if (collapseOnExit)
            fade.Completed += (_, _) =>
            {
                if (!_disposed && version == _trayDropBorderMotionVersion && !_trayDropBorderVisible)
                    TrayDropBorder.Visibility = Visibility.Collapsed;
            };
        Timeline.SetDesiredFrameRate(fade, AnimationConfig.TargetFps);
        TrayDropBorder.BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
    }
}
