using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Models;
using VNotch.Services;
using VNotch.Tests.Fakes;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class MainWindowUpdateNotificationTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void NewReleaseShowsAnimatedNotificationTooltipAndPulseThenClearsThem() => SharedStaTestRunner.RunAsync(async ct =>
    {
        var service = new PendingUpdateService();
        await using var fixture = CreateFixture(service);
        var window = fixture.Window;
        using var host = new HudContentHost(window);
        window.ExpandedContent.Visibility = Visibility.Visible;
        Field<NotchSettings>(window, "_settings").AutoCheckUpdates = true;
        var check = (Task)Invoke(window, "CheckForUpdatesAsync")!;
        service.Requests[0].SetResult(NewRelease("9.5.1"));
        await check;
        await WpfFrameWaiter.UntilAsync(() => Field<UiScheduledTimer?>(window, "_updatePulseTimer")?.IsEnabled == true, "update notification appeared", ct);
        Assert.True(Field<bool>(window, "_isUpdateAvailable"));
        Assert.Equal(Visibility.Visible, window.UpdateNotificationButton.Visibility);
        Assert.Contains("9.5.1", window.UpdateNotificationButton.Tag.ToString());
        Invoke(window, "ShowUpdateNotification");
        Set(window, "_updatePulseStartedAtUtc", DateTime.UtcNow.AddMilliseconds(-1500));
        Invoke(window, "UpdatePulseTimer_Tick", null, EventArgs.Empty);
        Assert.InRange(window.UpdateIconBrush.Color.R, 250, 255);
        Invoke(window, "UpdateNotification_MouseEnter", window, new MouseEventArgs(Mouse.PrimaryDevice, 0));
        await WpfFrameWaiter.UntilAsync(() => Math.Abs(window.UpdateInlineTooltip.Opacity - 1) < 1e-6, "update tooltip shown", ct);
        Assert.True(Field<bool>(window, "_isUpdateTooltipOpen"));
        Assert.Contains("9.5.1", window.UpdateInlineStatusText.Text);
        Assert.Null(window.UpdateNotificationButton.CacheMode);
        Invoke(window, "UpdateNotification_MouseLeave", window, new MouseEventArgs(Mouse.PrimaryDevice, 0));
        await WpfFrameWaiter.UntilAsync(() => window.UpdateInlineTooltip.Visibility == Visibility.Collapsed && window.UpdateNotificationButton.CacheMode == null, "update hover dismissed", ct);
        Assert.False(Field<bool>(window, "_isUpdateTooltipOpen"));
        var currentCheck = (Task)Invoke(window, "CheckForUpdatesAsync")!;
        service.Requests[1].SetResult(new UpdateInfo { Version = "9.5.1", IsNewerVersion = false });
        await currentCheck;
        await WpfFrameWaiter.UntilAsync(() => window.UpdateNotificationButton.Visibility == Visibility.Collapsed, "current version clears notification", ct);
        Assert.Null(Field<UpdateInfo?>(window, "_availableUpdate"));
        Assert.False(Field<UiScheduledTimer>(window, "_updatePulseTimer").IsEnabled);
        Assert.False(window.UpdateNotificationButton.IsHitTestVisible);
        Invoke(window, "ShowUpdateNotification");
        Invoke(window, "UpdatePulseTimer_Tick", null, EventArgs.Empty);
        Assert.Equal(Color.FromRgb(48, 209, 88), window.UpdateIconBrush.Color);
        Assert.Equal(0, service.InstallCalls);
    });

    [Fact]
    public void PulseStopsWithAncestorVisibilityAndFullscreen() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture(new PendingUpdateService());
        var window = fixture.Window;
        using var host = new HudContentHost(window);
        window.ExpandedContent.Visibility = Visibility.Visible;
        window.SetAvailableUpdate(NewRelease("9.5.4"));
        await WpfFrameWaiter.UntilAsync(() => Field<UiScheduledTimer?>(window, "_updatePulseTimer")?.IsEnabled == true, "update pulse starts", ct);
        window.NotchBorder.Visibility = Visibility.Hidden;
        Assert.False(Field<UiScheduledTimer>(window, "_updatePulseTimer").IsEnabled);
        window.NotchBorder.Visibility = Visibility.Visible;
        Assert.True(Field<UiScheduledTimer>(window, "_updatePulseTimer").IsEnabled);
        Set(window, "_isHiddenByFullscreen", true);
        Invoke(window, "RefreshAmbientAnimations");
        Assert.False(Field<UiScheduledTimer>(window, "_updatePulseTimer").IsEnabled);
        Set(window, "_isHiddenByFullscreen", false);
        Invoke(window, "RefreshAmbientAnimations");
        Assert.True(Field<UiScheduledTimer>(window, "_updatePulseTimer").IsEnabled);
    });

    [Fact]
    public void LatestCheckOwnsNotificationAndFailuresPreserveItsState() => SharedStaTestRunner.RunAsync(async ct =>
    {
        var service = new PendingUpdateService();
        await using var fixture = CreateFixture(service);
        var window = fixture.Window;
        var settings = Field<NotchSettings>(window, "_settings");
        settings.AutoCheckUpdates = true;
        var older = (Task)Invoke(window, "CheckForUpdatesAsync")!;
        var newer = (Task)Invoke(window, "CheckForUpdatesAsync")!;
        service.Requests[1].SetResult(NewRelease("9.5.2"));
        await newer;
        service.Requests[0].SetResult(NewRelease("9.5.1"));
        await older;
        Assert.Equal("9.5.2", Field<UpdateInfo>(window, "_availableUpdate").Version);
        var failed = (Task)Invoke(window, "CheckForUpdatesAsync")!;
        service.Requests[2].SetException(new HttpRequestException("fixture offline"));
        await failed;
        Assert.Equal("9.5.2", Field<UpdateInfo>(window, "_availableUpdate").Version);
        var duringInstall = (Task)Invoke(window, "CheckForUpdatesAsync")!;
        Set(window, "_isUpdateInstalling", true);
        service.Requests[3].SetResult(NewRelease("9.5.3"));
        await duringInstall;
        Assert.Equal("9.5.2", Field<UpdateInfo>(window, "_availableUpdate").Version);
        await (Task)Invoke(window, "CheckForUpdatesAsync")!;
        Set(window, "_isUpdateInstalling", false);
        settings.EnableLocalOnlyMode = true;
        await (Task)Invoke(window, "CheckForUpdatesAsync")!;
        settings.EnableLocalOnlyMode = false;
        settings.AutoCheckUpdates = false;
        await (Task)Invoke(window, "CheckForUpdatesAsync")!;
        Assert.Equal(4, service.Requests.Count);
        Set(window, "_isUpdateAvailable", false);
        Invoke(window, "HideUpdateNotification");
        await WpfFrameWaiter.UntilAsync(() => window.UpdateNotificationButton.Visibility == Visibility.Collapsed, "notification cleanup", ct);
    });

    [Fact]
    public void RapidHideAndShowLeavesNewestReleaseVisible() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture(new PendingUpdateService());
        var window = fixture.Window;
        using var host = new HudContentHost(window);
        window.ExpandedContent.Visibility = Visibility.Visible;
        Set(window, "_availableUpdate", NewRelease("9.5.4"));
        Set(window, "_isUpdateAvailable", true);
        Invoke(window, "ShowUpdateNotification");
        Invoke(window, "ShowUpdateInlineTooltip");
        Invoke(window, "HideUpdateNotification");
        Invoke(window, "ShowUpdateNotification");
        await WpfFrameWaiter.UntilAsync(() => Field<UiScheduledTimer?>(window, "_updatePulseTimer")?.IsEnabled == true, "latest update survives interrupted hide", ct);
        Assert.Equal(Visibility.Visible, window.UpdateNotificationButton.Visibility);
        Assert.True(window.UpdateNotificationButton.IsHitTestVisible);
        await WpfFrameWaiter.UntilAsync(() => !Motion.IsRunning(window.UpdateNotificationButton, UIElement.OpacityProperty), "update entrance settles", ct);
        await WpfFrameWaiter.UntilAsync(() => window.UpdateNotificationButton.Opacity > .999, "update entrance arrives", ct);
        Set(window, "_isUpdateAvailable", false);
        Invoke(window, "HideUpdateNotification");
        await WpfFrameWaiter.UntilAsync(() => window.UpdateNotificationButton.Visibility == Visibility.Collapsed, "update hidden", ct);
    });

    [Fact]
    public void ManualUpdateCheckViaServiceOrSettingsRevealsUpdateNotificationEvenWhenAutoCheckDisabled() => SharedStaTestRunner.RunAsync(async ct =>
    {
        var service = new PendingUpdateService();
        await using var fixture = CreateFixture(service);
        var window = fixture.Window;
        var settings = Field<NotchSettings>(window, "_settings");
        settings.AutoCheckUpdates = false;

        var backgroundCheck = (Task)Invoke(window, "CheckForUpdatesAsync")!;
        await backgroundCheck;
        Assert.False(Field<bool>(window, "_isUpdateAvailable"));

        service.RaiseUpdateCheckCompleted(NewRelease("9.5.5"));
        await WpfFrameWaiter.UntilAsync(() => Field<bool>(window, "_isUpdateAvailable") && window.UpdateNotificationButton.Visibility == Visibility.Visible, "update notification shown via event", ct);
        Assert.Equal("9.5.5", Field<UpdateInfo>(window, "_availableUpdate").Version);

        Invoke(window, "AnimateStatusBarReveal", true);
        Assert.Equal(Visibility.Visible, window.UpdateNotificationButton.Visibility);
        Assert.True(window.UpdateNotificationButton.IsHitTestVisible);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UpdatesArrivingAfterExpansionStayAlignedWithTheStatusIcons(bool island) => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = new GreetingAcceptanceTests.MainWindowFixture("en", greeting: false,
            configureSettings: settings =>
            {
                settings.EnableLocalOnlyMode = true;
                settings.DisableMouseLeaveAutoClose = true;
                settings.EnableDynamicIslandMode = island;
                settings.ShowBatteryIndicator = true;
            }, configureServices: services =>
            {
                services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService());
                services.AddSingleton<IUpdateService>(new PendingUpdateService());
            });
        var window = fixture.Window;
        var coordinator = Field<VNotch.Controllers.NotchTransitionCoordinator>(window, "_transitionCoordinator");
        window.Show();
        window.NavigateToNotchView(NotchView.Media);
        await WpfFrameWaiter.UntilAsync(() => !coordinator.IsTransitionActive &&
            coordinator.CurrentView == NotchView.Media && Math.Abs(window.SettingsRotate.Angle - 45) < 1e-6,
            "expanded status icons settled", ct);

        double CenterY(FrameworkElement element)
        {
            var bounds = element.TransformToAncestor(window.NotchContent).TransformBounds(new Rect(element.RenderSize));
            return bounds.Top + bounds.Height / 2;
        }

        foreach (string version in new[] { "9.8.0", "9.8.1" })
        {
            // Exercise both a newly arriving update and refreshing a visible one.
            window.SetAvailableUpdate(NewRelease(version));
            await WpfFrameWaiter.UntilAsync(() => window.UpdateNotificationButton.Opacity > .99 &&
                Field<UiScheduledTimer?>(window, "_updatePulseTimer")?.IsEnabled == true,
                "update icon appeared", ct);
            window.UpdateLayout();
            double offset = CenterY(window.UpdateNotificationButton) - CenterY(window.SettingsButton);
            Assert.True(Math.Abs(offset) <= .5,
                $"Update icon is offset by {offset:F2} DIPs (island={island}, version={version}).");
        }
    });

    private static UpdateInfo NewRelease(string version) => new() { Version = version, IsNewerVersion = true };

    [Fact]
    public void TheForkNeverOffersUpstreamReleasesFromTheRealFeed() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = new GreetingAcceptanceTests.MainWindowFixture("en", greeting: false,
            configureSettings: settings => settings.EnableLocalOnlyMode = true,
            configureServices: services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));
        var window = fixture.Window;
        Assert.IsType<UpdateService>(Field<IUpdateService?>(window, "_updateService"));
        window.SetAvailableUpdate(NewRelease("9.9.0"));
        await WpfFrameWaiter.UntilAsync(() => window.UpdateNotificationButton.Visibility == Visibility.Collapsed, "no upstream update offered", ct);
        Assert.False(Field<bool>(window, "_isUpdateAvailable"));
    });

    [Fact]
    public void OptingOutIgnoresLateBetaEventsAndClearsBetaNotification() => SharedStaTestRunner.RunAsync(async ct =>
    {
        var service = new PendingUpdateService();
        await using var fixture = CreateFixture(service);
        var window = fixture.Window;
        var settings = Field<NotchSettings>(window, "_settings");
        settings.IncludePrereleaseUpdates = true;
        var beta = new UpdateInfo { Version = "99.1.0-beta.1", IsNewerVersion = true, IsPrerelease = true };
        window.SetAvailableUpdate(beta);
        Assert.True(Field<bool>(window, "_isUpdateAvailable"));
        settings.IncludePrereleaseUpdates = false;
        window.SetAvailableUpdate(beta);
        Assert.False(Field<bool>(window, "_isUpdateAvailable"));
        Assert.Null(Field<UpdateInfo?>(window, "_availableUpdate"));
        service.RaiseUpdateCheckCompleted(beta);
        await WpfFrameWaiter.UntilAsync(() => window.UpdateNotificationButton.Visibility == Visibility.Collapsed, "beta notification dismissed", ct);
        Assert.False(Field<bool>(window, "_isUpdateAvailable"));
        window.SetAvailableUpdate(NewRelease("99.0.0"));
        window.SetAvailableUpdate(beta);
        Assert.Equal("99.0.0", Field<UpdateInfo>(window, "_availableUpdate").Version);
        Assert.True(Field<bool>(window, "_isUpdateAvailable"));
    });
    private static GreetingAcceptanceTests.MainWindowFixture CreateFixture(IUpdateService updates) => new("en", greeting: false,
        configureServices: services => { services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()); services.AddSingleton(updates); });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StablePromotionReplacesBetaNotificationRegardlessOfBetaPreference(bool includePrereleases) => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture(new PendingUpdateService());
        var window = fixture.Window;
        var settings = Field<NotchSettings>(window, "_settings");
        settings.IncludePrereleaseUpdates = true;
        window.SetAvailableUpdate(new() { Version = "99.0.0-beta.65534.65534", IsNewerVersion = true, IsPrerelease = true });
        settings.IncludePrereleaseUpdates = includePrereleases;
        window.SetAvailableUpdate(NewRelease("99.0.0"));
        await WpfFrameWaiter.UntilAsync(() => window.UpdateNotificationButton.Visibility == Visibility.Visible, "stable promotion notification shown", ct);
        var available = Field<UpdateInfo>(window, "_availableUpdate");
        Assert.Equal("99.0.0", available.Version);
        Assert.False(available.IsPrerelease);
        Assert.True(Field<bool>(window, "_isUpdateAvailable"));
        Assert.Contains("99.0.0", window.UpdateNotificationButton.Tag.ToString());
    });
    private static T Field<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
    private static void Set(MainWindow window, string name, object value)
    {
        var field = typeof(MainWindow).GetField(name, Private);
        if (field != null) field.SetValue(window, value);
        else typeof(MainWindow).GetProperty(name, Private)!.SetValue(window, value);
    }
    private static object? Invoke(MainWindow window, string name, params object?[] args) => typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, args);
    private sealed class PendingUpdateService : IUpdateService
    {
        public List<TaskCompletionSource<UpdateInfo?>> Requests { get; } = new();
        public List<bool> Channels { get; } = new();
        public int InstallCalls { get; private set; }
        public string CurrentVersion => "9.5.0";
        public event EventHandler<UpdateInfo?>? UpdateCheckCompleted;
        public UpdateInfo? LatestUpdateInfo => null;
        public void RaiseUpdateCheckCompleted(UpdateInfo? info) => UpdateCheckCompleted?.Invoke(this, info);
        public Task<UpdateInfo?> CheckForUpdatesAsync(bool includePrereleases = false, bool forceRefresh = false)
        {
            Channels.Add(includePrereleases);
            var request = new TaskCompletionSource<UpdateInfo?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Requests.Add(request);
            return request.Task;
        }
        public Task<IReadOnlyList<UpdateInfo>> GetAllReleasesAsync() => Task.FromResult<IReadOnlyList<UpdateInfo>>(Array.Empty<UpdateInfo>());
        public Task<bool> DownloadAndInstallUpdateAsync(UpdateInfo updateInfo, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        {
            InstallCalls++;
            throw new InvalidOperationException("Notification tests must never install an update.");
        }
    }

    [Fact]
    public void BackgroundCheckUsesBetaPreferenceAndDropsResultAfterOptOut() => SharedStaTestRunner.RunAsync(async ct =>
    {
        var service = new PendingUpdateService();
        await using var fixture = CreateFixture(service);
        var settings = Field<NotchSettings>(fixture.Window, "_settings");
        settings.AutoCheckUpdates = true;
        settings.IncludePrereleaseUpdates = true;
        var pending = (Task)Invoke(fixture.Window, "CheckForUpdatesAsync")!;
        Assert.True(Assert.Single(service.Channels));
        settings.IncludePrereleaseUpdates = false;
        service.Requests[0].SetResult(new() { Version = "99.1.0-beta.1", IsNewerVersion = true, IsPrerelease = true });
        await pending;
        Assert.False(Field<bool>(fixture.Window, "_isUpdateAvailable"));
    });

    [Fact]
    public void UpdateHandlersAvoidAsyncVoid()
    {
        foreach (string name in new[] { "UpdateCheckTimer_Tick", "UpdateNotification_Click" })
        {
            var method = typeof(MainWindow).GetMethod(name, Private)!;
            Assert.Null(method.GetCustomAttribute<System.Runtime.CompilerServices.AsyncStateMachineAttribute>());
        }
    }
}

// A real visual parent exercises effective visibility without starting the notch's desktop services.
internal sealed class HudContentHost : IDisposable
{
    private readonly MainWindow _window;
    private readonly object _content;
    private readonly Window _host;

    internal HudContentHost(MainWindow window)
    {
        _window = window;
        _content = window.Content;
        window.Content = null;
        _host = new Window { Left = -20000, Top = -20000, Width = 720, Height = 260,
            ShowActivated = false, ShowInTaskbar = false, Content = _content };
        _host.Show();
        _host.UpdateLayout();
    }

    public void Dispose()
    {
        _host.Content = null;
        _host.Close();
        _window.Content = _content;
    }
}
