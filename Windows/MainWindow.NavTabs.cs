using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using VNotch.Models;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;
using static VNotch.Services.VNotchNative;

namespace VNotch;

public partial class MainWindow
{
    private FrameworkElement? _navDragItem;
    private Point _navDragStartPoint;
    private double _navReleaseVelocity;
    private double _navSampleX;
    private long _navSampleTick;
    private bool _isNavDragging = false;
    private bool _hasCapturedNavMouse = false;
    private long _navMouseDownTick;
    private const double NavTabSlotWidth = 26.0; // 22 width + 4 margin
    private const long HoldToDragThresholdMs = 160;

    private bool _isEndingNavDrag = false;
    private int _dragInitialSlot = -1;
    private int _dragTargetSlot = -1;
    private readonly Dictionary<FrameworkElement, double> _neighborTargetOffsets = new();
    private readonly List<FrameworkElement> _visibleNavChildrenBuffer = new(8);
    private Border? _navSelectionIndicator;
    private readonly TranslateTransform _navSelectionTranslate = new();
    private NotchView? _navSelectionView;

    private void UpdateNavSelection(NotchView target, bool animate)
    {
        if (target == NotchView.Compact) return;
        var visible = CollectVisibleNavChildren();
        int slot = visible.FindIndex(item => item.Tag is string tag && tag == target.ToString());
        if (slot < 0) return;
        bool first = _navSelectionIndicator == null;
        if (first)
        {
            _navSelectionIndicator = new Border
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(7),
                Background = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Margin = NavTabsStackPanel.Margin, IsHitTestVisible = false, RenderTransform = _navSelectionTranslate
            };
            NavIconsPanel.Children.Insert(0, _navSelectionIndicator);
        }
        if (animate && !first)
            Motion.To(_navSelectionTranslate, TranslateTransform.XProperty, slot * NavTabSlotWidth, Motion.Shift);
        else Motion.Stop(_navSelectionTranslate, TranslateTransform.XProperty, slot * NavTabSlotWidth);
        if (_navSelectionView != target && animate && _navSelectionView != null &&
            visible[slot].RenderTransform is TransformGroup group && FindScaleTransform(group) is { } scale)
        {
            Motion.To(scale, ScaleTransform.ScaleXProperty, 1, Motion.Pop, from: .8);
            Motion.To(scale, ScaleTransform.ScaleYProperty, 1, Motion.Pop, from: .8);
        }
        _navSelectionView = target;
    }

    private static TranslateTransform? FindTranslateTransform(TransformGroup group)
    {
        var children = group.Children;
        for (int i = 0; i < children.Count; i++)
        {
            if (children[i] is TranslateTransform tt)
                return tt;
        }
        return null;
    }

    private static ScaleTransform? FindScaleTransform(TransformGroup group)
    {
        var children = group.Children;
        for (int i = 0; i < children.Count; i++)
        {
            if (children[i] is ScaleTransform st)
                return st;
        }
        return null;
    }

    private List<FrameworkElement> CollectVisibleNavChildren()
    {
        _visibleNavChildrenBuffer.Clear();
        if (NavTabsStackPanel == null) return _visibleNavChildrenBuffer;

        var children = NavTabsStackPanel.Children;
        int count = children.Count;
        for (int i = 0; i < count; i++)
        {
            if (children[i] is FrameworkElement fe && fe.Visibility == Visibility.Visible)
                _visibleNavChildrenBuffer.Add(fe);
        }
        return _visibleNavChildrenBuffer;
    }

    #region Hold-to-Drag Reordering Handlers

    private void NavIcon_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe)
        {
            _navDragItem = fe;
            _navDragStartPoint = e.GetPosition(NavTabsStackPanel);
            _navMouseDownTick = Environment.TickCount64;
            _navSampleTick = _navMouseDownTick;
            _navSampleX = _navDragStartPoint.X;
            _navReleaseVelocity = 0;
            _isNavDragging = false;
            _hasCapturedNavMouse = false;
            _dragInitialSlot = -1;
            _dragTargetSlot = -1;
            _neighborTargetOffsets.Clear();
            e.Handled = true;
        }
    }

    private void NavIcon_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_navDragItem == null || e.LeftButton != MouseButtonState.Pressed) return;

        Point current = e.GetPosition(NavTabsStackPanel);
        double deltaX = current.X - _navDragStartPoint.X;
        long sampleTick = Environment.TickCount64;
        long elapsed = sampleTick - _navSampleTick;
        if (elapsed > 0) _navReleaseVelocity = Math.Clamp((current.X - _navSampleX) * 1000 / elapsed, -1200, 1200);
        _navSampleTick = sampleTick;
        _navSampleX = current.X;

        if (!_isNavDragging)
        {
            bool movedPastThreshold = Math.Abs(deltaX) > 4;
            bool heldPastThreshold = (Environment.TickCount64 - _navMouseDownTick) >= HoldToDragThresholdMs && Math.Abs(deltaX) > 2;

            if (movedPastThreshold || heldPastThreshold)
            {
                _isNavDragging = true;
                _navDragStartPoint = current;
                deltaX = 0;
                _hasCapturedNavMouse = _navDragItem.CaptureMouse();
                Panel.SetZIndex(_navDragItem, 1000);
                AnimateNavDragLift(_navDragItem, true);
                Mouse.OverrideCursor = Cursors.SizeWE;

                if (NavTabsStackPanel != null)
                {
                    var visibleChildren = CollectVisibleNavChildren();
                    _dragInitialSlot = visibleChildren.IndexOf(_navDragItem);
                    _dragTargetSlot = _dragInitialSlot;
                }
            }
        }

        if (_isNavDragging)
        {
            e.Handled = true;

            // Live horizontal displacement tracking cursor directly
            if (_navDragItem.RenderTransform is TransformGroup group)
            {
                var translate = FindTranslateTransform(group);
                if (translate != null)
                {
                    Motion.Stop(translate, TranslateTransform.XProperty, deltaX);
                }
            }

            UpdateNeighborSlotDisplacements();
        }
    }

    private void UpdateNeighborSlotDisplacements()
    {
        if (_navDragItem == null || NavTabsStackPanel == null) return;

        var visibleChildren = CollectVisibleNavChildren();
        int totalSlots = visibleChildren.Count;
        if (totalSlots <= 1) return;

        int initialSlot = visibleChildren.IndexOf(_navDragItem);
        if (initialSlot < 0) return;

        _dragInitialSlot = initialSlot;

        // Calculate visual position of dragged item
        double currentTranslateX = 0.0;
        if (_navDragItem.RenderTransform is TransformGroup group)
        {
            var translate = FindTranslateTransform(group);
            if (translate != null)
            {
                currentTranslateX = translate.X;
            }
        }

        double visualX = (initialSlot * NavTabSlotWidth) + currentTranslateX;
        int targetSlot = CalculateNavTargetSlotWithHysteresis(_dragTargetSlot, visualX, NavTabSlotWidth, totalSlots);
        _dragTargetSlot = targetSlot;

        // Smoothly glide neighbors to open a gap for the dragged item
        for (int i = 0; i < totalSlots; i++)
        {
            var child = visibleChildren[i];
            if (child == _navDragItem) continue;

            double desiredOffset = vn_nav_tabs_calculate_neighbor_offset(i, initialSlot, targetSlot, NavTabSlotWidth, out double offset) == 0
                ? offset
                : 0.0;

            AnimateElementToX(child, desiredOffset);
        }
    }

    private static int CalculateNavTargetSlotWithHysteresis(int currentTarget, double visualPos, double pitch, int totalSlots)
    {
        return vn_nav_tabs_calculate_target_slot_hysteresis(currentTarget, visualPos, pitch, totalSlots, out int slot) == 0
            ? slot
            : Math.Clamp(currentTarget, 0, Math.Max(0, totalSlots - 1));
    }

    private void AnimateElementToX(FrameworkElement element, double targetX)
    {
        if (element.RenderTransform is not TransformGroup group) return;
        var translate = FindTranslateTransform(group);
        if (translate == null) return;

        if (_neighborTargetOffsets.TryGetValue(element, out double currentTarget) &&
            Math.Abs(currentTarget - targetX) < 0.5)
        {
            return;
        }

        _neighborTargetOffsets[element] = targetX;

        Motion.To(translate, TranslateTransform.XProperty, targetX, Motion.Glide);
    }

    private void NavIcon_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        bool wasDragging = _isNavDragging;
        var clickedItem = _navDragItem;

        EndNavDrag(saveOrder: wasDragging);

        if (!wasDragging && clickedItem != null && clickedItem.Tag is string tagStr && Enum.TryParse<NotchView>(tagStr, true, out var targetView))
        {
            NavigateToNotchView(targetView);
        }
    }

    private void NavIcon_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_isNavDragging)
        {
            EndNavDrag(saveOrder: true);
        }
        else
        {
            EndNavDrag(saveOrder: false);
        }
    }

    private void EndNavDrag(bool saveOrder)
    {
        if (_isEndingNavDrag) return;
        _isEndingNavDrag = true;

        try
        {
            var item = _navDragItem;
            bool wasDragging = _isNavDragging;

            _isNavDragging = false;
            _navDragItem = null;

            if (item != null)
            {
                if (_hasCapturedNavMouse)
                {
                    _hasCapturedNavMouse = false;
                    try
                    {
                        item.ReleaseMouseCapture();
                    }
                    catch (Exception)
                    {
                        // Ignore exceptions if mouse capture was already released
                    }
                }

                Mouse.OverrideCursor = null;

                if (wasDragging && _dragTargetSlot >= 0 && _dragInitialSlot >= 0 && _dragTargetSlot != _dragInitialSlot)
                {
                    double finalTranslateX = (_dragTargetSlot - _dragInitialSlot) * NavTabSlotWidth;
                    AnimateNavDragDropSettle(item, finalTranslateX, onCompleted: () =>
                    {
                        CommitFinalTabOrder();
                        if (saveOrder) PersistNavTabOrderFromUI();
                    });
                }
                else if (wasDragging)
                {
                    AnimateNavDragDropSettle(item, 0.0, onCompleted: () =>
                    {
                        ResetAllNavTabTransforms();
                        UpdateNavIconsActiveState();
                    });
                }
            }
            else if (wasDragging)
            {
                ResetAllNavTabTransforms();
                UpdateNavIconsActiveState();
            }
        }
        catch (Exception ex)
        {
            VNotch.Services.RuntimeLog.Error("NAV-DRAG", ex, "Error during EndNavDrag");
            ResetAllNavTabTransforms();
            UpdateNavIconsActiveState();
        }
        finally
        {
            _isEndingNavDrag = false;
            _neighborTargetOffsets.Clear();
        }
    }

    private void CommitFinalTabOrder()
    {
        if (NavTabsStackPanel == null || _dragInitialSlot < 0 || _dragTargetSlot < 0 || _dragInitialSlot == _dragTargetSlot)
        {
            ResetAllNavTabTransforms();
            UpdateNavIconsActiveState(animate: false);
            return;
        }

        var visibleChildren = CollectVisibleNavChildren();

        if (_dragInitialSlot < visibleChildren.Count && _dragTargetSlot < visibleChildren.Count)
        {
            var dragged = visibleChildren[_dragInitialSlot];
            var targetChild = visibleChildren[_dragTargetSlot];

            int fromIndex = NavTabsStackPanel.Children.IndexOf(dragged);
            int toIndex = NavTabsStackPanel.Children.IndexOf(targetChild);

            if (fromIndex >= 0 && toIndex >= 0 && fromIndex != toIndex)
            {
                NavTabsStackPanel.Children.RemoveAt(fromIndex);
                NavTabsStackPanel.Children.Insert(toIndex, dragged);
            }
        }

        ResetAllNavTabTransforms();
        UpdateNavIconsActiveState(animate: false);
    }

    private void AnimateNavDragLift(FrameworkElement? item, bool lifted)
    {
        if (item?.RenderTransform is not TransformGroup group) return;

        var scale = FindScaleTransform(group);
        var translate = FindTranslateTransform(group);
        var shadow = item.Effect as DropShadowEffect;

        if (!lifted) return;
        if (scale != null)
        {
            Motion.To(scale, ScaleTransform.ScaleXProperty, 1.14, Motion.Press);
            Motion.To(scale, ScaleTransform.ScaleYProperty, 1.14, Motion.Press);
        }
        if (translate != null) Motion.To(translate, TranslateTransform.YProperty, -2.5, Motion.Press);
        Motion.To(item, OpacityProperty, 1, Motion.Press);
        if (shadow != null)
        {
            if (shadow.IsFrozen) { shadow = shadow.Clone(); item.Effect = shadow; }
            Motion.To(shadow, DropShadowEffect.BlurRadiusProperty, 14, Motion.Press);
            Motion.To(shadow, DropShadowEffect.ShadowDepthProperty, 2.5, Motion.Press);
            Motion.To(shadow, DropShadowEffect.OpacityProperty, 0.95, Motion.Press);
        }
    }

    private void AnimateNavDragDropSettle(FrameworkElement item, double targetX, Action? onCompleted = null)
    {
        Panel.SetZIndex(item, 1000);

        if (item.RenderTransform is not TransformGroup group)
        {
            Panel.SetZIndex(item, 0);
            onCompleted?.Invoke();
            return;
        }

        var scale = FindScaleTransform(group);
        var translate = FindTranslateTransform(group);
        var shadow = item.Effect as DropShadowEffect;

        if (shadow != null)
        {
            if (shadow.IsFrozen) { shadow = shadow.Clone(); item.Effect = shadow; }
            Motion.To(shadow, DropShadowEffect.BlurRadiusProperty, 8, Motion.Press);
            Motion.To(shadow, DropShadowEffect.ShadowDepthProperty, 0, Motion.Press);
            Motion.To(shadow, DropShadowEffect.OpacityProperty, 0.9, Motion.Press);
        }

        // Determine target resting opacity for the dropped item based on active view state
        double targetOpacity = 0.45;
        if ((_isAudioView && item == AudioIconButton) ||
            (_isTimerView && item == TimerIconButton) ||
            (_isSecondaryView && item == ClipboardIconButton) ||
            (_isCameraView && item == CameraIconButton) ||
            (_isClaudeView && item == ClaudeIconButton) ||
            (_isPiView && item == PiIconButton) ||
            (_isSchoolView && item == SchoolIconButton) ||
            (_isRobloxView && item == RobloxIconButton) ||
            (_isAiUsageView && item == AiUsageIconButton) ||
            (!_isAudioView && !_isTimerView && !_isSecondaryView && !_isCameraView && !_isAiUsageView && !_isClaudeView && !_isPiView && !_isSchoolView && !_isRobloxView && item == HomeIconButton))
        {
            targetOpacity = 1.0;
        }

        Motion.To(item, OpacityProperty, targetOpacity, Motion.Press);
        if (scale != null)
        {
            Motion.To(scale, ScaleTransform.ScaleXProperty, 1, Motion.Press);
            Motion.To(scale, ScaleTransform.ScaleYProperty, 1, Motion.Press);
        }
        if (translate != null)
        {
            Motion.To(translate, TranslateTransform.YProperty, 0, Motion.Glide);
            double velocity = Environment.TickCount64 - _navSampleTick <= 100 ? _navReleaseVelocity : 0;
            Motion.To(translate, TranslateTransform.XProperty, targetX, Motion.Glide, velocity: velocity, done: () =>
            {
                Panel.SetZIndex(item, 0);
                onCompleted?.Invoke();
            });
        }
        else
        {
            Panel.SetZIndex(item, 0);
            onCompleted?.Invoke();
        }
    }

    private void ResetAllNavTabTransforms(FrameworkElement? excludeItem = null)
    {
        if (NavTabsStackPanel == null) return;

        var children = NavTabsStackPanel.Children;
        int count = children.Count;
        for (int i = 0; i < count; i++)
        {
            if (children[i] is not FrameworkElement child || child == excludeItem)
                continue;

            if (child.RenderTransform is TransformGroup group)
            {
                var translate = FindTranslateTransform(group);
                if (translate != null)
                {
                    translate.BeginAnimation(TranslateTransform.XProperty, null);
                    translate.BeginAnimation(TranslateTransform.YProperty, null);
                    translate.X = 0;
                    translate.Y = 0;
                }

                var scale = FindScaleTransform(group);
                if (scale != null)
                {
                    scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                    scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                    scale.ScaleX = 1.0;
                    scale.ScaleY = 1.0;
                }
            }

            Panel.SetZIndex(child, 0);

            if (child.Effect is DropShadowEffect shadow && !shadow.IsFrozen)
            {
                shadow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, null);
                shadow.BeginAnimation(DropShadowEffect.ShadowDepthProperty, null);
                shadow.BeginAnimation(DropShadowEffect.OpacityProperty, null);
                shadow.Color = Colors.Black;
                shadow.BlurRadius = 8;
                shadow.ShadowDepth = 0;
                shadow.Opacity = 0.9;
            }
        }
    }

    private void PersistNavTabOrderFromUI()
    {
        if (NavTabsStackPanel == null) return;

        var children = NavTabsStackPanel.Children;
        var orderedTags = new List<string>(children.Count);
        for (int i = 0; i < children.Count; i++)
        {
            if (children[i] is FrameworkElement fe && fe.Tag is string tag && !string.IsNullOrEmpty(tag))
            {
                orderedTags.Add(tag);
            }
        }

        if (orderedTags.Count > 0)
        {
            string newOrder = string.Join(",", orderedTags);
            if (!string.Equals(_settings.NavTabOrder, newOrder, StringComparison.OrdinalIgnoreCase))
            {
                _settings.NavTabOrder = newOrder;
                _settingsService.SaveAsync(_settings).SafeFireAndForget("SETTINGS-SAVE");
            }
        }
    }

    #endregion

    #region Tab Sequence & Universal Navigation

    public void ApplyNavTabOrderAndVisibility()
    {
        if (NavTabsStackPanel == null) return;

        var children = NavTabsStackPanel.Children;
        int childCount = children.Count;

        // Build mapping of Tag -> UIElement
        var elementsByTag = new Dictionary<string, FrameworkElement>(childCount, StringComparer.OrdinalIgnoreCase);
        var currentChildrenTags = new List<string>(childCount);
        for (int i = 0; i < childCount; i++)
        {
            if (children[i] is FrameworkElement fe && fe.Tag is string tag && !string.IsNullOrEmpty(tag))
            {
                elementsByTag[tag] = fe;
                currentChildrenTags.Add(tag);
            }
        }

        // Determine configured order
        var orderTokens = SettingsWindow.ResolveFullNavTabOrder(_settings.NavTabOrder, elementsByTag.Keys);

        // Reorder children in NavTabsStackPanel only if the order actually changed
        bool sameOrder = currentChildrenTags.Count == orderTokens.Count;
        for (int i = 0; sameOrder && i < currentChildrenTags.Count; i++)
        {
            if (!string.Equals(currentChildrenTags[i], orderTokens[i], StringComparison.OrdinalIgnoreCase))
                sameOrder = false;
        }

        if (!sameOrder)
        {
            children.Clear();
            for (int i = 0; i < orderTokens.Count; i++)
            {
                if (elementsByTag.TryGetValue(orderTokens[i], out var element))
                {
                    children.Add(element);
                }
            }
        }

        // Apply visibility from VisibleNavTabs
        var visibleTokens = new HashSet<string>(
            SettingsWindow.ParseUniqueTabTokens(_settings.VisibleNavTabs),
            StringComparer.OrdinalIgnoreCase);

        // Home/Media is always kept visible
        visibleTokens.Add("Media");
        if (_settings.HideCamera) visibleTokens.Remove("Camera");
        else visibleTokens.Add("Camera");

        for (int i = 0; i < children.Count; i++)
        {
            if (children[i] is FrameworkElement fe && fe.Tag is string tag)
            {
                fe.Visibility = visibleTokens.Contains(tag) ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        // If the active view's tab was just disabled, return gracefully to Home
        if ((_isAudioView && !visibleTokens.Contains("AudioMixer")) ||
            (_isTimerView && !visibleTokens.Contains("Timer")) ||
            (_isSecondaryView && !visibleTokens.Contains("Secondary")) || (_isCameraView && !visibleTokens.Contains("Camera")) ||
            (_isAiUsageView && !visibleTokens.Contains("AiUsage")) ||
            (_isClaudeView && !visibleTokens.Contains("Claude")) ||
            (_isPiView && !visibleTokens.Contains("Pi")) ||
            (_isSchoolView && !visibleTokens.Contains("School")) ||
            (_isRobloxView && !visibleTokens.Contains("Roblox")))
        {
            NavigateToNotchView(NotchView.Media);
        }

        ResetAllNavTabTransforms();
        UpdateNavIconsActiveState();
    }

    public List<NotchView> GetActiveTabSequence()
    {
        var sequence = new List<NotchView>(7);
        if (NavTabsStackPanel == null)
        {
            sequence.Add(NotchView.Media);
            return sequence;
        }

        var children = NavTabsStackPanel.Children;
        for (int i = 0; i < children.Count; i++)
        {
            if (children[i] is FrameworkElement fe &&
                fe.Visibility == Visibility.Visible &&
                fe.Tag is string tagStr &&
                Enum.TryParse<NotchView>(tagStr, true, out var view))
            {
                sequence.Add(view);
            }
        }

        if (sequence.Count == 0)
        {
            sequence.Add(NotchView.Media);
        }

        return sequence;
    }

    public void NavigateToNotchView(NotchView targetView)
    {
        _transitionCoordinator.RequestView(targetView, "NavTabClick");
    }

    #endregion
}
