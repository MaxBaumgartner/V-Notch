using System.Collections;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VNotch.Controls;
using VNotch.Services;
using VNotch.Services.ClaudeCode;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class ClaudeResetRowTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void ClaudeAndCodexResetsHaveUniformGridRowsAndAlignedActions() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = new Fixture();
        await Task.Delay(1100, ct);
        fixture.Window.UpdateLayout();
        var rows = Rows(fixture.View).ToArray();
        Assert.Equal(3, rows.Length);
        double? actionLeft = null;
        double? actionRight = null;
        foreach (var row in rows)
        {
            Assert.Equal(32, row.ActualHeight, 2);
            Assert.Contains(row.ColumnDefinitions.Cast<ColumnDefinition>(), c => c.SharedSizeGroup == "ResetName");
            Assert.Contains(row.ColumnDefinitions.Cast<ColumnDefinition>(), c => c.SharedSizeGroup == "ResetExpiry");
            Assert.Contains(row.ColumnDefinitions.Cast<ColumnDefinition>(), c => c.SharedSizeGroup == "ResetAction");
            var action = row.Children.Cast<FrameworkElement>().Single(c => Grid.GetColumn(c) == 3);
            var left = action.TranslatePoint(new Point(), fixture.View).X;
            var right = left + action.ActualWidth;
            if (actionLeft.HasValue)
            {
                Assert.Equal(actionLeft.Value, left, 2);
                Assert.Equal(actionRight!.Value, right, 2);
            }
            actionLeft = left;
            actionRight = right;
            var name = row.Children.Cast<FrameworkElement>().Single(c => Grid.GetColumn(c) == 0);
            var expiry = row.Children.Cast<FrameworkElement>().Single(c => Grid.GetColumn(c) == 1);
            Assert.Equal(name.TranslatePoint(new Point(), row).Y + name.ActualHeight / 2,
                expiry.TranslatePoint(new Point(), row).Y + expiry.ActualHeight / 2, 2);
        }
        Assert.Equal(0, fixture.Handler.Posts);
    });

    [Fact]
    public void ConfirmationExpiresAfterFourSecondsWithoutRedeemingAndAnotherTapOnlyRearms() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = new Fixture();
        var row = Rows(fixture.View).Single(r => Equals(r.Tag, "reset-row:claude:launch-2610-fable-week"));
        Click(row);
        Assert.True(States(fixture.View).Contains("claude:launch-2610-fable-week"));
        await Task.Delay(3500, ct);
        Assert.True(States(fixture.View).Contains("claude:launch-2610-fable-week"));
        await Task.Delay(900, ct);
        Assert.False(States(fixture.View).Contains("claude:launch-2610-fable-week"));
        Assert.Equal(0, fixture.Handler.Posts);
        var currentRow = Rows(fixture.View).Single(r => Equals(r.Tag, row.Tag));
        Click(currentRow);
        Assert.True(States(fixture.View).Contains("claude:launch-2610-fable-week"));
        Assert.Equal(0, fixture.Handler.Posts);
    });

    [Fact]
    public void HidingTheTabCancelsConfirmationAndItsTimer() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = new Fixture();
        Click(Rows(fixture.View).Single(r => Equals(r.Tag, "reset-row:claude:full-reset-2611")));
        await Task.Delay(100, ct);
        Assert.True(States(fixture.View).Contains("claude:full-reset-2611"));
        fixture.View.Visibility = Visibility.Collapsed;
        Assert.Empty(States(fixture.View).Keys.Cast<object>());
        Assert.False(Field<DispatcherTimer>(fixture.View, "_resetFeedbackTimer").IsEnabled);
        await Task.Delay(150, ct);
        Assert.Equal(0, fixture.Handler.Posts);
    });

    [Fact]
    public void OnlyTheSecondTapRedeemsAndSuccessStaysVisibleBeforeTheConsumedRowCollapses() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = new Fixture();
        fixture.Handler.HoldPost = true;
        const string key = "claude:launch-2610-fable-week";
        var row = Rows(fixture.View).Single(r => Equals(r.Tag, "reset-row:" + key));
        Click(row);
        Assert.Equal(0, fixture.Handler.Posts);
        Click(row);
        await fixture.Handler.PostStarted.Task.WaitAsync(ct);
        var button = Descendants(row).OfType<Button>().Single();
        Assert.False(button.IsEnabled);
        // A stale routed click must not send a duplicate while the request is in flight.
        Click(row);
        Assert.Equal(1, fixture.Handler.Posts);
        fixture.Handler.ReleasePost.TrySetResult();
        await WpfFrameWaiter.UntilAsync(() =>
            System.Windows.Automation.AutomationProperties.GetName(button).Contains(Loc.Get("control.claude.resetDone"), StringComparison.Ordinal),
            "the drawn success state", ct);
        Assert.Empty(Field<ClaudeUsageClient>(fixture.View, "_usage").Latest!.Grants);
        Assert.Same(row, Rows(fixture.View).Single(r => Equals(r.Tag, "reset-row:" + key)));
        Assert.False(button.IsEnabled);
        await WpfFrameWaiter.UntilAsync(() => !Rows(fixture.View).Any(r => Equals(r.Tag, "reset-row:" + key)),
            "the consumed reset row to collapse", ct);
        Assert.Equal(1, fixture.Handler.Posts);
    });

    [Fact]
    public void HiddenRunningRowsHaveNoAnimationClocks() => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previousMotion = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(false);
        try
        {
            using var fixture = new Fixture();
            fixture.Handler.HoldPost = true;
            const string key = "claude:full-reset-2611";
            var row = Rows(fixture.View).Single(r => Equals(r.Tag, "reset-row:" + key));
            Click(row);
            Click(row);
            await fixture.Handler.PostStarted.Task.WaitAsync(ct);
            Assert.Contains(RowAnimatables(fixture.View, key), item => item.HasAnimatedProperties);
            fixture.View.Visibility = Visibility.Collapsed;
            Assert.All(RowAnimatables(fixture.View, key), item => Assert.False(item.HasAnimatedProperties));
            fixture.Handler.ReleasePost.TrySetResult();
            await WpfFrameWaiter.UntilAsync(() => !States(fixture.View).Contains(key) ||
                States(fixture.View)[key]!.GetType().GetProperty("Result")!.GetValue(States(fixture.View)[key]) != null,
                "hidden reset completion", ct);
            Assert.All(RowAnimatables(fixture.View, key), item => Assert.False(item.HasAnimatedProperties));
            Assert.Equal(1, fixture.Handler.Posts);
        }
        finally { AnimationConfig.SetReduceMotion(previousMotion); }
    });

    [Fact]
    public void ReducedMotionKeepsHoverAndConfirmationStatic() => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previousMotion = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(true);
        try
        {
            using var fixture = new Fixture();
            const string key = "claude:full-reset-2611";
            var row = Rows(fixture.View).Single(r => Equals(r.Tag, "reset-row:" + key));
            var button = Descendants(row).OfType<Button>().Single();
            var pill = Assert.IsType<Border>(VisualTreeHelper.GetParent(button));
            pill.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
            Click(row);
            await Task.Delay(100, ct);
            Assert.True(States(fixture.View).Contains(key));
            Assert.All(RowAnimatables(fixture.View, key), item => Assert.False(item.HasAnimatedProperties));
            Assert.Equal(0, fixture.Handler.Posts);
        }
        finally { AnimationConfig.SetReduceMotion(previousMotion); }
    });

    private static IEnumerable<IAnimatable> RowAnimatables(ControlCenterView view, string key)
    {
        object row = Field<IDictionary>(view, "_resetRows")[key]!;
        return row.GetType().GetFields(Private).Select(field => field.GetValue(row)).OfType<IAnimatable>();
    }

    private static IEnumerable<Grid> Rows(DependencyObject root) => Descendants(root).OfType<Grid>()
        .Where(grid => grid.Tag is string tag && tag.StartsWith("reset-row:", StringComparison.Ordinal));

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Click(Grid row) => Descendants(row).OfType<Button>().Single()
        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static IDictionary States(ControlCenterView view) => Field<IDictionary>(view, "_resetStates");
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Private)!.SetValue(target, value);
    private static void Build(ControlCenterView view) => typeof(ControlCenterView).GetMethod("BuildClaudeSection", Private)!.Invoke(view, null);

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "vnotch-reset-test-" + Guid.NewGuid().ToString("N"));
        private readonly ClaudeUsageClient _usage;
        private readonly CodexUsageClient _codex;
        internal CountingHandler Handler { get; } = new();
        internal ControlCenterView View { get; } = new() { Page = ControlCenterPage.Claude };
        internal Window Window { get; }

        internal Fixture()
        {
            Directory.CreateDirectory(_directory);
            string credentials = Path.Combine(_directory, "credentials.json");
            string account = Path.Combine(_directory, "account.json");
            string versions = Path.Combine(_directory, "versions");
            File.WriteAllText(credentials, """{"claudeAiOauth":{"accessToken":"test-token","expiresAt":9999999999999}}""");
            File.WriteAllText(account, """{"oauthAccount":{"organizationUuid":"e93d1c87-63cf-405a-8941-05743303adea"}}""");
            Directory.CreateDirectory(Path.Combine(versions, "2.1.325"));
            _usage = new ClaudeUsageClient(Handler, credentials, account, versions);
            var limit = new ClaudeLimitWindow(35, DateTimeOffset.UtcNow.AddHours(3));
            _usage.Latest = new ClaudeLimits(limit, limit, new[]
            {
                new ClaudeResetGrant("launch-2610-fable-week", ClaudeResetKind.Fable, 1, true, false, null, DateTimeOffset.Parse("2026-11-17T07:59:00Z")),
                new ClaudeResetGrant("full-reset-2611", ClaudeResetKind.Full, 1, true, false, null, DateTimeOffset.Parse("2026-12-10T22:00:00Z"))
            });
            _codex = new CodexUsageClient(new CountingHandler(), Path.Combine(_directory, "codex"))
            {
                Latest = new CodexUsage(limit, limit, new[] { new CodexResetCredit("credit", DateTimeOffset.Parse("2026-12-12T22:00:00Z")) })
            };
            Set(_codex, "<HasSource>k__BackingField", true);
            Set(View, "_claude", new ClaudeSessionTracker());
            Set(View, "_usage", _usage);
            Set(View, "_codexUsage", _codex);
            Set(View, "_codexReading", true);
            // Show with local-only mode, then enable the fake redeem path after startup refreshes.
            Window = new Window
            {
                Left = -20000, Top = -20000, Width = 640, Height = 560,
                WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
                Background = new SolidColorBrush(Color.FromRgb(23, 23, 25)), Content = View
            };
            Window.Show();
            Set(View, "_isLocalOnly", (Func<bool>)(() => false));
            Build(View);
            Window.UpdateLayout();
        }

        public void Dispose()
        {
            View.Dispose();
            Window.Close();
            _usage.Dispose();
            _codex.Dispose();
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        private int _posts;
        internal int Posts => Volatile.Read(ref _posts);
        internal bool HoldPost { get; set; }
        internal TaskCompletionSource PostStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleasePost { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
            {
                Interlocked.Increment(ref _posts);
                PostStarted.TrySetResult();
                if (HoldPost) await ReleasePost.Task.WaitAsync(cancellationToken);
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.Method == HttpMethod.Post ? """{"result":"success"}""" :
                    """{"five_hour":{"utilization":12},"seven_day":{"utilization":20},"cedar_ember":{"eligible":true,"grants":[]}}""")
            };
        }
    }
}
