using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Services;

namespace VNotch;

public partial class SettingsWindow
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnSettingsSwitchMotionOut
    {
        public double TargetX;
        public double PeakScaleX;
        public double DipScaleY;
        public double ReboundScaleX;
        public double ReboundScaleY;
        public double PressScaleX;
        public double PressScaleY;
        public double PressShiftX;
        public int PeakTimeMs;
        public int ReboundTimeMs;
        public int DurationMs;
    }

    [LibraryImport("VNotch.Native", EntryPoint = "vn_animation_compute_settings_switch_motion")]
    private static unsafe partial int NativeComputeSettingsSwitchMotion(
        double trackWidth,
        double thumbWidth,
        double marginX,
        byte isChecked,
        byte isPressed,
        byte reducedMotion,
        VnSettingsSwitchMotionOut* outMotion);

    private void SettingsSwitch_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox check) return;
        check.IsVisibleChanged -= SettingsSwitch_VisibilityChanged;
        check.IsVisibleChanged += SettingsSwitch_VisibilityChanged;
        UpdateSettingsSwitch(check, animate: false);
    }

    private static void SettingsSwitch_VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is CheckBox { IsVisible: false } check) UpdateSettingsSwitch(check, animate: false);
    }

    private void SettingsSwitch_StateChanged(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { IsLoaded: true } check)
            UpdateSettingsSwitch(check, animate: check.IsVisible, isPressed: false);
    }

    private void SettingsSwitch_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is CheckBox { IsLoaded: true, IsEnabled: true } check)
            UpdateSettingsSwitchPress(check, isPressed: true);
    }

    private void SettingsSwitch_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is CheckBox { IsLoaded: true } check)
            UpdateSettingsSwitchPress(check, isPressed: false);
    }

    private void SettingsSwitch_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is CheckBox { IsLoaded: true } check && e.LeftButton == MouseButtonState.Pressed)
            UpdateSettingsSwitchPress(check, isPressed: false);
    }

    private static unsafe VnSettingsSwitchMotionOut ComputeSwitchMotion(
        double trackWidth,
        double thumbWidth,
        double marginX,
        bool isChecked,
        bool isPressed,
        bool animate)
    {
        VnSettingsSwitchMotionOut motion = default;
        try
        {
            if (NativeComputeSettingsSwitchMotion(
                    trackWidth,
                    thumbWidth,
                    marginX,
                    isChecked ? (byte)1 : (byte)0,
                    isPressed ? (byte)1 : (byte)0,
                    animate ? (byte)0 : (byte)1,
                    &motion) == 0)
            {
                return motion;
            }
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }

        double travel = Math.Max(0, trackWidth - thumbWidth - marginX * 2.0);
        return new VnSettingsSwitchMotionOut
        {
            TargetX = isChecked ? travel : 0.0,
            PeakScaleX = animate ? 1.22 : 1.0,
            DipScaleY = animate ? 0.90 : 1.0,
            ReboundScaleX = animate ? 0.96 : 1.0,
            ReboundScaleY = animate ? 1.035 : 1.0,
            PressScaleX = animate && isPressed ? 1.15 : 1.0,
            PressScaleY = animate && isPressed ? 0.94 : 1.0,
            PressShiftX = animate && isPressed ? (isChecked ? -1.8 : 1.8) : 0.0,
            PeakTimeMs = animate ? 75 : 0,
            ReboundTimeMs = animate ? 175 : 0,
            DurationMs = animate ? 250 : 0
        };
    }

    private static void EnsureMutableThumbTransforms(
        CheckBox check,
        Border thumbBorder,
        ref TranslateTransform thumbTranslate,
        out ScaleTransform? thumbCapScale)
    {
        if (thumbTranslate.IsFrozen)
        {
            thumbTranslate = thumbTranslate.CloneCurrentValue();
            thumbBorder.RenderTransform = thumbTranslate;
        }

        thumbCapScale = null;
        if (check.Template?.FindName("SwitchThumbCap", check) is Border { RenderTransform: ScaleTransform capScale } capBorder)
        {
            if (capScale.IsFrozen)
            {
                capScale = capScale.CloneCurrentValue();
                capBorder.RenderTransform = capScale;
            }
            thumbCapScale = capScale;
        }
    }

    private static void UpdateSettingsSwitchPress(CheckBox check, bool isPressed)
    {
        check.ApplyTemplate();
        if (check.Template is not { } template ||
            template.FindName("SwitchVisual", check) is not FrameworkElement track ||
            template.FindName("SwitchThumb", check) is not Border { RenderTransform: TranslateTransform thumb } thumbBorder) return;

        EnsureMutableThumbTransforms(check, thumbBorder, ref thumb, out var capScale);
        bool animate = check.IsVisible && !AnimationConfig.ReduceMotion && AnimationConfig.ClientAreaAnimation;
        bool on = check.IsChecked == true;
        double trackWidth = track.ActualWidth > 0 ? track.ActualWidth : track.Width;
        double thumbWidth = thumbBorder.ActualWidth > 0 ? thumbBorder.ActualWidth : thumbBorder.Width;
        var motion = ComputeSwitchMotion(trackWidth, thumbWidth, thumbBorder.Margin.Left, on, isPressed, animate);

        AnimateSettingsSwitchValue(thumb, TranslateTransform.XProperty, motion.TargetX + motion.PressShiftX, animate, isPressed ? Motion.PressDown : Motion.Press);
        if (capScale != null)
        {
            AnimateSettingsSwitchValue(capScale, ScaleTransform.ScaleXProperty, motion.PressScaleX, animate, isPressed ? Motion.PressDown : Motion.Press);
            AnimateSettingsSwitchValue(capScale, ScaleTransform.ScaleYProperty, motion.PressScaleY, animate, isPressed ? Motion.PressDown : Motion.Press);
        }
    }

    private static void UpdateSettingsSwitch(CheckBox check, bool animate, bool isPressed = false)
    {
        check.ApplyTemplate();
        if (check.Template is not { } template ||
            template.FindName("SwitchVisual", check) is not FrameworkElement track ||
            template.FindName("SwitchThumb", check) is not Border { RenderTransform: TranslateTransform thumb } thumbBorder ||
            template.FindName("SwitchOnTrack", check) is not Border onTrack) return;

        // WPF freezes transforms defined in a shared template until an instance needs motion.
        EnsureMutableThumbTransforms(check, thumbBorder, ref thumb, out var capScale);

        animate &= !AnimationConfig.ReduceMotion && SystemParameters.ClientAreaAnimation;
        bool on = check.IsChecked == true;
        double trackWidth = track.ActualWidth > 0 ? track.ActualWidth : track.Width;
        double thumbWidth = thumbBorder.ActualWidth > 0 ? thumbBorder.ActualWidth : thumbBorder.Width;
        var motion = ComputeSwitchMotion(trackWidth, thumbWidth, thumbBorder.Margin.Left, on, isPressed, animate);

        AnimateSettingsSwitchValue(thumb, TranslateTransform.XProperty, motion.TargetX, animate, Motion.Shift);
        AnimateSettingsSwitchValue(onTrack, OpacityProperty, on ? 1 : 0, animate, Motion.Exit);

        if (capScale != null)
        {
            AnimateSettingsSwitchThumbLiquidMorph(capScale, motion, animate);
        }
    }

    private static void AnimateSettingsSwitchThumbLiquidMorph(ScaleTransform capScale, VnSettingsSwitchMotionOut motion, bool animate)
    {
        if (!animate || motion.DurationMs <= 0)
        {
            Motion.Stop(capScale, ScaleTransform.ScaleXProperty, 1.0);
            Motion.Stop(capScale, ScaleTransform.ScaleYProperty, 1.0);
            return;
        }

        // The knob springs back from its press squeeze. A flip without a press (keyboard, click on the label)
        // gets a little stretch along its travel instead, so it still feels liquid.
        bool squeezed = Math.Abs(capScale.ScaleX - 1) > 0.01 || Math.Abs(capScale.ScaleY - 1) > 0.01;
        Motion.To(capScale, ScaleTransform.ScaleXProperty, 1.0, Motion.Press, velocity: squeezed ? null : 1.6);
        Motion.To(capScale, ScaleTransform.ScaleYProperty, 1.0, Motion.Press, velocity: squeezed ? null : -0.8);
    }

    // A pressed switch dips quickly; a released or flipped one springs, keeping its speed when flipped back mid-way.
    private static void AnimateSettingsSwitchValue(DependencyObject target, DependencyProperty property, double value, bool animate, SpringMotion spring)
    {
        if (!animate)
        {
            Motion.Stop(target, property, value);
            return;
        }
        Motion.To(target, property, value, spring);
    }

}
