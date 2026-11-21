using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VNotch.Controllers;
using VNotch.Services;
using VNotch.Services.ControlCenter;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

public partial class MainWindow
{
    private VNotch.Presenters.CompactSurfaceAttachment? _appPillAttachment;
    private bool _appPillExpanded;
    private int _appPillToken;
    private int _appPillMotionGeneration;
    private DispatcherTimer? _appPillDismissTimer;
    private double AppPillCompactHeight => Math.Max(_collapsedHeight + 18, 56);

    private const int AppPillExpandedMaxLines = 6;
    private static readonly TimeSpan AppPillIdleCollapse = TimeSpan.FromSeconds(8);
    private WindowsNotification? _appPillToast;
    private readonly Queue<(WindowsNotification Toast, ImageSource? Logo)> _appPillQueue = new();
    private DispatcherTimer? _appPillCollapseTimer;
    private int _appPillImageToken;

    /// <summary>Opens the app a notification came from; a test can swap it out.</summary>
    internal Func<string, bool> AppLauncher { get; set; } = LaunchApp;
    internal Action<string> AppPillClipboardWriter { get; set; } = Clipboard.SetText;
    private string? _appPillCode;

    /// <summary>Shows the sender and two message lines in the closed notch.</summary>
    private void ShowAppNotificationPill(WindowsNotification toast, ImageSource? logo, int othersWaiting)
    {
        if (_cleanedUp || _isGreetingActive || _isAnimating || _isExpanded || _isMusicExpanded || !CanShowClaudeAttention) return;
        if (_appPillExpanded)
        {
            // The open message stays until it is read; the newcomer waits and the sender line counts it.
            _appPillQueue.Enqueue((toast, logo));
            if (_appPillToast != null) CrossFadeText(AppPillSender, SenderLine(_appPillToast, _appPillQueue.Count));
            return;
        }
        if (_appPillToken == 0 && !TryAcquireCompactSlot(CompactPillSlot.AppNotification, out _appPillToken)) return;

        bool replacing = _appPillToast != null && AppNotificationPill.Visibility == Visibility.Visible;
        if (replacing) KeepCompactDeparture(AppNotificationPill);
        _appPillToast = toast;
        _appPillCode = OneTimeCode.Find(toast.Title, toast.Lines);
        AppPillCodeText.Text = _appPillCode ?? "";
        AppPillCodeChip.Visibility = _appPillCode == null ? Visibility.Collapsed : Visibility.Visible;
        LocalizeAppPillCodeChip();
        Motion.Stop(AppPillCodeTick, System.Windows.Shapes.Shape.StrokeDashOffsetProperty, 17);
        AppPillCodeTick.Visibility = Visibility.Hidden;
        _appPillExpanded = false;
        AppPillBody.Visibility = Visibility.Visible;
        // The frozen outgoing face crossfades with this new face, including its logo and code chip.
        AppPillSender.Text = SenderLine(toast, othersWaiting);
        AppPillBody.Text = string.Join("\n", toast.Lines.Take(2));
        AppPillLogo.Source = logo;
        AppPillLogo.Visibility = logo == null ? Visibility.Collapsed : Visibility.Visible;
        AppPillBell.Visibility = logo == null ? Visibility.Visible : Visibility.Collapsed;
        AppPillExpandedBody.Visibility = Visibility.Collapsed;
        AppPillImage.Visibility = Visibility.Collapsed;
        SuppressPrivacyDot();
        HideCompactSurface(CollapsedContent);
        HideCompactSurface(MusicCompactContent);
        AppNotificationPill.Visibility = Visibility.Visible;
        AppNotificationPill.IsHitTestVisible = true;
        SetAppNotificationOpacity(AppNotificationPill, replacing ? 0 : 1);
        SetAppNotificationValue(AppPillTranslate, TranslateTransform.YProperty, 0);
        if (replacing)
        {
            Motion.To(AppNotificationPill, OpacityProperty, 1, Motion.Exit, from: 0);
            Motion.To(AppPillTranslate, TranslateTransform.YProperty, 0, Motion.Exit, from: 6);
        }
        foreach (var part in new UIElement[] { AppPillLogo, AppPillBell, AppPillSender, AppPillBody })
            if (part.Visibility == Visibility.Visible) RevealAppPillPart(part, 0);
        ResizeAppPill(Math.Max(_collapsedWidth, Math.Min(PillMaxWidth, 380)), AppPillCompactHeight, 350);
        _appPillDismissTimer ??= CreateAppPillDismissTimer();
        _appPillDismissTimer.Stop();
        _appPillDismissTimer.Start();
    }

