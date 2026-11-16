using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Services;

namespace VNotch.Controls;

/// <summary>
/// Colour and motion shared by the control center pages. Every section has its own tint (Wi-Fi blue, the
/// microphone orange like the system's mic dot, Claude terracotta; the system tab one muted colour from the
/// PC's load), so nothing is green by default. Presses squish and spring back, switches stretch their knob while held,
/// state changes fade their colour instead of jumping.
/// </summary>
public partial class ControlCenterView
{
    #region Tints

    // Apple's dark-mode system colours.
    private static readonly Brush Blue = Frozen("#0A84FF");
    private static readonly Brush Purple = Frozen("#BF5AF2");
    private static readonly Brush Pink = Frozen("#FF375F");
    private static readonly Brush Teal = Frozen("#64D2FF");
    private static readonly Brush Mint = Frozen("#66D4CF");
    private static readonly Brush Orange = Frozen("#FF9F0A");
    private static readonly Brush Red = Frozen("#FF453A");

    /// <summary>The tint of the section being built; switches and accent buttons take it.</summary>
    private Brush _accent = Blue;

    /// <summary>Black on bright tints (yellow, mint), white on the rest.</summary>
    private static Brush InkOn(Brush tint)
    {
        if (tint is not SolidColorBrush { Color: var c }) return VNotch.Services.UiPalette.PrimaryBrush;
        double luminance = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;
        return luminance > 0.62 ? OnGreenText : VNotch.Services.UiPalette.PrimaryBrush;
    }

    /// <summary>A brush of the tint's colour at <paramref name="opacity"/>, for lit tile backgrounds.</summary>
    private static SolidColorBrush Wash(Brush tint, double opacity)
    {
        var color = tint is SolidColorBrush s ? s.Color : Colors.White;
        return new SolidColorBrush(Color.FromArgb((byte)(opacity * 255), color.R, color.G, color.B));
    }

    #endregion

    #region Press, hover, entrances

    private static readonly IEasingFunction PressEase = FreezeEase(new CubicEase { EasingMode = EasingMode.EaseOut });
    private static readonly IEasingFunction ReleaseSpring = FreezeEase(new ElasticEase { Oscillations = 1, Springiness = 7, EasingMode = EasingMode.EaseOut });
    // A spring that still swings back once, just less far (the Legion page).
    private static readonly IEasingFunction SoftSpring = FreezeEase(new ElasticEase { Oscillations = 1, Springiness = 9, EasingMode = EasingMode.EaseOut });
    private static readonly IEasingFunction KnobSpring = FreezeEase(new BackEase { Amplitude = 0.55, EasingMode = EasingMode.EaseOut });

    private static IEasingFunction FreezeEase(EasingFunctionBase ease)
    {
        ease.Freeze();
        return ease;
    }

    // How much a tile that grows under the pointer grows.
    private const double HoverGrow = 1.035;

    /// <summary>
    /// How far an element rises under the pointer. A scale is pixels on a wide tile, and one that reaches the edge
    /// of its tab got its sides cut off, so the rise is capped at three pixels a side, spring overshoot included.
    /// </summary>
    internal static double HoverScale(FrameworkElement element, double grow = HoverGrow)
    {
        double extent = Math.Max(element.ActualWidth, element.ActualHeight);
        return extent > 0 ? 1 + Math.Min(grow - 1, 5 / extent) : grow;
    }

