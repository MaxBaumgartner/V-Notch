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
/// Every tab has a size of its own again, the way upstream's do (music 480 wide, clock 600, camera 520,
/// the mixer 720 and as tall as its rows). The control-center tabs get a width that suits what they show
/// (Claude 640, home 680, school 580; the system tab stays the big one) and grow or shrink to their
/// content between a floor and the height where their list starts to scroll.
/// </summary>
public partial class MainWindow
{
    private const double HomeViewWidth = 680;
    private const double SchoolViewWidth = 580;
    private const double CenterViewMinHeight = 160;
    private const double CenterViewMaxHeight = 420;

    private double _homeViewHeight = CenterViewMaxHeight;
    private double _schoolViewHeight = CenterViewMaxHeight;
    private int _centerNotchHeightGeneration;

    /// <summary>The notch height that fits a control-center page laid out <paramref name="width"/> wide.</summary>
    private static double FitHeight(ControlCenterView center, FrameworkElement host, double width, double min, double max)
    {
        var margin = host.Margin;
        double content = center.MeasureContentHeight(width - margin.Left - margin.Right);
        return Math.Clamp(Math.Ceiling(content + margin.Top + margin.Bottom), min, max);
    }

    private double MeasureHomeViewHeight() =>
        _homeViewHeight = FitHeight(PiCenter, PiContent, HomeViewWidth, CenterViewMinHeight, CenterViewMaxHeight);

    private double MeasureSchoolViewHeight() =>
        _schoolViewHeight = FitHeight(SchoolCenter, SchoolContent, SchoolViewWidth, CenterViewMinHeight, CenterViewMaxHeight);

    private void OnHomeContentHeightChanged() => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, SettleHomeNotchToFit);

    private void OnSchoolContentHeightChanged() => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, SettleSchoolNotchToFit);

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
        Motion.To(content, HeightProperty, Math.Max(0, target - chrome), Motion.Glide);
        void Arrived()
        {
            if (generation != _centerNotchHeightGeneration || !stillOpen()) return;
            if (!growing) ResizeHostWindowHeight(target);
        }
        if (_notchShellPresenter?.RetargetHeight(target, Arrived) != true)
            Motion.To(NotchBorder, HeightProperty, target, Motion.Glide, done: Arrived);
    }
}
