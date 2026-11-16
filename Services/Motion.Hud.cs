using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media.Animation;

namespace VNotch.Services;

internal static partial class Motion
{
    private static readonly ConditionalWeakTable<FrameworkElement, MarginSpring> Margins = new();

    internal static void MarginTo(FrameworkElement element, Thickness margin, SpringMotion spring)
    {
        var state = Margins.GetValue(element, target => new MarginSpring(target));
        To(state, MarginSpring.LeftProperty, margin.Left, spring);
        To(state, MarginSpring.TopProperty, margin.Top, spring);
        To(state, MarginSpring.RightProperty, margin.Right, spring);
        To(state, MarginSpring.BottomProperty, margin.Bottom, spring);
    }

    // Separate scalar springs keep each inset's velocity when the layout changes direction.
    private sealed class MarginSpring : Animatable
    {
        internal static readonly DependencyProperty LeftProperty = Register("Left");
        internal static readonly DependencyProperty TopProperty = Register("Top");
        internal static readonly DependencyProperty RightProperty = Register("Right");
        internal static readonly DependencyProperty BottomProperty = Register("Bottom");
        private readonly FrameworkElement _element;

        internal MarginSpring(FrameworkElement element)
        {
            _element = element;
            var current = element.Margin;
            SetValue(LeftProperty, current.Left);
            SetValue(TopProperty, current.Top);
            SetValue(RightProperty, current.Right);
            SetValue(BottomProperty, current.Bottom);
        }

        private static DependencyProperty Register(string name) => DependencyProperty.Register(name, typeof(double),
            typeof(MarginSpring), new PropertyMetadata(0d, (target, _) => ((MarginSpring)target).Apply()));

        private void Apply() => _element.Margin = new Thickness((double)GetValue(LeftProperty),
            (double)GetValue(TopProperty), (double)GetValue(RightProperty), (double)GetValue(BottomProperty));

        protected override Freezable CreateInstanceCore() => new MarginSpring(_element);
    }
}
