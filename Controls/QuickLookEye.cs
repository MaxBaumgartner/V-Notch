using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using VNotch.Services;

namespace VNotch.Controls;

public sealed class QuickLookEye : Button
{
    private static readonly Geometry Eye = CreateGeometry(
        "M2,12 C2,12 5.5,6 12,6 S22,12 22,12 18.5,18 12,18 2,12 2,12 Z "
        + "M14.5,12 A2.5,2.5 0 1 1 9.5,12 A2.5,2.5 0 1 1 14.5,12");
    private static readonly Brush Ink = Frozen(Color.FromRgb(0xD4, 0xD5, 0xD9));
    private static readonly Brush Hover = Frozen(Color.FromArgb(0x12, 255, 255, 255));
    private static readonly ControlTemplate Chrome = BuildTemplate();
    private readonly QuickLookBridge _bridge;

    public static readonly DependencyProperty HostProperty = DependencyProperty.Register(nameof(Host),
        typeof(FrameworkElement), typeof(QuickLookEye), new PropertyMetadata(null, HostChanged));
    public static readonly DependencyProperty EligibleProperty = DependencyProperty.Register(nameof(Eligible),
        typeof(bool), typeof(QuickLookEye), new PropertyMetadata(true, StateChanged));
    public static readonly DependencyProperty SelectedProperty = DependencyProperty.Register(nameof(Selected),
        typeof(bool), typeof(QuickLookEye), new PropertyMetadata(false, StateChanged));
    public static readonly DependencyProperty ActiveProperty = DependencyProperty.Register(nameof(Active),
        typeof(bool), typeof(QuickLookEye), new PropertyMetadata(false, StateChanged));
    public static readonly DependencyProperty PreviewKeyProperty = DependencyProperty.Register(nameof(PreviewKey),
        typeof(object), typeof(QuickLookEye), new PropertyMetadata(null, StateChanged));

    public FrameworkElement? Host { get => (FrameworkElement?)GetValue(HostProperty); set => SetValue(HostProperty, value); }
    public bool Eligible { get => (bool)GetValue(EligibleProperty); set => SetValue(EligibleProperty, value); }
    public bool Selected { get => (bool)GetValue(SelectedProperty); set => SetValue(SelectedProperty, value); }
    public bool Active { get => (bool)GetValue(ActiveProperty); set => SetValue(ActiveProperty, value); }
    public object? PreviewKey { get => GetValue(PreviewKeyProperty); set => SetValue(PreviewKeyProperty, value); }
    internal object? PreviewOwner { get; init; }

    public QuickLookEye() : this(QuickLookBridge.Shared) { }

    internal QuickLookEye(QuickLookBridge bridge)
    {
        _bridge = bridge;
        Width = 29;
        Height = 27;
        Background = Brushes.Transparent;
        Template = Chrome;
        Cursor = Cursors.Hand;
        Opacity = 0;
        Visibility = Visibility.Collapsed;
        IsHitTestVisible = false;
        ToolTip = "Preview file";
        AutomationProperties.SetName(this, "Preview file");
        Content = new Viewbox
        {
            Width = 17, Height = 17, IsHitTestVisible = false,
            Child = new Canvas
            {
                Width = 24, Height = 24,
                Children = { new Path
                {
                    Data = Eye, Stroke = Ink, StrokeThickness = 1.65,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round
                } }
            }
        };
        Loaded += (_, _) =>
        {
            _bridge.Changed += OnBridgeChanged;
            Track(Host, true);
            _ = _bridge.RefreshAvailabilityAsync();
            Update(animate: false);
        };
        Unloaded += (_, _) =>
        {
            _bridge.Changed -= OnBridgeChanged;
            Track(Host, false);
            Motion.Stop(this, OpacityProperty, 0);
        };
        IsVisibleChanged += (_, _) => Update(animate: false);
        IsKeyboardFocusWithinChanged += (_, _) => Update();
        MouseEnter += (_, _) => Update();
        MouseLeave += (_, _) => Update();
    }

    private static void HostChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        var eye = (QuickLookEye)target;
        if (eye.IsLoaded)
        {
            eye.Track(e.OldValue as FrameworkElement, false);
            eye.Track(e.NewValue as FrameworkElement, true);
        }
        eye.Update();
    }

    private static void StateChanged(DependencyObject target, DependencyPropertyChangedEventArgs e) =>
        ((QuickLookEye)target).Update();

    private void Track(FrameworkElement? host, bool attach)
    {
        if (host == null) return;
        if (attach)
        {
            host.MouseEnter += HostMouseEnter;
            host.MouseLeave += HostMouseLeave;
            host.IsKeyboardFocusWithinChanged += HostChanged;
        }
        else
        {
            host.MouseEnter -= HostMouseEnter;
            host.MouseLeave -= HostMouseLeave;
            host.IsKeyboardFocusWithinChanged -= HostChanged;
        }
    }

    private void HostChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && Eligible) _ = _bridge.RefreshAvailabilityAsync();
        Update();
    }

    private void HostMouseEnter(object sender, MouseEventArgs e)
    {
        if (Eligible) _ = _bridge.RefreshAvailabilityAsync();
        Update();
    }

    private void HostMouseLeave(object sender, MouseEventArgs e) => Update();

    private void OnBridgeChanged()
    {
        if (Dispatcher.CheckAccess()) Update();
        else _ = Dispatcher.BeginInvoke(() => { if (IsLoaded) Update(); });
    }

    private bool _shown;
    private void Update(bool animate = true)
    {
        if (PreviewKey is { } key)
        {
            bool active = _bridge.HasPreviewKey(key, PreviewOwner);
            if (Active != active) SetCurrentValue(ActiveProperty, active);
        }
        bool offered = Eligible && _bridge.IsEnabled && _bridge.IsAvailable;
        Visibility = offered ? Visibility.Visible : Visibility.Collapsed;
        bool shown = IsVisible && (Active || Selected || IsKeyboardFocusWithin || Host?.IsMouseOver == true ||
            Host?.IsKeyboardFocusWithin == true);
        IsHitTestVisible = shown;
        Background = IsMouseOver || Active ? Hover : Brushes.Transparent;
        if (!IsVisible)
        {
            _shown = false;
            Motion.Stop(this, OpacityProperty, 0);
        }
        else if (!animate)
        {
            _shown = shown;
            Motion.Stop(this, OpacityProperty, shown ? 1 : 0);
        }
        else if (shown != _shown)
        {
            _shown = shown;
            Motion.To(this, OpacityProperty, shown ? 1 : 0, shown ? Motion.PressDown : Motion.Exit);
        }
    }

    private static Geometry CreateGeometry(string path)
    {
        var geometry = Geometry.Parse(path);
        geometry.Freeze();
        return geometry;
    }

    private static ControlTemplate BuildTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);
        return new ControlTemplate(typeof(Button)) { VisualTree = border };
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
