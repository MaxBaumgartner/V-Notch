using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using VNotch.Services.ClaudeCode;
using Xunit;

namespace VNotch.Tests;

public sealed class CodexUsageTests : IDisposable
{
    private readonly string _directory = Path.Combine(AppContext.BaseDirectory, "CodexUsageTests", Guid.NewGuid().ToString("N"));

    private const string UsageJson = """
        {"plan_type":"plus","rate_limit":{"primary_window":{"used_percent":12,"reset_at":1794666803},
        "secondary_window":{"used_percent":35,"reset_at":1795059618}},"rate_limit_reset_credits":{"available_count":1}}
        """;
    private const string CreditsJson = """
        {"credits":[{"id":"available","status":"available","is_supported_by_plan":true,"expires_at":"2026-12-12T22:01:26Z"},
        {"id":"used","status":"consumed","is_supported_by_plan":true},
        {"id":"unsupported","status":"available","is_supported_by_plan":false}]}
        """;

    [Fact]
    public void ParsesBothWhamWindowsAndUnixResetTimes()
    {
        var usage = Assert.IsType<CodexUsage>(CodexUsageClient.ParseUsage(UsageJson));
        Assert.Equal(12, usage.FiveHour!.UsedPercent);
        Assert.Equal(35, usage.Week!.UsedPercent);
        Assert.Equal(1794666803, usage.FiveHour.ResetsAt!.Value.ToUnixTimeSeconds());
        Assert.Null(CodexUsageClient.ParseRolloutLine("{\"payload\":{\"rate_limits\":{\"primary\":{\"used_percent\":12}}}}"));
        Assert.Equal(1795059618, usage.Week.ResetsAt!.Value.ToUnixTimeSeconds());
        Assert.Null(CodexUsageClient.ParseUsage("{\"rate_limit\":{\"primary_window\":{\"used_percent\":\"invalid\"}}}"));
    }

    [Fact]
    public void CreditsMustBeAvailableAndSupported()
    {
        var credit = Assert.Single(CodexUsageClient.ParseCredits(CreditsJson));
        Assert.Equal("available", credit.Id);
        Assert.Equal(DateTimeOffset.Parse("2026-12-12T22:01:26Z"), credit.ExpiresAt);
        Assert.Empty(CodexUsageClient.ParseCredits("[]"));
    }

    [Fact]
    public void RolloutFallbackReadsLatestValidLineAndToleratesPartialWrites()
    {
        string sessions = Path.Combine(_directory, "sessions");
        Directory.CreateDirectory(sessions);
        string path = Path.Combine(sessions, "rollout-test.jsonl");
        File.WriteAllLines(path, new[]
        {
            RolloutLine(5),
            "{\"payload\":{\"type\":\"other\"}}",
            RolloutLine(27),
            "{\"payload\":{\"rate_limits\":"
        });
        using var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        var usage = Assert.IsType<CodexUsage>(CodexUsageClient.ReadRolloutFallback(sessions));
        Assert.Equal(27, usage.FiveHour!.UsedPercent);
        Assert.Equal(63, usage.Week!.UsedPercent);
        Assert.Equal(1794666803, usage.FiveHour.ResetsAt!.Value.ToUnixTimeSeconds());
    }

