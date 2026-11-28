using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

[Collection(SpotlightWindowAnimationCollection.Name)]
public sealed class SpotlightAiRevealTests
{
    [Fact]
    public void CompletedArrivalCanReplayRepeatedly()
    {
        SharedStaTestRunner.RunAsync(async ct =>
        {
            bool previous = AnimationConfig.ReduceMotion;
            var element = new Border();
            var window = new BackgroundWindow { Content = element, Width = 100, Height = 100 };
            try
            {
                AnimationConfig.SetReduceMotion(false);
                window.Show();
                var method = typeof(SpotlightWindow).GetMethod("AnimateAiArrival", BindingFlags.Static | BindingFlags.NonPublic);
                Assert.NotNull(method);
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    // Reproduce a completed clock that remains attached to the element.
                    var completed = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(1))
                    { FillBehavior = FillBehavior.Stop };
                    var clock = completed.CreateClock();
                    element.ApplyAnimationClock(UIElement.OpacityProperty, clock);
                    clock.Controller!.SkipToFill();
                    Assert.True(element.HasAnimatedProperties);
                    Assert.Equal(1d, element.Opacity);

                    // Observe the fade whenever WPF publishes it. A fixed 80ms
                    // sample can arrive after completion on an instrumented runner.
                    double minimumOpacity = 1;
                    var descriptor = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(UIElement.OpacityProperty, typeof(Border));
                    EventHandler observe = (_, _) => minimumOpacity = Math.Min(minimumOpacity, element.Opacity);
                    descriptor.AddValueChanged(element, observe);
                    try
                    {
                        method.Invoke(null, new object[] { element });
                        var scale = Assert.IsType<TransformGroup>(element.RenderTransform).Children.OfType<ScaleTransform>().Single();
                        Assert.True(scale.ScaleY < 1 || Motion.IsRunning(scale, ScaleTransform.ScaleYProperty));
                        observe(null, EventArgs.Empty);
                        element.UpdateLayout();
                        await WpfFrameWaiter.UntilAsync(
                            () => minimumOpacity < 1 && element.Opacity == 1d,
                            $"AI reveal {attempt + 1} completion", ct);
                    }
                    finally { descriptor.RemoveValueChanged(element, observe); }
                    Assert.True(minimumOpacity < 1, $"Reveal {attempt + 1} never published a fade-in value.");
                    Assert.Equal(1d, element.Opacity);
                }
            }
            finally
            {
                window.Close();
                element.BeginAnimation(UIElement.OpacityProperty, null);
                AnimationConfig.SetReduceMotion(previous);
            }
        });
    }
}
