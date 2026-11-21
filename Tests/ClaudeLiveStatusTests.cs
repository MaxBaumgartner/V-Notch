using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Services;
using VNotch.Services.ClaudeCode;
using VNotch.Tests.Fakes;
using Xunit;

namespace VNotch.Tests;

/// <summary>The word the closed notch shows for a session, from the hook events the tracker saw.</summary>
[Collection("Localization")]
public class ClaudeLiveStatusTests
{
    private static JsonObject Hook(string hookEvent, string? tool = null)
    {
        var payload = new JsonObject { ["session_id"] = "s1", ["hook_event_name"] = hookEvent, ["cwd"] = @"C:\work\app" };
        if (tool != null)
        {
            payload["tool_name"] = tool;
            payload["tool_input"] = new JsonObject { ["command"] = "x", ["file_path"] = @"C:\work\app\a.cs" };
        }
        return payload;
    }

    private static string StatusOf(ClaudeSessionTracker tracker) =>
        MainWindow.ClaudeLiveStatusKey(Assert.Single(tracker.GetSessions()));

    [Fact]
    public void FullscreenStopsTheVisibleStarAndClockUntilAmbientMotionReturns() => SharedStaTestRunner.RunAsync(async ct =>
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        await using var fixture = new GreetingAcceptanceTests.MainWindowFixture("en", greeting: false,
            configureSettings: settings => settings.EnableLocalOnlyMode = true,
            configureServices: services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));
        var window = fixture.Window;
        using var host = new HudContentHost(window);
        await WpfFrameWaiter.UntilAsync(() => window.CollapsedContent.IsVisible, "closed notch visible", ct);
        typeof(MainWindow).GetField("_claudeLiveSession", flags)!.SetValue(window,
            new ClaudeSession { Id = "visible", Status = ClaudeSessionStatus.Working, TurnStartedUtc = DateTime.UtcNow });
        window.CollapsedContent.Visibility = Visibility.Visible;
        window.ClaudeLive.Visibility = Visibility.Visible;
        typeof(MainWindow).GetMethod("ApplyClaudeLiveMotion", flags)!.Invoke(window, null);
        Assert.True(window.ClaudeLiveStarRotate.HasAnimatedProperties);
        typeof(MainWindow).GetProperty("_isHiddenByFullscreen", flags)!.SetValue(window, true);
        typeof(MainWindow).GetMethod("ApplyClaudeLiveMotion", flags)!.Invoke(window, null);
        Assert.False(window.ClaudeLiveStarRotate.HasAnimatedProperties);
        Assert.False(window.ClaudeLiveStarScale.HasAnimatedProperties);
        var clock = (DispatcherTimer)typeof(MainWindow).GetField("_claudeLiveClock", flags)!.GetValue(window)!;
        Assert.False(clock.IsEnabled);
        typeof(MainWindow).GetProperty("_isHiddenByFullscreen", flags)!.SetValue(window, false);
        typeof(MainWindow).GetMethod("ApplyClaudeLiveMotion", flags)!.Invoke(window, null);
        Assert.True(clock.IsEnabled);
        Assert.True(window.ClaudeLiveStarRotate.HasAnimatedProperties);
        bool reduced = AnimationConfig.ReduceMotion;
        try
        {
            AnimationConfig.SetReduceMotion(true);
            typeof(MainWindow).GetMethod("ApplyClaudeLiveMotion", flags)!.Invoke(window, null);
            Assert.True(clock.IsEnabled);
            Assert.False(window.ClaudeLiveStarRotate.HasAnimatedProperties);
            Assert.False(window.ClaudeLiveStarScale.HasAnimatedProperties);
        }
        finally { AnimationConfig.SetReduceMotion(reduced); }
        ct.ThrowIfCancellationRequested();
    });

    [Fact]
    public void TheStatusWordRollsOverWithoutAGapAndSettlesWhenHidden() => SharedStaTestRunner.RunAsync(async ct =>
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        await using var fixture = new GreetingAcceptanceTests.MainWindowFixture("en", greeting: false,
            configureSettings: settings => settings.EnableLocalOnlyMode = true,
            configureServices: services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));
        var window = fixture.Window;
        using var host = new HudContentHost(window);
        await WpfFrameWaiter.UntilAsync(() => window.CollapsedContent.IsVisible, "closed notch visible", ct);
        window.ClaudeLive.Visibility = Visibility.Visible;
        await WpfFrameWaiter.UntilAsync(() => window.ClaudeLiveStatus.IsVisible, "live pill visible", ct);
        bool reduced = AnimationConfig.ReduceMotion;
        try
        {
            AnimationConfig.SetReduceMotion(false);
            var roll = typeof(MainWindow).GetMethod("RollClaudeLiveStatus", flags)!;
            window.ClaudeLiveStatus.Text = "Thinking";
            roll.Invoke(window, ["Running"]);
            // The new word is there at once; the old one leaves on its own label, so the pill is never empty.
            Assert.Equal("Running", window.ClaudeLiveStatus.Text);
            var ghost = (TextBlock)typeof(MainWindow).GetField("_claudeStatusGhost", flags)!.GetValue(window)!;
            Assert.Equal("Thinking", ghost.Text);
            Assert.Equal(Visibility.Visible, ghost.Visibility);
            Assert.True(ghost.HasAnimatedProperties);

            window.ClaudeLive.Visibility = Visibility.Collapsed;
            Assert.Equal(Visibility.Collapsed, ghost.Visibility);
            Assert.False(ghost.HasAnimatedProperties);
            Assert.False(window.ClaudeLiveStatus.HasAnimatedProperties);
            Assert.Null(window.ClaudeLiveStatus.Effect);
        }
        finally { AnimationConfig.SetReduceMotion(reduced); }
        ct.ThrowIfCancellationRequested();
    });

    [Theory]
    [InlineData("Edit", "claude.status.writing")]
    [InlineData("Write", "claude.status.writing")]
    [InlineData("Read", "claude.status.reading")]
    [InlineData("Grep", "claude.status.reading")]
    [InlineData("Bash", "claude.status.running")]
    [InlineData("PowerShell", "claude.status.running")]
    [InlineData("WebSearch", "claude.status.browsing")]
    [InlineData("Agent", "claude.status.delegating")]
    [InlineData("TodoWrite", "claude.status.planning")]
    [InlineData("mcp__github__get_me", "claude.status.working")]
    public async Task ARunningTool_NamesWhatClaudeDoes(string tool, string expected)
    {
        var tracker = new ClaudeSessionTracker();
        await tracker.HandleHookAsync(Hook("UserPromptSubmit"));
        await tracker.HandleHookAsync(Hook("PreToolUse", tool));
        Assert.Equal(expected, StatusOf(tracker));
    }

    [Fact]
    public async Task BetweenTools_ClaudeIsThinking()
    {
        var tracker = new ClaudeSessionTracker();
        await tracker.HandleHookAsync(Hook("UserPromptSubmit"));
        Assert.Equal("claude.status.thinking", StatusOf(tracker));
        await tracker.HandleHookAsync(Hook("PreToolUse", "Edit"));
        await tracker.HandleHookAsync(Hook("PostToolUse", "Edit"));
        Assert.Equal("claude.status.thinking", StatusOf(tracker));
    }

    [Fact]
    public async Task APermissionPrompt_IsWaiting()
    {
        var tracker = new ClaudeSessionTracker();
        _ = tracker.HandleHookAsync(Hook("PermissionRequest", "Bash"));
        await Task.Yield();
        Assert.Equal("claude.status.waiting", StatusOf(tracker));
    }

    [Fact]
    public async Task AskUserQuestion_IsAQuestion()
    {
        var tracker = new ClaudeSessionTracker();
        var ask = Hook("PermissionRequest");
        ask["tool_name"] = "AskUserQuestion";
        ask["tool_input"] = JsonNode.Parse("""
            {"questions":[{"question":"Which?","header":"Pick","multiSelect":false,
              "options":[{"label":"A","description":""},{"label":"B","description":""}]}]}
            """);
        _ = tracker.HandleHookAsync(ask);
        await Task.Yield();
        Assert.Equal("claude.status.question", StatusOf(tracker));
    }
}
