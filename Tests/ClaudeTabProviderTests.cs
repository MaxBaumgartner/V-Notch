using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using VNotch.Controls;
using VNotch.Services.ClaudeCode;
using Xunit;

namespace VNotch.Tests;

public sealed class ClaudeTabProviderTests
{
    [Fact]
    public void TokenRowHasOnlyContextAndLimitShare()
    {
        OnSta(() =>
        {
            using var view = new ControlCenterView();
            var stats = Assert.IsType<StackPanel>(Call(view, "TokenStats", new ClaudeSession { Id = "lead" }));
            var grid = Assert.IsType<Grid>(stats.Children[1]);
            Assert.Equal(2, grid.ColumnDefinitions.Count);
            Assert.Equal(2, grid.Children.Count);
        });
    }

    [Fact]
    public void CodexChipsAppearWithoutAnyClaudeSession()
    {
        OnSta(() =>
        {
            using var view = new ControlCenterView();
            Set(view, "_codexSessions", new List<CodexSession>
            {
                new("codex", "Build the widget", "Project", "C:\\Project", CodexSessionStatus.Working, DateTimeOffset.UtcNow)
            });
            var chips = Assert.IsType<WrapPanel>(Call(view, "Chips", new List<ClaudeSession>()));
            var chip = Assert.IsType<Border>(Assert.Single(chips.Children.Cast<UIElement>()));
            var row = Assert.IsType<StackPanel>(chip.Child);
            Assert.Equal("Build the widget", Assert.IsType<TextBlock>(row.Children[1]).Text);
            Assert.False(chip.IsVisible);
        });
    }

    [Fact]
    public void RebuildKeyIncludesGrantAvailabilityAndCodexSessionChanges()
    {
        OnSta(() =>
        {
            using var view = new ControlCenterView();
            var grant = new ClaudeResetGrant("full-reset", ClaudeResetKind.Full, 1, false, true, null, DateTimeOffset.UtcNow.AddDays(1));
            var limits = new ClaudeLimits(null, null, new[] { grant });
            string Key(ClaudeLimits value) => (string)Call(view, "ClaudeKey", new List<ClaudeSession>(), new List<ClaudePermissionRequest>(), value, false)!;
            string initial = Key(limits);
            Assert.NotEqual(initial, Key(limits with { ResetGrants = new[] { grant with { UsableNow = true } } }));
            var session = new CodexSession("codex", "Title", "Project", "C:\\Project", CodexSessionStatus.Idle, DateTimeOffset.UtcNow);
            Set(view, "_codexSessions", new List<CodexSession> { session });
            string idle = Key(limits);
            Assert.NotEqual(initial, idle);
            Set(view, "_codexSessions", new List<CodexSession> { session with { Status = CodexSessionStatus.Working } });
            Assert.NotEqual(idle, Key(limits));
            Set(view, "_codexSessions", new List<CodexSession> { session with { Title = "Renamed" } });
            Assert.NotEqual(idle, Key(limits));
        });
    }

    [Fact]
    public void ResetClockShowsTimeTodayAndDayForAnotherDate()
    {
        var now = DateTimeOffset.Now;
        Assert.Equal(now.ToString("HH:mm"), ControlCenterView.FormatResetTime(now, now));
        Assert.Equal(now.AddDays(1).ToString("ddd HH:mm"), ControlCenterView.FormatResetTime(now.AddDays(1), now));
    }

    [Theory]
    [InlineData("cooldown", "resetCooldown")]
    [InlineData("not_at_limit", "resetOnlyAtLimit")]
    [InlineData("not_started", "resetNotStarted")]
    [InlineData("unknown_reason", "resetUnavailable")]
    public void UnusableGrantsHaveQuietLocalizedReasons(string reason, string expected)
    {
        var grant = new ClaudeResetGrant("grant", ClaudeResetKind.Full, 1, false, false, reason, null);
        Assert.Equal("control.claude." + expected, ControlCenterView.ResetUnavailableReason(grant));
    }

    [Fact]
    public void HeroChipsAndBothProvidersWithResetsFitTheHeightCap()
    {
        OnSta(() =>
        {
            using var view = new ControlCenterView();
            using var codex = new CodexUsageClient();
            Set(view, "_codexUsage", codex);
            var window = new ClaudeLimitWindow(35, DateTimeOffset.UtcNow.AddHours(3));
            codex.Latest = new CodexUsage(window, window, new[] { new CodexResetCredit("credit", DateTimeOffset.UtcNow.AddDays(10)) });
            Set(view, "_codexSessions", new List<CodexSession>
            {
                new("codex", "Build the widget", "Project", "C:\\Project", CodexSessionStatus.Working, DateTimeOffset.UtcNow)
            });
            var limits = new ClaudeLimits(window, window, new[]
            {
                new ClaudeResetGrant("fable", ClaudeResetKind.Fable, 1, true, false, null, DateTimeOffset.UtcNow.AddDays(3)),
                new ClaudeResetGrant("full", ClaudeResetKind.Full, 1, true, false, null, DateTimeOffset.UtcNow.AddDays(20))
            });
            var panel = (StackPanel)typeof(ControlCenterView).GetField("_claudeSection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
            panel.Children.Add((UIElement)Call(view, "Hero", new ClaudeSession
            {
                Id = "lead", Title = "V-Notch", Cwd = "C:\\V-Notch", Status = ClaudeSessionStatus.Working, Activity = "Editing the tab"
            }, new List<ClaudePermissionRequest>())!);
            panel.Children.Add((UIElement)Call(view, "Chips", new List<ClaudeSession>())!);
            panel.Children.Add((UIElement)Call(view, "LimitStrip", limits)!);
            Call(view, "AddResetRows", limits);
            Call(view, "AddCodexLimits");
            panel.Measure(new Size(593, double.PositiveInfinity));
            Assert.InRange(panel.DesiredSize.Height + 48, 1, 560);
        });
    }

    private static object? Call(object target, string method, params object[] arguments) =>
        typeof(ControlCenterView).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, arguments);

    private static void Set(object target, string field, object value) =>
        typeof(ControlCenterView).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