    /// <summary>
    /// Makes a card feel physical: its background fades to <paramref name="hover"/> under the pointer, a press
    /// squishes it a little (wider than tall, like a soft key) and letting go springs it back. With
    /// <paramref name="grow"/> it also rises a little toward the pointer, with a spring.
    /// </summary>
    private static void Pressable(Border card, Func<Brush> rest, Brush hover, bool gentle = false, bool grow = false)
    {
        var scale = FeedbackScale(card);
        card.RenderTransformOrigin = new Point(0.5, 0.5);
        var fill = card.Background is SolidColorBrush { IsFrozen: false } own ? own : new SolidColorBrush(((SolidColorBrush)rest()).Color);
        card.Background = fill;
        Color HoverColor() => Blend(((SolidColorBrush)rest()).Color, ((SolidColorBrush)hover).Color);

        card.MouseEnter += (_, _) =>
        {
            WeatherMotion.Tint(fill, HoverColor(), 140);
            if (grow) Release(scale, gentle, HoverScale(card));
        };
        card.MouseLeave += (_, _) =>
        {
            WeatherMotion.Tint(fill, ((SolidColorBrush)rest()).Color, 160);
            Release(scale, gentle);
        };
        card.PreviewMouseLeftButtonDown += (_, _) =>
        {
            Motion.To(scale, ScaleTransform.ScaleXProperty, gentle ? 0.975 : 0.965, Motion.PressDown);
            Motion.To(scale, ScaleTransform.ScaleYProperty, gentle ? 0.945 : 0.93, Motion.PressDown);
        };
        card.PreviewMouseLeftButtonUp += (_, _) => Release(scale, gentle, grow && card.IsMouseOver ? HoverScale(card) : 1);
        card.LostMouseCapture += (_, _) => Release(scale, gentle);
        card.IsVisibleChanged += (_, _) => { if (!card.IsVisible) StopScale(scale); };
    }

    /// <summary>A button sinks a little while pressed and springs back on release.</summary>
    private static void Squish(FrameworkElement button, Func<bool>? active = null, int? fps = null)
    {
        var scale = FeedbackScale(button);
        button.RenderTransformOrigin = new Point(0.5, 0.5);
        void Settle()
        {
            double to = active?.Invoke() != false && button.IsMouseOver ? HoverScale(button) : 1;
            Motion.To(scale, ScaleTransform.ScaleXProperty, to, Motion.Press, fps: fps);
            Motion.To(scale, ScaleTransform.ScaleYProperty, to, Motion.Press, fps: fps);
        }
        button.PreviewMouseLeftButtonDown += (_, _) =>
        {
            if (active?.Invoke() == false) return;
            Motion.To(scale, ScaleTransform.ScaleXProperty, 0.96, Motion.PressDown, fps: fps);
            Motion.To(scale, ScaleTransform.ScaleYProperty, 0.92, Motion.PressDown, fps: fps);
        };
        button.PreviewMouseLeftButtonUp += (_, _) => Settle();
        button.MouseEnter += (_, _) => Settle();
        button.MouseLeave += (_, _) => Settle();
        button.LostMouseCapture += (_, _) => Settle();
        button.IsVisibleChanged += (_, _) => { if (!button.IsVisible) StopScale(scale); };
    }

    private static void Release(ScaleTransform scale, bool gentle = false, double to = 1)
    {
        Motion.To(scale, ScaleTransform.ScaleXProperty, to, Motion.Press);
        Motion.To(scale, ScaleTransform.ScaleYProperty, to, Motion.Press);
    }

    private static void StopScale(ScaleTransform scale)
    {
        Motion.Stop(scale, ScaleTransform.ScaleXProperty, 1);
        Motion.Stop(scale, ScaleTransform.ScaleYProperty, 1);
    }

    private static ScaleTransform FeedbackScale(FrameworkElement element)
    {
        if (element.RenderTransform is ScaleTransform scale) return scale;
        if (element.RenderTransform is TransformGroup group)
            foreach (var part in group.Children)
                if (part is ScaleTransform existing) return existing;
        var created = new ScaleTransform(1, 1);
        var transforms = new TransformGroup();
        transforms.Children.Add(created);
        if (element.RenderTransform != Transform.Identity) transforms.Children.Add(element.RenderTransform);
        element.RenderTransform = transforms;
        return created;
    }

    private sealed class DetailTextState
    {
        public required string Target;
        public required TranslateTransform Shift;
        public bool Leaving;
        public Func<bool>? Reparenting;
    }

    private static readonly ConditionalWeakTable<TextBlock, DetailTextState> DetailTexts = new();

