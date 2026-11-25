using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Threading;
using VNotch.Controllers;
using VNotch.Controls;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.ClaudeCode;
using VNotch.Services.ControlCenter;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class NotchOpenTabSizeTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(NotchView.Claude, false)]
    [InlineData(NotchView.Claude, true)]
    [InlineData(NotchView.Pi, false)]
    [InlineData(NotchView.School, false)]
    public void ReopeningAFittedTabStartsAtTheHeightOfItsUpdatedContent(NotchView view, bool afterResize) => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = MusicMotionTests.Create();
        var window = fixture.Window;
        bool reduced = AnimationConfig.ReduceMotion;
        bool? animations = AnimationConfig.ClientAreaAnimationOverride;
        try
        {
            ShowOffscreen(window);
            var coordinator = Field<NotchTransitionCoordinator>(window, "_transitionCoordinator");
            var (center, page) = view switch
            {
                NotchView.Claude => (window.ClaudeCenter, window.ClaudeContent),
                NotchView.Pi => (window.PiCenter, window.PiContent),
                _ => (window.SchoolCenter, window.SchoolContent)
            };
            if (afterResize) await Hook(window, "UserPromptSubmit");
            window.NavigateToNotchView(view);
            await WaitForView(coordinator, view, ct);
            await TestWait.DrainDispatcherAsync();
            if (afterResize)
            {
                double startingHeight = window.NotchBorder.Height;
                AnimationConfig.SetReduceMotion(false);
                await Hook(window, "SessionEnd");
                await TestWait.UntilStableAsync(() => window.NotchBorder.Height < startingHeight - 6 &&
                    !Motion.IsRunning(window.NotchBorder, FrameworkElement.HeightProperty) &&
                    !Motion.IsRunning(page, FrameworkElement.HeightProperty), "previous Claude resize settles", cancellationToken: ct);
            }
            double previous = window.NotchBorder.Height;
            window.SetDebugViewLock(false);
            Assert.True(coordinator.RequestView(NotchView.Compact, "Tab size test"));
            window.SetDebugViewLock(true);
            await WaitForView(coordinator, NotchView.Compact, ct);

            using var dashboard = new PiDashboardClient(() => "http://127.0.0.1:1");
            if (view == NotchView.Claude)
            {
                await Hook(window, "UserPromptSubmit");
                await TestWait.DrainDispatcherAsync();
            }
            else
            {
                Set(center, "_pi", dashboard);
                Set(center, "_isLocalOnly", (Func<bool>)(() => false));
                Set(center, "_piRefreshing", true);
                if (view == NotchView.School) SchoolV3Tests.Seed(center);
                else
                {
                    Set(center, "_home", new PiHome([], [], null, [], [new("socket", "PC", true, true, false, 140, 1.5)], [], new(55, 7200, [])));
                    Call(center, "BuildPiPage");
                }
            }
            AnimationConfig.SetReduceMotion(false);
            Call(window, "ExpandNotch", null, null, NotchView.Compact);
            var (openingTarget, openingContentHeight) = await window.Dispatcher.InvokeAsync(
                () => (Field<double>(window, "_shellOpenHeight"), page.Height + page.Margin.Top + page.Margin.Bottom), DispatcherPriority.Normal);
            await WaitForView(coordinator, view, ct);
            await TestWait.DrainDispatcherAsync();
            double needed = FittedHeight(center, page);
            Assert.True(needed > previous + 6, $"Expected new content to grow from {previous} to {needed} DIP.");
            Assert.InRange(openingTarget, needed - 1, needed + 1);
            Assert.InRange(openingContentHeight, needed - 1, needed + 1);
        }
        finally
        {
            AnimationConfig.SetReduceMotion(reduced);
            AnimationConfig.ClientAreaAnimationOverride = animations;
        }
    });

    [Fact]
    public void RemovingAClaudeSessionWhileClosedStartsAtTheSmallerHeight() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = MusicMotionTests.Create();
        var window = fixture.Window;
        bool reduced = AnimationConfig.ReduceMotion;
        bool? animations = AnimationConfig.ClientAreaAnimationOverride;
        try
        {
            ShowOffscreen(window);
            var coordinator = Field<NotchTransitionCoordinator>(window, "_transitionCoordinator");
            await Hook(window, "UserPromptSubmit");
            window.NavigateToNotchView(NotchView.Claude);
            await WaitForView(coordinator, NotchView.Claude, ct);
            await TestWait.DrainDispatcherAsync();
            double previous = window.NotchBorder.Height;
            window.SetDebugViewLock(false);
            Assert.True(coordinator.RequestView(NotchView.Compact, "Tab size test"));
            window.SetDebugViewLock(true);
            await WaitForView(coordinator, NotchView.Compact, ct);
            await Hook(window, "SessionEnd");
            await TestWait.DrainDispatcherAsync();

            AnimationConfig.SetReduceMotion(false);
            Call(window, "ExpandNotch", null, null, NotchView.Compact);
            double openingTarget = await window.Dispatcher.InvokeAsync(() => Field<double>(window, "_shellOpenHeight"), DispatcherPriority.Normal);
            await WaitForView(coordinator, NotchView.Claude, ct);
            await TestWait.DrainDispatcherAsync();
            double needed = FittedHeight(window.ClaudeCenter, window.ClaudeContent);
            Assert.True(needed < previous - 6, $"Expected removing the session to shrink the page from {previous} to {needed} DIP.");
            Assert.InRange(openingTarget, needed - 1, needed + 1);
        }
        finally
        {
            AnimationConfig.SetReduceMotion(reduced);
            AnimationConfig.ClientAreaAnimationOverride = animations;
        }
    });

    [Fact]
    public void ClaudeContentArrivingDuringOpenRetargetsTheAttachedShell() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = MusicMotionTests.Create();
        var window = fixture.Window;
        bool reduced = AnimationConfig.ReduceMotion;
        bool? animations = AnimationConfig.ClientAreaAnimationOverride;
        try
        {
            ShowOffscreen(window);
            var coordinator = Field<NotchTransitionCoordinator>(window, "_transitionCoordinator");
            AnimationConfig.SetReduceMotion(false);
            window.NavigateToNotchView(NotchView.Claude);
            await WpfFrameWaiter.UntilAsync(() => coordinator.IsTransitionActive && window.NotchBorder.Height > 60,
                "Claude shell opening", ct);
            double firstTarget = Field<double>(window, "_shellOpenHeight");
            await Hook(window, "UserPromptSubmit");
            await TestWait.DrainDispatcherAsync(DispatcherPriority.ContextIdle);

            Assert.True(coordinator.IsTransitionActive);
            double needed = FittedHeight(window.ClaudeCenter, window.ClaudeContent);
            Assert.True(needed > firstTarget + 6);
            var layer = (FrameworkElement)window.ClaudeContent.Parent;
            Assert.InRange(layer.Height, needed - 1, needed + 1);
            Assert.InRange(window.ClaudeContent.Height + window.ClaudeContent.Margin.Top, needed - 1, needed + 1);
            await WaitForView(coordinator, NotchView.Claude, ct);
            Assert.InRange(window.NotchBorder.Height, needed - 1, needed + 1);
        }
        finally
        {
            AnimationConfig.SetReduceMotion(reduced);
            AnimationConfig.ClientAreaAnimationOverride = animations;
        }
    });

    [Fact]
    public void ReleasingAnOpenClaudeShellFitsContentThatShrankDuringTheDrag() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = MusicMotionTests.Create();
        var window = fixture.Window;
        bool reduced = AnimationConfig.ReduceMotion;
        bool? animations = AnimationConfig.ClientAreaAnimationOverride;
        try
        {
            ShowOffscreen(window);
            var coordinator = Field<NotchTransitionCoordinator>(window, "_transitionCoordinator");
            await Hook(window, "UserPromptSubmit");
            window.NavigateToNotchView(NotchView.Claude);
            await WaitForView(coordinator, NotchView.Claude, ct);
            await TestWait.DrainDispatcherAsync();
            double previous = window.NotchBorder.Height;
            AnimationConfig.SetReduceMotion(false);

            var start = DateTime.UtcNow;
            window.BeginShellGestureTracking(new Point(), start);
            window.UpdateGestureTracking(new Point(0, 30), start.AddMilliseconds(250));
            Assert.True(Field<bool>(window, "_shellDragActive"));
            await Hook(window, "SessionEnd");
            await WpfFrameWaiter.UntilAsync(() => FittedHeight(window.ClaudeCenter, window.ClaudeContent) < previous - 6,
                "Claude session card folds away under the held shell", ct);
            await TestWait.DrainDispatcherAsync();

            window.FinishShellDrag(false, start.AddMilliseconds(250));
            await TestWait.UntilStableAsync(() => !Motion.IsRunning(window.NotchBorder, FrameworkElement.HeightProperty),
                "Claude shell return settles", cancellationToken: ct);
            Assert.Equal(NotchView.Claude, coordinator.CurrentView);
            double needed = FittedHeight(window.ClaudeCenter, window.ClaudeContent);
            Assert.InRange(window.NotchBorder.Height, needed - 1, needed + 1);
        }
        finally
        {
            AnimationConfig.SetReduceMotion(reduced);
            AnimationConfig.ClientAreaAnimationOverride = animations;
        }
    });

    private static void ShowOffscreen(MainWindow window)
    {
        AnimationConfig.ClientAreaAnimationOverride = true;
        AnimationConfig.SetReduceMotion(true);
        Field<NotchSettings>(window, "_settings").ReopenLastViewOnExpand = true;
        window.SetDebugViewLock(true);
        window.Left = window.Top = -20000;
        window.Show();
        window.Left = window.Top = -20000;
    }

    private static Task WaitForView(NotchTransitionCoordinator coordinator, NotchView view, CancellationToken ct) =>
        WpfFrameWaiter.UntilAsync(() => coordinator.CurrentView == view && !coordinator.IsTransitionActive, $"{view} settled", ct);

    private static double FittedHeight(ControlCenterView center, FrameworkElement page) =>
        Math.Min(580, center.MeasureContentHeight(page.Width) + page.Margin.Top + page.Margin.Bottom);

    private static Task<string> Hook(MainWindow window, string name) =>
        Field<ClaudeSessionTracker>(window, "_claudeTracker").HandleHookAsync(new JsonObject
        {
            ["session_id"] = "fit-session",
            ["hook_event_name"] = name,
            ["cwd"] = @"C:\work\notch-app",
            ["prompt"] = "Run the unit tests"
        });

    private static T Field<T>(object obj, string name) => (T)obj.GetType().GetField(name, Private)!.GetValue(obj)!;
    private static void Set(object obj, string name, object value) => obj.GetType().GetField(name, Private)!.SetValue(obj, value);
    private static void Call(object obj, string name, params object?[] args) => obj.GetType().GetMethod(name, Private)!.Invoke(obj, args);
}
