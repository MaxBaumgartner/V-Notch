using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using VNotch.Controls;
using VNotch.Services;

namespace VNotch.Presenters;

// The face keeps its final layout while the shell carries it out of, and back into, the notch.
internal sealed class CompactSurfaceAttachment
{
    private readonly FrameworkElement _shell;
    private readonly GlassClipGrid _layer = new() { UseLayoutRounding = false, Opacity = 0 };
    private readonly ScaleTransform _scale = new();
    private readonly DependencyPropertyDescriptor _width, _height;
    private readonly Func<Size> _rest;
    private bool _attached, _watching;
    private int _generation;
    private double _opacityFloor;
    private Size _open;
    // A leaving face whose shell another owner grows again (the notch opening) never arrives; it ends hidden then.
    private double _leaveFromHeight = double.NaN;
    private Action? _finish;

    internal CompactSurfaceAttachment(FrameworkElement shell, FrameworkElement face, Func<Size> rest)
    {
        _shell = shell;
        _rest = rest;
        var parent = (Panel)face.Parent;
        int index = parent.Children.IndexOf(face);
        Panel.SetZIndex(_layer, Panel.GetZIndex(face));
        parent.Children.RemoveAt(index);
        _layer.Children.Add(face);
        parent.Children.Insert(index, _layer);
        _layer.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(face.Visibility)) { Source = face });
        _layer.RenderTransformOrigin = new Point(.5, 0);
        _layer.RenderTransform = _scale;
        _width = DependencyPropertyDescriptor.FromProperty(FrameworkElement.WidthProperty, shell.GetType());
        _height = DependencyPropertyDescriptor.FromProperty(FrameworkElement.HeightProperty, shell.GetType());
        _layer.IsVisibleChanged += (_, _) => Watch();
    }

    internal void Attach(double width, double height)
    {
        _attached = true;
        _open = new Size(width, height);
        _layer.Width = width;
        _layer.Height = height;
        _layer.HorizontalAlignment = HorizontalAlignment.Center;
        _layer.VerticalAlignment = VerticalAlignment.Top;
        Watch();
        Render(null, EventArgs.Empty);
    }

    internal void Detach(bool shown)
    {
        ++_generation;
        _leaveFromHeight = double.NaN;
        _finish = null;
        _attached = false;
        Watch();
        _layer.Width = _layer.Height = double.NaN;
        _layer.HorizontalAlignment = HorizontalAlignment.Stretch;
        _layer.VerticalAlignment = VerticalAlignment.Stretch;
        _scale.ScaleX = _scale.ScaleY = 1;
        _layer.Opacity = shown ? 1 : 0;
    }

    internal void MoveTo(double width, double height, SpringMotion spring, bool shown, Action? done = null)
    {
        // Expanding an already readable message must not dim it back to the closed-notch reveal threshold.
        _opacityFloor = shown ? _layer.Opacity : 0;
        if (shown) Attach(width, height);
        else if (!_attached) Attach(_shell.Width, _shell.Height);
        int generation = ++_generation;
        int remaining = 2;
        _leaveFromHeight = shown ? double.NaN : _shell.Height;
        _finish = () =>
        {
            if (generation != _generation) return;
            Detach(shown);
            done?.Invoke();
        };
        void Arrived()
        {
            if (generation != _generation || --remaining != 0) return;
            _finish?.Invoke();
        }
        Motion.To(_shell, FrameworkElement.WidthProperty, width, spring, done: Arrived);
        Motion.To(_shell, FrameworkElement.HeightProperty, height, spring, done: Arrived);
    }

    private void Watch()
    {
        bool watch = _attached && _layer.IsVisible;
        if (_watching == watch) return;
        _watching = watch;
        if (watch)
        {
            _width.AddValueChanged(_shell, Render);
            _height.AddValueChanged(_shell, Render);
            Render(null, EventArgs.Empty);
        }
        else
        {
            _width.RemoveValueChanged(_shell, Render);
            _height.RemoveValueChanged(_shell, Render);
        }
    }

    private void Render(object? sender, EventArgs args)
    {
        if (_shell.Height > _leaveFromHeight + 8)
        {
            _finish?.Invoke();
            return;
        }
        Size rest = _rest();
        // Some compact alerts only widen the notch, so width carries their reveal progress.
        double progress = Math.Abs(_open.Height - rest.Height) > .5
            ? (_shell.Height - rest.Height) / (_open.Height - rest.Height)
            : (_shell.Width - rest.Width) / Math.Max(1, _open.Width - rest.Width);
        _scale.ScaleX = Math.Clamp(_shell.Width / _open.Width, .04, 1.3);
        _scale.ScaleY = Math.Clamp(_shell.Height / _open.Height, .04, 1.3);
        _layer.Opacity = AnimationConfig.ReduceMotion ? 1 : Math.Max(_opacityFloor, Math.Clamp((progress - .08) * 2.2, 0, 1));
    }
}
