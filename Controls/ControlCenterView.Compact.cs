using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using VNotch.Services;

namespace VNotch.Controls;

public partial class ControlCenterView
{
    private static readonly Brush CompactInk = Frozen("#F5F5F7");
    private static readonly Brush CompactLabel = Frozen("#94FFFFFF");
    private static readonly Brush CompactTrack = Frozen("#1AFFFFFF");
    private static readonly FontFamily FluentFont = new("Segoe Fluent Icons");
    private readonly StackPanel _compactPage = new();
    private readonly StackPanel _drawerBody = new();
    private Border? _drawerClip;
    private WeatherTween? _drawerHeight;
    private bool _drawerOpen, _drawerDragging, _drawerWasOpen;
    private double _drawerStartHeight;
    private double _drawerExtent, _drawerWheelTarget, _drawerWheelVelocity;
    private bool _drawerWheelActive, _drawerWheelSettling;
    private TimeSpan? _drawerWheelFrame;
    private TimeSpan _drawerWheelIdle;
    internal bool IsLayoutFollowing { get; private set; }
    internal bool DrawerOpen => _drawerOpen;
    internal double DrawerHeight => _drawerClip?.Height ?? 0;
    internal bool DismissDrawer()
    {
        if (!_drawerOpen && !_drawerWheelActive && !_drawerDragging && DrawerHeight <= 0) return false;
        SetDrawerOpen(false);
        return true;
    }
    internal event Action? StatusChanged;
    internal string CompactStatus { get; private set; } = "";

    private void SetCompactStatus(string text)
    {
        if (text == CompactStatus) return;
        CompactStatus = text;
        StatusChanged?.Invoke();
    }