    /// <summary>The newest label replaces the old one at the bottom of a small, interruptible dip.</summary>
    private static void SetDetailText(TextBlock text, string value)
    {
        var state = DetailTexts.GetValue(text, label =>
        {
            var created = new DetailTextState { Target = label.Text, Shift = new TranslateTransform() };
            AddDetailTransform(label, created.Shift);
            label.IsVisibleChanged += (_, _) =>
            {
                if (label.IsVisible || created.Reparenting?.Invoke() == true) return;
                SettleDetailText(label, created);
            };
            return created;
        });
        if (!text.IsVisible || AnimationConfig.ReduceMotion)
        {
            state.Target = value;
            SettleDetailText(text, state);
            return;
        }
        if (state.Target == value) return;
        state.Target = value;
        if (text.Text == value)
        {
            state.Leaving = false;
            Motion.To(text, OpacityProperty, 1, Motion.Press);
            Motion.To(state.Shift, TranslateTransform.YProperty, 0, Motion.Press);
            return;
        }
        if (state.Leaving) return; // A burst updates the destination without delaying the swap.
        state.Leaving = true;
        Motion.To(state.Shift, TranslateTransform.YProperty, 2, Motion.PressDown);
        Motion.To(text, OpacityProperty, 0, Motion.PressDown, done: () =>
        {
            if (!state.Leaving || text.Opacity > .01) return;
            state.Leaving = false;
            text.Text = state.Target;
            Motion.To(state.Shift, TranslateTransform.YProperty, 0, Motion.Press);
            Motion.To(text, OpacityProperty, 1, Motion.Press);
        });
    }

    private static void SettleDetailText(TextBlock text, DetailTextState state)
    {
        state.Leaving = false;
        text.Text = state.Target;
        Motion.Stop(text, OpacityProperty, 1);
        Motion.Stop(state.Shift, TranslateTransform.YProperty, 0);
    }

    // A numeric roll can arrive during a label swap; let the dip reveal its latest frame.
    private static void SetRollingDetailText(TextBlock text, string value)
    {
        if (DetailTexts.TryGetValue(text, out var state))
        {
            state.Target = value;
            if (state.Leaving) return;
        }
        text.Text = value;
    }

    private static void AddDetailTransform(FrameworkElement element, Transform transform)
    {
        if (element.RenderTransform is TransformGroup group && !group.IsFrozen) group.Children.Add(transform);
        else
        {
            var parts = new TransformGroup();
            if (element.RenderTransform != Transform.Identity) parts.Children.Add(element.RenderTransform);
            parts.Children.Add(transform);
            element.RenderTransform = parts;
        }
    }

    /// <summary>The glyph follows the pointer by a few pixels, independently of its parent's squish.</summary>
    private static void DetailHover(FrameworkElement control, FrameworkElement glyph, double x = 0, double y = -2)
    {
        var shift = new TranslateTransform();
        AddDetailTransform(glyph, shift);
        void Move(double dx, double dy, SpringMotion spring)
        {
            if (!control.IsVisible || !glyph.IsVisible || AnimationConfig.ReduceMotion)
            {
                Motion.Stop(shift, TranslateTransform.XProperty, 0);
                Motion.Stop(shift, TranslateTransform.YProperty, 0);
                return;
            }
            Motion.To(shift, TranslateTransform.XProperty, dx, spring);
            Motion.To(shift, TranslateTransform.YProperty, dy, spring);
        }
        control.MouseEnter += (_, _) => Move(x, y, Motion.Press);
        control.MouseLeave += (_, _) => Move(0, 0, Motion.Press);
        control.PreviewMouseLeftButtonDown += (_, _) => Move(-x * .5, 1, Motion.PressDown);
        control.PreviewMouseLeftButtonUp += (_, _) => Move(control.IsMouseOver ? x : 0, control.IsMouseOver ? y : 0, Motion.Press);
        control.LostMouseCapture += (_, _) => Move(0, 0, Motion.Press);
        control.IsVisibleChanged += (_, _) => { if (!control.IsVisible) Move(0, 0, Motion.Press); };
        glyph.IsVisibleChanged += (_, _) => { if (!glyph.IsVisible) Move(0, 0, Motion.Press); };
    }

