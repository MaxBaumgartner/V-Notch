using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Services;

namespace VNotch.Controls;

public sealed partial class ClipboardTray
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct VnCategoryPillMotionNative
    {
        public double StartScaleX;
        public double PeakScaleX;
        public double SettleOvershootScaleX;
        public double DipScaleY;
        public double ReboundScaleY;
        public double ContentPopScale;
        public double GlowPeakOpacity;
        public int PeakTimeMs;
        public int SettleOvershootTimeMs;
        public int DurationMs;
    }

    [LibraryImport("VNotch.Native")]
    internal static partial int vn_animation_compute_category_pill_motion(
        double currentX,
        double currentWidth,
        double targetX,
        double targetWidth,
        byte reducedMotion,
        out VnCategoryPillMotionNative outMotion);

    private Rect _categorySelectionBounds = Rect.Empty;
    private string? _animatedCategoryName;

    private void CategoryPanel_LayoutUpdated(object? sender, EventArgs e)
    {
        if (_categoryReordering) { CategorySelection.Opacity = 0; return; }
        if (_disposed || !IsVisible || !CategoryPanel.IsArrangeValid) return;
        var selected = _categoryItems.FirstOrDefault(item => item.IsSelected);
        if (selected == null || CategoryPanel.ItemContainerGenerator.ContainerFromItem(selected) is not ContentPresenter presenter ||
            VisualTreeHelper.GetChildrenCount(presenter) == 0 || VisualTreeHelper.GetChild(presenter, 0) is not Button button ||
            button.ActualWidth <= 0) return;

        if (CategorySelection.RenderTransform is TransformGroup transformGroup &&
            !transformGroup.IsFrozen &&
            !transformGroup.Children.Contains(_categoryElasticTransform))
        {
            transformGroup.Children.Add(_categoryElasticTransform);
        }

        var pos = button.TranslatePoint(new Point(), CategoryPanel);
        var bounds = new Rect(pos, button.RenderSize);
        CategorySelection.Opacity = 1;
        if (bounds == _categorySelectionBounds && _animatedCategoryName == selected.Name) return;

        bool categoryChanged = _animatedCategoryName != null && _animatedCategoryName != selected.Name;
        bool animate = !_categorySelectionBounds.IsEmpty && !AnimationConfig.ReduceMotion && !_trayLeaving;
        _animatedCategoryName = selected.Name;
        _categorySelectionBounds = bounds;

        if (!animate)
        {
            Motion.Stop(CategorySelection, WidthProperty, bounds.Width);
            Motion.Stop(CategorySelectionScale, ScaleTransform.ScaleXProperty, 1);
            Motion.Stop(CategorySelectionScale, ScaleTransform.ScaleYProperty, 1);
        }
        else
        {
            Motion.To(CategorySelection, WidthProperty, bounds.Width, Motion.Glide);
            Motion.To(CategorySelectionScale, ScaleTransform.ScaleXProperty, 1, Motion.Press);
            Motion.To(CategorySelectionScale, ScaleTransform.ScaleYProperty, 1, Motion.Press);
        }
        MoveCategoryIndicator(CategorySelectionOffset, TranslateTransform.XProperty, bounds.X, animate);
        if (categoryChanged)
        {
            AnimateSelectedCategoryContent(button, 1.08, animate);
            EnsureCategoryButtonVisible(bounds, animate);
        }
    }

    private static void AnimateSelectedCategoryContent(Button button, double peakScale, bool animate)
    {
        if (button.Template?.FindName("ContentHost", button) is not FrameworkElement contentHost ||
            contentHost.RenderTransform is not TransformGroup group || group.IsFrozen ||
            FindTransform<ScaleTransform>(group.Children) is not { } scale) return;
        double rest = button.IsMouseOver && !AnimationConfig.ReduceMotion ? 1.03 : 1;
        if (!animate)
        {
            Motion.Stop(scale, ScaleTransform.ScaleXProperty, rest);
            Motion.Stop(scale, ScaleTransform.ScaleYProperty, rest);
            return;
        }
        Motion.To(scale, ScaleTransform.ScaleXProperty, peakScale, Motion.PressDown,
            done: () => Motion.To(scale, ScaleTransform.ScaleXProperty, rest, Motion.Press));
        Motion.To(scale, ScaleTransform.ScaleYProperty, peakScale, Motion.PressDown,
            done: () => Motion.To(scale, ScaleTransform.ScaleYProperty, rest, Motion.Press));
    }

    private void EnsureCategoryButtonVisible(Rect bounds, bool animate)
    {
        if (CategoryScroll == null || CategoryScroll.ViewportWidth <= 0 || CategoryScroll.ScrollableWidth <= 0) return;
        const double edgePadding = 24.0;
        double viewLeft = CategoryScroll.HorizontalOffset;
        double viewRight = viewLeft + CategoryScroll.ViewportWidth;
        double targetOffset = viewLeft;

        if (bounds.Left - edgePadding < viewLeft)
            targetOffset = Math.Max(0, bounds.Left - edgePadding);
        else if (bounds.Right + edgePadding > viewRight)
            targetOffset = Math.Min(CategoryScroll.ScrollableWidth, bounds.Right + edgePadding - CategoryScroll.ViewportWidth);

        if (Math.Abs(targetOffset - viewLeft) < 0.5) return;
        _categoryScrollTarget = targetOffset;
        if (!animate || AnimationConfig.ReduceMotion)
        {
            CategoryScroll.ScrollToHorizontalOffset(targetOffset);
            StopCategoryScrollFrames();
            return;
        }
        StartCategoryScrollFrames();
    }

    private static void MoveCategoryIndicator(Animatable target, DependencyProperty property, double value, bool animate)
    {
        if (!animate) Motion.Stop(target, property, value);
        else Motion.To(target, property, value, Motion.Press);
    }

    private void Passcode_FocusChanged(object sender, KeyboardFocusChangedEventArgs e) => UpdatePinCaret();
    private void Passcode_VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdatePinCaret();

    private void UpdatePinCaret()
    {
        if (PinCaret == null || PasscodeBox == null) return;
        PinCaret.BeginAnimation(OpacityProperty, null);
        bool visible = !_disposed && !_trayLeaving && IsVisible && PasscodeBox.IsVisible && PasscodeBox.IsKeyboardFocused && _previousPinLength < 6;
        PinCaret.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) return;
        // The six dot slots are 19 DIPs wide. Start centered, then follow the filled slots.
        double offset = _previousPinLength == 0 ? 0 : (_previousPinLength - 3) * 19;
        AnimateValue(PinCaretOffset, TranslateTransform.XProperty, offset, MotionFast);
        PinCaret.Opacity = 1;
        if (AnimationConfig.ReduceMotion) return;
        var blink = new DoubleAnimation(1, 0.15, TimeSpan.FromMilliseconds(520))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        Timeline.SetDesiredFrameRate(blink, AnimationConfig.TargetFps);
        PinCaret.BeginAnimation(OpacityProperty, blink);
    }

    internal void ShakeLockContent()
    {
        if (_disposed || LockContent == null) return;
        if (LockContent.RenderTransform is not TranslateTransform translate || translate.IsFrozen)
        {
            translate = new TranslateTransform();
            LockContent.RenderTransform = translate;
        }
        if (AnimationConfig.ReduceMotion) { Motion.Stop(translate, TranslateTransform.XProperty, 0); return; }
        Motion.To(translate, TranslateTransform.XProperty, -7, Motion.PressDown,
            done: () => Motion.To(translate, TranslateTransform.XProperty, 0, Motion.Press));
    }
}
