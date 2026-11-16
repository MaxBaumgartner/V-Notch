using System;
using System.Windows.Threading;
using VNotch.Models;

namespace VNotch;

/// <summary>
/// The first moments of an open notch:
/// - Its content takes clicks as soon as the shape is mostly open, not only when the spring has fully settled
///   (that left ~300 ms where everything looked ready but nothing reacted). Clicks on empty space while it still
///   moves are swallowed as before, so they never close it again.
/// - A tab tapped while the notch is still opening is remembered and opened right after, instead of dropped.
/// - The tabs that load their data on first view (home, school, system, Claude) read it once in the background
///   shortly after start, so the first open shows them filled and at their real height.
/// </summary>
public partial class MainWindow
{
    private static readonly TimeSpan EarlyHitTestDelay = TimeSpan.FromMilliseconds(280);
    private static readonly TimeSpan PrefetchDelay = TimeSpan.FromSeconds(12);

    private DispatcherTimer? _earlyHitTestTimer;
    private int _earlyHitTestGeneration;
    private (NotchView View, int Generation)? _queuedNavView;

    /// <summary>Called when an open from the closed notch starts.</summary>
    private void ArmEarlyHitTest(int generation)
    {
        _earlyHitTestGeneration = generation;
        if (_earlyHitTestTimer == null)
        {
            _earlyHitTestTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = EarlyHitTestDelay };
            _earlyHitTestTimer.Tick += (_, _) =>
            {
                _earlyHitTestTimer.Stop();
                if (_cleanedUp || !_isAnimating || _earlyHitTestGeneration != _viewTransitionGeneration) return;
                if (_transitionCoordinator.TargetView == NotchView.Compact) return;
                NotchBorder.IsHitTestVisible = true;
            };
        }
        _earlyHitTestTimer.Stop();
        _earlyHitTestTimer.Start();
    }

    /// <summary>True when the tab was remembered because the notch is still opening from closed.</summary>
    private bool TryQueueNavigation(NotchView target)
    {
        if (!_isAnimating || _transitionCoordinator.CurrentView != NotchView.Compact || _transitionCoordinator.TargetView == NotchView.Compact)
            return false;
        _queuedNavView = (target, _viewTransitionGeneration);
        return true;
    }

    /// <summary>After an open completes: the tab tapped meanwhile.</summary>
    private void RunQueuedNavigation(int generation)
    {
        if (_queuedNavView is not { } queued) return;
        _queuedNavView = null;
        if (queued.Generation != generation || queued.View == _transitionCoordinator.CurrentView) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => NavigateToNotchView(queued.View));
    }

    private void SchedulePrefetch()
    {
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = PrefetchDelay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_cleanedUp) return;
            foreach (var center in new[] { PiCenter, SchoolCenter, ControlCenter, ClaudeCenter })
                center.Prefetch();
        };
        timer.Start();
    }
}