    private static readonly ConditionalWeakTable<FrameworkElement, TranslateTransform> DetailReactions = new();

    /// <summary>A state change gives a glyph a little momentum; another change carries it onward.</summary>
    private static void DetailReaction(FrameworkElement glyph)
    {
        var shift = DetailReactions.GetValue(glyph, element =>
        {
            var created = new TranslateTransform();
            AddDetailTransform(element, created);
            element.IsVisibleChanged += (_, _) => { if (!element.IsVisible) Motion.Stop(created, TranslateTransform.YProperty, 0); };
            return created;
        });
        if (!glyph.IsVisible || AnimationConfig.ReduceMotion)
        {
            Motion.Stop(shift, TranslateTransform.YProperty, 0);
            return;
        }
        Motion.To(shift, TranslateTransform.YProperty, 0, Motion.Press,
            velocity: Motion.VelocityOf(shift, TranslateTransform.YProperty) - 24);
    }

    private static readonly ConditionalWeakTable<FrameworkElement, TranslateTransform> DetailArrivals = new();

    private static void DetailArrive(FrameworkElement element, int index)
    {
        var shift = DetailArrivals.GetValue(element, part =>
        {
            var created = new TranslateTransform();
            AddDetailTransform(part, created);
            part.IsVisibleChanged += (_, _) =>
            {
                if (part.IsVisible) return;
                Motion.Stop(created, TranslateTransform.YProperty, 0);
                Motion.Stop(part, OpacityProperty, 1);
            };
            return created;
        });
        if (!element.IsVisible || AnimationConfig.ReduceMotion) return;
        bool moving = Motion.IsRunning(shift, TranslateTransform.YProperty);
        var delay = moving ? TimeSpan.Zero : Motion.Stagger(index);
        Motion.To(shift, TranslateTransform.YProperty, 0, Motion.Tile, from: moving ? null : 3, delay: delay);
        Motion.To(element, OpacityProperty, 1, Motion.Tile, from: moving ? null : 0, delay: delay);
    }

    private static void ExitElement(FrameworkElement element, Action? done = null)
    {
        var scale = FeedbackScale(element);
        Motion.To(scale, ScaleTransform.ScaleXProperty, Motion.ExitToScale, Motion.Exit);
        Motion.To(scale, ScaleTransform.ScaleYProperty, Motion.ExitToScale, Motion.Exit);
        if (AnimationConfig.ReduceMotion)
        {
            Motion.Stop(element, OpacityProperty, 0);
            done?.Invoke();
            return;
        }
        var fade = WeatherMotion.To(element.Opacity, 0, 160, WeatherMotion.Out);
        fade.Completed += (_, _) => done?.Invoke();
        element.BeginAnimation(OpacityProperty, fade);
    }

    // A lit tile keeps its tint under the pointer, just a touch brighter.
    private static Color Blend(Color baseColor, Color over)
    {
        double a = over.A / 255.0;
        return Color.FromArgb((byte)Math.Max(baseColor.A, over.A),
            (byte)(baseColor.R + (255 - baseColor.R) * a * 0.5),
            (byte)(baseColor.G + (255 - baseColor.G) * a * 0.5),
            (byte)(baseColor.B + (255 - baseColor.B) * a * 0.5));
    }

