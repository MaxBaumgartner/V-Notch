using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using VNotch.Controllers;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

public partial class MainWindow
{
    private int _compactWidthAnimationVersion = 0;
    private bool _compactPresentationGuardInstalled;
    private bool _enforcingCompactPresentation;

    private void EnsureCompactPresentationGuard()
    {
        if (_compactPresentationGuardInstalled) return;
        _compactPresentationGuardInstalled = true;
        foreach (var element in new UIElement[] { MusicViz, CompactThumbnailBorder,
            MusicCompactContent, CollapsedContent,
            ClipboardCheckIcon, ClipboardCopiedText, VolumeIndicatorContainer,
            BluetoothNotification, BluetoothDisconnectNotification, ChargingNotification, ClaudeNotification, AppNotificationPill })
            element.IsVisibleChanged += (_, _) => EnforceCompactPresentationOwner();
    }

    private void HideCompactSurface(UIElement element)
    {
        if (element.Visibility != Visibility.Visible) return;
        if (!element.IsVisible || !AmbientAnimationsAllowed)
        {
            Motion.Stop(element, OpacityProperty, 0);
            element.Visibility = Visibility.Collapsed;
            return;
        }
        Motion.To(element, OpacityProperty, 0, Motion.Exit,
            done: () => { if (element.Opacity < .01) element.Visibility = Visibility.Collapsed; });
    }

