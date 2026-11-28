using System.Reflection;
using System.Windows;
using VNotch.Models;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

/// <summary>
/// The search caret's blink is a Forever animation. Left running behind a closed Spotlight it kept the whole app
/// repainting at the display rate, so it must stop whenever the caret is not shown.
/// </summary>
[Collection("Localization")]
public sealed class SpotlightCaretBlinkTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void LosingFocusStopsTheBlink() => SharedStaTestRunner.Run(() =>
    {
        using var fixture = new SpotlightWindowFixture(new NotchSettings(), saveChatHistory: true);
        var window = fixture.Window;
        StartBlink(window);
        Assert.True(window.GlowingCaret.HasAnimatedProperties);

        typeof(SpotlightWindow).GetMethod("SearchBox_LostFocus", Private)!.Invoke(window, [window.SearchBox, new RoutedEventArgs()]);

        Assert.False(window.GlowingCaret.HasAnimatedProperties);
        Assert.Equal(Visibility.Collapsed, window.GlowingCaret.Visibility);
    });

    [Fact]
    public void HidingSpotlightStopsTheBlink() => SharedStaTestRunner.Run(() =>
    {
        using var fixture = new SpotlightWindowFixture(new NotchSettings(), saveChatHistory: true);
        var window = fixture.Window;
        window.Show();
        StartBlink(window);
        Assert.True(window.GlowingCaret.HasAnimatedProperties);

        window.Hide();

        Assert.False(window.GlowingCaret.HasAnimatedProperties);
    });

    private static void StartBlink(SpotlightWindow window)
    {
        bool reduced = AnimationConfig.ReduceMotion;
        try
        {
            AnimationConfig.SetReduceMotion(false);
            typeof(SpotlightWindow).GetMethod("ResetGlowingCaretBlink", Private)!.Invoke(window, null);
        }
        finally { AnimationConfig.SetReduceMotion(reduced); }
    }
}
