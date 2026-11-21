using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Hardcodet.Wpf.TaskbarNotification;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Controllers;
using VNotch.Models;
using VNotch.Services;
using VNotch.Tests.Fakes;
using Xunit;
using Xunit.Abstractions;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class FirstOpenLatencyTests(ITestOutputHelper output)
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void FirstSettingsOpenKeepsTheUiDispatcherResponsive() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = Create();
        var window = fixture.Window;
        window.Show();
        HoverTray(window);
        await WaitForPreparedSettingsAsync(window, ct);

        var pauses = new List<double>();
        void Capture(string message) => output.WriteLine(message);
        SettingsWindow.FirstOpenProbe += Capture;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var watch = Stopwatch.StartNew();
            double lastTick = 0, longestPause = 0;
            bool closed = false;
            var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) =>
            {
                double now = watch.Elapsed.TotalMilliseconds;
                longestPause = Math.Max(longestPause, now - lastTick);
                lastTick = now;
                if (now >= 500 && Application.Current.Windows.OfType<SettingsWindow>().LastOrDefault() is { } settings)
                {
                    settings.Closed += (_, _) => closed = true;
                    settings.Close();
                }
            };
            timer.Start();
            var starts = new Dictionary<DispatcherOperation, double>();
            void Started(object? s, DispatcherHookEventArgs e) => starts[e.Operation] = watch.Elapsed.TotalMilliseconds;
            void Finished(object? s, DispatcherHookEventArgs e)
            {
                if (!starts.Remove(e.Operation, out var start)) return;
                if (watch.Elapsed.TotalMilliseconds - start < 25) return;
                var op = e.Operation;
                var callback = typeof(DispatcherOperation).GetField("_method", Private)?.GetValue(op) as Delegate;
                output.WriteLine($"[{start:F0}] {watch.Elapsed.TotalMilliseconds - start:F0} {op.Priority} {callback?.Method.DeclaringType}/{callback?.Method.Name}");
            }
            window.Dispatcher.Hooks.OperationStarted += Started;
            window.Dispatcher.Hooks.OperationCompleted += Finished;
            try
            {
                typeof(MainWindow).GetMethod("OpenAppSettings", Private)!.Invoke(window, [false]);
                output.WriteLine($"click returned at {watch.Elapsed.TotalMilliseconds:F0}");
                await WpfFrameWaiter.UntilAsync(() => closed, "settings close completes", ct);
            }
            finally { timer.Stop(); window.Dispatcher.Hooks.OperationStarted -= Started; window.Dispatcher.Hooks.OperationCompleted -= Finished; }
            pauses.Add(longestPause);
            output.WriteLine($"Settings open {attempt + 1}: longest UI pause {longestPause:F1} ms");
            await WpfFrameWaiter.UntilAsync(() => !Animating(window), "settings return settles", ct);
        }
        SettingsWindow.FirstOpenProbe -= Capture;
        Assert.True(pauses[0] < 100, $"First Settings click stalled the UI for {pauses[0]:F1} ms (second: {pauses[1]:F1} ms).");
    });

    [Fact]
    public void FirstNotchExpansionKeepsTheUiDispatcherResponsive() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = Create();
        var window = fixture.Window;
        window.Show();
        await WpfFrameWaiter.UntilAsync(() => !Animating(window), "notch startup settles", ct);
        var coordinator = (NotchTransitionCoordinator)typeof(MainWindow).GetField("_transitionCoordinator", Private)!.GetValue(window)!;
        var pauses = new List<double>();
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var watch = Stopwatch.StartNew();
            double lastTick = 0, longestPause = 0;
            var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) =>
            {
                double now = watch.Elapsed.TotalMilliseconds;
                longestPause = Math.Max(longestPause, now - lastTick);
                lastTick = now;
            };
            timer.Start();
            try
            {
                coordinator.RequestView(NotchView.Media, "first-open-latency");
                await WpfFrameWaiter.UntilAsync(() => !Animating(window) && coordinator.CurrentView == NotchView.Media, "notch expansion completes", ct);
            }
            finally { timer.Stop(); }
            pauses.Add(longestPause);
            output.WriteLine($"Notch open {attempt + 1}: longest UI pause {longestPause:F1} ms");
            coordinator.RequestView(NotchView.Compact, "first-open-latency");
            await WpfFrameWaiter.UntilAsync(() => !Animating(window) && coordinator.CurrentView == NotchView.Compact, "notch collapse completes", ct);
        }
        Assert.True(pauses[0] < 100, $"First notch click stalled the UI for {pauses[0]:F1} ms (second: {pauses[1]:F1} ms).");
    });

    [Fact]
    public void SettingsPreparationWaitsForTrayHoverAfterTheStartupEntrance() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = Create();
        var window = fixture.Window;
        bool? afterEntrance = null;
        void Capture(string message)
        {
            if (message.Contains("Settings XAML", StringComparison.Ordinal))
                afterEntrance = (bool)typeof(MainWindow).GetField("_postAppearInitializationStarted", Private)!.GetValue(window)!;
        }
        SettingsWindow.FirstOpenProbe += Capture;
        try
        {
            window.Show();
            await WpfFrameWaiter.UntilAsync(() =>
                (bool)typeof(MainWindow).GetField("_postAppearInitializationStarted", Private)!.GetValue(window)!,
                "startup entrance finishes", ct);
            await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);
            Assert.Null(typeof(MainWindow).GetField("_preparedSettingsWindow", Private)!.GetValue(window));
            Assert.Null(afterEntrance);
            HoverTray(window);
            await WaitForPreparedSettingsAsync(window, ct);
            Assert.True(afterEntrance);
        }
        finally { SettingsWindow.FirstOpenProbe -= Capture; }
    });

    [Fact]
    public void SettingsPreparationExpiresAndTheNextHoverPreparesAFreshWindow() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = Create();
        var window = fixture.Window;
        window.Show();
        HoverTray(window);
        var first = await WaitForPreparedSettingsAsync(window, ct);
        var expiryField = typeof(MainWindow).GetField("_settingsPreparationExpiry", Private)!;
        var expiry = Assert.IsType<UiScheduledTimer>(expiryField.GetValue(window));
        Assert.Equal(TimeSpan.FromSeconds(30), expiry.Interval);
        long firstDeadline = expiry.DueTimestamp;
        HoverTray(window);
        Assert.True(expiry.DueTimestamp > firstDeadline);
        bool closed = false;
        first.Closed += (_, _) => closed = true;
        string originalLanguage = Loc.CurrentLanguage;
        try
        {
            // Expiring an unused Settings snapshot must preserve changes made elsewhere.
            Loc.SetLanguage("vi");
            expiry.Interval = TimeSpan.FromMilliseconds(1);
            await WpfFrameWaiter.UntilAsync(() => closed, "unused settings window is released", ct);
            Assert.Equal("vi", Loc.CurrentLanguage);
        }
        finally { Loc.SetLanguage(originalLanguage); }
        Assert.Null(expiryField.GetValue(window));
        Assert.Null(typeof(MainWindow).GetField("_preparedSettingsWindow", Private)!.GetValue(window));

        HoverTray(window);
        var next = await WaitForPreparedSettingsAsync(window, ct);
        Assert.NotSame(first, next);
        typeof(MainWindow).GetMethod("OpenAppSettings", Private)!.Invoke(window, [false]);
        Assert.True(next.IsVisible);
        Assert.Null(expiryField.GetValue(window));
        HoverTray(window);
        await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);
        Assert.Null(typeof(MainWindow).GetField("_preparedSettingsWindow", Private)!.GetValue(window));
    });

    private static void HoverTray(MainWindow window) =>
        window.TrayIcon.RaiseEvent(new RoutedEventArgs(TaskbarIcon.TrayMouseMoveEvent));

    private static async Task<SettingsWindow> WaitForPreparedSettingsAsync(MainWindow window, CancellationToken ct)
    {
        var preparedField = typeof(MainWindow).GetField("_preparedSettingsWindow", Private)!;
        await WpfFrameWaiter.UntilAsync(() =>
            preparedField.GetValue(window) is SettingsWindow { IsLoaded: true, IsVisible: false } &&
            typeof(MainWindow).GetField("_settingsPreparationExpiry", Private)!.GetValue(window) != null,
            "tray hover finishes preparing Settings", ct);
        return (SettingsWindow)preparedField.GetValue(window)!;
    }

    private static bool Animating(MainWindow window) =>
        (bool)typeof(MainWindow).GetProperty("_isAnimating", Private)!.GetValue(window)!;

    private static GreetingAcceptanceTests.MainWindowFixture Create() => new("en", false,
        settings => { settings.EnableLocalOnlyMode = true; settings.EnableDiagnosticLogging = false; },
        services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));
}
