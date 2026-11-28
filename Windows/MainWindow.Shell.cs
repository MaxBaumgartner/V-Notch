using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using VNotch.Controls;
using VNotch.Controllers;
using VNotch.Models;
using VNotch.Presenters;
using VNotch.Services;

namespace VNotch;

public partial class MainWindow
{
    private NotchShellAttachment _shellAttachment = null!;
    private double _shellOpenWidth, _shellOpenHeight;
    private double _shellDragStartWidth, _shellDragStartHeight;
    private double _shellDragStartRadius, _shellDragOriginY;
    private bool _shellGestureInMotion;
    private bool _shellGestureWasOpen, _shellDragActive;
    private double? _shellReleaseVelocity;
    private double _shellReleaseWidthRatio;
    private NotchView _shellDragView;
    private DateTime _shellLastSampleAt;
    private double _shellDrawerStartHeight;
    private ScrollViewer? _shellPendingScroll;
    private readonly TextBlock _compactTabStatus = new()
    {
        FontSize = 11.5, Foreground = new SolidColorBrush(Color.FromRgb(245, 245, 247)),
        VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false
    };
    private readonly StackPanel _compactStatusHost = new()
    {
        Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 44, 0), IsHitTestVisible = false
    };
    private readonly Border _compactSchoolStatus = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock _compactWarningText = new() { FontSize = 10.5, Foreground = new SolidColorBrush(Color.FromRgb(231, 191, 185)) };
    private readonly Border _compactLoadWarning = new()
    {
        CornerRadius = new CornerRadius(9), Background = new SolidColorBrush(Color.FromArgb(48, 201, 138, 132)),
        Padding = new Thickness(7, 3, 7, 3), Margin = new Thickness(0, 0, 7, 0), Visibility = Visibility.Collapsed
    };
    private readonly StackPanel _compactSystemBattery = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

    private void InitializeShellAttachment()
    {
        var open = new GlassClipGrid { Name = "OpenShellContent", Opacity = 0, UseLayoutRounding = false };
        FrameworkElement[] elements = [ExpandedContent, TimerContent, SecondaryContent, CameraContent,
            ClaudeContent, PiContent, SchoolContent, RobloxContent, AudioContent,
            NavIconsPanel, NavIconsBackground, SharedSettingsHost,
            SystemBackdrop, HomeBackdrop, SchoolBackdrop, RobloxBackdrop, ClaudeBackdrop];
        foreach (var element in elements)
        {
            ((Panel)element.Parent).Children.Remove(element);
            open.Children.Add(element);
        }
        NotchContent.Children.Add(open);
        Panel.SetZIndex(_compactStatusHost, 190);
        _compactLoadWarning.Child = _compactWarningText;
        _compactStatusHost.Children.Add(_compactLoadWarning);
        _compactStatusHost.Children.Add(_compactTabStatus);
        _compactSchoolStatus.Child = SchoolCenter.SchoolStatus;
        _compactStatusHost.Children.Add(_compactSchoolStatus);
        var batteryText = new TextBlock { FontSize = 11.5, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
        batteryText.SetBinding(TextBlock.TextProperty, new Binding("Text") { Source = BatteryPercent });
        _compactSystemBattery.Children.Add(batteryText);
        var batteryFill = new ScaleTransform(1, 1);
        BindingOperations.SetBinding(batteryFill, ScaleTransform.ScaleXProperty, new Binding("ScaleX") { Source = BatteryFillScale });
        // Same calm battery as the header: hairline shell, inset fill with a gap, its own terminal.
        var shellBrush = new SolidColorBrush(Color.FromArgb(0x5C, 255, 255, 255));
        shellBrush.Freeze();
        _compactSystemBattery.Children.Add(new Border { Width = 21, Height = 11, CornerRadius = new CornerRadius(3.4),
            BorderBrush = shellBrush, BorderThickness = new Thickness(1), Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(1.4), VerticalAlignment = VerticalAlignment.Center,
            Child = new Border { Background = new SolidColorBrush(Color.FromArgb(0xEB, 255, 255, 255)), CornerRadius = new CornerRadius(1.9),
                RenderTransformOrigin = new Point(0, .5), RenderTransform = batteryFill } });
        _compactSystemBattery.Children.Add(new Border { Width = 1.5, Height = 4, Margin = new Thickness(0.9, 0, 0, 0),
            Background = shellBrush, CornerRadius = new CornerRadius(0, 1, 1, 0), VerticalAlignment = VerticalAlignment.Center });
        _compactStatusHost.Children.Add(_compactSystemBattery);
        open.Children.Add(_compactStatusHost);
        PiCenter.StatusChanged += UpdateCompactTabStatus;
        ControlCenter.StatusChanged += UpdateCompactTabStatus;
        _shellAttachment = new NotchShellAttachment(NotchBorder, open, () => _collapsedHeight,
            CollapsedContent, MusicCompactContent);
        // Wrap the music face before it loads, so a copy never unloads the live visualizer.
        _clipboardCopiedAttachment = new(NotchBorder, MusicCompactContent, () => new Size(_collapsedWidth, _collapsedHeight));
        _clipboardCopiedAttachment.Detach(true);
        NotchWrapper.PreviewMouseMove += ContinueScrollShellGesture;
        NotchWrapper.LostMouseCapture += (_, e) =>
        {
            if (!ReferenceEquals(e.OriginalSource, NotchWrapper)) return;
            if (!_isGestureActive) return;
            if (_musicDragging) FinishMusicSwipe(true, DateTime.UtcNow);
            else if (_shellDragActive) FinishShellDrag(canceled: true);
            else AnimateGestureSnapBack();
            _gestureController.CancelTracking();
            _isGestureActive = false;
        };
    }

    internal bool IsShellDragControl(DependencyObject? element)
    {
        while (element != null && element is not Window)
        {
            if (ReferenceEquals(element, NotchBorder) || ReferenceEquals(element, NotchWrapper)) break;
            if (ReferenceEquals(element, ThumbnailBorder) && CanSwipeMusic) return false;
            if (element is ActionBorder or Hyperlink) return true;
            if (element is System.Windows.Controls.Primitives.ButtonBase or
                System.Windows.Controls.Primitives.RangeBase or System.Windows.Controls.Primitives.Thumb or
                System.Windows.Controls.Primitives.TextBoxBase or PasswordBox or ComboBox or MenuItem)
                return true;
            // An upward push at the top of a list belongs to the shell, including lists too short to scroll.
            if (element is ScrollViewer scroll && scroll.ScrollableHeight > 0 && scroll.VerticalOffset > .5)
                return true;
            if (element is FrameworkElement control && element is not ScrollViewer and not ListBox and not ListBoxItem &&
                control.ReadLocalValue(CursorProperty) is Cursor cursor &&
                (cursor == Cursors.Hand || cursor == Cursors.SizeWE || cursor == Cursors.SizeNS)) return true;
            element = ShellInputParent(element);
        }
        return false;
    }

    private static DependencyObject? ShellInputParent(DependencyObject element) =>
        element is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);

    internal ScrollViewer? FindShellScrollCandidate(DependencyObject? source)
    {
        ScrollViewer? scroll = null;
        bool list = false;
        for (var node = source; node != null && node != NotchWrapper; node = ShellInputParent(node))
        {
            if (node is ListBox) list = true;
            if (node is not ScrollViewer viewer) continue;
            if (viewer.VerticalOffset > .5) return null;
            scroll ??= viewer;
        }
        return scroll != null && (scroll.ScrollableHeight > 0 || list) ? scroll : null;
    }

    internal static bool ShouldClaimScrollDrag(ScrollViewer scroll, Point delta) =>
        Math.Abs(delta.Y) >= 6 && Math.Abs(delta.Y) > Math.Abs(delta.X) && scroll.VerticalOffset <= .5 &&
        (delta.Y < 0 || scroll.ScrollableHeight <= 0);

    private void ContinueScrollShellGesture(object sender, MouseEventArgs e)
    {
        if (_shellPendingScroll is not { } scroll || !_gestureController.IsTracking) return;
        if (e.LeftButton != MouseButtonState.Pressed) { CancelScrollShellCandidate(); return; }
        var position = e.GetPosition(this);
        var delta = position - _shellPendingScrollOrigin;
        if (ShouldClaimScrollDrag(scroll, new Point(delta.X, delta.Y)))
        {
            _shellPendingScroll = null;
            // Selection and ordinary list clicks keep their input until an upward push claims the shell.
            NotchWrapper.CaptureMouse();
            UpdateGestureTracking(position, DateTime.UtcNow);
            e.Handled = true;
        }
        else if (Math.Abs(delta.X) >= 6 || delta.Y >= 6 && scroll.ScrollableHeight > 0)
            CancelScrollShellCandidate();
    }

    private Point _shellPendingScrollOrigin;
    private void CancelScrollShellCandidate()
    {
        if (_shellPendingScroll == null) return;
        _shellPendingScroll = null;
        _gestureController.CancelTracking();
        _isGestureActive = false;
    }

    internal void BeginShellGestureTracking(Point position, DateTime timestamp)
    {
        _shellGestureWasOpen = _transitionCoordinator.TargetView != NotchView.Compact;
        _shellDragStartWidth = NotchBorder.Width;
        _shellDragStartHeight = NotchBorder.Height;
        _shellDragStartRadius = CurrentCornerRadius;
        _shellDragOriginY = 0;
        _shellGestureInMotion = Motion.IsRunning(NotchBorder, WidthProperty) || Motion.IsRunning(NotchBorder, HeightProperty);
        _shellDragActive = false;
        _shellDrawerStartHeight = ActiveDrawer?.DrawerHeight ?? 0;
        _shellLastSampleAt = timestamp;
        _gestureController.BeginTracking(position, allowShellDrag: true, expanded: _shellGestureWasOpen, timestamp: timestamp,
            allowMediaGestures: _currentMediaInfo?.IsAnyMediaPlaying == true);
    }

    internal bool UpdateGestureTracking(Point position, DateTime timestamp)
    {
        bool triggered = _gestureController.UpdateTracking(position, timestamp);
        _shellLastSampleAt = timestamp;
        if (_shellGestureWasOpen && CanSwipeMusic && _gestureController.HasDragAxis && !_gestureController.IsVerticalDrag)
        {
            MoveMusicSwipe(_gestureController.AccumulatedX);
            return false;
        }
        if (!_gestureController.IsVerticalDrag) return triggered;
        if (!_shellDragActive)
        {
            _shellDragActive = true;
            _isAnimating = true;
            if (_shellGestureInMotion)
            {
                // Slop must not rewind a shell that kept moving between the press and axis lock.
                _shellDragStartWidth = NotchBorder.Width;
                _shellDragStartHeight = NotchBorder.Height;
                _shellDragStartRadius = CurrentCornerRadius;
                _shellDragOriginY = _gestureController.AccumulatedY;
            }
            _hoverCollapseTimer.Stop();
            _hoverThumbnailDelayTimer.Stop();
            _notchShellPresenter!.CancelCurrentAnimation();
            if (!_shellGestureWasOpen)
            {
                _shellDragView = NotchView.Secondary;
                _shellOpenWidth = ClipboardTrayWidth;
                _shellOpenHeight = ClipboardTrayHeight;
                SecondaryContent.Width = _shellOpenWidth - SecondaryContent.Margin.Left - SecondaryContent.Margin.Right;
                SecondaryContent.Height = _shellOpenHeight - SecondaryContent.Margin.Top - SecondaryContent.Margin.Bottom;
                SecondaryContent.VerticalAlignment = VerticalAlignment.Top;
                _notchContentPresenter!.SnapToView(_shellDragView);
                ClipboardTrayView.BeginEntrance(animate: false);
                _navigationVisualTarget = _shellDragView;
                _shellAttachment.Attach(_shellOpenWidth, _shellOpenHeight);
                AnimateStatusBarReveal(true);
            }
            else
            {
                _shellDragView = _transitionCoordinator.TargetView;
                if (!_shellAttachment.IsAttached)
                {
                    _shellOpenWidth = _shellDragStartWidth;
                    _shellOpenHeight = _shellDragStartHeight;
                }
            }
            ResizeHostWindowHeight(_shellOpenHeight + 50);
            _shellAttachment.Attach(_shellOpenWidth, _shellOpenHeight);
        }

        double dy = _gestureController.AccumulatedY - _shellDragOriginY;
        bool drawerHandled = false;
        if (_shellGestureWasOpen) MoveShellDrawer(dy, ref drawerHandled);
        if (drawerHandled) return false;
        double height, width, radius;
        if (!_shellGestureWasOpen)
        {
            double span = Math.Max(1, _shellOpenHeight - _shellDragStartHeight);
            double pull = dy < 0 ? -ShellRubber(-dy, 20) : dy <= span ? dy : span + ShellRubber(dy - span, 50);
            height = _shellDragStartHeight + pull;
            double p = Math.Clamp(pull / span, 0, 1);
            width = _shellDragStartWidth + (_shellOpenWidth - _shellDragStartWidth) * p;
            radius = _shellDragStartRadius + (_cornerRadiusExpanded - _shellDragStartRadius) * p;
        }
        else if (dy < 0)
        {
            height = Math.Max(_collapsedHeight, _shellDragStartHeight + dy);
            double p = Math.Clamp((height - _collapsedHeight) / Math.Max(1, _shellDragStartHeight - _collapsedHeight), 0, 1);
            width = _collapsedWidth + (_shellDragStartWidth - _collapsedWidth) * p;
            radius = _cornerRadiusCollapsed + (_shellDragStartRadius - _cornerRadiusCollapsed) * p;
        }
        else
        {
            height = _shellDragStartHeight + ShellRubber(dy, 26);
            width = _shellDragStartWidth;
            radius = _shellDragStartRadius;
        }
        Motion.Stop(NotchBorder, WidthProperty, width);
        Motion.Stop(NotchBorder, HeightProperty, height);
        AnimateCornerRadius(radius, (SpringMotion?)null);
    
        return false;
    }

    internal static double ShellRubber(double distance, double limit) =>
        (1 - 1 / (distance * .55 / limit + 1)) * limit;

    private void ApplyShellReleaseVelocity()
    {
        if (_shellReleaseVelocity is not { } velocity) return;
        _shellReleaseVelocity = null;
        _notchShellPresenter!.SetReleaseVelocity(velocity, _shellReleaseWidthRatio);
    }

    internal void FinishShellDrag(bool canceled, DateTime? timestamp = null)
    {
        if (!_shellDragActive) return;
        _shellDragActive = false;
        double dy = _gestureController.AccumulatedY - _shellDragOriginY;
        double velocity = ((timestamp ?? DateTime.UtcNow) - _shellLastSampleAt).TotalMilliseconds < 60
            ? _gestureController.VerticalVelocity : 0;
        bool drawerHandled = false;
        if (_shellGestureWasOpen) FinishShellDrawer(velocity, canceled, ref drawerHandled);
        if (drawerHandled) return;
        bool open = _shellGestureWasOpen
            ? canceled || !(dy < -44 || velocity < -380)
            : !canceled && (dy > 40 || velocity > 380);
        _shellReleaseVelocity = velocity;
        if (_shellGestureWasOpen)
            _shellReleaseWidthRatio = dy >= 0 ? 0 :
                (_shellDragStartWidth - _collapsedWidth) / Math.Max(1, _shellDragStartHeight - _collapsedHeight);
        else
            _shellReleaseWidthRatio = dy >= _shellOpenHeight - _shellDragStartHeight ? 0 :
                (_shellOpenWidth - _shellDragStartWidth) / Math.Max(1, _shellOpenHeight - _shellDragStartHeight);
        if (_transitionCoordinator.RequestView(open ? _shellDragView : NotchView.Compact, "ShellDrag")) return;
        open = _transitionCoordinator.TargetView != NotchView.Compact;
        if (open && _shellDragView == NotchView.Claude)
        {
            // Content may have changed while the pointer owned the shell's height.
            _claudeViewHeight = MeasureClaudeViewHeight();
            ResizeHostWindowHeight(_claudeViewHeight);
            FitAttachedTab(ClaudeContent, _claudeViewHeight);
        }

        // A rejected drag returns to the existing view without creating a second navigation session.
        var plan = new TransitionPlan(_transitionCoordinator.ActiveTransitionId,
            open ? NotchView.Compact : _shellDragView, open ? _shellDragView : NotchView.Compact,
            open ? NotchShapeState.Expanding : NotchShapeState.Collapsing,
            open ? _shellOpenWidth : _collapsedWidth, open ? _shellOpenHeight : _collapsedHeight,
            open ? _cornerRadiusExpanded : _cornerRadiusCollapsed,
            new(TimeSpan.FromMilliseconds(500), AnimationPrimitives._easeUiOut, AnimationConfig.TargetFps,
                AnimationConfig.ReduceMotion, open ? Motion.Shift : Motion.ShellCloseWidth));
        ApplyShellReleaseVelocity();
        _notchShellPresenter!.AnimateShell(plan, _ =>
        {
            _isAnimating = false;
            if (open && _transitionCoordinator.IsTransitionActive)
                OnExpandCompleted((int)plan.SessionId, IsCountdownCompletionVisualActive, _shellDragView);
            else if (!open)
            {
                _notchContentPresenter!.SnapToView(NotchView.Compact);
                _navigationVisualTarget = null;
                AnimateStatusBarReveal(false);
                if (_transitionCoordinator.IsTransitionActive)
                    OnCollapseCompleted((int)plan.SessionId, _isAudioView,
                        _isMusicCompactMode ? MusicCompactContent : CollapsedContent, IsCountdownCompletionVisualActive);
                else RestoreExpandedWindowSize();
            }
        });
    }

    partial void MoveShellDrawer(double deltaY, ref bool handled);
    partial void FinishShellDrawer(double velocity, bool canceled, ref bool handled);

    private ControlCenterView? ActiveDrawer => _transitionCoordinator?.TargetView switch
    {
        NotchView.Pi => PiCenter,
        NotchView.School => SchoolCenter,
        NotchView.Roblox => RobloxCenter,
        NotchView.Timer => ControlCenter,
        _ => null
    };

    private void UpdateCompactTabStatus()
    {
        var center = ActiveDrawer;
        bool system = ReferenceEquals(center, ControlCenter);
        bool school = ReferenceEquals(center, SchoolCenter);
        _compactStatusHost.Visibility = center == null ? Visibility.Collapsed : Visibility.Visible;
        _compactSchoolStatus.Visibility = school ? Visibility.Visible : Visibility.Collapsed;
        _compactTabStatus.Visibility = school ? Visibility.Collapsed : Visibility.Visible;
        _compactSystemBattery.Visibility = system && _settings?.ShowBatteryIndicator == true ? Visibility.Visible : Visibility.Collapsed;
        string status = center?.CompactStatus ?? "";
        _compactTabStatus.Inlines.Clear();
        if (!system && !school)
        {
            if (status.StartsWith("♨ ", StringComparison.Ordinal))
            {
                _compactTabStatus.Inlines.Add(new Run("\uECAD ") { FontFamily = new FontFamily("Segoe Fluent Icons"), Foreground = new SolidColorBrush(Color.FromRgb(201, 176, 142)) });
                status = status[2..];
            }
            int arrow = status.IndexOf('→');
            _compactTabStatus.Inlines.Add(new Run(arrow < 0 ? status : status[..arrow]));
            if (arrow >= 0) _compactTabStatus.Inlines.Add(new Run(status[arrow..]) { Foreground = new SolidColorBrush(Color.FromArgb(148, 255, 255, 255)) });
        }
        bool warning = system && status.Length > 0;
        if (warning)
        {
            bool entering = _compactLoadWarning.Visibility != Visibility.Visible;
            _compactWarningText.Text = status;
            _compactLoadWarning.Visibility = Visibility.Visible;
            if (entering) Motion.To(_compactLoadWarning, OpacityProperty, 1, Motion.Exit, from: 0);
        }
        else if (!system)
        {
            Motion.Stop(_compactLoadWarning, OpacityProperty, 0);
            _compactLoadWarning.Visibility = Visibility.Collapsed;
        }
        else if (_compactLoadWarning.Visibility == Visibility.Visible)
            Motion.To(_compactLoadWarning, OpacityProperty, 0, Motion.Exit, done: () =>
            {
                if (ControlCenter.CompactStatus.Length == 0) _compactLoadWarning.Visibility = Visibility.Collapsed;
            });
    }

    partial void MoveShellDrawer(double deltaY, ref bool handled)
    {
        if (ActiveDrawer is not { } center || !center.MoveDrawer(deltaY)) return;
        handled = true;
        _shellAttachment.Detach(true);
        FrameworkElement host = ReferenceEquals(center, PiCenter) ? PiContent : ReferenceEquals(center, SchoolCenter) ? SchoolContent :
            ReferenceEquals(center, RobloxCenter) ? RobloxContent : TimerContent;
        double height = ReferenceEquals(center, PiCenter) ? MeasureHomeViewHeight() :
            ReferenceEquals(center, SchoolCenter) ? MeasureSchoolViewHeight() :
            Math.Min(CenterViewMaxHeight, _shellDragStartHeight + center.DrawerHeight - _shellDrawerStartHeight);
        Motion.Stop(NotchBorder, HeightProperty, height);
        Motion.Stop(host, HeightProperty, Math.Max(0, height - host.Margin.Top - host.Margin.Bottom));
    }

    partial void FinishShellDrawer(double velocity, bool canceled, ref bool handled)
    {
        if (ActiveDrawer?.ReleaseDrawer(velocity, canceled) != true) return;
        handled = true;
        _isAnimating = false;
    }
}
