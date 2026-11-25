using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using VNotch.Services;

namespace VNotch.Presenters;

// A separate layout layer keeps page-specific transforms intact while the whole shell squeezes.
internal sealed class NotchShellAttachment : IDisposable
{
    private readonly FrameworkElement _shell, _content;
    private readonly FrameworkElement[] _compact;
    private readonly ScaleTransform _scale = new();
    private readonly DependencyPropertyDescriptor _width, _height;
    private readonly Func<double> _closedHeight;
    private double _openWidth, _openHeight;
    internal bool IsAttached { get; private set; }

    internal NotchShellAttachment(FrameworkElement shell, FrameworkElement content,
        Func<double> closedHeight, params FrameworkElement[] compact)
    {
        _shell = shell;
        _content = content;
        _compact = compact;
        _closedHeight = closedHeight;
        content.RenderTransformOrigin = new Point(.5, 0);
        content.RenderTransform = _scale;
        _width = DependencyPropertyDescriptor.FromProperty(FrameworkElement.WidthProperty, shell.GetType());
        _height = DependencyPropertyDescriptor.FromProperty(FrameworkElement.HeightProperty, shell.GetType());
    }

    internal void Attach(double width, double height)
    {
        // Reversals retain the same coordinate space, including the partially squeezed frame.
        if (IsAttached) return;
        IsAttached = true;
        _openWidth = width;
        _openHeight = height;
        _content.Width = width;
        _content.Height = height;
        _content.HorizontalAlignment = HorizontalAlignment.Center;
        _content.VerticalAlignment = VerticalAlignment.Top;
        foreach (var compact in _compact) Motion.Stop(compact, UIElement.OpacityProperty);
        _width.AddValueChanged(_shell, Render);
        _height.AddValueChanged(_shell, Render);
        Render(null, EventArgs.Empty);
    }

    private void Render(object? sender, EventArgs args)
    {
        double p = Math.Clamp((_shell.Height - _closedHeight()) / Math.Max(1, _openHeight - _closedHeight()), 0, 1);
        _scale.ScaleX = Math.Clamp(_shell.Width / _openWidth, .04, 1.3);
        _scale.ScaleY = Math.Clamp(_shell.Height / _openHeight, .04, 1.3);
        _content.Opacity = Math.Clamp((p - .08) * 2.2, 0, 1);
        foreach (var compact in _compact) compact.Opacity = Math.Clamp(1 - 4 * p, 0, 1);
    }

    internal void SetOpenHeight(double height)
    {
        if (!IsAttached || _openHeight == height) return;
        _openHeight = _content.Height = height;
        Render(null, EventArgs.Empty);
    }

    internal void Detach(bool open)
    {
        if (!IsAttached) return;
        _width.RemoveValueChanged(_shell, Render);
        _height.RemoveValueChanged(_shell, Render);
        IsAttached = false;
        _scale.ScaleX = _scale.ScaleY = 1;
        _content.Width = _content.Height = double.NaN;
        _content.HorizontalAlignment = HorizontalAlignment.Stretch;
        _content.VerticalAlignment = VerticalAlignment.Stretch;
        _content.Opacity = open ? 1 : 0;
        foreach (var compact in _compact) compact.Opacity = open ? 0 : 1;
    }

    public void Dispose() => Detach(false);
}
