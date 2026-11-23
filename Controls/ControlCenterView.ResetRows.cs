using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using VNotch.Services;

namespace VNotch.Controls;

public partial class ControlCenterView
{
    // Keep the pill alive across usage/session rebuilds, including the in-flight second tap.
    private readonly Dictionary<string, ResetRow> _resetRows = new(StringComparer.Ordinal);
    private bool _rebuildingResetRows;

    private void OnResetMotionChanged() => Dispatcher.BeginInvoke(new Action(() =>
    {
        if (_disposed) return;
        StopClaudeFaceMotion();
        foreach (var row in _resetRows.Values)
        {
            row.Stop();
            if (IsVisible && Page == ControlCenterPage.Claude) row.Refresh();
        }
    }));

    private void AddResetRow(string provider, List<ResetOption> options)
    {
        var panel = new StackPanel { Margin = new Thickness(4, 8, 4, 0) };
        if (options.Count == 0 && !_resetRows.Keys.Any(k => k.StartsWith(provider + ":", StringComparison.Ordinal))) return;
        _claudeSection.Children.Add(panel);
        bool busy = _resetStates.Any(s => s.Key.StartsWith(provider + ":", StringComparison.Ordinal) && s.Value.Running);
        foreach (var option in options)
        {
            string key = provider + ":" + option.Id;
            if (!_resetRows.TryGetValue(key, out var row))
                _resetRows[key] = row = new ResetRow(this, provider, option);
            row.Option = option;
        }
        foreach (var pair in _resetRows.Where(r => r.Key.StartsWith(provider + ":", StringComparison.Ordinal)).ToList())
        {
            var row = pair.Value;
            if (row.Grid.Parent is Panel old) old.Children.Remove(row.Grid);
            panel.Children.Add(row.Grid);
            _resetStates.TryGetValue(pair.Key, out var state);
            bool present = options.Any(o => o.Id == row.Option.Id);
            row.Update(state, busy);
            if (!present && state == null) row.Collapse();
            else row.Restore();
        }
    }

    private sealed class ResetRow
    {
        private readonly ControlCenterView _view;
        private readonly string _provider;
        private readonly string _key;
        private readonly TextBlock _name, _expiry, _reason, _label, _oldLabel;
        private readonly Border _pill;
        private readonly Button _button;
        private readonly SolidColorBrush _fill = new(Colors.Transparent);
        private readonly SolidColorBrush _ink = new(Colors.White);
        private readonly ScaleTransform _scale;
        private readonly TranslateTransform _lift = new();
        private readonly ScaleTransform _remaining = new(1, 1);
        private readonly RotateTransform _spin = new();
        private readonly Border _progress;
        private readonly Path _spinner, _check;
        private readonly HashSet<(IAnimatable Target, DependencyProperty Property)> _clocks = new();
        private ResetState? _state;
        private string? _appearance;
        private bool _collapsing;

        public Grid Grid { get; }
        public ResetOption Option { get; set; }
        private bool OnScreen => !_view._disposed && _view.IsVisible && _view.Page == ControlCenterPage.Claude && Grid.IsVisible;
        private bool Animate => OnScreen && !AnimationConfig.ReduceMotion;
        private Brush Accent => _provider == "codex" ? CodexAccent : _view._moodInk;

