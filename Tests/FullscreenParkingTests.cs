using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Contracts;
using VNotch.Controllers;
using VNotch.Services;
using VNotch.Tests.Fakes;
using Xunit;

namespace VNotch.Tests;

/// <summary>
/// A see-through top-most window over a fullscreen game makes Windows compose every game frame, so once the notch
/// has slid away for one, its window leaves the screen, and it is back before anything shows again.
/// </summary>
[Collection("Localization")]
public sealed class FullscreenParkingTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void WindowLeavesTheScreenBehindAFullscreenAppAndComesBackFirst() => WithShownNotch(async (window, hwnd, ct) =>
    {
        Invoke(window, "FullscreenController_HideStateChanged", true);
        await WpfFrameWaiter.UntilAsync(() => !Win32Interop.IsWindowVisible(hwnd), "window off the screen", ct);
        Assert.True(window.IsVisible);

        // Size and z-order upkeep passes SWP_SHOWWINDOW; that must not put it back over the game.
        Field<OverlayWindowController>(window, "_overlayWindow").ReassertBounds();
        Assert.False(Win32Interop.IsWindowVisible(hwnd));

        Invoke(window, "FullscreenController_HideStateChanged", false);
        Assert.True(Win32Interop.IsWindowVisible(hwnd));
    });

    [Fact]
    public void MutedMicTabKeepsTheWindowOnTheScreen() => WithShownNotch(async (window, hwnd, ct) =>
    {
        SetField(window, "_micMutedShown", true);
        Invoke(window, "FullscreenController_HideStateChanged", true);
        await WpfFrameWaiter.UntilAsync(() => !Field<bool>(window, "_isFullscreenSlideAnimating"), "notch slid away", ct);
        Assert.True(Win32Interop.IsWindowVisible(hwnd));

        SetField(window, "_micMutedShown", false);
        Invoke(window, "ApplyMicMutedPresentation");
        await WpfFrameWaiter.UntilAsync(() => !Win32Interop.IsWindowVisible(hwnd), "window off after the tab left", ct);
    });

    private static void WithShownNotch(Func<MainWindow, IntPtr, CancellationToken, Task> test)
        => SharedStaTestRunner.RunAsync(async ct =>
        {
            bool reducedMotion = AnimationConfig.ReduceMotion;
            await using var fixture = new GreetingAcceptanceTests.MainWindowFixture("en", false,
                settings => settings.EnableLocalOnlyMode = true,
                services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));
            var window = fixture.Window;
            try
            {
                AnimationConfig.SetReduceMotion(true);
                window.ScreenCaptureActivityProbe = () => false;
                window.ShowActivated = false;
                window.Show();
                await WpfFrameWaiter.UntilAsync(() => Field<bool>(window, "_isStartupLayoutReady"), "startup", ct);
                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                Assert.True(Win32Interop.IsWindowVisible(hwnd));
                await test(window, hwnd, ct);
            }
            finally { AnimationConfig.SetReduceMotion(reducedMotion); }
        });

    private static object? Invoke(MainWindow window, string method, params object?[] args) =>
        typeof(MainWindow).GetMethod(method, Private)!.Invoke(window, args);
    private static T Field<T>(MainWindow window, string name) =>
        (T)typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
    private static void SetField(MainWindow window, string name, object value) =>
        typeof(MainWindow).GetField(name, Private)!.SetValue(window, value);
}
