using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Services;
using VNotch.ViewModels;

namespace VNotch.Controls;

public sealed partial class ClipboardTray
{
    [LibraryImport("VNotch.Native")]
    private static partial int vn_clipboard_category_compute_preview_shift(
        int itemIndex,
        int sourceIndex,
        int destinationIndex,
        double span,
        out double outShift);

    [LibraryImport("VNotch.Native")]
    private static partial int vn_clipboard_category_compute_gap_x(
        int sourceIndex,
        int beforeIndex,
        double beforeOriginX,
        double lastOriginX,
        double lastWidth,
        double lastMarginRight,
        double span,
        out double outGapX);

    private sealed record CategoryPreview(Transform Original, TranslateTransform Offset, int Index);
    private readonly Dictionary<Button, CategoryPreview> _categoryReorderPreviews = [];
    private bool _categoryReorderVisualsReady;
    private bool _categoryReorderGapPlaced;
    private Button? _categoryLandingButton;
    private int _categoryLandingVersion;

    private Point CategorySlotOrigin(ClipboardCategoryViewModel category) =>
        CategoryPanel.ItemContainerGenerator.ContainerFromItem(category) is ContentPresenter presenter
            ? presenter.TranslatePoint(new Point(), CategoryScroll) : new Point();

    private void BeginCategoryReorderVisuals()
    {
        if (_categoryReorderSource is not { } source || _categoryReorderVisualsReady) return;
        StopCategoryReorderLanding();
        _categoryReorderVisualsReady = true;
        _categoryReorderGapPlaced = false;
        ToolTipService.SetIsEnabled(source, false);
        SetButtonScale(source, 1, 0);
        source.Opacity = 0;
        CategorySelection.Opacity = 0;
        for (int index = 0; index < _categoryItems.Length; index++)
        {
            var category = _categoryItems[index];
            if (CategoryButton(category) is not { } button || button == source) continue;
            var original = button.RenderTransform;
            var offset = new TranslateTransform();
            button.RenderTransform = new TransformGroup { Children = { original, offset } };
            _categoryReorderPreviews[button] = new CategoryPreview(original, offset, index);
        }
        CategoryReorderGhost.DataContext = source.DataContext;
        CategoryReorderGhost.Width = CategoryReorderMarker.Width = source.ActualWidth;
        CategoryReorderGhost.Height = CategoryReorderMarker.Height = source.ActualHeight;
        CategoryReorderOverlay.Visibility = Visibility.Visible;
        CategoryReorderGhostScale.ScaleX = CategoryReorderGhostScale.ScaleY = 1;
        if (!AnimationConfig.ReduceMotion)
        {
            AnimateCategoryReorderValue(CategoryReorderGhostScale, ScaleTransform.ScaleXProperty, 1.04, 130);
            AnimateCategoryReorderValue(CategoryReorderGhostScale, ScaleTransform.ScaleYProperty, 1.04, 130);
        }
    }

    private void UpdateCategoryReorderPreview(ClipboardCategoryViewModel source)
    {
        if (!_categoryReorderVisualsReady || _categoryReorderSource is not { } sourceButton) return;
        var clip = new Rect(0, -8, CategoryScroll.ActualWidth, CategoryScroll.ActualHeight + 16);
        if (CategoryReorderOverlay.Clip is not RectangleGeometry geometry || geometry.Rect != clip)
            CategoryReorderOverlay.Clip = new RectangleGeometry(clip);
        CategoryReorderGhost.Width = CategoryReorderMarker.Width = sourceButton.ActualWidth;
        CategoryReorderGhost.Height = CategoryReorderMarker.Height = sourceButton.ActualHeight;
        int sourceIndex = Array.IndexOf(_categoryItems, source);
        int beforeIndex = _categoryReorderBeforeId == null ? -1 :
            Array.FindIndex(_categoryItems, category => category.Name == _categoryReorderBeforeId);
        if (_categoryReorderBeforeId != null && beforeIndex < 0) beforeIndex = _categoryItems.Length;
        _ = vn_clipboard_category_resolve_move(_categoryItems.Length, sourceIndex, beforeIndex, out int destination, out _);
        double span = sourceButton.ActualWidth + sourceButton.Margin.Left + sourceButton.Margin.Right;
        foreach (var (_, preview) in _categoryReorderPreviews)
        {
            _ = vn_clipboard_category_compute_preview_shift(preview.Index, sourceIndex, destination, span, out double shift);
            AnimateCategoryReorderValue(preview.Offset, TranslateTransform.XProperty, shift, 170);
        }

        double beforeOriginX = 0, lastOriginX = 0, lastWidth = 0, lastMarginRight = 0;
        if (beforeIndex >= 0 && beforeIndex < _categoryItems.Length)
            beforeOriginX = CategorySlotOrigin(_categoryItems[beforeIndex]).X;
        else
        {
            if (_categoryItems.Length == 0) return;
            var last = _categoryItems[^1];
            if (CategoryButton(last) is not { } lastButton) return;
            lastOriginX = CategorySlotOrigin(last).X;
            lastWidth = lastButton.ActualWidth;
            lastMarginRight = lastButton.Margin.Right;
        }
        _ = vn_clipboard_category_compute_gap_x(
            sourceIndex,
            beforeIndex < _categoryItems.Length ? beforeIndex : -1,
            beforeOriginX,
            lastOriginX,
            lastWidth,
            lastMarginRight,
            span,
            out double x);
        Canvas.SetTop(CategoryReorderMarker, Math.Max(0, (CategoryScroll.ActualHeight - sourceButton.ActualHeight) / 2));
        AnimateCategoryReorderValue(CategoryReorderGapOffset, TranslateTransform.XProperty, x, _categoryReorderGapPlaced ? 170 : 0);
        _categoryReorderGapPlaced = true;
    }