    private void KeepCompactDeparture(UIElement surface)
    {
        if (!AmbientAnimationsAllowed || !surface.IsVisible || surface.Opacity <= 0 ||
            surface.RenderSize.Width <= 0 || surface.RenderSize.Height <= 0) return;
        // Freeze the outgoing face before its owner clears state; it can leave without keeping any live loops alive.
        var size = surface.RenderSize;
        Rect bounds = surface.TransformToAncestor(NotchLiveContent).TransformBounds(new Rect(size));
        double parentOpacity = 1;
        for (var parent = VisualTreeHelper.GetParent(surface); parent != null && parent != NotchLiveContent;
             parent = VisualTreeHelper.GetParent(parent))
            if (parent is UIElement element) parentOpacity *= element.Opacity;
        if (bounds.Width <= 0 || bounds.Height <= 0 || parentOpacity <= 0) return;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width * 2),
            (int)Math.Ceiling(size.Height * 2), 192, 192, PixelFormats.Pbgra32);
        // Render in local coordinates: a child's layout offset otherwise cuts its glyph out of the bitmap.
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen())
            dc.DrawRectangle(new VisualBrush(surface) { Stretch = Stretch.Fill,
                ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(size) }, null, new Rect(size));
        bitmap.Render(drawing);
        bitmap.Freeze();
        Point position = bounds.TopLeft;
        var scale = new ScaleTransform(1, 1);
        var slide = new TranslateTransform();
        var transforms = new TransformGroup();
        transforms.Children.Add(scale);
        transforms.Children.Add(slide);
        var face = new Image { Source = bitmap, Width = bounds.Width, Height = bounds.Height, Opacity = parentOpacity,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(position.X, position.Y, 0, 0), IsHitTestVisible = false,
            RenderTransformOrigin = new Point(.5, .5), RenderTransform = transforms };
        Panel.SetZIndex(face, 100);
        NotchLiveContent.Children.Add(face);
        Motion.To(scale, ScaleTransform.ScaleXProperty, Motion.ExitToScale, Motion.Exit);
        Motion.To(scale, ScaleTransform.ScaleYProperty, Motion.ExitToScale, Motion.Exit);
        Motion.To(slide, TranslateTransform.YProperty, -4, Motion.Exit);
        Motion.To(face, OpacityProperty, 0, Motion.Exit, done: () => NotchLiveContent.Children.Remove(face));
    }

    private void KeepCompactSlotDeparture(CompactPillSlot slot)
    {
        switch (slot)
        {
            case CompactPillSlot.Clipboard:
                KeepCompactDeparture(ClipboardCheckIcon);
                KeepCompactDeparture(ClipboardCopiedText);
                break;
            case CompactPillSlot.Volume: KeepCompactDeparture(VolumeIndicatorContainer); break;
            case CompactPillSlot.Bluetooth:
                KeepCompactDeparture(BluetoothNotification);
                KeepCompactDeparture(BluetoothDisconnectNotification);
                break;
            case CompactPillSlot.Charging: KeepCompactDeparture(ChargingNotification); break;
            case CompactPillSlot.Claude: KeepCompactDeparture(ClaudeNotification); break;
            case CompactPillSlot.AppNotification: KeepCompactDeparture(AppNotificationPill); break;
        }
    }

    private void EnforceCompactPresentationOwner()
    {
        if (_enforcingCompactPresentation) return;
        _enforcingCompactPresentation = true;
        try
        {
            var owner = _compactPillArbiter.ActiveSlot;
            if (owner is CompactPillSlot.Bluetooth or CompactPillSlot.Charging or CompactPillSlot.Greeting or CompactPillSlot.Claude or CompactPillSlot.AppNotification)
            {
                HideCompactSurface(MusicCompactContent);
                HideCompactSurface(CollapsedContent);
            }
            if (owner != CompactPillSlot.None)
            {
                if (owner != CompactPillSlot.Clipboard) HideCompactSurface(MusicViz);
                HideCompactSurface(CompactThumbnailBorder);
            }
            if (owner != CompactPillSlot.Clipboard)
            {
                HideCompactSurface(ClipboardCheckIcon);
                HideCompactSurface(ClipboardCopiedText);
            }
            if (owner != CompactPillSlot.Volume &&
                !(_isVolumeIndicatorExiting && owner == CompactPillSlot.None))
                HideCompactSurface(VolumeIndicatorContainer);
            if (owner != CompactPillSlot.Bluetooth)
            {
                HideCompactSurface(BluetoothNotification);
                HideCompactSurface(BluetoothDisconnectNotification);
            }
            if (owner != CompactPillSlot.Charging) HideCompactSurface(ChargingNotification);
            if (owner != CompactPillSlot.Claude) HideCompactSurface(ClaudeNotification);
            if (owner != CompactPillSlot.AppNotification) HideCompactSurface(AppNotificationPill);
        }
        finally { _enforcingCompactPresentation = false; }
    }

    private void RestoreCompactMediaPresentation()
    {
        if (_isExpanded || _isAnimating || !_isMusicCompactMode ||
            !_compactPillArbiter.CanRestoreMedia(_compactPillArbiter.Revision)) return;
        EnforceCompactPresentationOwner();
        AnimateCompactWidth(_collapsedWidth, _dur350, _easeExpOut6, 0);
        Motion.To(NotchBorder, HeightProperty, _collapsedHeight, Motion.PopBack);
        CompactThumbnailBorder.BeginAnimation(OpacityProperty, null);
        Motion.To(CompactThumbnailScale, ScaleTransform.ScaleXProperty, 1, Motion.TileScale);
        Motion.To(CompactThumbnailScale, ScaleTransform.ScaleYProperty, 1, Motion.TileScale);
        if (MusicCompactContentBlur != null)
        {
            MusicCompactContentBlur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, null);
            MusicCompactContentBlur.Radius = 0;
        }
        CompactThumbnailBorder.Visibility = Visibility.Visible;
        Motion.To(CompactThumbnailBorder, OpacityProperty, 1, Motion.Tile);
        ShowMusicVisualizer();
    }

    private bool TryAcquireCompactSlot(CompactPillSlot slot, out int token)
    {
        WakeFromIdle();
        EnsureCompactPresentationGuard();
        if (_isAppNotificationBannerVisible)
        {
            token = 0;
            return false;
        }
        // A finished Claude turn holds the pill for good, so any other alert may pass it; it returns afterwards.
        if (slot != CompactPillSlot.Claude && (IsClaudeDonePillShown || IsCodexJobPillShown)) StepClaudePillAside();

        var result = _compactPillArbiter.TryAcquire(slot);
        token = result.Token;
        if (!result.Won)
        {
            return false;
        }

        if (result.Preempted != CompactPillSlot.None)
            CancelCompactSlotImmediate(result.Preempted);
        EnforceCompactPresentationOwner();
        return true;
    }

    private void CancelCompactSlotImmediate(CompactPillSlot slot)
    {
        KeepCompactSlotDeparture(slot);
        double width = double.IsFinite(NotchBorder.Width) ? NotchBorder.Width : _collapsedWidth;
        double height = double.IsFinite(NotchBorder.Height) ? NotchBorder.Height : _collapsedHeight;
        double widthSpeed = Motion.VelocityOf(NotchBorder, WidthProperty);
        double heightSpeed = Motion.VelocityOf(NotchBorder, HeightProperty);
        switch (slot)
        {
            case CompactPillSlot.Clipboard:
                CancelClipboardPeekImmediate();
                break;
            case CompactPillSlot.Volume:
                DismissVolumeIndicatorImmediate();
                break;
            case CompactPillSlot.Bluetooth:
                CancelBluetoothNotificationImmediate();
                break;
            case CompactPillSlot.Charging:
                CancelChargingGlanceImmediate();
                break;
            case CompactPillSlot.Greeting:
                break;
            case CompactPillSlot.Claude:
                CancelClaudeNotificationImmediate();
                break;
            case CompactPillSlot.AppNotification:
                CancelAppPillImmediate();
                break;
        }
        // Cancellation clears the old owner's state; the next owner inherits its rendered shell and momentum.
        Motion.To(NotchBorder, WidthProperty, width, Motion.Pop, from: width, velocity: widthSpeed);
        Motion.To(NotchBorder, HeightProperty, height, Motion.Pop, from: height, velocity: heightSpeed);
    }

    private void AnimateCompactWidth(double targetWidth, TimeSpan duration, IEasingFunction ease, int token)
        => AnimateCompactWidth(targetWidth, new Duration(duration), ease, token);

    private void AnimateCompactWidth(double targetWidth, Duration duration, IEasingFunction ease, int token)
    {
        int version = ++_compactWidthAnimationVersion;
        long revision = _compactPillArbiter.Revision;
        bool returningToRest = Math.Abs(targetWidth - _collapsedWidth) < 0.5;
        if (!double.IsFinite(NotchBorder.Width))
            Motion.Stop(NotchBorder, WidthProperty, NotchBorder.ActualWidth > 0 ? NotchBorder.ActualWidth : _collapsedWidth);
        Motion.To(NotchBorder, WidthProperty, targetWidth, returningToRest ? Motion.PopBack : Motion.Pop, done: () =>
        {
            if (version != _compactWidthAnimationVersion) return;
            // A pill's width belongs to the closed notch. Once the notch is open (a click right as the pill
            // arrived), committing it would cancel the open notch's size and leave it pill-wide.
            if (_isExpanded) return;
            if (token == 0 && !_compactPillArbiter.CanRestoreMedia(revision)) return;

            // Allow dismiss animations to finish post token release, but prevent
            // expansions from committing if preempted by another notification.
            bool canCommitTarget = token == 0
                || _compactPillArbiter.IsTokenCurrent(token)
                || (returningToRest && _compactPillArbiter.CanRestoreMedia(revision + 1));
            if (canCommitTarget && Math.Abs(NotchBorder.Width - targetWidth) < .5)
                Motion.Stop(NotchBorder, WidthProperty, targetWidth);
        });
    }

    private bool IsCompactSlotStale(int token) => !_compactPillArbiter.IsTokenCurrent(token);
}
