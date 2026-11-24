using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VNotch.Models;
using VNotch.Services;

namespace VNotch;

public partial class SpotlightWindow
{
    private static readonly Brush PeekSelection = new SolidColorBrush(Color.FromArgb(0x13, 255, 255, 255));
    private static readonly Brush PeekSelectionRim = new SolidColorBrush(Color.FromArgb(0x0C, 255, 255, 255));
    private static readonly FontFamily PeekFont = new("Segoe UI");
    private readonly QuickLookBridge _quickLook = QuickLookBridge.Shared;
    private int _peekPending;
    private bool _dismissAfterPeek;
    private CornerRadius _beforePeekCorners;

    public static readonly DependencyProperty IsFilePreviewOpenProperty = DependencyProperty.Register(
        nameof(IsFilePreviewOpen), typeof(bool), typeof(SpotlightWindow),
        new PropertyMetadata(false, (target, args) => ((SpotlightWindow)target).ApplyQuickLookPresentation((bool)args.NewValue)));

    public bool IsFilePreviewOpen
    {
        get => (bool)GetValue(IsFilePreviewOpenProperty);
        private set => SetValue(IsFilePreviewOpenProperty, value);
    }

    private static string? FilePreviewPath(SpotlightSearchItem? item) =>
        item is { Kind: SpotlightResultKind.File } && QuickLookBridge.IsPreviewPath(item.Target) ? item.Target : null;

    private void InitializeQuickLook()
    {
        _quickLook.Changed += OnQuickLookChanged;
        _ = _quickLook.RefreshAvailabilityAsync();
    }

    private void OnQuickLookChanged()
    {
        if (Dispatcher.CheckAccess()) UpdateQuickLook();
        else _ = Dispatcher.BeginInvoke(UpdateQuickLook);
    }

    private void UpdateQuickLook()
    {
        if (!_isClosing) IsFilePreviewOpen = IsSpotlightOpen && _quickLook.IsPreviewing(this);
    }

    private void ApplyQuickLookPresentation(bool open)
    {
        if (open) _beforePeekCorners = Shell.CornerRadius;
        Shell.CornerRadius = open ? new CornerRadius(0, 0, 23, 23) : _beforePeekCorners;
        Shell.MaxWidth = open ? 562 : double.PositiveInfinity;
        SearchHeaderRow.Height = new GridLength(open ? 47 : 64);
        SearchHeader.Margin = open ? new(23, 13, 23, 0) : new(22, 0, 20, 0);
        SearchBox.FontFamily = open ? PeekFont : (FontFamily)FindResource("SFProDisplay");
        SearchBox.FontSize = open ? 21 : 20;
        SearchBox.FontWeight = open ? FontWeight.FromOpenTypeWeight(480) : FontWeights.Bold;
        AutocompleteText.FontFamily = SearchBox.FontFamily;
        AutocompleteText.FontSize = SearchBox.FontSize;
        AutocompleteText.FontWeight = SearchBox.FontWeight;
        PeekSearchIcon.Visibility = PeekHeaderLabel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        SearchIcon.Visibility = EscBadge.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
        ContentRegion.BorderThickness = open ? new(0) : new(0, 1, 0, 0);
        ResultsViewport.Margin = open ? new(0, 6, 0, 11) : new(0, 6, 0, 7);
        if (_selectionGlide != null)
        {
            _selectionGlide.BorderThickness = open ? new(1) : new(0);
            _selectionGlide.BorderBrush = open ? PeekSelectionRim : null;
            if (open) _selectionGlide.Background = PeekSelection;
            else _selectionGlide.SetResourceReference(Border.BackgroundProperty, "AccentSubtleBrush");
        }
        ScheduleGlideUpdate();
    }

    private bool TryHandleQuickLookSpace(KeyEventArgs e)
    {
        if (e.Key != Key.Space || Keyboard.Modifiers != ModifierKeys.Control ||
            _aiMode || !_settings.PreviewFilesWithQuickLook || !_quickLook.IsEnabled ||
            !_quickLook.IsAvailable || FilePreviewPath(_viewModel.SelectedResult) is not { } path)
            return false;
        if (!e.IsRepeat) ToggleQuickLook(path);
        e.Handled = true;
        return true;
    }

    private void ResultEye_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { DataContext: SpotlightSearchItem item } &&
            _settings.PreviewFilesWithQuickLook && FilePreviewPath(item) is { } path)
        {
            _viewModel.SelectedResult = item;
            SearchBox.Focus();
            ToggleQuickLook(path);
        }
    }

    private async void ToggleQuickLook(string path)
    {
        _dismissAfterPeek = false;
        _peekPending++;
        try { await _quickLook.ToggleAsync(this, path); }
        finally { _peekPending--; }
    }

    private void FollowQuickLookSelection()
    {
        if (_peekPending == 0 && !_quickLook.IsPreviewing(this)) return;
        if (FilePreviewPath(_viewModel.SelectedResult) is { } path)
            _ = _quickLook.SwitchAsync(this, path);
        else
            _ = _quickLook.CloseAsync(this);
    }

    private bool EscapeQuickLook()
    {
        if (_dismissAfterPeek)
        {
            DismissFromGlobalShortcut();
            return true;
        }
        if (_peekPending == 0 && !_quickLook.IsPreviewing(this)) return false;
        _dismissAfterPeek = true;
        _ = _quickLook.CloseAsync(this);
        return true;
    }

    private void EndQuickLook()
    {
        _dismissAfterPeek = false;
        if (_peekPending != 0 || _quickLook.IsPreviewing(this)) _ = _quickLook.CloseAsync(this);
    }
}