    private void MountCompactPage()
    {
        if (ControlRoot.Children.Contains(_compactPage)) return;
        ControlRoot.Children.Clear();
        ControlRoot.Children.Add(_compactPage);
        _compactPage.Margin = new Thickness(0, 3, 0, 0);
        if (Page == ControlCenterPage.Pi)
            _drawerBody.IsVisibleChanged += (_, _) => { if (_drawerBody.IsVisible && _home is { } home) UpdateHome(home); };
        ControlScrollViewer.PreviewMouseWheel += (_, e) =>
        {
            if (_drawerClip == null || e.Handled || e.Delta == 0 || Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
            if (Page != ControlCenterPage.Pi)
            {
                // The other pages' sliders take the wheel themselves. A Run inside a label needs its logical parent.
                for (var source = e.OriginalSource as DependencyObject; source != null && source != ControlScrollViewer;
                     source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source))
                    if (source is FrameworkElement element && !ReferenceEquals(element, _drawerGrabber) &&
                        element.ReadLocalValue(CursorProperty) is Cursor cursor && (cursor == Cursors.SizeWE || cursor == Cursors.SizeNS)) return;
            }
            // A long drawer keeps scrolling until it reaches its upper boundary.
            if (_drawerOpen && !IsLayoutFollowing && ControlScrollViewer.ScrollableHeight > 0 &&
                (e.Delta < 0 || ControlScrollViewer.VerticalOffset > .5)) return;
            ScrubDrawer(e.Delta);
            e.Handled = true;
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible || _drawerHeight == null) return;
            StopDrawerWheel();
            _drawerDragging = false;
            IsLayoutFollowing = false;
            // Out of sight means done with it: the tab opens closed again next time, at its compact size.
            SetDrawerDestination(false);
            Motion.Stop(_drawerHeight, WeatherTween.ValueProperty, 0);
        };
    }

    private void AddDrawer(FrameworkElement body, bool replaceHomeFace = false)
    {
        // A rebuild commits the click destination and keeps an active wheel gesture continuous.
        double previousHeight = DrawerHeight;
        if (_drawerHeight != null) Motion.Stop(_drawerHeight, WeatherTween.ValueProperty);
        _drawerDragging = false;
        IsLayoutFollowing = _drawerWheelActive;
        if (_drawerClip != null) { DetachDrawerBody(); _compactPage.Children.Remove(_drawerClip); }
        if (_drawerGrabber != null) _compactPage.Children.Remove(_drawerGrabber);
        _drawerBody.Children.Clear();
        _drawerBody.Children.Add(body);
        _drawerBody.Margin = replaceHomeFace ? new Thickness() : new Thickness(0, 10, 0, 3);
        // The clip reaches DrawerBleed px past the page on both sides (the hairline and rows stay in place), so a
        // row or button at the edge that rises under the pointer keeps its sides.
        _drawerFrame = new Border { Margin = new Thickness(DrawerBleed, 0, DrawerBleed, 0), Child = _drawerBody,
            BorderBrush = Frozen("#14FFFFFF"), BorderThickness = replaceHomeFace ? new Thickness() : new Thickness(0, 1, 0, 0) };
        _drawerClip = new Border { Height = 0, ClipToBounds = true, Child = _drawerFrame,
            Margin = new Thickness(-DrawerBleed, 0, -DrawerBleed, 0) };
        _drawerHeight = new WeatherTween { Step = height => PaintDrawer(height, replaceHomeFace) };
        _compactPage.Children.Add(_drawerClip);
        _drawerGrabber = DrawerGrabber();
        _compactPage.Children.Add(_drawerGrabber);
        _drawerExtent = MeasureDrawer();
        if (_drawerWheelActive)
            _drawerWheelTarget = _drawerWheelSettling ? (_drawerOpen ? _drawerExtent : 0) : Math.Min(_drawerWheelTarget, _drawerExtent);
        double height = IsLayoutFollowing ? Math.Min(previousHeight, _drawerExtent) : _drawerOpen ? _drawerExtent : 0;
        _drawerHeight.Value = height;
        if (height == 0) PaintDrawer(0, replaceHomeFace);
    }

    private void PaintDrawer(double height, bool replaceHomeFace)
    {
        _drawerClip!.Height = Math.Max(0, height);
        _drawerClip.IsHitTestVisible = height > 1;
        _drawerBody.Visibility = height > 0 ? Visibility.Visible : Visibility.Hidden;
        if (replaceHomeFace) RevealHomeDetails(_drawerExtent > 0 ? Math.Clamp(height / _drawerExtent, 0, 1) : 0);
        ContentHeightChanged?.Invoke();
    }

    private FrameworkElement? _drawerGrabber;
    private Border? _drawerFrame;
    private const double DrawerBleed = 6;

    private void DetachDrawerBody()
    {
        if (_drawerFrame != null) _drawerFrame.Child = null;
        if (_drawerClip != null) _drawerClip.Child = null;
        _drawerFrame = null;
    }

    private void RemoveCompactDrawer()
    {
        StopDrawerWheel();
        if (_drawerHeight != null) Motion.Stop(_drawerHeight, WeatherTween.ValueProperty, 0);
        if (_drawerClip != null) { DetachDrawerBody(); _compactPage.Children.Remove(_drawerClip); }
        if (_drawerGrabber != null) _compactPage.Children.Remove(_drawerGrabber);
        _drawerBody.Children.Clear();
        _drawerClip = null; _drawerHeight = null; _drawerGrabber = null;
        _drawerOpen = false;
        IsLayoutFollowing = false;
    }
    private double MeasureDrawer()
    {
        double width = ActualWidth > 0 ? ActualWidth : Page switch { ControlCenterPage.School => 398, ControlCenterPage.Pi => 558, ControlCenterPage.Roblox => 552, _ => 528 };
        if (_drawerFrame == null)
        {
            _drawerBody.Measure(new Size(width, double.PositiveInfinity));
            return _drawerBody.DesiredSize.Height + 1;
        }
        _drawerFrame.Measure(new Size(width + 2 * DrawerBleed, double.PositiveInfinity));
        return _drawerFrame.DesiredSize.Height;
    }

    private FrameworkElement DrawerGrabber()
    {
        bool home = Page == ControlCenterPage.Pi;
        double width = home ? 26 : 30;
        var line = new Border { Width = width, Height = home ? 3 : 4, CornerRadius = new CornerRadius(2),
            Background = Frozen(home ? "#38FFFFFF" : "#33FFFFFF") };
        var grabber = new Border { Height = home ? 13 : 17, Background = Brushes.Transparent, Cursor = Cursors.SizeNS,
            Child = line, Tag = "drawer-grabber" };
        grabber.MouseEnter += (_, _) => { Motion.To(line, WidthProperty, width + 8, Motion.Press); line.Background = Frozen("#80FFFFFF"); };
        grabber.MouseLeave += (_, _) => { Motion.To(line, WidthProperty, width, Motion.Press); line.Background = Frozen(home ? "#38FFFFFF" : "#33FFFFFF"); };
        grabber.IsVisibleChanged += (_, _) => { if (!grabber.IsVisible) Motion.Stop(line, WidthProperty, width); };
        Point start = new();
        DateTime sample = default;
        double lastY = 0, speed = 0;
        bool held = false, moved = false;
        grabber.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true; held = true; moved = false;
            start = e.GetPosition(this); sample = DateTime.UtcNow; lastY = start.Y; speed = 0;
            grabber.CaptureMouse();
        };
        grabber.MouseMove += (_, e) =>
        {
            if (!held) return;
            double y = e.GetPosition(this).Y, dy = y - start.Y;
            var now = DateTime.UtcNow;
            if ((now - sample).TotalSeconds is > 0 and var dt) speed = (y - lastY) / dt;
            sample = now; lastY = y;
            if (!moved && Math.Abs(dy) < 6) return;
            moved = true;
            MoveDrawer(dy, force: true); e.Handled = true;
        };
        grabber.MouseLeftButtonUp += (_, e) =>
        {
            if (!held) return;
            held = false;
            if (moved) ReleaseDrawer((DateTime.UtcNow - sample).TotalMilliseconds < 60 ? speed : 0, false);
            else SetDrawerOpen(!(_drawerWheelActive ? _drawerWheelTarget > _drawerExtent / 2 : _drawerOpen));
            grabber.ReleaseMouseCapture(); e.Handled = true;
        };
        grabber.LostMouseCapture += (_, _) => { if (held && moved) ReleaseDrawer(0, true); held = false; };
        return grabber;
    }

    internal void SetDrawerOpen(bool open, double? velocity = null)
    {
        if (_drawerHeight == null) return;
        if (_drawerWheelActive) velocity ??= _drawerWheelVelocity;
        StopDrawerWheel();
        _drawerDragging = false;
        SetDrawerDestination(open);
        _drawerExtent = MeasureDrawer();
        double target = open ? _drawerExtent : 0;
        if (!IsVisible || AnimationConfig.ReduceMotion)
        {
            Motion.Stop(_drawerHeight, WeatherTween.ValueProperty, target);
            IsLayoutFollowing = false;
            ContentHeightChanged?.Invoke();
            return;
        }
        IsLayoutFollowing = true;
        Motion.To(_drawerHeight, WeatherTween.ValueProperty, target, Motion.Shift,
            velocity: velocity, done: () => { IsLayoutFollowing = false; ContentHeightChanged?.Invoke(); });
    }

    private void SetDrawerDestination(bool open)
    {
        _drawerOpen = open;
        if (Page == ControlCenterPage.System) UpdatePeakTimer();
        if (Page == ControlCenterPage.Pi) UpdateHomeDetailsButton();
    }

    internal void ScrubDrawer(int delta)
    {
        if (_drawerHeight == null || _drawerDragging || !IsVisible || delta == 0) return;
        if (AnimationConfig.ReduceMotion)
        {
            SetDrawerOpen(delta < 0);
            return;
        }
        if (!_drawerWheelActive)
        {
            _drawerExtent = MeasureDrawer();
            _drawerWheelTarget = DrawerHeight;
            double target = Math.Clamp(_drawerWheelTarget - delta * .55, 0, _drawerExtent);
            if (target == _drawerWheelTarget) return;
            _drawerWheelVelocity = Motion.VelocityOf(_drawerHeight, WeatherTween.ValueProperty);
            Motion.Stop(_drawerHeight, WeatherTween.ValueProperty, DrawerHeight);
            _drawerWheelActive = true;
            CompositionTarget.Rendering += RenderDrawerWheel;
        }
        else if (_drawerWheelSettling)
        {
            _drawerWheelTarget = DrawerHeight;
        }
        _drawerWheelTarget = Math.Clamp(_drawerWheelTarget - delta * .55, 0, _drawerExtent);
        _drawerWheelIdle = TimeSpan.Zero;
        _drawerWheelSettling = false;
        IsLayoutFollowing = true;
    }

    private void RenderDrawerWheel(object? sender, EventArgs e)
    {
        if (e is not RenderingEventArgs frame || _drawerWheelFrame == frame.RenderingTime) return;
        var elapsed = _drawerWheelFrame is { } previous ? frame.RenderingTime - previous : TimeSpan.Zero;
        _drawerWheelFrame = frame.RenderingTime;
        AdvanceDrawerWheel(elapsed);
    }

    internal void AdvanceDrawerWheel(TimeSpan elapsed)
    {
        if (!_drawerWheelActive || _drawerHeight == null || elapsed <= TimeSpan.Zero) return;
        if (AnimationConfig.ReduceMotion) { SetDrawerOpen(_drawerWheelTarget >= _drawerExtent / 2); return; }
        _drawerWheelIdle += elapsed;
        double seconds = Math.Min(elapsed.TotalSeconds, .05);
        if (!_drawerWheelSettling && _drawerWheelIdle >= Motion.DrawerWheelIdle)
        {
            // Integrate up to the precise idle deadline before changing the destination.
            double remaining = Math.Min(seconds, (_drawerWheelIdle - Motion.DrawerWheelIdle).TotalSeconds);
            StepDrawerWheel(seconds - remaining);
            seconds = remaining;
            bool open = DrawerHeight + .12 * _drawerWheelVelocity >= _drawerExtent / 2;
            SetDrawerDestination(open);
            _drawerWheelTarget = open ? _drawerExtent : 0;
            _drawerWheelSettling = true;
        }
        StepDrawerWheel(seconds);
        if (!_drawerWheelSettling || Math.Abs(DrawerHeight - _drawerWheelTarget) > .025 || Math.Abs(_drawerWheelVelocity) > .3) return;
        _drawerHeight.Value = _drawerWheelTarget;
        StopDrawerWheel();
        IsLayoutFollowing = false;
        ContentHeightChanged?.Invoke();
        if (Page == ControlCenterPage.System) RefitDrawer();
    }

    private void StepDrawerWheel(double seconds)
    {
        if (seconds <= 0) return;
        var (position, velocity) = Motion.FollowCritical(DrawerHeight, _drawerWheelVelocity, _drawerWheelTarget, seconds, Motion.DrawerScrub);
        double visible = Math.Clamp(position, 0, _drawerExtent);
        _drawerWheelVelocity = visible == position ? velocity : 0;
        _drawerHeight!.Value = visible;
    }

    private void StopDrawerWheel()
    {
        if (!_drawerWheelActive) return;
        CompositionTarget.Rendering -= RenderDrawerWheel;
        _drawerWheelActive = _drawerWheelSettling = false;
        _drawerWheelFrame = null;
    }

    internal bool MoveDrawer(double dy, bool force = false)
    {
        if (_drawerHeight == null) return false;
        if (!_drawerDragging)
        {
            if (!force && !(_drawerOpen && dy < 0 || !_drawerOpen && dy > 0)) return false;
            _drawerDragging = true; _drawerWasOpen = _drawerOpen;
            _drawerStartHeight = DrawerHeight;
            StopDrawerWheel();
            Motion.Stop(_drawerHeight, WeatherTween.ValueProperty);
        }
        double limit = MeasureDrawer(), height = _drawerStartHeight + dy;
        if (height < 0) height = 0;
        if (height > limit) height = limit + (1 - 1 / ((height - limit) * .55 / 30 + 1)) * 30;
        IsLayoutFollowing = true;
        Motion.Stop(_drawerHeight, WeatherTween.ValueProperty, height);
        return true;
    }

    internal bool ReleaseDrawer(double velocity, bool canceled)
    {
        if (!_drawerDragging) return false;
        _drawerDragging = false;
        double distance = DrawerHeight - _drawerStartHeight;
        bool open = canceled ? _drawerWasOpen : _drawerWasOpen
            ? !(distance < -28 || velocity < -320) : distance > 28 || velocity > 320;
        SetDrawerOpen(open, velocity);
        return true;
    }

    private TextBlock CompactText(string text, double size, Brush foreground, double maxWidth = double.PositiveInfinity)
    {
        var label = MakeLabel(text, size, foreground, maxWidth);
        label.FontWeight = FontWeights.Normal;
        return label;
    }

    private TextBlock CompactGlyph(int code, Brush? ink = null) => new()
    {
        Text = Glyph(code), FontFamily = FluentFont, FontSize = 16, Foreground = ink ?? CompactLabel,
        Width = 18, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center
    };

    private Border CompactPill(string text, Action click)
    {
        var label = CompactText(text, 11, CompactInk);
        // Centred, not stretched: in a 29 px row a stretched pill touched the pills of the rows above and below.
        var button = new Border { CornerRadius = new CornerRadius(10), Background = CompactTrack,
            Padding = new Thickness(9, 4, 9, 4), Cursor = Cursors.Hand, Child = label, HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center };
        Pressable(button, () => CompactTrack, CardHover);
        button.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
        return button;
    }

    private Grid CompactLine(int glyph, string name, FrameworkElement? right = null)
    {
        var row = new Grid { MinHeight = 29, Background = Brushes.Transparent, Tag = "drawer-row" };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(25) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(CompactGlyph(glyph));
        var label = CompactText(name, 12, CompactInk); Grid.SetColumn(label, 1); row.Children.Add(label);
        if (right != null)
        {
            if (right.ReadLocalValue(VerticalAlignmentProperty) == DependencyProperty.UnsetValue) right.VerticalAlignment = VerticalAlignment.Center;
            // The value never runs into the name.
            if (right.ReadLocalValue(MarginProperty) == DependencyProperty.UnsetValue) right.Margin = new Thickness(10, 0, 0, 0);
            Grid.SetColumn(right, 2); row.Children.Add(right);
        }
        return row;
    }

    private static Grid CompactColumns(FrameworkElement left, FrameworkElement right)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(left); Grid.SetColumn(right, 2); grid.Children.Add(right);
        return grid;
    }

    private Grid ThinBar(double value, Brush color, Action<double> changed)
    {
        var fill = new ScaleTransform(Math.Clamp(value, 0, 1), 1);
        var track = new Grid { Height = 6, IsHitTestVisible = false, Children =
        {
            new Border { Background = CompactTrack, CornerRadius = new CornerRadius(3) },
            new Border { Background = color, CornerRadius = new CornerRadius(3), RenderTransform = fill,
                RenderTransformOrigin = new Point(0, .5) }
        } };
        var hit = new Grid { Height = 20, MinWidth = 45, Background = Brushes.Transparent, Cursor = Cursors.SizeWE, Children = { track }, Tag = fill };
        bool held = false;
        void Set(MouseEventArgs e) { double p = Math.Clamp(e.GetPosition(hit).X / Math.Max(1, hit.ActualWidth), 0, 1); Motion.Stop(fill, ScaleTransform.ScaleXProperty, p); changed(p); }
        hit.MouseEnter += (_, _) => Motion.To(track, HeightProperty, 8, Motion.PressDown);
        hit.MouseLeave += (_, _) => Motion.To(track, HeightProperty, 6, Motion.PressDown);
        hit.MouseLeftButtonDown += (_, e) => { held = true; hit.CaptureMouse(); Set(e); e.Handled = true; };
        hit.MouseMove += (_, e) => { if (held) { Set(e); e.Handled = true; } };
        hit.MouseLeftButtonUp += (_, e) => { if (!held) return; Set(e); held = false; hit.ReleaseMouseCapture(); e.Handled = true; };
        hit.LostMouseCapture += (_, _) => held = false;
        hit.IsVisibleChanged += (_, _) => { if (!hit.IsVisible) { Motion.Stop(fill, ScaleTransform.ScaleXProperty); Motion.Stop(track, HeightProperty, 6); } };
        return hit;
    }

    private void FoldInline(Border clip, bool open, bool animate)
    {
        double width = ActualWidth > 0 ? ActualWidth : 398;
        clip.Child.Measure(new Size(width, double.PositiveInfinity));
        double target = open ? clip.Child.DesiredSize.Height : 0;
        clip.Tag = target;
        var tween = HeightTweens.GetValue(clip, _ =>
        {
            var created = new WeatherTween { Value = clip.Height, Step = h =>
            {
                clip.Height = Math.Max(0, h);
                if (!clip.IsDescendantOf(this) || _disposed) return;
                if (_drawerOpen && !_drawerDragging && _drawerHeight != null)
                    Motion.Stop(_drawerHeight, WeatherTween.ValueProperty, MeasureDrawer());
                else ContentHeightChanged?.Invoke();
            } };
            clip.IsVisibleChanged += (_, _) =>
            {
                if (clip.IsVisible || clip.Tag is not double end) return;
                Motion.Stop(created, WeatherTween.ValueProperty, end);
                IsLayoutFollowing = false;
            };
            return created;
        });
        IsLayoutFollowing = true;
        if (!animate || !IsVisible || AnimationConfig.ReduceMotion)
        {
            Motion.Stop(tween, WeatherTween.ValueProperty, target);
            IsLayoutFollowing = false;
            ContentHeightChanged?.Invoke();
        }
        else Motion.To(tween, WeatherTween.ValueProperty, target, Motion.Shift,
            done: () => { IsLayoutFollowing = false; ContentHeightChanged?.Invoke(); });
    }

    private void SwapCompactLabel(Grid holder, string text, bool horizontal = true, int direction = 1, double size = 11, bool animate = true)
    {
        if (holder.Tag is string previous && previous == text) return;
        if (holder.Tag is not string)
            holder.IsVisibleChanged += (_, _) =>
            {
                if (holder.IsVisible) return;
                var labels = holder.Children.OfType<TextBlock>().ToArray();
                foreach (var item in labels)
                {
                    if (item.RenderTransform is TranslateTransform translation)
                    { Motion.Stop(translation, TranslateTransform.XProperty, 0); Motion.Stop(translation, TranslateTransform.YProperty, 0); }
                    if (item.Effect is System.Windows.Media.Effects.BlurEffect effect) Motion.Stop(effect, System.Windows.Media.Effects.BlurEffect.RadiusProperty, 0);
                    Motion.Stop(item, OpacityProperty, 1);
                    if (item != labels.LastOrDefault()) holder.Children.Remove(item);
                }
            };
        holder.Tag = text;
        var label = CompactText(text, size, CompactInk);
        label.VerticalAlignment = VerticalAlignment.Center;
        label.TextTrimming = TextTrimming.None;
        Typography.SetNumeralAlignment(label, FontNumeralAlignment.Tabular);
        var shift = new TranslateTransform();
        label.RenderTransform = shift;
        var blur = new System.Windows.Media.Effects.BlurEffect { Radius = 0, RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance };
        label.Effect = blur;
        var property = horizontal ? TranslateTransform.XProperty : TranslateTransform.YProperty;
        if (!animate || !holder.IsVisible || AnimationConfig.ReduceMotion)
        {
            holder.Children.Clear(); holder.Children.Add(label); return;
        }
        foreach (var old in holder.Children.OfType<TextBlock>().ToArray())
        {
            if (old.RenderTransform is TranslateTransform oldShift)
                Motion.To(oldShift, property, -direction * (horizontal ? 9 : 11), Motion.Exit);
            Motion.To(old, OpacityProperty, 0, Motion.Exit, done: () => holder.Children.Remove(old));
        }
        shift.SetValue(property, direction * (horizontal ? 9d : 11d));
        label.Opacity = 0; blur.Radius = 2;
        holder.Children.Add(label);
        Motion.To(shift, property, 0, Motion.Shift);
        Motion.To(label, OpacityProperty, 1, Motion.Exit);
        Motion.To(blur, System.Windows.Media.Effects.BlurEffect.RadiusProperty, 0, Motion.Exit);
    }
}
