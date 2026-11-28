using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.Clipboard;
using VNotch.ViewModels;

namespace VNotch.Controls;

public sealed partial class ClipboardTray
{
    [LibraryImport(NativeDll)]
    private static partial int vn_clipboard_selection_merge_optimistic_order(
        int desiredCount,
        ReadOnlySpan<int> optimisticToDesiredIndex,
        int optimisticCount,
        Span<byte> scratchTakenMask,
        Span<int> outMergedDesiredIndices,
        out int outMergedCount);

    private int _labelEditorVersion;
    private bool _labelEditorOpen;
    private void SynchronizeLabels()
    {
        if (_controller == null) return;
        var storeOrder = _controller.Store.GetCategoryOrder();
        int desiredCount = storeOrder.Count;
        if (desiredCount == 0)
        {
            if (_categoryItems.Length != 0)
            {
                CancelCategoryReorder();
                _categoryItems = [];
                CategoryPanel.ItemsSource = _categoryItems;
            }
            return;
        }

        Span<int> mergedIndices = desiredCount <= 256 ? stackalloc int[desiredCount] : new int[desiredCount];
        int mergedCount;
        if (_optimisticCategoryOrder is { Length: > 0 } optimistic)
        {
            int optCount = optimistic.Length;
            Span<int> optToDesired = optCount <= 256 ? stackalloc int[optCount] : new int[optCount];
            for (int i = 0; i < optCount; i++)
            {
                optToDesired[i] = IndexOfCategoryName(storeOrder, optimistic[i]);
            }
            Span<byte> scratchTaken = desiredCount <= 256 ? stackalloc byte[desiredCount] : new byte[desiredCount];
            _ = vn_clipboard_selection_merge_optimistic_order(
                desiredCount, optToDesired, optCount, scratchTaken, mergedIndices, out mergedCount);
        }
        else
        {
            mergedCount = desiredCount;
            for (int i = 0; i < desiredCount; i++)
                mergedIndices[i] = i;
        }

        if (MatchesCurrentCategoryOrder(storeOrder, mergedIndices[..mergedCount]))
            return;

        CancelCategoryReorder();
        var existingItems = _categoryItems;
        var labels = _controller.Store.GetLabels();
        var nextItems = new ClipboardCategoryViewModel[mergedCount];
        for (int i = 0; i < mergedCount; i++)
        {
            string name = storeOrder[mergedIndices[i]];
            nextItems[i] = FindExistingCategoryItem(existingItems, name) ??
                (ClipboardLabel.IsCustomId(name)
                    ? new ClipboardCategoryViewModel(TrayIconKind.Shopping, name, FindCustomLabelName(labels, name))
                    : new ClipboardCategoryViewModel(Enum.Parse<TrayIconKind>(name)));
        }
        _categoryItems = nextItems;
        CategoryPanel.ItemsSource = _categoryItems;
    }

    private static int IndexOfCategoryName(IReadOnlyList<string> storeOrder, string name)
    {
        for (int i = 0; i < storeOrder.Count; i++)
        {
            if (string.Equals(storeOrder[i], name, StringComparison.Ordinal))
                return i;
        }
        return -1;
    }

