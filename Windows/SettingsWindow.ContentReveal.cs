using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VNotch.Services;

namespace VNotch;

public partial class SettingsWindow
{
    private readonly Dictionary<Panel, Action> _sectionArrivals = [];

    private void AnimateActivePanel(string section, int baseDelayMs = 0)
    {
        ResetProgressiveBlurElements(isMenuList: false);
        if (_navPanels.TryGetValue(section, out var page))
            AnimateSectionContent(page, 6 * _lastNavDirection, baseDelayMs);
    }

    private void StopComponentReveal()
    {
        foreach (var settle in _sectionArrivals.Values.ToArray()) settle();
    }

    private void RevealSettingsRows(Panel group) => AnimateSectionContent(group, -4, 0, fade: false);

    private void AnimateSectionContent(Panel content, double drift, int delayMs, bool fade = true)
    {
        if (!content.IsVisible || !IsLoaded || AnimationConfig.ReduceMotion ||
            !AnimationConfig.ClientAreaAnimation || _sectionArrivals.ContainsKey(content)) return;

        double restingOpacity = (double)content.GetAnimationBaseValue(OpacityProperty);
        var offset = EnsureElementTranslateTransform(content);
        var delay = TimeSpan.FromMilliseconds(delayMs);
        void Settle()
        {
            _sectionArrivals.Remove(content);
            content.IsVisibleChanged -= OnVisibleChanged;
            if (fade)
            {
                Motion.Forget(content, OpacityProperty);
                content.BeginAnimation(OpacityProperty, null);
            }
            Motion.Stop(offset, TranslateTransform.YProperty, 0);
        }
        void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!content.IsVisible) Settle();
        }
        _sectionArrivals.Add(content, Settle);
        content.IsVisibleChanged += OnVisibleChanged;
        if (fade) Motion.To(content, OpacityProperty, restingOpacity, Motion.Exit, from: restingOpacity * 0.7, delay: delay);
        Motion.To(offset, TranslateTransform.YProperty, 0, Motion.Shift, from: drift, delay: delay, done: Settle);
    }
}
