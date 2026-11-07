using System.Reflection;
using VNotch.Models;
using VNotch.Services;
using VNotch.Windows;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class SpotlightWinTapTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void ToggleWhileClosingFromAFocusLossClosesInsteadOfReopening() => WithOpenSpotlight(async (window, ct) =>
    {
        // Another app takes the focus: Spotlight starts closing by itself, but to the user it is still open.
        Invoke(window, "Window_Deactivated", window, EventArgs.Empty);
        await WpfFrameWaiter.UntilAsync(() => Field<bool>(window, "_isClosing"), "focus-loss close", ct);

        window.ToggleFromHotkey(); // the second Win press

        Assert.False(window.IsSpotlightOpen);
        Assert.False(Field<bool>(window, "_isClosing"));
    });

    [Fact]
    public void ToggleWhileClosingFromAToggleReopens() => WithOpenSpotlight((window, _) =>
    {
        window.DismissFromGlobalShortcut(); // the second Win press
        Assert.True(Field<bool>(window, "_isClosing"));

        window.ToggleFromHotkey(); // a third press mid-way out brings it back

        Assert.True(window.IsSpotlightOpen);
        Assert.False(Field<bool>(window, "_isClosing"));
        return Task.CompletedTask;
    });

    private static void WithOpenSpotlight(Func<SpotlightWindow, CancellationToken, Task> test) => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(false);
        try
        {
            using var fixture = new SpotlightWindowFixture(new NotchSettings { EnableSpotlightHistory = false, AutoCheckUpdates = false });
            var window = fixture.Window;
            window.ShowSpotlight();
            await WpfFrameWaiter.UntilAsync(() => !Field<bool>(window, "_entranceActive"), "Spotlight entrance", ct);
            await test(window, ct);
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    private static T Field<T>(SpotlightWindow window, string name) => (T)typeof(SpotlightWindow).GetField(name, Private)!.GetValue(window)!;
    private static void Invoke(SpotlightWindow window, string name, params object?[] args) => typeof(SpotlightWindow).GetMethod(name, Private)!.Invoke(window, args);
}