    [Fact]
    public void RolloutFallbackScansOnlyTenNewestFiles()
    {
        string sessions = Path.Combine(_directory, "sessions");
        Directory.CreateDirectory(sessions);
        for (int i = 0; i < 11; i++)
        {
            string path = Path.Combine(sessions, $"rollout-{i}.jsonl");
            File.WriteAllText(path, i == 0 ? RolloutLine(98) : "{}");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(i));
        }
        Assert.Null(CodexUsageClient.ReadRolloutFallback(sessions));
    }

    [Fact]
    public async Task ExpiredTokenNeverContactsNetworkAndUsesRolloutFallback()
    {
        WriteAuth(DateTimeOffset.UtcNow.AddMinutes(-1));
        string sessions = Path.Combine(_directory, "sessions");
        Directory.CreateDirectory(sessions);
        File.WriteAllText(Path.Combine(sessions, "rollout-test.jsonl"), RolloutLine(18));
        var handler = new RecordingHandler((_, _) => throw new InvalidOperationException("Expired auth must not send requests"));
        using var client = new CodexUsageClient(handler, _directory);
        var usage = await client.RefreshAsync(TimeSpan.FromMinutes(1));
        Assert.Equal(18, usage!.FiveHour!.UsedPercent);
        Assert.True(client.HasSource);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task LocalOnlyReadsFallbackWithoutNetworkAndIsThrottled()
    {
        WriteAuth(DateTimeOffset.UtcNow.AddHours(1));
        var handler = new RecordingHandler((_, _) => throw new InvalidOperationException("Local-only must not send requests"));
        using var client = new CodexUsageClient(handler, _directory);
        Assert.NotNull(await client.RefreshAsync(TimeSpan.FromMinutes(1), allowNetwork: false));
        Assert.True(client.HasSource);
        Assert.Null(await client.RefreshAsync(TimeSpan.FromMinutes(1), allowNetwork: false));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task NoAuthAndNoRolloutsLeavesProviderHidden()
    {
        var handler = new RecordingHandler((_, _) => throw new InvalidOperationException("Missing auth must not send requests"));
        using var client = new CodexUsageClient(handler, _directory);
        Assert.Null(await client.RefreshAsync(TimeSpan.FromMinutes(1)));
        Assert.False(client.HasSource);
        Assert.Null(client.Latest);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RefreshUsesRequiredHeadersAndNeverConsumesCredit()
    {
        string token = WriteAuth(DateTimeOffset.UtcNow.AddHours(1));
        var handler = new RecordingHandler((request, _) => JsonResponse(request.RequestUri!.AbsolutePath.EndsWith("/usage", StringComparison.Ordinal) ? UsageJson : CreditsJson));
        using var client = new CodexUsageClient(handler, _directory);
        var usage = await client.RefreshAsync(TimeSpan.FromMinutes(1));
        Assert.Single(usage!.Credits);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("Bearer " + token, request.Authorization);
            Assert.Equal("test-account", request.Account);
            Assert.Equal("codex_cli_rs", request.UserAgent);
        });
        Assert.Null(await client.RefreshAsync(TimeSpan.FromMinutes(1)));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ConfirmedRedemptionBuildsConsumeRequestAndImmediatelyRefreshes()
    {
        WriteAuth(DateTimeOffset.UtcNow.AddHours(1));
        var handler = new RecordingHandler((request, _) => JsonResponse(request.Method == HttpMethod.Post ? "{}" :
            request.RequestUri!.AbsolutePath.EndsWith("/usage", StringComparison.Ordinal) ? UsageJson : "{\"credits\":[]}"));
        using var client = new CodexUsageClient(handler, _directory);
        Assert.Equal("resetDone", await client.RedeemAsync("RateLimitResetCredit_test"));
        var consume = Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post);
        Assert.Equal("https://chatgpt.com/backend-api/wham/rate-limit-reset-credits/consume", consume.Url);
        using var body = JsonDocument.Parse(consume.Body!);
        Assert.Equal("RateLimitResetCredit_test", body.RootElement.GetProperty("credit_id").GetString());
        Assert.True(Guid.TryParse(body.RootElement.GetProperty("redeem_request_id").GetString(), out _));
        Assert.Equal(2, body.RootElement.EnumerateObject().Count());
        Assert.Equal(3, handler.Requests.Count);
        Assert.Empty(client.Latest!.Credits);
    }

    [Fact]
    public async Task RedemptionWaitsForAnExistingRefreshThenReadsUsageAndCreditsAgain()
    {
        WriteAuth(DateTimeOffset.UtcNow.AddHours(1));
        var handler = new BlockingRefreshHandler();
        using var client = new CodexUsageClient(handler, _directory);
        Task<CodexUsage?> periodicRefresh = client.RefreshAsync(TimeSpan.FromMinutes(1));
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<string> redeem = client.RedeemAsync("RateLimitResetCredit_test");
        bool waitedForRefresh = !redeem.IsCompleted;
        handler.Release.TrySetResult(true);
        await periodicRefresh.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("resetDone", await redeem.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(waitedForRefresh);
        Assert.Equal(2, handler.UsageCalls);
        Assert.Equal(2, handler.CreditCalls);
        Assert.Empty(client.Latest!.Credits);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "resetRetry")]
    [InlineData(HttpStatusCode.Unauthorized, "resetAuth")]
    [InlineData(HttpStatusCode.Forbidden, "resetAuth")]
    [InlineData(HttpStatusCode.BadRequest, "resetFailed")]
    public async Task RedemptionErrorsDoNotReportSuccess(HttpStatusCode status, string expected)
    {
        WriteAuth(DateTimeOffset.UtcNow.AddHours(1));
        using var client = new CodexUsageClient(new RecordingHandler((_, _) => new HttpResponseMessage(status) { Content = new StringContent("{\"error\":\"test\"}") }), _directory);
        Assert.Equal(expected, await client.RedeemAsync("test"));
    }

    private static string RolloutLine(int percent) => JsonSerializer.Serialize(new
    {
        payload = new
        {
            rate_limits = new
            {
                primary = new { used_percent = percent, resets_at = 1794666803 },
                secondary = new { used_percent = 63, resets_at = 1795059618 }
            }
        }
    });

    private string WriteAuth(DateTimeOffset expiry)
    {
        Directory.CreateDirectory(_directory);
        string payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { exp = expiry.ToUnixTimeSeconds() })))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        string token = "header." + payload + ".signature";
        File.WriteAllText(Path.Combine(_directory, "auth.json"), JsonSerializer.Serialize(new { tokens = new { access_token = token, account_id = "test-account" } }));
        return token;
    }

    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed record CapturedRequest(HttpMethod Method, string Url, string? Authorization, string Account, string UserAgent, string? Body);

    private sealed class RecordingHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(request.Method, request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(),
                request.Headers.GetValues("ChatGPT-Account-Id").Single(), request.Headers.GetValues("User-Agent").Single(), body));
            return respond(request, body);
        }
    }

    private sealed class BlockingRefreshHandler : HttpMessageHandler
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int UsageCalls { get; private set; }
        public int CreditCalls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post) return JsonResponse("{}");
            if (request.RequestUri!.AbsolutePath.EndsWith("/usage", StringComparison.Ordinal))
            {
                if (++UsageCalls == 1)
                {
                    Started.TrySetResult(true);
                    await Release.Task.WaitAsync(cancellationToken);
                }
                return JsonResponse(UsageJson);
            }
            return JsonResponse(++CreditCalls == 1 ? CreditsJson : "{\"credits\":[]}");
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
