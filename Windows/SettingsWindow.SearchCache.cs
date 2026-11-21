using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using VNotch.Controls;

namespace VNotch;

public partial class SettingsWindow
{
    private readonly HashSet<SearchRowEntry> _dirtySearchRows = [];
    private readonly Dictionary<FrameworkElement, SearchInputObserver> _searchObservers = [];
    private bool _updatingSearchPresentation;

    private void InvalidateSearchIndex() => _dirtySearchRows.UnionWith(_searchRows);

    private void ObserveSearchInputs(FrameworkElement root, SearchRowEntry? row)
    {
        if (!_searchObservers.TryGetValue(root, out var observer))
        {
            observer = new SearchInputObserver(this, root, row);
            _searchObservers.Add(root, observer);
        }
        observer.Refresh();
    }

    private void DetachSearchObservers()
    {
        foreach (var observer in _searchObservers.Values) observer.Dispose();
        _searchObservers.Clear();
        _dirtySearchRows.Clear();
    }

    private sealed class SearchInputObserver : IDisposable
    {
        private readonly SettingsWindow _window;
        private readonly FrameworkElement _root;
        private readonly SearchRowEntry? _row;
        private HashSet<DependencyObject> _observed = [];
        private HashSet<DependencyObject> _current = [];
        private DispatcherOperation? _pendingRefresh;

        internal SearchInputObserver(SettingsWindow window, FrameworkElement root, SearchRowEntry? row)
        {
            _window = window;
            _root = root;
            _row = row;
            root.Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e) => Refresh();

        internal void Refresh()
        {
            CollectInputs(_root, _current);
            if (!_observed.SetEquals(_current))
            {
                foreach (var input in _observed)
                    if (!_current.Contains(input)) Watch(input, false);
                foreach (var input in _current)
                    if (!_observed.Contains(input)) Watch(input, true);
                (_current, _observed) = (_observed, _current);
                MarkDirty();
            }
            _current.Clear();
        }

        private static void CollectInputs(DependencyObject input, HashSet<DependencyObject> inputs)
        {
            if (!inputs.Add(input)) return;
            if (input is ItemsControl items)
                foreach (var item in items.Items)
                    if (item is DependencyObject child) CollectInputs(child, inputs);

            if (input is not Visual) return;
            int count = VisualTreeHelper.GetChildrenCount(input);
            for (int i = 0; i < count; i++)
                CollectInputs(VisualTreeHelper.GetChild(input, i), inputs);
        }

        private void Watch(DependencyObject input, bool attach)
        {
            void Observe(DependencyProperty property, EventHandler handler)
            {
                var descriptor = DependencyPropertyDescriptor.FromProperty(property, input.GetType());
                if (attach) descriptor?.AddValueChanged(input, handler);
                else descriptor?.RemoveValueChanged(input, handler);
            }

            if (input is TextBlock) Observe(TextBlock.TextProperty, OnTextChanged);
            if (input is ContentControl) Observe(ContentControl.ContentProperty, OnContentChanged);
            if (input is ElasticSlider)
            {
                Observe(ElasticSlider.LabelProperty, OnTextChanged);
                Observe(ElasticSlider.DescriptionProperty, OnTextChanged);
                Observe(ElasticSlider.UnitProperty, OnTextChanged);
            }
            if (input is UIElement) Observe(UIElement.VisibilityProperty, OnVisibilityChanged);
            if (input is ItemsControl items)
            {
                var collection = (INotifyCollectionChanged)items.Items;
                if (attach) collection.CollectionChanged += OnContentChanged;
                else collection.CollectionChanged -= OnContentChanged;
            }
        }

        private void MarkDirty()
        {
            if (_row == null) _window.InvalidateSearchIndex();
            else _window._dirtySearchRows.Add(_row);
        }

        private void OnTextChanged(object? sender, EventArgs e)
        {
            if (!_window._updatingSearchPresentation) MarkDirty();
        }

        private void OnVisibilityChanged(object? sender, EventArgs e)
        {
            if (_window._updatingSearchPresentation) return;
            if (_row != null && ReferenceEquals(sender, _row.Row))
                _row.OriginalVisibility = _row.Row.Visibility;
            MarkDirty();
        }

        private void OnContentChanged(object? sender, EventArgs e)
        {
            OnTextChanged(sender, e);
            // WPF can still expose the retired visuals until layout catches up.
            _pendingRefresh ??= _root.Dispatcher.InvokeAsync(() =>
            {
                _pendingRefresh = null;
                Refresh();
            }, DispatcherPriority.Loaded);
        }

        public void Dispose()
        {
            _root.Loaded -= OnLoaded;
            _pendingRefresh?.Abort();
            foreach (var input in _observed) Watch(input, false);
            _observed.Clear();
        }
    }

    private void RestoreSearchRow(SearchRowEntry row)
    {
        if (ReferenceEquals(row.Row.Parent, row.OriginalParent)) return;
        if (row.Row.Parent is Panel parent) parent.Children.Remove(row.Row);
        int insertIndex = row.OriginalIndex;
        // Earlier rows can remain in the results. Account for their vacant slots
        // so restoring only one changed result still preserves its source order.
        foreach (var peer in _searchRows)
            if (ReferenceEquals(peer.OriginalParent, row.OriginalParent) &&
                peer.OriginalIndex < row.OriginalIndex && !ReferenceEquals(peer.Row.Parent, row.OriginalParent))
                insertIndex--;
        row.OriginalParent.Children.Insert(Math.Clamp(insertIndex, 0, row.OriginalParent.Children.Count), row.Row);
        row.Row.Visibility = row.OriginalVisibility;
    }

    private void ApplySearchResultDiff(ReadOnlySpan<VnSettingsSearchMatchResult> matches)
    {
        var desired = new HashSet<Border>();
        foreach (var match in matches)
            if ((uint)match.RowIndex < (uint)_searchRows.Count) desired.Add(_searchRows[match.RowIndex].Row);

        _updatingSearchPresentation = true;
        try
        {
            foreach (var row in _searchRows)
                if (ReferenceEquals(row.Row.Parent, SearchResultsStack) && !desired.Contains(row.Row))
                    RestoreSearchRow(row);

            int index = 0;
            foreach (var match in matches)
            {
                if ((uint)match.RowIndex >= (uint)_searchRows.Count) continue;
                var row = _searchRows[match.RowIndex];
                if (index >= SearchResultsStack.Children.Count || !ReferenceEquals(SearchResultsStack.Children[index], row.Row))
                {
                    if (row.Row.Parent is Panel parent) parent.Children.Remove(row.Row);
                    SearchResultsStack.Children.Insert(index, row.Row);
                }
                row.Row.Visibility = Visibility.Visible;
                index++;
            }
        }
        finally { _updatingSearchPresentation = false; }
    }
}
