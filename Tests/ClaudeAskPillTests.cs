using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Controllers;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.ClaudeCode;
using VNotch.Tests.Fakes;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class ClaudeAskPillTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    private static JsonObject Ask(string tool, string? command = null) => new()
    {
        ["session_id"] = "pill-session",
        ["hook_event_name"] = "PermissionRequest",
        ["cwd"] = @"C:\work\notch-app",
        ["tool_name"] = tool,
        ["tool_input"] = command == null ? new JsonObject() : new JsonObject { ["command"] = command }
    };

    internal static JsonObject AskQuestions(params ClaudeQuestion[] questions)
    {
        var hook = Ask("AskUserQuestion");
        var array = new JsonArray();
        foreach (var question in questions)
        {
            var options = new JsonArray();
            foreach (var option in question.Options)
                options.Add(new JsonObject { ["label"] = option.Label, ["description"] = option.Description });
            array.Add(new JsonObject
            {
                ["question"] = question.Text, ["header"] = question.Header, ["multiSelect"] = question.MultiSelect, ["options"] = options
            });
        }
        hook["tool_input"] = new JsonObject { ["questions"] = array };
        return hook;
    }

    [Fact]
    public void ReturningAnInterruptedSwipeKeepsItsRenderedPositionAndSpeed() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = new GreetingAcceptanceTests.MainWindowFixture("en", greeting: false,
            configureSettings: settings => settings.EnableLocalOnlyMode = true,
            configureServices: services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));
        var window = fixture.Window;
        window.Show();
        Motion.Stop(window.ClaudeNotificationTranslate, TranslateTransform.XProperty, 35);
        typeof(MainWindow).GetMethod("SpringAskBack", Private)!.Invoke(window, null);
        await Task.Delay(70, ct);
        double position = window.ClaudeNotificationTranslate.X;
        double speed = Motion.VelocityOf(window.ClaudeNotificationTranslate, TranslateTransform.XProperty);
        Assert.True(Math.Abs(speed) > 1);
        typeof(MainWindow).GetMethod("SpringAskBack", Private)!.Invoke(window, null);
        Assert.InRange(Math.Abs(window.ClaudeNotificationTranslate.X - position), 0, 1);
        Assert.InRange(Math.Abs(Motion.VelocityOf(window.ClaudeNotificationTranslate, TranslateTransform.XProperty) - speed), 0, 20);
    });

    [Fact]
    public void AToolToAllowWaitsAsAPillInTheClosedNotchUntilItIsSettledElsewhere() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = new GreetingAcceptanceTests.MainWindowFixture("en", greeting: false,
            configureSettings: settings => settings.EnableLocalOnlyMode = true,
            configureServices: services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));
        var window = fixture.Window;
        window.Show();
        var tracker = Field<ClaudeSessionTracker>(window, "_claudeTracker");

        var answer = tracker.HandleHookAsync(Ask("Bash", "git push"));
        await WpfFrameWaiter.UntilAsync(() => Field<ClaudePermissionRequest?>(window, "_askRequest") != null, "ask pill shown", ct);
        Assert.Contains("git push", window.ClaudeNotificationStatus.Text);
        Assert.Equal("notch-app", window.ClaudeNotificationTitle.Text);
        // The pill, not the tab: the notch stays closed (a pill only shows on the closed notch).
        Assert.Equal(Visibility.Visible, window.ClaudeNotification.Visibility);
        Assert.False(Field<System.Windows.Threading.DispatcherTimer>(window, "_claudeNotificationTimer").IsEnabled);

        // Answered in the terminal: the pill goes.
        var request = tracker.GetPending().Single();
        Assert.True(tracker.Decide(request.Id, ClaudeDecision.Allow));
        await answer;
        await WpfFrameWaiter.UntilAsync(() => window.ClaudeNotification.Visibility != Visibility.Visible, "pill gone", ct);
        Assert.Null(Field<ClaudePermissionRequest?>(window, "_askRequest"));
    });

    [Fact]
    public void SettlingInThePillNamesTheAnswerInItsColour() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = new GreetingAcceptanceTests.MainWindowFixture("en", greeting: false,
            configureSettings: settings => settings.EnableLocalOnlyMode = true,
            configureServices: services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));
        var window = fixture.Window;
        window.Show();
        var tracker = Field<ClaudeSessionTracker>(window, "_claudeTracker");
        _ = tracker.HandleHookAsync(Ask("Edit"));
        await WpfFrameWaiter.UntilAsync(() => Field<ClaudePermissionRequest?>(window, "_askRequest") != null, "ask pill shown", ct);

        // As the swipe does: decide, then show it, in one go.
        Assert.True(tracker.Decide(tracker.GetPending().Single().Id, ClaudeDecision.Deny));
        typeof(MainWindow).GetMethod("PlayAskSettled", Private)!.Invoke(window, new object[] { false });
        await WpfFrameWaiter.UntilAsync(() => window.ClaudeNotificationStatus.Text == "Denied", "settled answer after the text fade", ct);
        Assert.Null(Field<ClaudePermissionRequest?>(window, "_askRequest"));
        await WpfFrameWaiter.UntilAsync(() => window.ClaudeNotification.Visibility != Visibility.Visible, "pill gone after the linger", ct);
    });

    [Fact]
    public void PullOpensTheSheetAndTappingAChipAnswersInPlace() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = new GreetingAcceptanceTests.MainWindowFixture("en", greeting: false,
            configureSettings: settings => settings.EnableLocalOnlyMode = true,
            configureServices: services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));
        var window = fixture.Window;
        window.Left = window.Top = -20000;
        window.Show();
        var tracker = Field<ClaudeSessionTracker>(window, "_claudeTracker");
        const string prompt = "Which database?";
        var answer = tracker.HandleHookAsync(AskQuestions(new ClaudeQuestion(prompt, "Database",
            [new("SQLite", null), new("Postgres", null)], false)));
        await WpfFrameWaiter.UntilAsync(() => Field<ClaudePermissionRequest?>(window, "_askRequest") != null, "question pill shown", ct);

        PullQuestionPill(window, 20);
        Assert.Null(Field<Grid?>(window, "_askSheet"));
        PullQuestionPill(window, 40);
        var sheet = Assert.IsType<Grid>(Field<Grid?>(window, "_askSheet"));
        var transition = Field<NotchTransitionCoordinator>(window, "_transitionCoordinator");
        Assert.Equal(NotchView.Compact, transition.CurrentView);
        var chip = Assert.IsType<Button>(Chips(sheet).Children[0]);
        chip.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        var values = JsonNode.Parse(await answer.WaitAsync(ct))!["hookSpecificOutput"]!["decision"]!["updatedInput"]!["answers"];
        Assert.Equal("SQLite", (string?)values![prompt]);
        await WpfFrameWaiter.UntilAsync(() => Field<Grid?>(window, "_askSheet") == null, "answer sheet folded", ct);
        Assert.Equal("Answered · SQLite", window.ClaudeNotificationStatus.Text);
        Assert.Equal(NotchView.Compact, transition.CurrentView);
    });

    internal static void PullQuestionPill(MainWindow window, double distance)
    {
        var gesture = typeof(MainWindow).GetField("_askGesture", Private)!;
        gesture.SetValue(window, Enum.Parse(gesture.FieldType, "Pressed"));
        var delta = new Vector(0, distance);
        typeof(MainWindow).GetMethod("MoveAskGesture", Private)!.Invoke(window, [delta]);
        typeof(MainWindow).GetMethod("CompleteAskGesture", Private)!.Invoke(window, [delta, 1]);
    }

    internal static WrapPanel Chips(Grid sheet) => (WrapPanel)((StackPanel)sheet.Children[0]).Children[^1];

    private static T Field<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
}