    private bool MatchesCurrentCategoryOrder(IReadOnlyList<string> storeOrder, ReadOnlySpan<int> mergedIndices)
    {
        if (_categoryItems.Length != mergedIndices.Length)
            return false;

        for (int i = 0; i < mergedIndices.Length; i++)
        {
            if (!string.Equals(_categoryItems[i].Name, storeOrder[mergedIndices[i]], StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private static ClipboardCategoryViewModel? FindExistingCategoryItem(ClipboardCategoryViewModel[] items, string name)
    {
        for (int i = 0; i < items.Length; i++)
        {
            if (string.Equals(items[i].Name, name, StringComparison.Ordinal))
                return items[i];
        }
        return null;
    }

    private static string FindCustomLabelName(IReadOnlyList<ClipboardLabel> labels, string id)
    {
        for (int i = 0; i < labels.Count; i++)
        {
            if (string.Equals(labels[i].Id, id, StringComparison.Ordinal))
                return labels[i].Name;
        }
        return id;
    }

    private static T? FindTransform<T>(TransformCollection children) where T : Transform
    {
        for (int i = 0; i < children.Count; i++)
        {
            if (children[i] is T match)
                return match;
        }
        return null;
    }

    private void LocalizeLabelActions()
    {
        AddLabelButton.ToolTip = Loc.Get("clipboard.addLabel");
        SelectAllButton.ToolTip = Loc.Get("clipboard.selectAll");
        System.Windows.Automation.AutomationProperties.SetName(AddLabelButton, Loc.Get("clipboard.addLabel"));
        System.Windows.Automation.AutomationProperties.SetName(SelectAllButton, Loc.Get("clipboard.selectAll"));
        LabelEditorTitle.Text = Loc.Get("clipboard.addLabel");
        LabelEditorHint.Text = Loc.Get("clipboard.labelHint");
        SaveLabelButton.Content = Loc.Get("clipboard.createLabel");
        CancelLabelButton.Content = Loc.Get("clipboard.cancel");
        System.Windows.Automation.AutomationProperties.SetName(LabelNameBox, Loc.Get("clipboard.labelName"));
        UpdateSelectionActions();
    }

    private void Cards_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelectionActions();

    private void UpdateSelectionActions()
    {
        if (DeleteSelectedButton == null || Cards == null) return;
        DeleteSelectedButton.Visibility = Cards.SelectedItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTrayButton.Visibility = Cards.SelectedItems.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        string label = Loc.Get("clipboard.deleteSelected", Cards.SelectedItems.Count);
        DeleteSelectedButton.ToolTip = label;
        System.Windows.Automation.AutomationProperties.SetName(DeleteSelectedButton, label);
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e) => SelectAllTrayItems(null);
    internal void DeleteSelected_Click(object sender, RoutedEventArgs e)
        => SafeAsync(() => ConfirmDeleteAsync(SelectedCards())).SafeFireAndForget("CLIPBOARD-UI");

    private void AddLabel_Click(object sender, RoutedEventArgs e)
    {
        if (_controller == null || _disposed) return;
        ++_labelEditorVersion;
        _labelEditorOpen = true;
        EndSelection();
        CloseAllOpenMenus();
        LabelNameBox.Clear();
        LabelEditorError.Text = "";
        TrayContentGrid.IsHitTestVisible = false;
        TrayContentGrid.Effect = null;
        TrayContentGrid.BeginAnimation(OpacityProperty, new DoubleAnimation(0.38, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        });
        TrayMotion.SetBlurRadius(TrayContentGrid, 0.0);
        ShowTransientLayer(LabelEditorPanel);
        AnimateLabelEditorBorder(entering: true);
        LabelNameBox.Focus();
    }

    private void CloseLabelEditor(bool immediately = false)
    {
        bool wasOpen = _labelEditorOpen || LabelEditorPanel.Visibility == Visibility.Visible;
        _labelEditorOpen = false;
        ++_labelEditorVersion;
        if (!_deleteConfirmationOpen)
        {
            TrayContentGrid.IsHitTestVisible = true;
            TrayContentGrid.Effect = null;
            TrayContentGrid.BeginAnimation(OpacityProperty, new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(immediately ? 0 : 220))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });
            TrayMotion.SetBlurRadius(TrayContentGrid, 0.0);
        }
        if (!immediately && wasOpen && !_disposed && !_trayLeaving && IsVisible)
            AnimateLabelEditorBorder(entering: false);
        DismissTransientLayer(LabelEditorPanel, immediately: immediately);
        LabelNameBox.Clear();
        if (wasOpen && !_disposed && !_trayLeaving && IsVisible) AddLabelButton.Focus();
    }

    private void LabelEditorBackdrop_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, LabelEditorPanel))
        {
            CloseLabelEditor();
            e.Handled = true;
        }
    }

    private void AnimateLabelEditorBorder(bool entering)
    {
        if (LabelEditorBorder?.RenderTransform is not TransformGroup group ||
            FindTransform<ScaleTransform>(group.Children) is not { } scale ||
            FindTransform<TranslateTransform>(group.Children) is not { } translate) return;

        bool fresh = entering && !scale.HasAnimatedProperties && !translate.HasAnimatedProperties;
        var spring = entering ? Motion.Tile : Motion.Exit;
        Motion.To(scale, ScaleTransform.ScaleXProperty, entering ? 1 : Motion.ExitToScale, entering ? Motion.TileScale : spring,
            from: fresh ? Motion.TileFromScale : null);
        Motion.To(scale, ScaleTransform.ScaleYProperty, entering ? 1 : Motion.ExitToScale, entering ? Motion.TileScale : spring,
            from: fresh ? Motion.TileFromScale : null);
        Motion.To(translate, TranslateTransform.YProperty, entering ? 0 : 6, spring, from: fresh ? 10 : null);
    }

    private void ShakeLabelEditor()
    {
        if (AnimationConfig.ReduceMotion || LabelEditorBorder?.RenderTransform is not TransformGroup group ||
            FindTransform<TranslateTransform>(group.Children) is not { } translate) return;
        Motion.To(translate, TranslateTransform.XProperty, -7, Motion.PressDown,
            done: () => Motion.To(translate, TranslateTransform.XProperty, 0, Motion.Press));
    }

    private void CancelLabel_Click(object sender, RoutedEventArgs e) => CloseLabelEditor();
    private void SaveLabel_Click(object sender, RoutedEventArgs e)
        => SaveLabelAsync().SafeFireAndForget("CLIPBOARD-UI");
    private void LabelName_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        SaveLabelAsync().SafeFireAndForget("CLIPBOARD-UI");
    }

    private async Task SaveLabelAsync()
    {
        if (_controller == null || !SaveLabelButton.IsEnabled) return;
        int version = _labelEditorVersion;
        SaveLabelButton.IsEnabled = false;
        try
        {
            var label = await _controller.Store.AddLabelAsync(LabelNameBox.Text);
            if (_disposed || version != _labelEditorVersion || !_labelEditorOpen) return;
            CloseLabelEditor();
            RebuildCategories();
            SelectCategory(label.Id);
            CategoryScroll.ScrollToRightEnd();
        }
        catch (ArgumentException)
        {
            LabelEditorError.Text = Loc.Get("clipboard.invalidLabel");
            ShakeLabelEditor();
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("CLIPBOARD", ex.GetType().Name);
            LabelEditorError.Text = Loc.Get("clipboard.error");
            ShakeLabelEditor();
        }
        finally { SaveLabelButton.IsEnabled = true; }
    }

}