    private void ResetCategoryReorderVisuals()
    {
        StopCategoryReorderLanding();
        foreach (var (button, preview) in _categoryReorderPreviews)
        {
            preview.Offset.BeginAnimation(TranslateTransform.XProperty, null);
            button.RenderTransform = preview.Original;
        }
        _categoryReorderPreviews.Clear();
        _categoryReorderVisualsReady = false;
        _categoryReorderGapPlaced = false;
        CategoryReorderGapOffset.BeginAnimation(TranslateTransform.XProperty, null);
        CategoryReorderGhostScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CategoryReorderGhostScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        CategoryReorderGhostScale.ScaleX = CategoryReorderGhostScale.ScaleY = 1;
    }

    private void BeginCategoryReorderLanding(string id, Point origin)
    {
        if (AnimationConfig.ReduceMotion || _disposed || !IsVisible || _trayLeaving ||
            _categoryItems.FirstOrDefault(category => category.Name == id) is not { } category ||
            CategoryButton(category) is not { } button) return;
        StopCategoryReorderLanding();
        int version = _categoryLandingVersion;
        _categoryLandingButton = button;
        button.Opacity = 0;
        CategoryReorderGhost.DataContext = category;
        CategoryReorderGhost.Width = button.ActualWidth;
        CategoryReorderGhost.Height = button.ActualHeight;
        CategoryReorderGhost.Opacity = 1;
        CategoryReorderGhostScale.ScaleX = CategoryReorderGhostScale.ScaleY = 1.04;
        CategoryReorderMarker.Visibility = Visibility.Collapsed;
        CategoryReorderOverlay.Visibility = Visibility.Visible;
        // The ghost rests in its slot and only the spring carries it there, so a stopped landing never strands it.
        var target = button.TranslatePoint(new Point(), CategoryScroll);
        Canvas.SetLeft(CategoryReorderGhost, target.X);
        Canvas.SetTop(CategoryReorderGhost, target.Y);
        Motion.To(CategoryReorderGhost, Canvas.LeftProperty, target.X, Motion.Glide, from: origin.X);
        Motion.To(CategoryReorderGhost, Canvas.TopProperty, target.Y, Motion.Glide, from: origin.Y);
        Motion.To(CategoryReorderGhostScale, ScaleTransform.ScaleXProperty, 1, Motion.Glide);
        Motion.To(CategoryReorderGhostScale, ScaleTransform.ScaleYProperty, 1, Motion.Glide,
            done: () => { if (version == _categoryLandingVersion) StopCategoryReorderLanding(); });
    }

    private void StopCategoryReorderLanding()
    {
        ++_categoryLandingVersion;
        if (_categoryLandingButton != null) _categoryLandingButton.Opacity = 1;
        _categoryLandingButton = null;
        CategoryReorderGhost.BeginAnimation(Canvas.LeftProperty, null);
        CategoryReorderGhost.BeginAnimation(Canvas.TopProperty, null);
        CategoryReorderGhost.BeginAnimation(OpacityProperty, null);
        CategoryReorderOverlay.Visibility = Visibility.Collapsed;
        CategoryReorderGhost.DataContext = null;
    }

    private static void AnimateCategoryReorderValue(DependencyObject target, DependencyProperty property, double value, int duration)
    {
        if (duration <= 0 || AnimationConfig.ReduceMotion) Motion.Stop(target, property, value);
        else Motion.To(target, property, value, Motion.Glide);
    }
}