    private static string SenderLine(WindowsNotification toast, int othersWaiting) =>
        (toast.Title.Length > 0 ? toast.Title : toast.AppName) + (othersWaiting > 0 ? $"  +{othersWaiting}" : "");

    private void AppPillCodeChip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        CopyAppPillCode();
    }

    private void AppPillCodeChip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void LocalizeAppPillCodeChip()
    {
        AppPillCodeChip.ToolTip = Loc.Get("notifications.copyCode");
        System.Windows.Automation.AutomationProperties.SetName(AppPillCodeChip, Loc.Get("notifications.copyCode"));
    }

    private void AppPillCodeChip_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space)) return;
        e.Handled = true;
        CopyAppPillCode();
    }

    /// <summary>The chip gives a little under the finger and springs back, like the control center's buttons.</summary>
    private void SquishAppPillCodeChip()
    {
        if (AnimationConfig.ReduceMotion) return;
        if (AppPillCodeChip.RenderTransform is not ScaleTransform scale || scale.IsFrozen)
        {
            AppPillCodeChip.RenderTransformOrigin = new Point(0.5, 0.5);
            AppPillCodeChip.RenderTransform = scale = new ScaleTransform(1, 1);
        }
        Motion.To(scale, ScaleTransform.ScaleXProperty, 1, Motion.Press,
            velocity: Motion.VelocityOf(scale, ScaleTransform.ScaleXProperty) - 2);
        Motion.To(scale, ScaleTransform.ScaleYProperty, 1, Motion.Press,
            velocity: Motion.VelocityOf(scale, ScaleTransform.ScaleYProperty) - 2);
    }

    private void CopyAppPillCode()
    {
        if (_appPillCode == null) return;
        try { AppPillClipboardWriter(_appPillCode); }
        catch (System.Runtime.InteropServices.ExternalException) { return; }
        AppPillCodeTick.Visibility = Visibility.Visible;
        Motion.To(AppPillCodeTick, System.Windows.Shapes.Shape.StrokeDashOffsetProperty, 0, Motion.Exit);
        SquishAppPillCodeChip();
    }

    /// <summary>
    /// A tap on the app message: the first opens it up to the whole text (and its picture), the second opens the
    /// app with the drop falling toward it. False when no app message is showing.
    /// </summary>
    private bool TryHandleAppPillTap()
    {
        if (_appPillToken == 0 || _appPillToast is not { } toast) return false;
        if (!_appPillExpanded)
        {
            ExpandAppPill();
            return true;
        }
        if (toast.AppUserModelId is { Length: > 0 } appId)
        {
            IntPtr foregroundBefore = Controllers.OverlayWindowController.GetForegroundWindowHandle();
            if (AppLauncher(appId))
            {
                if (IsLoaded) PlayNotificationDrop(foregroundBefore);
                _appPillQueue.Clear();
                _appPillExpanded = false;
                DismissAppPill();
                return true;
            }
        }
        CollapseAppPill();
        return true;
    }

    /// <summary>The whole message grows with the notch, keeping sender and body together.</summary>
    private void ExpandAppPill()
    {
        if (_appPillToast is not { } toast) return;
        _appPillExpanded = true;
        _appPillDismissTimer?.Stop();
        HideAppPillPart(AppPillBody);
        var lines = toast.Lines.Count > 0 ? toast.Lines : (IReadOnlyList<string>)new[] { toast.Body ?? "" };
        AppPillExpandedBody.Text = string.Join("\n", lines);
        AppPillExpandedBody.MaxHeight = AppPillExpandedBody.LineHeight * AppPillExpandedMaxLines;
        RevealAppPillPart(AppPillExpandedBody, 1);
        LoadAppPillImage(toast);
        ResizeAppPill(ExpandedAppPillWidth(), MeasureAppPillHeight(ExpandedAppPillWidth()), 420);
        RestartAppPillCollapseTimer();
    }

    /// <summary>Back to the two-line pill; a message that waited meanwhile takes its place.</summary>
    private void CollapseAppPill()
    {
        _appPillCollapseTimer?.Stop();
        if (_appPillToken == 0) return;
        _appPillExpanded = false;
        ++_appPillImageToken;
        HideAppPillPart(AppPillImage);
        HideAppPillPart(AppPillExpandedBody);
        RevealAppPillPart(AppPillBody, 1);
        if (_appPillQueue.Count > 0)
        {
            var (next, logo) = _appPillQueue.Dequeue();
            ShowAppNotificationPill(next, logo, _appPillQueue.Count);
            return;
        }
        if (_appPillToast != null) CrossFadeText(AppPillSender, SenderLine(_appPillToast, 0));
        ResizeAppPill(Math.Max(_collapsedWidth, Math.Min(PillMaxWidth, 380)), AppPillCompactHeight, 260);
        _appPillDismissTimer?.Stop();
        _appPillDismissTimer?.Start();
    }

    private double ExpandedAppPillWidth() => Math.Max(_collapsedWidth, Math.Min(PillMaxWidth, 440));

    /// <summary>How tall the pill must be for its content at this width (its own margins included).</summary>
    private double MeasureAppPillHeight(double width)
    {
        var margin = AppNotificationPill.Margin;
        AppNotificationPill.Measure(new Size(Math.Max(0, width - margin.Left - margin.Right), double.PositiveInfinity));
        return Math.Max(AppPillCompactHeight, Math.Ceiling(AppNotificationPill.DesiredSize.Height + margin.Top + margin.Bottom));
    }

    /// <summary>Folds the open message back after a while without the cursor on it.</summary>
    private void RestartAppPillCollapseTimer()
    {
        if (_appPillCollapseTimer == null)
        {
            _appPillCollapseTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = AppPillIdleCollapse };
            _appPillCollapseTimer.Tick += (_, _) => { if (_appPillExpanded) CollapseAppPill(); };
            AppNotificationPill.MouseEnter += (_, _) => _appPillCollapseTimer?.Stop();
            AppNotificationPill.MouseLeave += (_, _) => { if (_appPillExpanded) RestartAppPillCollapseTimer(); };
        }
        _appPillCollapseTimer.Stop();
        _appPillCollapseTimer.Start();
    }

    /// <summary>The toast's picture, if it has one, fades in under the text once loaded.</summary>
    private async void LoadAppPillImage(WindowsNotification toast)
    {
        if (toast.Image == null) return;
        int token = ++_appPillImageToken;
        try
        {
            using var stream = await toast.Image.OpenReadAsync();
            using var source = System.IO.WindowsRuntimeStreamExtensions.AsStreamForRead(stream);
            var copy = new System.IO.MemoryStream();
            await source.CopyToAsync(copy);
            copy.Position = 0;
            var bitmap = new System.Windows.Media.Imaging.BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bitmap.DecodePixelHeight = 240;
            bitmap.StreamSource = copy;
            bitmap.EndInit();
            bitmap.Freeze();
            if (token != _appPillImageToken || !_appPillExpanded || !ReferenceEquals(toast, _appPillToast)) return;
            AppPillImage.Source = bitmap;
            RevealAppPillPart(AppPillImage, 2);
            ResizeAppPill(ExpandedAppPillWidth(), MeasureAppPillHeight(ExpandedAppPillWidth()), 300);
        }
        catch (Exception ex) when (ex is System.IO.IOException or NotSupportedException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            RuntimeLog.Warn("NOTIFICATIONS", $"No picture for {toast.AppName}: {ex.Message}");
        }
    }

    private DispatcherTimer CreateAppPillDismissTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = AppNotificationDuration };
        timer.Tick += (_, _) => DismissAppPill();
        return timer;
    }

    private void ResizeAppPill(double width, double height, int milliseconds, Action? completed = null)
    {
        int generation = ++_appPillMotionGeneration;
        int token = _appPillToken;
        if (height + 32 > Height) ResizeHostWindowHeight(height);
        bool closing = Math.Abs(width - _collapsedWidth) < .5 && Math.Abs(height - _collapsedHeight) < .5;
        var spring = closing ? Motion.PopBack : _appPillExpanded ? Motion.Shift : Motion.Pop;
        if (!double.IsFinite(NotchBorder.Width)) Motion.Stop(NotchBorder, WidthProperty, _collapsedWidth);
        if (!double.IsFinite(NotchBorder.Height)) Motion.Stop(NotchBorder, HeightProperty, _collapsedHeight);
        _notchShellPresenter?.CancelCurrentAnimation();
        _appPillAttachment ??= new(NotchBorder, AppNotificationPill,
            () => new Size(_collapsedWidth, _collapsedHeight));
        _appPillAttachment.MoveTo(width, height, spring, shown: !closing, done: () =>
        {
            if (generation != _appPillMotionGeneration || token != _appPillToken || IsCompactSlotStale(token)) return;
            completed?.Invoke();
        });
    }
    /// <summary>The message slips back into the resting notch.</summary>
    private void DismissAppPill()
    {
        if (_appPillToken == 0) return;
        _appPillDismissTimer?.Stop();
        AppNotificationPill.IsHitTestVisible = false;
        ResizeAppPill(_collapsedWidth, _collapsedHeight, 260, () =>
        {
            int token = _appPillToken;
            CancelAppPillImmediate();
            _compactPillArbiter.Release(token);
            RestorePrivacyDotVisibility();
            RestoreCompactMediaPresentation();
            if (_isExpanded || _isAnimating) return;
            FrameworkElement content = _isMusicCompactMode && _currentMediaInfo != null ? MusicCompactContent : CollapsedContent;
            content.Visibility = Visibility.Visible;
            if (AnimationConfig.ReduceMotion) SetAppNotificationOpacity(content, 1);
            else content.BeginAnimation(OpacityProperty, MakeCompactContentRestore());
        });
    }

    /// <summary>Another surface takes the notch, so the app message steps aside.</summary>
    private void CancelAppPillImmediate()
    {
        _appPillDismissTimer?.Stop();
        _appPillCollapseTimer?.Stop();
        ++_appPillMotionGeneration;
        ++_appPillImageToken;
        if (_appPillToken == 0) return;
        _appPillToken = 0;
        _appPillExpanded = false;
        _appPillToast = null;
        _appPillCode = null;
        AppPillCodeChip.Visibility = Visibility.Collapsed;
        Motion.Stop(AppPillCodeTick, System.Windows.Shapes.Shape.StrokeDashOffsetProperty);
        _appPillQueue.Clear();
        if (_compactPillArbiter.ActiveSlot is CompactPillSlot.None or CompactPillSlot.AppNotification)
        {
            Motion.Stop(NotchBorder, WidthProperty, _collapsedWidth);
            Motion.Stop(NotchBorder, HeightProperty, _collapsedHeight);
        }
        _appPillAttachment?.Detach(false);
        Motion.Stop(AppNotificationPill, OpacityProperty, 0);
        AppNotificationPill.Visibility = Visibility.Collapsed;
        Motion.Stop(AppPillTranslate, TranslateTransform.YProperty, 0);
        AppPillLogo.Source = null;
        AppPillImage.Source = null;
    }

    private static void RevealAppPillPart(UIElement element, int index)
    {
        element.Visibility = Visibility.Visible;
        if (element.RenderTransform is ScaleTransform scale)
        {
            Motion.Stop(scale, ScaleTransform.ScaleXProperty, 1);
            Motion.Stop(scale, ScaleTransform.ScaleYProperty, 1);
        }
        // Layout and reveal belong to the attached face, rather than a stagger of individual words.
        Motion.Stop(element, OpacityProperty, 1);
    }
    private void HideAppPillPart(UIElement element)
    {
        if (element.Visibility != Visibility.Visible) return;
        // The old body leaves above the layout while the new body's measured height takes over immediately.
        KeepCompactDeparture(element);
        Motion.Stop(element, OpacityProperty, 0);
        if (element.RenderTransform is ScaleTransform scale)
        {
            Motion.Stop(scale, ScaleTransform.ScaleXProperty);
            Motion.Stop(scale, ScaleTransform.ScaleYProperty);
        }
        element.Visibility = Visibility.Collapsed;
    }
}
