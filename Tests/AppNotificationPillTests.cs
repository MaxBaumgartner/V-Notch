using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.ControlCenter;
using VNotch.Tests.Fakes;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class AppNotificationPillTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void CodeChipCopiesAndDoesNotExpand() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = Create();
        var window = fixture.Window;
        await ShowAsync(window, ct);
        string? copied = null;
        int launches = 0;
        window.AppPillClipboardWriter = code => copied = code;
        window.AppLauncher = _ => { launches++; return true; };
        ShowPill(window, Toast("Messages", "Your code is 482193"));
        var chip = Element<FrameworkElement>(window, "AppPillCodeChip");
        var text = Element<TextBlock>(window, "AppPillCodeText");
        var tick = Element<System.Windows.Shapes.Path>(window, "AppPillCodeTick");
        Assert.Equal(Visibility.Visible, chip.Visibility);
        Assert.Equal("482193", text.Text);
        Assert.Equal(Visibility.Hidden, tick.Visibility);

        void TapCode()
        {
            var down = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                { RoutedEvent = Mouse.MouseDownEvent };
            text.RaiseEvent(down);
            Assert.True(down.Handled);
            var up = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                { RoutedEvent = Mouse.MouseUpEvent };
            text.RaiseEvent(up);
            Assert.True(up.Handled);
        }

        TapCode();
        Assert.Equal("482193", copied);
        Assert.False(Get<bool>(window, "_appPillExpanded"));
        Assert.Equal(0, launches);
        Assert.Equal(Visibility.Visible, tick.Visibility);
        await WpfFrameWaiter.UntilAsync(() => Math.Abs(tick.StrokeDashOffset) < .005, "tick drawn", ct);

        Tap(window);
        copied = null;
        TapCode();
        Assert.Equal("482193", copied);
        Assert.True(Get<bool>(window, "_appPillExpanded"));
        Assert.Equal(Visibility.Visible, chip.Visibility);
        Assert.Equal(0, launches);

        Invoke(window, "CollapseAppPill");
        ShowPill(window, Toast("Messages", "PIN: 1234"));
        Assert.Equal(Visibility.Hidden, tick.Visibility);
        window.AppPillClipboardWriter = _ => throw new System.Runtime.InteropServices.COMException("Clipboard busy");
        TapCode();
        Assert.Equal(Visibility.Hidden, tick.Visibility);
        Assert.False(Get<bool>(window, "_appPillExpanded"));

        bool oldReduceMotion = AnimationConfig.ReduceMotion;
        try
        {
            AnimationConfig.SetReduceMotion(true);
            window.AppPillClipboardWriter = code => copied = code;
            TapCode();
            Assert.Equal("1234", copied);
            Assert.Equal(0, tick.StrokeDashOffset);
            Assert.False(tick.HasAnimatedProperties);
            Assert.False(Get<bool>(window, "_appPillExpanded"));
        }
        finally { AnimationConfig.SetReduceMotion(oldReduceMotion); }

        ShowPill(window, Toast("Shop", "Bestellung 4711 versendet"));
        Assert.Equal(Visibility.Collapsed, chip.Visibility);
    });

    [Fact]
    public void CompactShowsSenderAndTwoLines() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = Create();
        var window = fixture.Window;
        await ShowAsync(window, ct);
        ShowPill(window, Toast("Anna", "one", "two", "three"));
        await Task.Delay(400, ct);
        Assert.Equal(Visibility.Visible, Element<FrameworkElement>(window, "AppNotificationPill").Visibility);
        Assert.Equal("Anna", Element<TextBlock>(window, "AppPillSender").Text);
        var body = Element<TextBlock>(window, "AppPillBody");
        Assert.Equal("one\ntwo", body.Text);
        Assert.Equal(TextTrimming.CharacterEllipsis, body.TextTrimming);
        Assert.Equal(body.LineHeight * 2, body.ActualHeight, precision: 1);
        Assert.Equal(28, Element<System.Windows.Controls.Image>(window, "AppPillLogo").Width);
        await WpfFrameWaiter.UntilAsync(() => Math.Abs(window.NotchBorder.Width - 380) < .1, "app pill entrance complete", ct);
        double pillWidth = window.NotchBorder.Width;
        Invoke(window, "UpdateMusicCompactMode", new MediaInfo { IsAnyMediaPlaying = true, IsPlaying = true, CurrentTrack = "Song" });
        await Task.Delay(500, ct);
        Assert.InRange(Math.Abs(pillWidth - window.NotchBorder.Width), 0, .1);
        Assert.True((bool)typeof(MainWindow).GetProperty("IsPrivacySpotTaken", Private)!.GetValue(window)!);
    });

    [Fact]
    public void CompactAtDefaultHeightKeepsBothBodyLinesInsideThePill() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = Create(height: 34);
        var window = fixture.Window;
        await ShowAsync(window, ct);
        ShowPill(window, Toast("Anna", "one", "two", "three"));
        await WpfFrameWaiter.UntilAsync(() => Math.Abs(window.NotchBorder.Width - 380) < .1, "app pill entrance complete", ct);
        var pill = Element<FrameworkElement>(window, "AppNotificationPill");
        foreach (string name in new[] { "AppPillSender", "AppPillBody" })
        {
            var text = Element<TextBlock>(window, name);
            Rect bounds = text.TransformToAncestor(pill).TransformBounds(new Rect(text.RenderSize));
            Assert.True(bounds.Top >= -.1 && bounds.Bottom <= pill.ActualHeight + .1,
                $"{name} bounds {bounds.Top}..{bounds.Bottom} exceed pill height {pill.ActualHeight}.");
            Rect shellBounds = text.TransformToAncestor(window.NotchBorder).TransformBounds(new Rect(text.RenderSize));
            Assert.True(shellBounds.Top >= -.1 && shellBounds.Bottom <= window.NotchBorder.ActualHeight + .1,
                $"{name} extends outside the notch shell.");
        }
    });

    [Fact]
    public void NoPillWhileAttentionBlocked() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = Create();
        var window = fixture.Window;
        await ShowAsync(window, ct);
        var shell = typeof(MainWindow).GetField("_shellState", Private)!.GetValue(window)!;
        shell.GetType().GetProperty("IsNotchVisible")!.SetValue(shell, false);
        Assert.False((bool)typeof(MainWindow).GetProperty("CanShowClaudeAttention", Private)!.GetValue(window)!);
        ShowPill(window, Toast("Anna", "one"));
        Assert.Equal(Visibility.Collapsed, Element<FrameworkElement>(window, "AppNotificationPill").Visibility);
    });

    [Fact]
    public void FirstTapExpandsWithAllLines() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = Create();
        var window = fixture.Window;
        await ShowAsync(window, ct);
        ShowPill(window, Toast("Anna", "one", "two", "three"));
        Assert.True(Tap(window));
        Assert.True(Get<bool>(window, "_appPillExpanded"));
        var expanded = Element<TextBlock>(window, "AppPillExpandedBody");
        Assert.Equal(Visibility.Visible, expanded.Visibility);
        Assert.Equal("one\ntwo\nthree", expanded.Text);
        Assert.Equal(Visibility.Collapsed, Element<TextBlock>(window, "AppPillBody").Visibility);
        await WpfFrameWaiter.UntilAsync(() => window.NotchBorder.Height > window.NotchBorder.MinHeight + 60, "pill grew", ct);
    });

    [Fact]
    public void SecondTapOpensTheAppOnce() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = Create();
        var window = fixture.Window;
        await ShowAsync(window, ct);
        var opened = new List<string>();
        window.AppLauncher = id => { opened.Add(id); return true; };
        ShowPill(window, Toast("Anna", "one"));
        Tap(window);
        Assert.True(Tap(window));
        Assert.Equal(new[] { "test.app" }, opened);
        Assert.False(Get<bool>(window, "_appPillExpanded"));
        await Task.CompletedTask;
    });

    [Fact]
    public void SecondTapWithoutAppIdCollapses() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = Create();
        var window = fixture.Window;
        await ShowAsync(window, ct);
        int launches = 0;
        window.AppLauncher = _ => { launches++; return true; };
        ShowPill(window, new WindowsNotification(1, "Old app", null, "Anna", "one", null, new[] { "one" }, null));
        Tap(window);
        Assert.True(Tap(window));
        Assert.Equal(0, launches);
        Assert.False(Get<bool>(window, "_appPillExpanded"));
        await Task.CompletedTask;
    });

    [Fact]
    public void ExpandedWrapsALongLinkWithinSixLines() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = Create();
        var window = fixture.Window;
        await ShowAsync(window, ct);
        ShowPill(window, Toast("Anna", "https://example.org/" + new string('a', 400)));
        Tap(window);
        var expanded = Element<TextBlock>(window, "AppPillExpandedBody");
        await WpfFrameWaiter.UntilAsync(() => expanded.ActualHeight > 0, "expanded laid out", ct);
        Assert.InRange(expanded.ActualHeight, 1, expanded.LineHeight * 6 + .5);
    });

    [Fact]
    public void CompactReplacementKeepsTheOldFaceWhileTheNewFaceFadesIn() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = Create();
        var window = fixture.Window;
        await ShowAsync(window, ct);
        ShowPill(window, Toast("Anna", "First message"));
        await WpfFrameWaiter.UntilAsync(() => window.AppNotificationPill.Opacity == 1 &&
            !Motion.IsRunning(window.NotchBorder, FrameworkElement.WidthProperty), "first message arrives", ct);
        int children = window.NotchLiveContent.Children.Count;
        double width = window.NotchBorder.Width;
        ShowPill(window, Toast("Ben", "Second message"));
        Assert.Equal(width, window.NotchBorder.Width);
        Assert.Equal("Ben", window.AppPillSender.Text);
        Assert.Equal(0, window.AppNotificationPill.Opacity);
        Assert.True(window.NotchLiveContent.Children.Count > children);
        await WpfFrameWaiter.UntilAsync(() => window.NotchLiveContent.Children.Count == children &&
            window.AppNotificationPill.Opacity == 1, "replacement crossfade releases the old face", ct);
    });

    [Fact]
    public void NewToastWaitsWhileExpanded() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = Create();
        var window = fixture.Window;
        await ShowAsync(window, ct);
        ShowPill(window, Toast("Anna", "one"));
        Tap(window);
        ShowPill(window, Toast("Ben", "two"));
        // The "+1" cross-fades in (60 ms out, 60 ms in).
        await WpfFrameWaiter.UntilAsync(() => Element<TextBlock>(window, "AppPillSender").Text.EndsWith("+1"), "+1 faded in", ct);
        Assert.StartsWith("Anna", Element<TextBlock>(window, "AppPillSender").Text);
        Invoke(window, "CollapseAppPill");
        await WpfFrameWaiter.UntilAsync(() => Element<TextBlock>(window, "AppPillSender").Text.StartsWith("Ben"),
            "queued sender crossfades into the compact pill", ct);
    });

    private static bool Tap(MainWindow window) =>
        (bool)typeof(MainWindow).GetMethod("TryHandleAppPillTap", Private)!.Invoke(window, null)!;

    private static T Get<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;

    private static GreetingAcceptanceTests.MainWindowFixture Create(int height = 40) => new("en", false,
        settings => { settings.Height = height; settings.EnableLocalOnlyMode = true; settings.ShowAppNotifications = false; settings.ShowSystemNotifications = false; settings.DisableMouseLeaveAutoClose = true; settings.EnableHoverExpand = false; },
        services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));

    private static async Task ShowAsync(MainWindow window, CancellationToken ct)
    {
        window.ShowActivated = false;
        window.Show();
        await WpfFrameWaiter.UntilAsync(() => window.IsLoaded && window.CollapsedContent.IsVisible &&
            !(bool)typeof(MainWindow).GetProperty("_isAnimating", Private)!.GetValue(window)!, "collapsed notch", ct);
    }

    private static WindowsNotification Toast(string title, params string[] lines) =>
        new(1, "Messages", "test.app", title, lines.FirstOrDefault(), null, lines, null);

    private static void ShowPill(MainWindow window, WindowsNotification toast) =>
        Invoke(window, "ShowAppNotificationPill", toast, null, 0);

    private static T Element<T>(MainWindow window, string name) where T : FrameworkElement =>
        Assert.IsAssignableFrom<T>(window.FindName(name));

    private static void Invoke(MainWindow window, string name, params object?[] args)
    {
        var method = typeof(MainWindow).GetMethod(name, Private);
        Assert.NotNull(method);
        method.Invoke(window, args);
    }
}