    /// <summary>Rows rise into place one after another; tile grids pop their tiles in one by one.</summary>
    private static int RevealChildren(Panel section, TimeSpan baseDelay, int index, int skip = 0)
    {
        for (int i = skip; i < section.Children.Count; i++)
        {
            if (section.Children[i] is not FrameworkElement row || row.Visibility != Visibility.Visible) continue;
            if (row is Panel { Tag: NestedSectionTag } nested)
            {
                index = RevealChildren(nested, baseDelay, index);
                continue;
            }
            if (row is Grid { Tag: TileGridTag } tiles)
            {
                // Decorations that do not take clicks (the Legion pill) keep their own entrance.
                foreach (UIElement tile in tiles.Children)
                    if (tile is FrameworkElement element && element.Visibility == Visibility.Visible && element.IsHitTestVisible)
                        PopIn(element, (baseDelay + Motion.Stagger(index++)).TotalMilliseconds);
                continue;
            }
            var delay = baseDelay + Motion.Stagger(index++);
            // A pressable card on its own row keeps its scale for the squish: it pops instead of rising.
            RevealElement(row, delay);
        }
        return index;
    }

    private const string TileGridTag = "tiles";
    private const string NestedSectionTag = "section";

    /// <summary>Sets a colour at once (first paint) or fades to it.</summary>
    private static void SetOrTint(SolidColorBrush brush, Color color, double ms)
    {
        if (ms <= 0)
        {
            brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            brush.Color = color;
        }
        else WeatherMotion.Tint(brush, color, ms);
    }

    /// <summary>
    /// Pops a tile in from a little smaller with a springy overshoot. Reuses the tile's own scale, so the
    /// press squish keeps working afterwards.
    /// </summary>
    private static void PopIn(FrameworkElement tile, double delayMs, double fromScale = Motion.TileFromScale)
    {
        var scale = FeedbackScale(tile);
        tile.RenderTransformOrigin = new Point(0.5, 0.5);
        bool moving = Motion.VelocityOf(scale, ScaleTransform.ScaleXProperty) != 0;
        var delay = TimeSpan.FromMilliseconds(delayMs);
        if (AnimationConfig.ReduceMotion) Motion.Stop(tile, OpacityProperty, 1);
        else tile.BeginAnimation(OpacityProperty, WeatherMotion.To(moving ? tile.Opacity : 0, 1, 180, WeatherMotion.Out, delayMs));
        if (!moving && !AnimationConfig.ReduceMotion) tile.SetCurrentValue(OpacityProperty, 0d);
        Motion.To(scale, ScaleTransform.ScaleXProperty, 1, Motion.TileScale, from: moving ? null : fromScale, delay: delay);
        Motion.To(scale, ScaleTransform.ScaleYProperty, 1, Motion.TileScale, from: moving ? null : fromScale, delay: delay);
        tile.IsVisibleChanged += StopHidden;
        void StopHidden(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (tile.IsVisible) return;
            StopScale(scale);
            Motion.Stop(tile, OpacityProperty, 1);
            tile.IsVisibleChanged -= StopHidden;
        }
    }

    /// <summary>A quick bounce of an icon when its state flips, so the change is felt where the eye is.</summary>
    private static void Bounce(FrameworkElement glyph, double peak = 1.35)
    {
        if (AnimationConfig.ReduceMotion) return;
        var scale = FeedbackScale(glyph);
        glyph.RenderTransformOrigin = new Point(0.5, 0.5);
        Motion.To(scale, ScaleTransform.ScaleXProperty, peak, Motion.PressDown,
            done: () => Motion.To(scale, ScaleTransform.ScaleXProperty, 1, Motion.Press));
        Motion.To(scale, ScaleTransform.ScaleYProperty, peak, Motion.PressDown,
            done: () => Motion.To(scale, ScaleTransform.ScaleYProperty, 1, Motion.Press));
    }

    /// <summary>A number that rolls to its new value instead of jumping.</summary>
    private sealed class RollingNumber
    {
        private readonly WeatherTween _tween = new();
        private readonly FrameworkElement? _owner;
        private double _shown = double.NaN;

        public RollingNumber(Action<double> show, FrameworkElement? owner = null)
        {
            _tween.Step = show;
            _owner = owner;
            if (owner != null) owner.IsVisibleChanged += (_, _) =>
            {
                if (!owner.IsVisible && !double.IsNaN(_shown)) _tween.Stop(_shown);
            };
        }

