using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using VNotch.Services;

namespace VNotch;

public partial class SpotlightWindow
{
    private readonly Controls.SpotlightSearchActivityMotion _searchMotion;
    private bool _searchInputPending;
    private HashSet<string> _publishedResultIds = new(StringComparer.Ordinal);

    private void ScheduleResultArrivals()
    {
        var previous = _publishedResultIds;
        var published = _viewModel.Results.ToArray();
        _publishedResultIds = published.Select(result => result.Id).ToHashSet(StringComparer.Ordinal);
        if (AnimationConfig.ReduceMotion || _isClosing || _entranceActive || _aiMode) return;

        int session = _animationGeneration;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (session != _animationGeneration || _isClosing || !ResultsList.IsVisible ||
                IsFilePreviewOpen || AnimationConfig.ReduceMotion) return;
            foreach (var result in published)
            {
                if (previous.Contains(result.Id) ||
                    ResultsList.ItemContainerGenerator.ContainerFromItem(result) is not ListBoxItem { IsVisible: true } row ||
                    row.Template?.FindName("ResultPad", row) is not Border pad) continue;
                var offset = pad.RenderTransform as TranslateTransform ?? new TranslateTransform();
                pad.RenderTransform = offset;
                if (Motion.IsRunning(offset, TranslateTransform.YProperty)) continue;

                // Keep the hit target and selection on their final layout slot from the first frame.
                void Settle()
                {
                    pad.IsVisibleChanged -= OnVisibleChanged;
                    Motion.Stop(offset, TranslateTransform.YProperty, 0);
                }
                void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
                {
                    if (!pad.IsVisible) Settle();
                }
                pad.IsVisibleChanged += OnVisibleChanged;
                Motion.To(offset, TranslateTransform.YProperty, 0, Motion.Shift, from: -4, done: Settle);
            }
        });
    }

    private void OnSearchMotionPreferenceChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(OnSearchMotionPreferenceChanged);
            return;
        }
        if (AnimationConfig.ReduceMotion)
        {
            ResetAiPromptReveal();
            Motion.Stop(StatusPanel, OpacityProperty, 1);
        }
        _searchMotion.Refresh();
    }
}
