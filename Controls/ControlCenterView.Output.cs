using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using VNotch.Services;
using VNotch.Services.ControlCenter;

namespace VNotch.Controls;

/// <summary>
/// The system tab's sound: an "Output" tile next to CPU, GPU and RAM with the speaker in use and its volume (wheel on
/// it = louder/quieter). A click folds the speakers out underneath as a row;
/// a click on one switches to it and folds the row back.
/// At the bottom of the tab the apps that play sound, each with its own volume.
/// </summary>
public partial class ControlCenterView
{
    private const float WheelVolumeStep = 0.02f;

    private MasterVolumeWatcher? _volume;
    private Func<AudioMixerService?>? _mixer;
    private Action<float, bool>? _onVolumeChanged;
    private Action<float, bool>? _onSpeakerSwitched;
    private Action<string, bool>? _onEndpointChanged;

    private sealed class OutputTile
    {
        public required Border Card;
        public required TextBlock Name, Sub;
        public required ScaleTransform Fill;
        public required RotateTransform Chevron;
        public required RollingNumber Number;
        public double Level;
        public bool Muted;
    }

    private OutputTile? _outputTile;
    private readonly Grid _outputDrawer = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 2) };
    private readonly StackPanel _outputSection = new();
    private bool _outputOpen;
    private List<OutputDevice> _outputs = new();
    private string _outputKey = "";
    private string _appsKey = "";
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<FrameworkElement, WeatherTween> HeightTweens = new();

    private static void GlideHeight(FrameworkElement element, double to, Action? done = null, double? from = null)
    {
        var tween = HeightTweens.GetValue(element, e => new WeatherTween { Value = e.ActualHeight,
            Step = height => e.Height = Math.Max(0, height) });
        Motion.To(tween, WeatherTween.ValueProperty, to, Motion.Glide, from: from, done: done);
    }
    private readonly StackPanel _appsSection = new();
    private sealed class AppBar
    {
        public required ScaleTransform Fill;
        public required TextBlock Value;
        public float Volume;
        public bool Muted;
    }

    private readonly Dictionary<uint, AppBar> _appBars = new();
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TextBlock, RollingNumber> AppNumbers = new();

    /// <summary>For the <see cref="ControlCenterPage.System"/> page: the default speaker's volume and the app mixer.</summary>
    internal void AttachOutput(MasterVolumeWatcher volume, Func<AudioMixerService?> mixer)
    {
        _volume = volume;
        _mixer = mixer;
        _onVolumeChanged = (v, m) => Dispatcher.BeginInvoke(() => ShowOutputLevel(v, m));
        _onSpeakerSwitched = (v, m) => Dispatcher.BeginInvoke(() =>
        {
            ShowOutputLevel(v, m);
            if (IsVisible) Refresh();
        });
        _onEndpointChanged = (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (IsVisible) Refresh();
        });
        volume.Changed += _onVolumeChanged;
        volume.SpeakerSwitched += _onSpeakerSwitched;
        volume.EndpointStateChanged += _onEndpointChanged;
    }

    private void DetachOutput()
    {
        if (_volume == null) return;
        _volume.Changed -= _onVolumeChanged;
        _volume.SpeakerSwitched -= _onSpeakerSwitched;
        _volume.EndpointStateChanged -= _onEndpointChanged;
    }

    /// <summary>Background part of the snapshot: speakers and the apps playing sound.</summary>
    private void ReadSound(Snapshot s)
    {
        s.Outputs = OutputDevices.List(s.Devices.Select(d => d.Name));
        try
        {
            s.Apps = _mixer?.Invoke()?.GetSessions(includeIcons: false)
                .Where(a => !a.IsSystemSounds && a.DisplayName.Length > 0)
                .GroupBy(a => a.ProcessId).Select(g => g.First())
                .OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or DllNotFoundException)
        {
            RuntimeLog.Warn("CONTROL-CENTER", $"App volumes not read: {ex.Message}");
        }
    }

    #region Output tile

    private OutputTile AddOutputTile(Grid grid, int column, int span)
    {
        var label = MakeLabel(Loc.Get("control.output"), 11, _moodInk);
        var chevron = new RotateTransform(0);
        var arrow = new TextBlock
        {
            Text = "",
            FontFamily = SegoeSymbolFont,
            FontSize = 9,
            Foreground = _moodInk,
            Margin = new Thickness(6, 1, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = chevron
        };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Children = { label, arrow } };
        var name = MakeLabel("–", 15, VNotch.Services.UiPalette.PrimaryBrush);
        name.Margin = new Thickness(0, 3, 0, 0);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        var sub = MakeLabel("", 11, Muted);
        sub.Margin = new Thickness(8, 0, 0, 1);
        sub.VerticalAlignment = VerticalAlignment.Bottom;
        var nameRow = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(sub, Dock.Right);
        nameRow.Children.Add(sub);
        nameRow.Children.Add(name);

        var fill = new ScaleTransform(0, 1);
        var bar = new Grid
        {
            Height = 3,
            Margin = new Thickness(0, 7, 0, 1),
            Children =
            {
                new Border { Background = Track, CornerRadius = new CornerRadius(1.5) },
                new Border { Background = MoodBar, CornerRadius = new CornerRadius(1.5), RenderTransform = fill, RenderTransformOrigin = new Point(0, 0.5) }
            }
        };

        var card = Card();
        card.Padding = new Thickness(12, 9, 12, 9);
        card.Child = new StackPanel { Children = { head, nameRow, bar } };
        card.Cursor = Cursors.Hand;
        System.Windows.Automation.AutomationProperties.SetName(card, Loc.Get("control.output"));
        Pressable(card, () => CardBg, CardHover, grow: true);
        DetailHover(card, arrow, y: -2);
        card.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            SetOutputOpen(!_outputOpen);
        };
        // The wheel over the tile turns the speaker in use up or down.
        card.PreviewMouseWheel += (_, e) =>
        {
            e.Handled = true;
            if (_volume == null) return;
            var (level, _) = _volume.Current;
            float next = Math.Clamp(level + Math.Sign(e.Delta) * WheelVolumeStep, 0f, 1f);
            next = MathF.Round(next / WheelVolumeStep) * WheelVolumeStep;
            _volume.SetVolume(next);
            ShowOutputLevel(next, false);
        };
        Grid.SetColumn(card, column);
        Grid.SetColumnSpan(card, span);
        grid.Children.Add(card);

        var tile = new OutputTile { Card = card, Name = name, Sub = sub, Fill = fill, Chevron = chevron,
            Number = new RollingNumber(n =>
            {
                if (sub.Tag is not true) SetRollingDetailText(sub, $"{Math.Round(n)} %");
            }, sub) };
        card.IsVisibleChanged += (_, _) =>
        {
            if (!card.IsVisible)
            {
                Motion.Stop(fill, ScaleTransform.ScaleXProperty, tile.Muted ? 0 : tile.Level);
                return;
            }
            DetailArrive(head, 0);
            DetailArrive(nameRow, 1);
            tile.Number.To(0, 0);
            tile.Number.To(tile.Level * 100);
            Motion.To(fill, ScaleTransform.ScaleXProperty, tile.Muted ? 0 : tile.Level, Motion.Glide,
                from: AnimationConfig.ReduceMotion ? null : 0, delay: Motion.Stagger(2));
        };
        if (_volume != null)
        {
            var (v, m) = _volume.Current;
            SetOutputLevel(tile, v, m, animate: false);
        }
        ShowOutputName(tile);
        return tile;
    }

    private void ShowOutputLevel(float volume, bool muted)
    {
        if (_outputTile is { } tile) SetOutputLevel(tile, volume, muted, animate: IsVisible);
    }

    private static void SetOutputLevel(OutputTile tile, float volume, bool muted, bool animate)
    {
        double target = muted ? 0 : Math.Clamp(volume, 0, 1);
        bool changed = tile.Sub.Tag is bool previous && previous != muted;
        tile.Level = Math.Clamp(volume, 0, 1);
        tile.Muted = muted;
        tile.Sub.Tag = muted;
        if (changed) SetDetailText(tile.Sub, muted ? "🔇" : $"{Math.Round(volume * 100)} %");
        tile.Number.To(volume * 100, animate && !muted ? 650 : 0);
        if (muted && !changed) SetDetailText(tile.Sub, "🔇");
        if (changed && animate) DetailReaction(tile.Sub);
        if (!animate || AnimationConfig.ReduceMotion || !tile.Card.IsVisible)
        {
            Motion.Stop(tile.Fill, ScaleTransform.ScaleXProperty, target);
            return;
        }
        Motion.To(tile.Fill, ScaleTransform.ScaleXProperty, target, Motion.Glide);
    }

    private void ShowOutputName(OutputTile tile)
    {
        var current = _outputs.FirstOrDefault(o => o.IsDefault);
        SetDetailText(tile.Name, current?.Name ?? "–");
        tile.Card.ToolTip = current?.FriendlyName;
    }

    private int? Battery(OutputDevice output) =>
        _snapshot?.Devices.FirstOrDefault(d => d.Battery >= 0 && output.FriendlyName.Contains(d.Name, StringComparison.OrdinalIgnoreCase))?.Battery;

    #endregion

    #region Speaker row

    private void ApplySound(Snapshot s)
    {
        if (s.Outputs != null)
        {
            _outputs = s.Outputs;
            if (_outputTile != null) ShowOutputName(_outputTile);
            string key = OutputKey();
            if (key != _outputKey)
            {
                _outputKey = key;
                if (_outputOpen) BuildOutputRow(animate: false);
            }
        }
        if (s.Apps != null) ApplyApps(s.Apps);
    }

    private string OutputKey()
    {
        var sb = new StringBuilder();
        foreach (var o in _outputs) sb.Append(o.Id).Append(o.IsDefault).Append(o.Bluetooth ? Battery(o) : null).Append(';');
        return sb.ToString();
    }

    private void SetOutputOpen(bool open)
    {
        if (open == _outputOpen && (open == (_outputDrawer.Visibility == Visibility.Visible))) return;
        _outputOpen = open;
        if (_outputTile is { } tile)
        {
            Motion.To(tile.Chevron, RotateTransform.AngleProperty, open ? 180 : 0, Motion.Press);
        }
        if (open)
        {
            if (_outputDrawer.Children.Count == 0) BuildOutputRow(animate: true);
            else RevealOutputDrawer(fresh: _outputDrawer.Visibility != Visibility.Visible);
            return;
        }
        if (AnimationConfig.ReduceMotion || !IsVisible)
        {
            if (HeightTweens.TryGetValue(_outputDrawer, out var height)) Motion.Stop(height, WeatherTween.ValueProperty, 0);
            _outputDrawer.BeginAnimation(OpacityProperty, null);
            if (_outputDrawer.RenderTransform is TranslateTransform shift) Motion.Stop(shift, TranslateTransform.YProperty, -10);
            _outputDrawer.Height = 0;
            _outputDrawer.Visibility = Visibility.Collapsed;
            return;
        }
        // Folds back up into the tile.
        var lift = _outputDrawer.RenderTransform as TranslateTransform ?? new TranslateTransform();
        _outputDrawer.RenderTransform = lift;
        Motion.To(lift, TranslateTransform.YProperty, -10, Motion.Exit);
        GlideHeight(_outputDrawer, 0, done: () =>
        {
            if (!_outputOpen) _outputDrawer.Visibility = Visibility.Collapsed;
        });
        var fade = WeatherMotion.To(_outputDrawer.Opacity, 0, 170, WeatherMotion.Out);
        _outputDrawer.BeginAnimation(OpacityProperty, fade);
    }

    private void BuildOutputRow(bool animate)
    {
        _outputDrawer.Children.Clear();
        _outputDrawer.ColumnDefinitions.Clear();
        int count = _outputs.Count;
        if (count == 0)
        {
            _outputDrawer.Visibility = Visibility.Collapsed;
            return;
        }
        for (int i = 0; i < count * 2 - 1; i++)
            _outputDrawer.ColumnDefinitions.Add(new ColumnDefinition { Width = i % 2 == 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(8) });

        int index = 0;
        foreach (var output in _outputs)
        {
            var o = output;
            string sub = o.Bluetooth
                ? "Bluetooth" + (Battery(o) is { } b ? $" · {b} %" : "")
                : Connection(o.FriendlyName);
            Place(Chip(o.Name, sub, active: o.IsDefault, () => SwitchOutput(o)));
        }
        _outputDrawer.Visibility = Visibility.Visible;
        if (!animate || AnimationConfig.ReduceMotion || !IsVisible)
        {
            _outputDrawer.BeginAnimation(OpacityProperty, null);
            _outputDrawer.Opacity = 1;
            if (_outputDrawer.RenderTransform is TranslateTransform still) { still.BeginAnimation(TranslateTransform.YProperty, null); still.Y = 0; }
            return;
        }
        RevealOutputDrawer(fresh: true);
        int n = 0;
        foreach (FrameworkElement chip in _outputDrawer.Children)
        {
            PopIn(chip, Motion.Stagger(n++).TotalMilliseconds);
        }

        void Place(Border chip)
        {
            Grid.SetColumn(chip, index * 2);
            _outputDrawer.Children.Add(chip);
            index++;
        }
    }

    private void RevealOutputDrawer(bool fresh)
    {
        var drop = _outputDrawer.RenderTransform as TranslateTransform ?? new TranslateTransform();
        _outputDrawer.RenderTransform = drop;
        _outputDrawer.Visibility = Visibility.Visible;
        _outputDrawer.Measure(new Size(Math.Max(1, ActualWidth), double.PositiveInfinity));
        double height = _outputDrawer.Children.Cast<FrameworkElement>().Select(c => c.DesiredSize.Height).DefaultIfEmpty(0).Max();
        GlideHeight(_outputDrawer, height, from: fresh ? 0 : null);
        _outputDrawer.BeginAnimation(OpacityProperty, WeatherMotion.To(fresh ? 0 : _outputDrawer.Opacity, 1, 160, WeatherMotion.Out));
        Motion.To(drop, TranslateTransform.YProperty, 0, Motion.Tile, from: fresh ? -10 : null);
    }

    private Border Chip(string name, string sub, bool active, Action onClick)
    {
        var title = MakeLabel(name, 13, VNotch.Services.UiPalette.PrimaryBrush);
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        var detail = MakeLabel(sub, 10.5, Muted);
        detail.Margin = new Thickness(0, 2, 0, 0);
        detail.TextTrimming = TextTrimming.CharacterEllipsis;
        var chip = Card();
        chip.Padding = new Thickness(11, 8, 11, 8);
        chip.Child = new StackPanel { Children = { title, detail } };
        chip.Cursor = Cursors.Hand;
        chip.RenderTransformOrigin = new Point(0.5, 0.5);
        chip.RenderTransform = new ScaleTransform(1, 1);
        if (active)
        {
            chip.Background = new SolidColorBrush(Color.FromArgb(0x2E, _mood.Color.R, _mood.Color.G, _mood.Color.B));
            chip.BorderBrush = new SolidColorBrush(Color.FromArgb(0x80, _mood.Color.R, _mood.Color.G, _mood.Color.B));
            chip.BorderThickness = new Thickness(1);
            chip.Padding = new Thickness(10, 7, 10, 7);
        }
        Brush rest = chip.Background;
        Pressable(chip, () => rest, CardHover);
        DetailHover(chip, title, x: 2, y: 0);
        System.Windows.Automation.AutomationProperties.SetName(chip, name);
        chip.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            onClick();
        };
        return chip;
    }

    /// <summary>How it is plugged in, under its short name: the laptop's jack, its speakers, or USB.</summary>
    private static string Connection(string friendlyName) =>
        OutputDevices.IsBuiltInSpeaker(friendlyName) ? Loc.Get("sound.builtIn")
        : friendlyName.Contains("Realtek", StringComparison.OrdinalIgnoreCase) || friendlyName.Contains("High Definition Audio", StringComparison.OrdinalIgnoreCase) ? "3,5 mm"
        : "USB";

    private void SwitchOutput(OutputDevice output)
    {
        SetOutputOpen(false);
        if (output.IsDefault) return;
        // Shown at once; the speaker-switch event confirms it.
        _outputs = _outputs.Select(o => o with { IsDefault = o.Id == output.Id }).ToList();
        if (_outputTile != null) ShowOutputName(_outputTile);
        Task.Run(() => AudioMixerService.SwitchDefaultOutputDevice(output.Id));
    }

    #endregion

    #region Apps

    private void ApplyApps(List<AudioSessionInfo> apps)
    {
        string key = (IsExpanded("apps") ? "1" : "0") + string.Join(";", apps.Select(a => $"{a.ProcessId}:{a.DisplayName}"));
        if (key != _appsKey)
        {
            _appsKey = key;
            BuildApps(apps);
            return;
        }
        foreach (var app in apps)
            if (_appBars.TryGetValue(app.ProcessId, out var bar)) SetAppBar(bar, app.Volume, app.IsMuted, animate: true);
    }

    private void BuildApps(List<AudioSessionInfo> apps)
    {
        _appsSection.Children.Clear();
        _appBars.Clear();
        if (apps.Count == 0) return;
        _appsSection.Children.Add(BuildSectionHeader("apps", Loc.Get("sound.apps"), "", null, topMargin: 14));
        if (!IsExpanded("apps")) return;
        foreach (var app in apps)
        {
            var a = app;
            var row = NewRow("", a.DisplayName);
            var fill = new ScaleTransform(0, 1);
            var value = MakeLabel("", 11, Muted);
            value.Width = 38;
            value.TextAlignment = TextAlignment.Right;
            value.VerticalAlignment = VerticalAlignment.Center;
            var track = new Grid
            {
                Height = 4,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new Border { Background = Track, CornerRadius = new CornerRadius(2) },
                    new Border { Background = MoodBar, CornerRadius = new CornerRadius(2), RenderTransform = fill, RenderTransformOrigin = new Point(0, 0.5) }
                }
            };
            // A taller, invisible strip to grab: click or drag sets the volume, the wheel nudges it.
            var hit = new Grid { Background = Brushes.Transparent, Cursor = Cursors.Hand, Children = { track } };
            var slider = new DockPanel { Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Stretch };
            DockPanel.SetDock(value, Dock.Right);
            slider.Children.Add(value);
            slider.Children.Add(hit);
            Grid.SetColumn(slider, 1);
            Grid.SetColumnSpan(slider, 2);
            row.Children.Add(slider);

            var bar = new AppBar { Fill = fill, Value = value };
            _appBars[a.ProcessId] = bar;
            SetAppBar(bar, a.Volume, a.IsMuted, animate: false);
            value.IsVisibleChanged += (_, _) =>
            {
                double target = bar.Muted ? 0 : Math.Clamp(bar.Volume, 0, 1);
                if (!value.IsVisible) Motion.Stop(fill, ScaleTransform.ScaleXProperty, target);
                else
                {
                    SetAppBar(bar, bar.Volume, bar.Muted, animate: true);
                    Motion.To(fill, ScaleTransform.ScaleXProperty, target, Motion.Glide,
                        from: AnimationConfig.ReduceMotion ? null : 0);
                }
            };

            void SetFrom(MouseEventArgs e)
            {
                double w = track.ActualWidth;
                if (w <= 0) return;
                float v = (float)Math.Clamp(e.GetPosition(track).X / w, 0, 1);
                SetAppVolume(a.ProcessId, v, bar);
            }
            hit.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                hit.CaptureMouse();
                SetFrom(e);
            };
            hit.MouseMove += (_, e) =>
            {
                if (hit.IsMouseCaptured) SetFrom(e);
            };
            hit.MouseLeftButtonUp += (_, _) => hit.ReleaseMouseCapture();
            hit.PreviewMouseWheel += (_, e) =>
            {
                e.Handled = true;
                float v = (float)Math.Clamp(Math.Round(fill.ScaleX / WheelVolumeStep) * WheelVolumeStep + Math.Sign(e.Delta) * WheelVolumeStep, 0, 1);
                SetAppVolume(a.ProcessId, v, bar);
            };
            _appsSection.Children.Add(row);
        }
    }

    private void SetAppVolume(uint processId, float volume, AppBar bar)
    {
        SetAppBar(bar, volume, false, animate: true);
        var mixer = _mixer?.Invoke();
        if (mixer != null) Task.Run(() => mixer.SetSessionVolume(processId, volume));
    }

    private static void SetAppBar(AppBar bar, float volume, bool muted, bool animate)
    {
        double target = muted ? 0 : Math.Clamp(volume, 0, 1);
        bool changed = bar.Value.Tag is bool previous && previous != muted;
        bar.Volume = volume;
        bar.Muted = muted;
        bar.Value.Tag = muted;
        var number = AppNumbers.GetValue(bar.Value, label => new RollingNumber(
            n => { if (label.Tag is not true) SetRollingDetailText(label, $"{Math.Round(n)} %"); }, label));
        if (changed) SetDetailText(bar.Value, muted ? "🔇" : $"{Math.Round(volume * 100)} %");
        number.To(volume * 100, animate && !muted ? 650 : 0);
        if (muted && !changed) SetDetailText(bar.Value, "🔇");
        if (changed && animate) DetailReaction(bar.Value);
        if (!animate || AnimationConfig.ReduceMotion || !bar.Value.IsVisible)
        {
            Motion.Stop(bar.Fill, ScaleTransform.ScaleXProperty, target);
            return;
        }
        Motion.To(bar.Fill, ScaleTransform.ScaleXProperty, target, Motion.Glide);
    }

    #endregion
}