        public ResetRow(ControlCenterView view, string provider, ResetOption option)
        {
            _view = view;
            _provider = provider;
            Option = option;
            _key = provider + ":" + option.Id;
            Grid = new Grid { Height = 32, Tag = "reset-row:" + _key };
            Grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "ResetName" });
            Grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "ResetExpiry" });
            Grid.ColumnDefinitions.Add(new ColumnDefinition());
            // Reserve the largest label so every pill keeps its right edge during a width spring.
            Grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "ResetAction", MinWidth = 92 });
            _name = view.QuietText(option.Name, LimitLabel);
            _name.FontSize = 11.5;
            _name.Margin = new Thickness(0, 0, 16, 0);
            _expiry = view.QuietText("", ChipSoft);
            _expiry.Margin = new Thickness(0, 0, 16, 0);
            _reason = view.QuietText("", ChipSoft);
            _reason.FontSize = 11;
            _reason.TextTrimming = TextTrimming.CharacterEllipsis;
            _reason.Margin = new Thickness(0, 0, 12, 0);
            Grid.Children.Add(_name);
            Grid.Children.Add(_expiry);
            Grid.Children.Add(_reason);
            System.Windows.Controls.Grid.SetColumn(_expiry, 1);
            System.Windows.Controls.Grid.SetColumn(_reason, 2);

            _label = view.QuietText("", _ink);
            _oldLabel = view.QuietText("", _ink);
            _label.HorizontalAlignment = _oldLabel.HorizontalAlignment = HorizontalAlignment.Center;
            _oldLabel.Opacity = 0;
            var content = new Grid { IsHitTestVisible = false };
            content.Children.Add(_oldLabel);
            content.Children.Add(_label);
            _progress = new Border
            {
                Height = 1.5, Margin = new Thickness(10, 0, 10, 3), CornerRadius = new CornerRadius(0.75),
                Background = _ink, VerticalAlignment = VerticalAlignment.Bottom, Opacity = 0,
                RenderTransform = _remaining, RenderTransformOrigin = new Point(0, 0.5)
            };
            content.Children.Add(_progress);
            _spinner = new Path
            {
                Data = Geometry.Parse("M 7,1 A 6,6 0 1 1 1,7"), Stroke = _ink, StrokeThickness = 1.5,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                Width = 14, Height = 14, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed,
                RenderTransform = _spin, RenderTransformOrigin = new Point(0.5, 0.5)
            };
            _check = new Path
            {
                Data = Geometry.Parse("M 1,7 L 5,11 L 13,2"), Stroke = _ink, StrokeThickness = 1.6,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
                StrokeDashArray = new DoubleCollection { 12, 12 }, Width = 14, Height = 14,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed
            };
            content.Children.Add(_spinner);
            content.Children.Add(_check);
            _button = new Button
            {
                Content = content, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch, Cursor = System.Windows.Input.Cursors.Hand,
                FontFamily = view.ControlFont, FocusVisualStyle = null
            };
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
            _button.Template = new ControlTemplate(typeof(Button)) { VisualTree = presenter };
            _button.Click += async (_, _) => await view.RedeemReset(provider, Option.Id);
            _pill = new Border
            {
                Child = _button, Background = _fill, CornerRadius = new CornerRadius(12), Height = 24, Width = 82,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = new Point(0.5, 0.5)
            };
            Squish(_pill, () => OnScreen && _button.IsEnabled, 60);
            _scale = FeedbackScale(_pill);
            var host = new Border { Child = _pill, RenderTransform = _lift };
            System.Windows.Controls.Grid.SetColumn(host, 3);
            Grid.Children.Add(host);
            _pill.MouseEnter += (_, _) => Hover(true);
            _pill.MouseLeave += (_, _) => Hover(false);
            _pill.PreviewMouseLeftButtonUp += (_, _) => Hover(_pill.IsMouseOver);
            Grid.IsVisibleChanged += (_, _) =>
            {
                if (_view._rebuildingResetRows) return;
                if (!Grid.IsVisible) Stop();
                else Update(_state, _view._resetStates.Any(s => s.Key.StartsWith(provider + ":", StringComparison.Ordinal) && s.Value.Running));
            };
        }

        private Color FillColor(bool warm) => ((SolidColorBrush)Wash(warm ? Accent : LimitValue, warm ? 0.16 : 0.065)).Color;

        private void Hover(bool over)
        {
            if (!OnScreen || !_button.IsEnabled) return;
            var color = FillColor(_appearance == "confirm");
            if (over) color.A = (byte)Math.Min(255, color.A + 18);
            Tint(_fill, color, over ? 140 : 220);
            Move(_scale, ScaleTransform.ScaleXProperty, _scale.ScaleX, over ? 1.04 : 1, 420, WeatherMotion.Out);
            Move(_scale, ScaleTransform.ScaleYProperty, _scale.ScaleY, over ? 1.04 : 1, 420, WeatherMotion.Out);
            Move(_lift, TranslateTransform.YProperty, _lift.Y, over ? -1 : 0, 380, WeatherMotion.Out);
        }

        public void Update(ResetState? state, bool busy)
        {
            _state = state;
            _name.Text = Option.Name;
            _expiry.Text = Option.Expiry is { } expiry
                ? Loc.Get("control.claude.resetUntil", expiry.ToLocalTime().ToString("dd.MM.", CultureInfo.CurrentCulture)) : "";
            string appearance = state?.Running == true ? "running" : state?.Result ??
                (state?.Until > DateTimeOffset.UtcNow ? "confirm" : "idle");
            bool usable = Option.Usable && !busy && !_view._isLocalOnly() && state?.Result != "resetDone";
            _button.IsEnabled = usable;
            _name.Opacity = _expiry.Opacity = Option.Usable ? 1 : 0.5;
            _pill.Opacity = usable || state?.Running == true || state?.Result != null ? 1 : 0.4;
            _reason.Text = state?.Result is { } result ? Loc.Get("control.claude." + result) : !Option.Usable
                ? Loc.Get(Option.Reason ?? "control.claude.resetUnavailable") : "";
            _reason.Foreground = state?.Result != null ? Accent : ChipSoft;
            _button.ToolTip = _reason.Text.Length > 0 ? _reason.Text : Option.Name;
            if (_appearance == appearance) return;
            StopClocks();
            _appearance = appearance;
            bool confirm = appearance == "confirm";
            bool running = appearance == "running";
            bool done = appearance == "resetDone";
            bool error = state?.Result != null && !done;
            _progress.Opacity = confirm ? 0.75 : 0;
            _remaining.ScaleX = 1;
            _spinner.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            _check.Visibility = done ? Visibility.Visible : Visibility.Collapsed;
            string text = running || done ? "" : Loc.Get(confirm ? "control.claude.resetConfirm" : "control.claude.resetRedeem");
            double width = done ? 32 : running ? 54 : confirm ? 68 : 82;
            if (error) width = 82;
            Move(_pill, FrameworkElement.WidthProperty, _pill.Width, width, 420, WeatherMotion.Out);
            Tint(_fill, FillColor(confirm || running || done), 180);
            Tint(_ink, ((SolidColorBrush)(confirm || running || done ? Accent : LimitValue)).Color, 180);
            if (_label.Text != text)
            {
                _oldLabel.Text = _label.Text;
                _label.Text = text;
                Move(_oldLabel, UIElement.OpacityProperty, 1, 0, 110, WeatherMotion.Out);
                Move(_label, UIElement.OpacityProperty, 0, 1, 180, WeatherMotion.Out);
            }
            System.Windows.Automation.AutomationProperties.SetName(_button, Option.Name + ": " +
                (running ? "…" : done ? Loc.Get("control.claude.resetDone") : text));
            if (confirm && Animate)
            {
                double ms = Math.Max(0, (state!.Until - DateTimeOffset.UtcNow).TotalMilliseconds);
                Move(_remaining, ScaleTransform.ScaleXProperty, 1, 0, ms, null);
                var pulse = new DoubleAnimation(0.45, 0.9, TimeSpan.FromMilliseconds(600))
                {
                    AutoReverse = true, RepeatBehavior = new RepeatBehavior(TimeSpan.FromMilliseconds(ms)),
                    EasingFunction = WeatherMotion.Sine
                };
                Play(_progress, UIElement.OpacityProperty, pulse);
            }
            if (running && Animate)
                Move(_spin, RotateTransform.AngleProperty, 0, 21600, 60000, null);
            if (done) Move(_check, Shape.StrokeDashOffsetProperty, 12, 0, 300, WeatherMotion.Out);
            if (error && Animate)
            {
                Motion.To(_lift, TranslateTransform.XProperty, -3, Motion.PressDown,
                    done: () => Motion.To(_lift, TranslateTransform.XProperty, 0, Motion.Press));
            }
        }

        private void Tint(SolidColorBrush brush, Color color, double ms)
        {
            var from = brush.Color;
            brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            brush.Color = color;
            if (!Animate) { brush.BeginAnimation(SolidColorBrush.ColorProperty, null); brush.Color = color; return; }
            Play(brush, SolidColorBrush.ColorProperty, new ColorAnimation(from, color, TimeSpan.FromMilliseconds(ms)));
        }

        private void Move(IAnimatable target, DependencyProperty property, double from, double to, double ms, IEasingFunction? ease)
        {
            var dependency = (DependencyObject)target;
            if (!Animate) { Motion.Stop(dependency, property, to); return; }
            // Countdown and spin are clocks; all spatial state changes retain the live spring.
            if (ease == null || property == UIElement.OpacityProperty)
                Play(target, property, WeatherMotion.To(from, to, ms, ease));
            else Motion.To(dependency, property, to,
                property == FrameworkElement.WidthProperty ? Motion.Glide : Motion.Press, fps: 60);
        }

        private void Play(IAnimatable target, DependencyProperty property, AnimationTimeline animation)
        {
            Timeline.SetDesiredFrameRate(animation, 60);
            target.BeginAnimation(property, animation);
            _clocks.Add((target, property));
        }

        private void StopClocks()
        {
            foreach (var (target, property) in _clocks) target.BeginAnimation(property, null);
            _clocks.Clear();
            _oldLabel.Opacity = 0;
            _label.Opacity = 1;
        }

        public void Stop()
        {
            StopClocks();
            Motion.Stop(_scale, ScaleTransform.ScaleXProperty, 1);
            Motion.Stop(_scale, ScaleTransform.ScaleYProperty, 1);
            Motion.Stop(_lift, TranslateTransform.XProperty, 0);
            Motion.Stop(_lift, TranslateTransform.YProperty, 0);
            Motion.Stop(_pill, FrameworkElement.WidthProperty);
            Grid.BeginAnimation(UIElement.OpacityProperty, null);
            Grid.BeginAnimation(FrameworkElement.HeightProperty, null);
            if (Grid.RenderTransform is TransformGroup group)
                foreach (var transform in group.Children)
                {
                    if (transform is ScaleTransform scale)
                    {
                        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                        scale.ScaleX = scale.ScaleY = 1;
                    }
                    if (transform is TranslateTransform shift)
                    {
                        shift.BeginAnimation(TranslateTransform.YProperty, null);
                        shift.Y = 0;
                    }
                }
            Grid.Opacity = 1;
            _appearance = null;
        }

        public void Refresh() => Update(_state, _view._resetStates.Any(s => s.Key.StartsWith(_provider + ":", StringComparison.Ordinal) && s.Value.Running));

        public void Restore()
        {
            if (!_collapsing) return;
            _collapsing = false;
            GlideHeight(Grid, 32);
            Move(Grid, UIElement.OpacityProperty, Grid.Opacity, 1, 160, WeatherMotion.Out);
        }

        public void Collapse()
        {
            if (_collapsing) return;
            _collapsing = true;
            _button.IsEnabled = false;
            void Remove()
            {
                if (!_collapsing) return;
                Stop();
                if (Grid.Parent is Panel parent) parent.Children.Remove(Grid);
                _view._resetRows.Remove(_key);
                _view.ContentHeightChanged?.Invoke();
            }
            if (!Animate) { Remove(); return; }
            Move(Grid, UIElement.OpacityProperty, Grid.Opacity, 0, 150, WeatherMotion.Out);
            GlideHeight(Grid, 0, done: Remove);
        }
    }
}
