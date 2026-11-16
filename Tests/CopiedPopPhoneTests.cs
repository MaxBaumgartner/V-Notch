using System.Reflection;
using System.Windows;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Controllers;
using VNotch.Models;
using VNotch.Services;
using VNotch.Tests.Fakes;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class CopiedPopPhoneTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void PullUpSendsText() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = await CreateAsync();
        var window = fixture.Window;
        string? sent = null;
        window.PhoneSender = text => { sent = text; return Task.FromResult(true); };
        await VNotch.Services.Clipboard.ClipboardRetry.RunAsync(() => Clipboard.SetText("hello phone"), () => { });
        ShowCopied(window);
        PullUp(window);
        await WpfFrameWaiter.UntilAsync(() => window.ClaudeNotificationStatus.Text == Loc.Get("clipboard.sentToPhone"), "sent label", ct);
        Assert.Equal("hello phone", sent);
        Assert.Equal("hello phone", Clipboard.GetText());
    });

    [Theory]
    [InlineData("ExcludeClipboardContentFromMonitorProcessing")]
    [InlineData("Clipboard Viewer Ignore")]
    [InlineData("CanIncludeInClipboardHistory")]
    public void PrivateClipIsNeverSent(string marker) => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = await CreateAsync();
        bool sent = false;
        fixture.Window.PhoneSender = _ => { sent = true; return Task.FromResult(true); };
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, "secret");
        data.SetData(marker, new System.IO.MemoryStream(new byte[sizeof(uint)]));
        await VNotch.Services.Clipboard.ClipboardRetry.RunAsync(() => Clipboard.SetDataObject(data, false), () => { });
        Assert.True(ClipboardHistoryController.ShouldIgnoreClipboardData(Clipboard.GetDataObject()!));
        Assert.Equal("", MainWindow.DescribeClipboard());
        ShowCopied(fixture.Window);
        PullUp(fixture.Window);
        await WpfFrameWaiter.NextAsync(ct);
        Assert.False(sent);
        Assert.Equal("secret", Clipboard.GetText());
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PiOfflineShowsNotSent(bool throws) => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = await CreateAsync();
        var window = fixture.Window;
        window.PhoneSender = _ => throws
            ? Task.FromException<bool>(new System.Net.Http.HttpRequestException("offline"))
            : Task.FromResult(false);
        var history = ((ClipboardHistoryController)typeof(MainWindow).GetField("_clipboardHistory", Private)!
            .GetValue(window)!).Store;
        var entry = new ClipboardEntry { Kind = ClipboardKind.Text, Title = "keep this", Preview = "keep this" };
        // Seed existing history without involving the Windows encryption store in this send test.
        history.GetType().GetMethod("PutEntry", Private)!.Invoke(history, [entry, false]);
        await VNotch.Services.Clipboard.ClipboardRetry.RunAsync(() => Clipboard.SetText("keep this"), () => { });
        ShowCopied(window);
        PullUp(window);
        await WpfFrameWaiter.UntilAsync(() => window.ClaudeNotificationStatus.Text == Loc.Get("clipboard.notSent"), "failure label", ct);
        Assert.Equal("keep this", Clipboard.GetText());
        Assert.Equal(entry, Assert.Single(history.Snapshot()));
    });

    internal static async Task<GreetingAcceptanceTests.MainWindowFixture> CreateAsync()
    {
        var fixture = new GreetingAcceptanceTests.MainWindowFixture("en", false,
            settings => { settings.EnableLocalOnlyMode = true; settings.PiDashboardUrl = ""; },
            services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));
        // Drain deferred startup without requesting real Windows notification access in these helper tests.
        var cleanedUp = typeof(MainWindow).GetField("_cleanedUp", Private)!;
        cleanedUp.SetValue(fixture.Window, true);
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        cleanedUp.SetValue(fixture.Window, false);
        return fixture;
    }

    private static void ShowCopied(MainWindow window)
    {
        typeof(MainWindow).GetField("_isCopiedPillShown", Private)!.SetValue(window, true);
    }

    private static void PullUp(MainWindow window)
    {
        var released = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left);
        var gesture = typeof(MainWindow).GetField("_copiedGesture", Private)!;
        gesture.SetValue(window, Enum.Parse(gesture.FieldType, "ToPhone"));
        typeof(MainWindow).GetField("_copiedGestureStart", Private)!
            .SetValue(window, released.GetPosition(window) + new Vector(0, 80));
        Assert.True((bool)typeof(MainWindow).GetMethod("HandleCopiedGestureUp", Private)!.Invoke(window, [released])!);
    }
}
