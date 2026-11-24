using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VNotch.Controls;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

/// <summary>
/// Home, school and system fit their compact controls and grow with their drawers up to the scroll cap.
/// </summary>
public partial class MainWindow
{
    private const double HomeViewWidth = 600;
    private const double SchoolViewWidth = 584;
    private const double RobloxViewWidth = 584;
    private const double CenterViewMinHeight = 82;
    // As tall as the Claude tab may grow, so an open drawer fits instead of scrolling.
    private const double CenterViewMaxHeight = 580;

    private double _homeViewHeight = CenterViewMaxHeight;
    private double _schoolViewHeight = CenterViewMaxHeight;
    private double _robloxViewHeight = CenterViewMaxHeight;
    private int _centerNotchHeightGeneration;

    /// <summary>The notch height that fits a control-center page laid out <paramref name="width"/> wide.</summary>
    private static double FitHeight(ControlCenterView center, FrameworkElement host, double width, double min, double max)
    {
        var margin = host.Margin;
        double content = center.MeasureContentHeight(width - margin.Left - margin.Right);
        double height = content + margin.Top + margin.Bottom;
        return Math.Clamp(center.IsLayoutFollowing ? height : Math.Ceiling(height), min, max);
    }

    private double MeasureHomeViewHeight() =>
        _homeViewHeight = FitHeight(PiCenter, PiContent, HomeViewWidth, CenterViewMinHeight, CenterViewMaxHeight);

    private double MeasureSchoolViewHeight() =>
        _schoolViewHeight = FitHeight(SchoolCenter, SchoolContent, SchoolViewWidth, CenterViewMinHeight, CenterViewMaxHeight);

    private void OnHomeContentHeightChanged() => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, SettleHomeNotchToFit);

    private double MeasureSystemViewHeight() =>
        FitHeight(ControlCenter, TimerContent, _clockViewWidth, 82, CenterViewMaxHeight);

    private void OnSystemContentHeightChanged() => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
    {
        if (_transitionCoordinator.TargetView != VNotch.Models.NotchView.Timer || _cleanedUp || IsCountdownCompletionVisualActive) return;
        SettleNotchToContent(TimerContent, MeasureSystemViewHeight(), () => _transitionCoordinator.TargetView == VNotch.Models.NotchView.Timer);
    }));

    private void OnSchoolContentHeightChanged() => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, SettleSchoolNotchToFit);

    private double MeasureRobloxViewHeight() =>
        _robloxViewHeight = FitHeight(RobloxCenter, RobloxContent, RobloxViewWidth, CenterViewMinHeight, CenterViewMaxHeight);

    private void OnRobloxContentHeightChanged() => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, SettleRobloxNotchToFit);

    private void SettleRobloxNotchToFit()
    {
        if (_transitionCoordinator.TargetView != VNotch.Models.NotchView.Roblox || _cleanedUp) return;
        SettleNotchToContent(RobloxContent, MeasureRobloxViewHeight(),
            () => _transitionCoordinator.TargetView == VNotch.Models.NotchView.Roblox);
    }

    private void SettleHomeNotchToFit()
    {
        if (_transitionCoordinator.TargetView != VNotch.Models.NotchView.Pi || _cleanedUp) return;
        SettleNotchToContent(PiContent, MeasureHomeViewHeight(),
            () => _transitionCoordinator.TargetView == VNotch.Models.NotchView.Pi);
    }

    private void SettleSchoolNotchToFit()
    {
        if (_transitionCoordinator.TargetView != VNotch.Models.NotchView.School || _cleanedUp) return;
        SettleNotchToContent(SchoolContent, MeasureSchoolViewHeight(),
            () => _transitionCoordinator.TargetView == VNotch.Models.NotchView.School);
    }

    /// <summary>
    /// Grows or shrinks the open tab to <paramref name="target"/> when its content changes (sessions come and
    /// go, the home data arrives, a section folds), like the mixer with its devices.
    /// </summary>
    private void SettleNotchToContent(FrameworkElement content, double target, Func<bool> stillOpen)
    {
        if (_shellDragActive) return;
        var center = ReferenceEquals(content, PiContent) ? PiCenter : ReferenceEquals(content, SchoolContent) ? SchoolCenter :
            ReferenceEquals(content, RobloxContent) ? RobloxCenter :
            ReferenceEquals(content, TimerContent) ? ControlCenter : ClaudeCenter;
        if (center.IsLayoutFollowing && !_shellAttachment.IsAttached)
        {
            Motion.Stop(content, HeightProperty, Math.Max(0, target - content.Margin.Top - content.Margin.Bottom));
            Motion.Stop(NotchBorder, HeightProperty, target);
            ResizeHostWindowHeight(target);
            return;
        }
        double current = double.IsFinite(NotchBorder.Height) ? NotchBorder.Height :
            NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight : target;
        double chrome = content.Margin.Top + content.Margin.Bottom;
        if (Math.Abs(current - target) < 0.5 &&
            !DependencyPropertyHelper.GetValueSource(NotchBorder, HeightProperty).IsAnimated) return;

        int generation = ++_centerNotchHeightGeneration;
        bool growing = target > current;
        if (growing) ResizeHostWindowHeight(target);

        // Auto-sized content needs a finite initial value; subsequent changes retain the live spring.
        if (!double.IsFinite(content.Height)) content.Height = Math.Max(0, current - chrome);
        var spring = ReferenceEquals(center, ClaudeCenter) ? Motion.Exit : Motion.Shift;
        Motion.To(content, HeightProperty, Math.Max(0, target - chrome), spring);
        void Arrived()
        {
            if (generation != _centerNotchHeightGeneration || !stillOpen()) return;
            if (!growing) ResizeHostWindowHeight(target);
        }
        if (_notchShellPresenter?.RetargetHeight(target, Arrived) != true)
            Motion.To(NotchBorder, HeightProperty, target, spring, done: Arrived);
    }
}
