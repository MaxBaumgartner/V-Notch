using System;
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
    /// Makes a card feel physical: its background fades to <paramref name="hover"/> under the pointer, a press
    /// squishes it a little (wider than tall, like a soft key) and letting go springs it back. With
    /// <paramref name="grow"/> it also rises a little toward the pointer, with a spring.
    /// </summary>
    private static void Pressable(Border card, Func<Brush> rest, Brush hover, bool gentle = false, bool grow = false)
    {
        var scale = card.RenderTransform as ScaleTransform ?? new ScaleTransform(1, 1);
        card.RenderTransform = scale;
        card.RenderTransformOrigin = new Point(0.5, 0.5);
        var fill = card.Background is SolidColorBrush { IsFrozen: false } own ? own : new SolidColorBrush(((SolidColorBrush)rest()).Color);
        card.Background = fill;
        Color HoverColor() => Blend(((SolidColorBrush)rest()).Color, ((SolidColorBrush)hover).Color);

        card.MouseEnter += (_, _) =>
        {
            WeatherMotion.Tint(fill, HoverColor(), 140);
            if (grow) Release(scale, gentle, HoverGrow);
        };
        card.MouseLeave += (_, _) =>
        {
            WeatherMotion.Tint(fill, ((SolidColorBrush)rest()).Color, 220);
            Release(scale, gentle);
        };
        card.PreviewMouseLeftButtonDown += (_, _) =>
        {
            if (AnimationConfig.ReduceMotion) return;
            // Gentle (Legion): a lighter press and a softer spring back.
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, WeatherMotion.To(scale.ScaleX, gentle ? 0.975 : 0.965, 110, PressEase));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, WeatherMotion.To(scale.ScaleY, gentle ? 0.945 : 0.93, 110, PressEase));
        };
        card.PreviewMouseLeftButtonUp += (_, _) => Release(scale, gentle, grow && card.IsMouseOver ? HoverGrow : 1);
    }

    /// <summary>A button sinks a little while pressed and springs back on release.</summary>
    private static void Squish(Border button)
    {
        var scale = new ScaleTransform(1, 1);
        button.RenderTransform = scale;
        button.RenderTransformOrigin = new Point(0.5, 0.5);
        button.PreviewMouseLeftButtonDown += (_, _) =>
        {
            if (AnimationConfig.ReduceMotion) return;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, WeatherMotion.To(scale.ScaleX, 0.92, 100, PressEase));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, WeatherMotion.To(scale.ScaleY, 0.92, 100, PressEase));
        };
        button.PreviewMouseLeftButtonUp += (_, _) => Release(scale);
        button.MouseLeave += (_, _) => Release(scale);
    }

    private static void Release(ScaleTransform scale, bool gentle = false, double to = 1)
    {
        if (AnimationConfig.ReduceMotion) return;
        var ease = gentle ? SoftSpring : ReleaseSpring;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, WeatherMotion.To(scale.ScaleX, to, 520, ease));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, WeatherMotion.To(scale.ScaleY, to, 520, ease));
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
                        PopIn(element, (baseDelay + TimeSpan.FromMilliseconds(Math.Min(index++ * 40, 320))).TotalMilliseconds);
                continue;
            }
            var delay = baseDelay + TimeSpan.FromMilliseconds(Math.Min(index++ * 30, 240));
            // A pressable card on its own row keeps its scale for the squish: it pops instead of rising.
            if (row.RenderTransform is ScaleTransform) PopIn(row, delay.TotalMilliseconds, fromScale: 0.92);
            else RevealElement(row, delay);
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
    private static void PopIn(FrameworkElement tile, double delayMs, double fromScale = 0.82)
    {
        if (AnimationConfig.ReduceMotion) return;
        var scale = tile.RenderTransform as ScaleTransform ?? new ScaleTransform(1, 1);
        tile.RenderTransform = scale;
        tile.RenderTransformOrigin = new Point(0.5, 0.5);
        tile.Opacity = 0;
        var fade = WeatherMotion.To(0, 1, 260, WeatherMotion.Out, delayMs);
        fade.Completed += (_, _) =>
        {
            tile.BeginAnimation(OpacityProperty, null);
            tile.Opacity = 1;
        };
        tile.BeginAnimation(OpacityProperty, fade);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, WeatherMotion.To(fromScale, 1, 620, ReleaseSpring, delayMs));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, WeatherMotion.To(fromScale, 1, 620, ReleaseSpring, delayMs));
    }

    /// <summary>A quick bounce of an icon when its state flips, so the change is felt where the eye is.</summary>
    private static void Bounce(FrameworkElement glyph, double peak = 1.35)
    {
        if (AnimationConfig.ReduceMotion) return;
        var scale = glyph.RenderTransform as ScaleTransform ?? new ScaleTransform(1, 1);
        glyph.RenderTransform = scale;
        glyph.RenderTransformOrigin = new Point(0.5, 0.5);
        var bounce = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(560) };
        bounce.KeyFrames.Add(new EasingDoubleKeyFrame(peak, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120)), PressEase));
        bounce.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(560)), ReleaseSpring));
        Timeline.SetDesiredFrameRate(bounce, AnimationConfig.TargetFps);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, bounce);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, bounce);
    }

    /// <summary>A number that rolls to its new value instead of jumping.</summary>
    private sealed class RollingNumber
    {
        private readonly WeatherTween _tween = new();
        private double _shown = double.NaN;

        public RollingNumber(Action<double> show) => _tween.Step = show;

        public void To(double value, double ms = 650)
        {
            // From wherever the roll is right now, so a new value mid-roll just turns it around.
            double from = double.IsNaN(_shown) ? value : _tween.Value;
            _shown = value;
            _tween.Play(from, value, from == value ? 0 : ms, WeatherMotion.OutQuint);
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
        var knob = new Border
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(8),
            Background = KnobBrush,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(2, 0, 0, 0),
            RenderTransform = knobShift,
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
        const double Stretch = 5;
        track.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            if (AnimationConfig.ReduceMotion) return;
            // Held: the knob grows toward the middle.
            knob.BeginAnimation(WidthProperty, WeatherMotion.To(knob.ActualWidth, 16 + Stretch, 120, PressEase));
            if (state) knobShift.BeginAnimation(TranslateTransform.XProperty, WeatherMotion.To(knobShift.X, 14 - Stretch, 120, PressEase));
        };
        track.MouseLeave += (_, _) =>
        {
            if (knob.Width > 16 || knob.HasAnimatedProperties) Settle();
        };
        track.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            state = !state;
            if (live != null)
            {
                // Fade on a brush of its own, then hand the lit track back to the tab's colour.
                trackFill.BeginAnimation(SolidColorBrush.ColorProperty, null);
                trackFill.Color = state ? offColor : live.Color;
                track.Background = trackFill;
                if (state)
                {
                    var settle = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
                    settle.Tick += (_, _) =>
                    {
                        settle.Stop();
                        if (state) track.Background = live;
                    };
                    settle.Start();
                }
            }
            WeatherMotion.Tint(trackFill, state ? live?.Color ?? tint : offColor, 220);
            Settle();
            onChanged(state);
        };
        return track;

        void Settle()
        {
            if (AnimationConfig.ReduceMotion)
            {
                knob.BeginAnimation(WidthProperty, null);
                knobShift.BeginAnimation(TranslateTransform.XProperty, null);
                knobShift.X = state ? 14 : 0;
                return;
            }
            knob.BeginAnimation(WidthProperty, WeatherMotion.To(knob.ActualWidth, 16, 380, KnobSpring));
            knobShift.BeginAnimation(TranslateTransform.XProperty, WeatherMotion.To(knobShift.X, state ? 14 : 0, 380, KnobSpring));
        }
    }

    #endregion
}
