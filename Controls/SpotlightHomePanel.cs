using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.Spotlight;

namespace VNotch.Controls;

/// <summary>
/// The empty Spotlight when it stands in for the Start menu: a grid of apps and a row of the apps running
/// in the background (their tray icons). Built in code like the weather view.
/// </summary>
internal sealed class SpotlightHomePanel : StackPanel
{
    private const int Columns = 7;
    private static readonly Brush LabelBrush = Frozen(Color.FromArgb(0x7A, 0xFF, 0xFF, 0xFF));
    private static readonly Brush NameBrush = Frozen(Color.FromArgb(0xD8, 0xFF, 0xFF, 0xFF));
    private static readonly Brush HoverBrush = Frozen(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF));
    private static readonly Brush ChipBrush = Frozen(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF));
    private static readonly Brush ChipHoverBrush = Frozen(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));

    private readonly TextBlock _appsLabel = Label();
    private readonly TextBlock _trayLabel = Label();
    private readonly UniformGrid _apps = new() { Columns = Columns, Margin = new Thickness(0, 0, 0, 4) };
    private readonly WrapPanel _tray = new() { Margin = new Thickness(2, 0, 0, 0) };

    internal event Action<SpotlightSearchItem>? AppActivated;
    /// <summary>A right-click on an app: Spotlight shows the same menu as for a result (open, as admin, uninstall …).</summary>
    internal event Action<SpotlightSearchItem, FrameworkElement>? AppMenuRequested;
    internal event Action<string>? TrayActivated;

    internal bool HasContent => _apps.Children.Count > 0 || _tray.Children.Count > 0;

    public SpotlightHomePanel()
    {
        Margin = new Thickness(14, 4, 14, 8);
        Children.Add(_appsLabel);
        Children.Add(_apps);
        Children.Add(_trayLabel);
        Children.Add(_tray);
        RefreshLocalization();
        SetApps(Array.Empty<SpotlightSearchItem>());
        SetTray(Array.Empty<TrayApp>());
    }

    internal void RefreshLocalization()
    {
        var culture = Loc.GetCulture();
        _appsLabel.Text = Loc.Get("spotlight.home.apps").ToUpper(culture);
        _trayLabel.Text = Loc.Get("spotlight.home.background").ToUpper(culture);
    }

    internal void SetApps(IReadOnlyList<SpotlightSearchItem> apps)
    {
        _apps.Children.Clear();
        foreach (var app in apps) _apps.Children.Add(AppTile(app));
        // Short lists keep their tiles at grid width instead of stretching across the panel.
        _apps.Columns = Columns;
        _apps.Visibility = _appsLabel.Visibility = apps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    internal void SetTray(IReadOnlyList<TrayApp> tray)
    {
        _tray.Children.Clear();
        foreach (var app in tray) _tray.Children.Add(TrayChip(app));
        _tray.Visibility = _trayLabel.Visibility = tray.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Tiles rise in one after another, a little Apple-style cascade.</summary>
    internal void PlayReveal()
    {
        if (AnimationConfig.ReduceMotion) return;
        int index = 0;
        foreach (var element in _apps.Children.OfType<FrameworkElement>().Concat(_tray.Children.OfType<FrameworkElement>()))
        {
            var begin = TimeSpan.FromMilliseconds(Math.Min(index++, 16) * 18);
            var move = element.RenderTransform as TranslateTransform ?? new TranslateTransform();
            element.RenderTransform = move;
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { BeginTime = begin, EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
            var rise = new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(420)) { BeginTime = begin, EasingFunction = new DampedSpringEase(0.78) };
            Timeline.SetDesiredFrameRate(fade, AnimationConfig.TargetFps);
            Timeline.SetDesiredFrameRate(rise, AnimationConfig.TargetFps);
            element.Opacity = 0;
            element.BeginAnimation(OpacityProperty, fade);
            move.BeginAnimation(TranslateTransform.YProperty, rise);
        }
    }

    private FrameworkElement AppTile(SpotlightSearchItem app)
    {
        var icon = new Image
        {
            Width = 38,
            Height = 38,
            Source = app.Icon,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        var name = new TextBlock
        {
            Text = app.DisplayTitle,
            Foreground = NameBrush,
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(4, 6, 4, 0),
        };
        var surface = new Border
        {
            CornerRadius = new CornerRadius(14),
            Background = Brushes.Transparent,
            Padding = new Thickness(2, 9, 2, 7),
            Margin = new Thickness(2),
            Cursor = Cursors.Hand,
            Child = new StackPanel { Children = { icon, name } },
            ToolTip = app.DisplayTitle,
        };
        AutomationName(surface, app.DisplayTitle);
        WireClick(surface, HoverBrush, Brushes.Transparent, () => AppActivated?.Invoke(app));
        surface.MouseRightButtonUp += (_, e) =>
        {
            AppMenuRequested?.Invoke(app, surface);
            e.Handled = true;
        };
        // Icons hydrate after the tile exists; follow late arrivals.
        app.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SpotlightSearchItem.Icon)) icon.Source = app.Icon;
        };
        return surface;
    }

    private FrameworkElement TrayChip(TrayApp app)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (app.Icon != null)
        {
            var icon = new Image { Width = 16, Height = 16, Source = app.Icon, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
            RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
            row.Children.Add(icon);
        }
        row.Children.Add(new TextBlock
        {
            Text = app.DisplayName,
            Foreground = NameBrush,
            FontSize = 11.5,
            MaxWidth = 150,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var chip = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = ChipBrush,
            Padding = new Thickness(9, 5, 10, 5),
            Margin = new Thickness(0, 0, 6, 6),
            Cursor = Cursors.Hand,
            Child = row,
        };
        AutomationName(chip, app.DisplayName);
        WireClick(chip, ChipHoverBrush, ChipBrush, () => TrayActivated?.Invoke(app.Name));
        return chip;
    }

    private static void WireClick(Border surface, Brush hover, Brush rest, Action activate)
    {
        bool pressed = false;
        surface.MouseEnter += (_, _) => surface.Background = hover;
        surface.MouseLeave += (_, _) => { surface.Background = rest; pressed = false; };
        surface.MouseLeftButtonDown += (_, e) =>
        {
            pressed = true;
            ButtonFeedback.PlayButtonPressAnimation(surface);
            e.Handled = true;
        };
        surface.MouseLeftButtonUp += (_, e) =>
        {
            if (!pressed) return;
            pressed = false;
            e.Handled = true;
            activate();
        };
    }

    private static void AutomationName(UIElement element, string name) =>
        System.Windows.Automation.AutomationProperties.SetName(element, name);

    private static TextBlock Label() => new()
    {
        Foreground = LabelBrush,
        FontSize = 10.5,
        Margin = new Thickness(6, 8, 0, 6),
    };

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
