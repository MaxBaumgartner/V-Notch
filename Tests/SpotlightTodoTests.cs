using System.Reflection;
using System.Windows;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Models;
using VNotch.Services;
using VNotch.Tests.Fakes;
using VNotch.ViewModels;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class SpotlightTodoTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void PlusTextAddsTodo() => WithWindows(async (notch, spotlight, ct) =>
    {
        var sent = new List<string>();
        notch.TodoSender = text => { sent.Add(text); return Task.FromResult(true); };
        await Query(spotlight, "+  Druckerpatrone kaufen  ", ct);
        var row = Assert.Single(((SpotlightViewModel)spotlight.DataContext).Results);
        Assert.Equal(SpotlightResultKind.AddTodo, row.Kind);
        Assert.Equal(Loc.Get("spotlight.addTodo"), row.Title);
        PressEnter(spotlight);
        await WpfFrameWaiter.UntilAsync(() => !spotlight.IsSpotlightOpen &&
            notch.ClaudeNotificationTitle.Text == Loc.Get("notch.todoAdded"), "to-do added and Spotlight closed", ct);
        Assert.Equal("Druckerpatrone kaufen", Assert.Single(sent));
    });

    [Theory]
    [InlineData("+")]
    [InlineData("+   ")]
    public void PlusAloneIsNotATodo(string query) => WithWindows(async (notch, spotlight, ct) =>
    {
        bool sent = false;
        notch.TodoSender = _ => { sent = true; return Task.FromResult(true); };
        await Query(spotlight, query, ct);
        Assert.DoesNotContain(((SpotlightViewModel)spotlight.DataContext).Results,
            row => row.Kind == SpotlightResultKind.AddTodo);
        PressEnter(spotlight);
        await WpfFrameWaiter.NextAsync(ct);
        Assert.False(sent);
        Assert.True(spotlight.IsSpotlightOpen);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TodoFailsQuietlyWhenPiOffline(bool throws) => WithWindows(async (notch, spotlight, ct) =>
    {
        int attempts = 0;
        notch.TodoSender = _ =>
        {
            attempts++;
            return throws ? Task.FromException<bool>(new System.Net.Http.HttpRequestException("offline")) : Task.FromResult(false);
        };
        await Query(spotlight, "+ keep this task", ct);
        PressEnter(spotlight);
        await WpfFrameWaiter.UntilAsync(() => !spotlight.IsSpotlightOpen &&
            notch.ClaudeNotificationTitle.Text == Loc.Get("notch.todoFailed"), "failed to-do confirmation", ct);
        Assert.Equal(1, attempts);
    });

    private static void WithWindows(Func<MainWindow, SpotlightWindow, CancellationToken, Task> action) =>
        SharedStaTestRunner.RunAsync(async ct =>
        {
            bool reducedMotion = AnimationConfig.ReduceMotion;
            AnimationConfig.SetReduceMotion(true);
            try
            {
                await using var notch = await CopiedPopPhoneTests.CreateAsync();
                using var fixture = new SpotlightWindowFixture(new NotchSettings { EnableSpotlightHistory = false, AutoCheckUpdates = false });
                fixture.Window.MorphHostOverride = notch.Window;
                fixture.Window.ShowSpotlight();
                await WpfFrameWaiter.UntilAsync(() => !(bool)typeof(SpotlightWindow).GetField("_entranceActive", Private)!
                    .GetValue(fixture.Window)!, "Spotlight entrance", ct);
                await action(notch.Window, fixture.Window, ct);
            }
            finally { AnimationConfig.SetReduceMotion(reducedMotion); }
        });

    private static async Task Query(SpotlightWindow window, string query, CancellationToken ct)
    {
        window.SearchBox.Text = query;
        Invoke(window, "CancelSearchDebounce");
        await ((SpotlightViewModel)window.DataContext).SearchAsync(query);
        await WpfFrameWaiter.NextAsync(ct);
    }

    private static void PressEnter(SpotlightWindow window)
    {
        var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, Environment.TickCount, Key.Enter)
            { RoutedEvent = UIElement.PreviewKeyDownEvent };
        Invoke(window, "Window_PreviewKeyDown", window, key);
        Assert.True(key.Handled);
    }

    private static void Invoke(SpotlightWindow window, string method, params object[] arguments) =>
        typeof(SpotlightWindow).GetMethod(method, Private)!.Invoke(window, arguments);
}
