using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VNotch.Controls;
using VNotch.Services;
using VNotch.Services.ClaudeCode;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class ClaudeFaceTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    public void SummaryUnfoldsIntoTheSameColumnsAtEverySlotCount(int count) => SharedStaTestRunner.Run(() =>
    {
        using var fixture = new Fixture(count);
        var face = Get<Grid>(fixture.View, "_claudeFace");
        var summary = Get<Grid>(fixture.View, "_accountFront");
        var rows = Get<Grid>(fixture.View, "_accountRows");
        Assert.Equal(180, face.ActualHeight);
        Assert.True(summary.IsVisible);
        Assert.False(rows.IsVisible);
        Assert.Equal($"+{count}", Get<TextBlock>(fixture.View, "_accountCount").Text);

        fixture.Open();
        Assert.False(summary.IsVisible);
        var accountRows = rows.Children.OfType<Grid>().ToArray();
        Assert.Equal(count + 1, accountRows.Length);
        var actionPositions = accountRows.Select(row => row.Children.OfType<Button>().Single().TranslatePoint(new Point(), face).X);
        Assert.Single(actionPositions.Distinct());
        Assert.All(accountRows, row =>
        {
            double top = row.TranslatePoint(new Point(), face).Y;
            Assert.True(top >= 43);
            Assert.True(top + row.ActualHeight <= face.ActualHeight);
        });
        Assert.Equal(count == 5 ? 295 : 49 + (count + 1) * 54, face.ActualHeight);
        Assert.True(fixture.View.TryFoldClaudeAccounts());
        Assert.False(fixture.View.TryFoldClaudeAccounts());
    });

    [Fact]
    public void RoundedButtonsKeepTheirShape() => SharedStaTestRunner.Run(() =>
    {
        using var fixture = new Fixture(2);
        var face = Get<Grid>(fixture.View, "_claudeFace");
        CornerShapes.AssertNoneStretched(face);
        fixture.Open();
        CornerShapes.AssertNoneStretched(face);
    });

    [Fact]
    public void ChangedUsageMovesOnceAndHiddenUpdatesStayStill() => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previousMotion = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(false);
        try
        {
            using var fixture = new Fixture(2);
            var meter = Get<Array>(fixture.View, "_claudeFrontMeters").GetValue(0)!;
            var fill = (ScaleTransform)meter.GetType().GetProperty("Grow")!.GetValue(meter)!;
            Assert.Equal(.32, fill.ScaleX);
            Assert.False(fill.HasAnimatedProperties);
            fixture.Usage.Latest = fixture.Usage.Latest! with { FiveHour = new(62, null) };
            fixture.Build();
            Assert.True(Motion.IsRunning(fill, ScaleTransform.ScaleXProperty));
            await WpfFrameWaiter.UntilAsync(() => !Motion.IsRunning(fill, ScaleTransform.ScaleXProperty), "first usage change", ct);
            Assert.Equal(.62, fill.ScaleX, 8);
            fixture.Build();
            Assert.False(Motion.IsRunning(fill, ScaleTransform.ScaleXProperty));
            fixture.View.Visibility = Visibility.Collapsed;
            fixture.Usage.Latest = fixture.Usage.Latest with { FiveHour = new(44, null) };
            fixture.Build();
            Assert.False(fill.HasAnimatedProperties);
        }
        finally { AnimationConfig.SetReduceMotion(previousMotion); }
    });

    [Fact]
    public void NextUsesTheLowestMaximumAndSkipsBlockedSlots()
    {
        var now = DateTimeOffset.UtcNow;
        using var a = new CodexAccount(new("A", @"C:\SlotA"), new());
        using var b = new CodexAccount(new("B", @"C:\SlotB"), new());
        a.Budget = new(new(24, now.AddHours(1)), new(38, now.AddDays(1)), null, null);
        b.Budget = new(new(68, now.AddHours(1)), new(72, now.AddDays(1)), null, null);
        Assert.Same(a, CodexAccount.Next([a, b], now));
        a.Budget = a.Budget with { BlockedUntil = now.AddMinutes(15) };
        Assert.Same(b, CodexAccount.Next([a, b], now));
        Assert.Same(a, CodexAccount.Next([a, b], now.AddMinutes(16)));
    }

    [Fact]
    public void RouterFilesUseNamedSlotsAndIgnoreEmailFields()
    {
        using var slots = JsonDocument.Parse("""
            [{"name":"A","home":"C:\\SlotA","email":"private@example.test"},{"name":"private@example.test","home":"C:\\SlotX"}]
            """);
        Assert.Equal(new CodexSlot("A", @"C:\SlotA"), Assert.Single(CodexAccounts.ParseSlots(slots.RootElement)));
        using var budgets = JsonDocument.Parse("""
            {"codexAccounts":[{"name":"A","fiveHour":{"used":24,"resetsAt":"2026-11-24T19:40:00Z"},
            "week":{"used":38,"resetsAt":"2026-11-27T08:00:00Z"},"blocked":"2026-11-24T19:40:00Z"}]}
            """);
        var budget = CodexAccounts.ParseBudget(budgets.RootElement)["a"];
        Assert.Equal(24, budget.FiveHour!.UsedPercent);
        Assert.Equal(38, budget.Week!.UsedPercent);
        Assert.Equal(budget.FiveHour.ResetsAt, budget.BlockedUntil);
    }

    [Fact]
    public void OnlyTheConfirmedSlotUsesItsOwnAuth() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = new Fixture(2);
        fixture.Open();
        var rows = Get<Grid>(fixture.View, "_accountRows").Children.OfType<Grid>().ToArray();
        var first = rows[1].Children.OfType<Button>().Single();
        var second = rows[2].Children.OfType<Button>().Single();
        first.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(0, fixture.Handlers[0].Posts);
        second.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(0, fixture.Handlers[1].Posts);
        second.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await fixture.Handlers[1].Posted.Task.WaitAsync(ct);
        Assert.Equal(0, fixture.Handlers[0].Posts);
        Assert.Equal(1, fixture.Handlers[1].Posts);
        Assert.Equal("account-B", fixture.Handlers[1].Account);
    });

    [Fact]
    public void LowAllowanceNoticeKeepsWhiteTextAndStaysForAtLeastEightSeconds() => SharedStaTestRunner.Run(() =>
    {
        using var fixture = new Fixture(2);
        Assert.True(fixture.View.TryShowAllowanceNotice("Codex A · 18 % left · resets 00:15", 20));
        Assert.False(fixture.View.TryShowAllowanceNotice("Codex B · 45 % left · resets 01:15", 50));
        var text = Get<TextBlock>(fixture.View, "_accountNoticeText");
        Assert.Equal("Codex A · 18 % left · resets 00:15", text.Text);
        Assert.Equal(Color.FromRgb(245, 245, 247), Assert.IsType<SolidColorBrush>(text.Foreground).Color);
        var mark = Get<Border>(fixture.View, "_accountNoticeMark");
        Assert.Equal(Color.FromRgb(210, 133, 126), Assert.IsType<SolidColorBrush>(mark.Background).Color);
        var timer = Get<System.Windows.Threading.DispatcherTimer>(fixture.View, "_accountNoticeTimer");
        Assert.True(timer.IsEnabled);
        Assert.True(timer.Interval >= TimeSpan.FromSeconds(8));
    });

    private static T Get<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "vnotch-face-" + Guid.NewGuid().ToString("N"));
        private readonly Window _window;
        public ControlCenterView View { get; } = new() { Page = ControlCenterPage.Claude };
        public ClaudeUsageClient Usage { get; } = new();
        public List<CreditHandler> Handlers { get; } = [];

        public Fixture(int slots)
        {
            Set("_claude", new ClaudeSessionTracker());
            Set("_usage", Usage);
            Set("_claudeInstalled", true);
            Set("_codexReading", true);
            Set("_isLocalOnly", (Func<bool>)(() => true));
            Usage.Latest = new(new(32, null), new(46, null), Plan: "Pro");
            var accounts = Get<List<CodexAccount>>(View, "_codexAccounts");
            for (int i = 0; i < slots; i++)
            {
                string label = ((char)('A' + i)).ToString();
                string home = Path.Combine(_directory, label);
                string account = "account-" + label;
                Directory.CreateDirectory(home);
                string payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"exp":4102444800}""")).TrimEnd('=');
                File.WriteAllText(Path.Combine(home, "auth.json"), JsonSerializer.Serialize(new
                {
                    tokens = new { account_id = account, access_token = $"synthetic.{payload}.signature" }
                }));
                var handler = new CreditHandler();
                Handlers.Add(handler);
                accounts.Add(new(new(label, home), new(handler, home)
                {
                    Latest = new(new(24 + i, null), new(38 + i, null), [new("credit", null, account)], "Plus")
                }));
            }
            _window = new BackgroundWindow { Width = 628, Height = 500, Content = View, WindowStyle = WindowStyle.None };
            _window.Show();
            Set("_isLocalOnly", (Func<bool>)(() => false));
            Build();
        }

        public void Open()
        {
            typeof(ControlCenterView).GetMethod("SetAccountsOpen", Private)!.Invoke(View, [true, false]);
            _window.UpdateLayout();
        }

        public void Build()
        {
            typeof(ControlCenterView).GetMethod("BuildClaudeSection", Private)!.Invoke(View, null);
            _window.UpdateLayout();
        }

        private void Set(string name, object value) => typeof(ControlCenterView).GetField(name, Private)!.SetValue(View, value);

        public void Dispose()
        {
            View.Dispose();
            _window.Close();
            Usage.Dispose();
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class CreditHandler : HttpMessageHandler
    {
        public int Posts { get; private set; }
        public string? Account { get; private set; }
        public TaskCompletionSource Posted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
            {
                Posts++;
                Account = request.Headers.GetValues("ChatGPT-Account-Id").Single();
                Posted.TrySetResult();
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }
    }
}