        public void To(double value, double ms = 650)
        {
            if (_shown == value && ms > 0 && !AnimationConfig.ReduceMotion && _owner is not { IsVisible: false }) return;
            // From wherever the roll is right now, so a new value mid-roll just turns it around.
            double from = double.IsNaN(_shown) ? value : _tween.Value;
            _shown = value;
            if (from == value || ms <= 0 || _owner is { IsVisible: false }) _tween.Play(value, value, 0, WeatherMotion.OutQuint);
            else _tween.To(value);
        }

        public void Reveal()
        {
            if (!double.IsFinite(_shown) || _owner is not { IsVisible: true } || AnimationConfig.ReduceMotion ||
                Motion.IsRunning(_tween, WeatherTween.ValueProperty)) return;
            _tween.To(_shown, from: _shown * .8);
        }
    }

    #endregion

    #region Switch

    /// <summary>
    /// A pill switch: the knob springs across with a slight overshoot and stretches while it is held, like
    /// iOS; the track fades between grey and the section's tint. On the system tab a lit track wears the tab's
    /// own brush, so it keeps following the colour as the load changes.
    /// </summary>
    private FrameworkElement Toggle(bool isOn, Action<bool> onChanged)
    {
        var live = AccentIsMood ? _mood : null;
        var tint = ((SolidColorBrush)_accent).Color;
        var offColor = ((SolidColorBrush)Track).Color;
        var knobShift = new TranslateTransform(isOn ? 14 : 0, 0);
        var knobScale = new ScaleTransform(1, 1);
        var knob = new Border
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(8),
            Background = KnobBrush,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(2, 0, 0, 0),
            RenderTransform = new TransformGroup { Children = { knobScale, knobShift } },
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 4, ShadowDepth = 1, Opacity = 0.35, Direction = 270 }
        };
        var trackFill = new SolidColorBrush(isOn ? tint : offColor);
        var track = new Border
        {
            Width = 34,
            Height = 20,
            CornerRadius = new CornerRadius(10),
            Background = isOn && live != null ? live : trackFill,
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Child = knob
        };
        bool state = isOn;
        bool held = false;
        Squish(track);
        track.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            held = true;
            knobScale.CenterX = state ? 16 : 0;
            Motion.To(knobScale, ScaleTransform.ScaleXProperty, 1.3125, Motion.PressDown);
            Motion.To(knobShift, TranslateTransform.XProperty, state ? 14 : 0, Motion.Press);
        };
        track.MouseLeave += (_, _) =>
        {
            held = false;
            Settle();
        };
        track.LostMouseCapture += (_, _) => { held = false; Settle(); };
        track.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            held = false;
            state = !state;
            if (live != null)
            {
                // Sample the live tint before handing the track to its own transition brush.
                if (track.Background == live) trackFill.Color = live.Color;
                track.Background = trackFill;
            }
            if (AnimationConfig.ReduceMotion)
            {
                trackFill.BeginAnimation(SolidColorBrush.ColorProperty, null);
                trackFill.Color = state ? live?.Color ?? tint : offColor;
                if (state && live != null) track.Background = live;
            }
            else
            {
                bool next = state;
                var tintChange = new ColorAnimation(state ? live?.Color ?? tint : offColor, TimeSpan.FromMilliseconds(160));
                Timeline.SetDesiredFrameRate(tintChange, AnimationConfig.TargetFps);
                tintChange.Completed += (_, _) => { if (state == next && state && live != null) track.Background = live; };
                trackFill.BeginAnimation(SolidColorBrush.ColorProperty, tintChange);
            }
            Settle();
            onChanged(state);
        };
        return track;

        void Settle()
        {
            Motion.To(knobScale, ScaleTransform.ScaleXProperty, held ? 1.3125 : 1, Motion.Press);
            Motion.To(knobShift, TranslateTransform.XProperty, state ? 14 : 0, Motion.Press);
        }
    }

    #endregion
}
