using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using VNotch.Services.ClaudeCode;
using Xunit;

namespace VNotch.Tests;

public sealed class ClaudeResetGrantTests : IDisposable
{
    private readonly string _directory = Path.Combine(AppContext.BaseDirectory, "claude-reset-tests-" + Guid.NewGuid().ToString("N"));
    private const string Organization = "49efb1b3-45dc-4665-b0a0-611f5f50e82c";
    private const string Usage = """
        {"five_hour":{"utilization":12,"resets_at":"2026-11-17T19:00:00Z"},"seven_day":{"utilization":35},
         "cedar_ember":{"eligible":true,"grants":[
          {"id":"fable","resets_left":1,"usable_now":true,"gives_back_weekly":true,"clears":["five_hour"],"ends_at":"2026-11-17T07:59:00Z"},
          {"id":"full","resets_left":2,"usable_now":false,"use_requires_limit":true,"unavailable_reason":"not_at_limit","clears":["five_hour","seven_day"]},
          {"id":"five","resets_left":1,"usable_now":true,"clears":["five_hour"]},
          {"id":"used","resets_left":0,"usable_now":true},
          {"id":"paused","resets_left":1,"paused":true,"usable_now":true}]}}
        """;

    [Fact]
    public void ParsesGrantKindsAndAvailabilityWithoutShowingSpentOrPausedGrants()
    {
        var limits = Assert.IsType<ClaudeLimits>(ClaudeUsageClient.Parse(Usage));
        Assert.Equal(12, limits.FiveHour!.UsedPercent);
        Assert.Equal(35, limits.Week!.UsedPercent);
        Assert.Collection(limits.Grants,
            grant =>
            {
                Assert.Equal(ClaudeResetKind.Fable, grant.Kind);
                Assert.True(grant.UsableNow);
                Assert.Equal(DateTimeOffset.Parse("2026-11-17T07:59:00Z"), grant.EndsAt);
            },
            grant =>
            {
                Assert.Equal(ClaudeResetKind.Full, grant.Kind);
                Assert.False(grant.UsableNow);
                Assert.True(grant.UseRequiresLimit);
                Assert.Equal("not_at_limit", grant.UnavailableReason);
                Assert.Equal(2, grant.ResetsLeft);
            },
            grant => Assert.Equal(ClaudeResetKind.FiveHour, grant.Kind));
        Assert.Empty(Assert.IsType<ClaudeLimits>(ClaudeUsageClient.Parse("""{"five_hour":{"utilization":0}}""")).Grants);
    }

    [Fact]
    public void PicksHighestSemverDirectoryIncludingPrereleaseOrdering()
    {
        Directory.CreateDirectory(_directory);
        foreach (string version in new[] { "2.1.9", "2.1.325", "2.2.0-rc.9", "2.2.0-rc.10", "garbage", "02.3.4", "9.0.0-..", "9.0.0-01" })
            Directory.CreateDirectory(Path.Combine(_directory, version));
        Assert.Equal("2.2.0-rc.10", ClaudeUsageClient.FindCliVersion(_directory));
        Directory.CreateDirectory(Path.Combine(_directory, "2.2.0"));
        Assert.Equal("2.2.0", ClaudeUsageClient.FindCliVersion(_directory));
        Assert.Null(ClaudeUsageClient.FindCliVersion(Path.Combine(_directory, "missing")));
    }

    [Fact]
    public void FindsTheVersionWhenEachVersionIsAnExecutableFile()
    {
        // Native Claude Code installs keep ~/.local/share/claude/versions/<version> as plain files.
        string versions = Path.Combine(_directory, "files");
        Directory.CreateDirectory(versions);
        File.WriteAllText(Path.Combine(versions, "2.1.322"), "");
        File.WriteAllText(Path.Combine(versions, "2.1.325"), "");
        Assert.Equal("2.1.325", ClaudeUsageClient.FindCliVersion(versions));
    }

    [Fact]
    public async Task ClaimUsesTheCliProtocolAndImmediatelyRefreshesUsage()
    {
        var handler = new RecordingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"result":"success","resets_left":0,"cleared":["five_hour"]}""")
        });
        using var client = Client(handler);
        Assert.NotNull(await client.RefreshAsync(TimeSpan.Zero));
        var result = await client.ClaimAsync("launch-2610-fable-week");
        Assert.True(result.Success);
        Assert.Equal(3, handler.Calls.Count);
        var post = handler.Calls[1];
        Assert.Equal(HttpMethod.Post, post.Method);
        Assert.Equal($"https://api.anthropic.com/api/organizations/{Organization}/reset_rate_limits", post.Url);
        Assert.Equal("Bearer test-access", post.Authorization);
        Assert.Equal("oauth-2025-04-20", post.Beta);
        Assert.Equal("claude-cli/2.1.325 (external, cli)", post.Agent);
        Assert.Equal("application/json", post.ContentType);
        var body = Assert.IsType<JsonObject>(JsonNode.Parse(post.Body!));
        Assert.Equal(3, body.Count);
        Assert.Equal("cedar_ember", (string?)body["program"]);
        Assert.Equal("launch-2610-fable-week", (string?)body["grant_id"]);
        Assert.Matches("^[A-Za-z0-9_-]{1,64}$", (string)body["request_id"]!);
        Assert.Equal(32, ((string)body["request_id"]!).Length);
        Assert.Equal("https://api.anthropic.com/api/oauth/usage?cedar_ember=1&skip_spend=1", handler.Calls[2].Url);
        Assert.NotNull(client.Latest);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "retry")]
    [InlineData(HttpStatusCode.Unauthorized, "auth")]
    [InlineData(HttpStatusCode.Forbidden, "auth")]
    [InlineData(HttpStatusCode.InternalServerError, "failed")]
    public async Task ClaimErrorsAreQuietAndStillRefreshGrants(HttpStatusCode status, string reason)
    {
        var handler = new RecordingHandler((_, _) => new HttpResponseMessage(status));
        using var client = Client(handler);
        var result = await client.ClaimAsync("full-reset-2611");
        Assert.False(result.Success);
        Assert.Equal(reason, result.Reason);
        Assert.Equal(2, handler.Calls.Count);
        Assert.Equal(HttpMethod.Get, handler.Calls[1].Method);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Full-reset")]
    [InlineData("has/slash")]
    [InlineData("full\n")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task InvalidGrantIdsNeverSendAClaim(string id)
    {
        var handler = new RecordingHandler((_, _) => throw new InvalidOperationException("No request expected"));
        using var client = Client(handler);
        Assert.False((await client.ClaimAsync(id)).Success);
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task MissingCliVersionUsesPlainUsageAndCannotClaim()
    {
        var handler = new RecordingHandler((_, _) => throw new InvalidOperationException("No post expected"));
        using var client = Client(handler, withVersion: false);
        await client.RefreshAsync(TimeSpan.Zero);
        Assert.False((await client.ClaimAsync("full-reset")).Success);
        var call = Assert.Single(handler.Calls);
        Assert.Equal("https://api.anthropic.com/api/oauth/usage", call.Url);
        Assert.Null(call.Agent);
        Assert.Empty(client.Latest!.Grants);
    }

    [Fact]
    public async Task ClaimWaitsForAnOlderUsageRequestThenFetchesFreshGrants()
    {
        var handler = new BlockingUsageHandler();
        using var client = Client(handler);
        var oldRefresh = client.RefreshAsync(TimeSpan.Zero);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var claim = client.ClaimAsync("full-reset");
        Assert.False(claim.IsCompleted);
        handler.Release.TrySetResult();
        await oldRefresh;
        Assert.True((await claim).Success);
        Assert.Equal(2, handler.UsageCount);
    }

    private ClaudeUsageClient Client(HttpMessageHandler handler, bool withVersion = true)
    {
        Directory.CreateDirectory(_directory);
        string credentials = Path.Combine(_directory, "credentials.json");
        string account = Path.Combine(_directory, "account.json");
        string versions = Path.Combine(_directory, "versions");
        File.WriteAllText(credentials, """{"claudeAiOauth":{"accessToken":"test-access","expiresAt":9999999999999}}""");
        File.WriteAllText(account, System.Text.Json.JsonSerializer.Serialize(new { oauthAccount = new { organizationUuid = Organization } }));
        if (withVersion) Directory.CreateDirectory(Path.Combine(versions, "2.1.325"));
        return new ClaudeUsageClient(handler, credentials, account, versions);
    }

    private sealed record Call(HttpMethod Method, string Url, string? Body, string? Authorization, string? Beta, string? Agent, string? ContentType);

    private sealed class BlockingUsageHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int UsageCount { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get && ++UsageCount == 1)
            {
                Started.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(request.Method == HttpMethod.Get ? Usage : """{"result":"success"}""")
            };
        }
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Call> Calls { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add(new(request.Method, request.RequestUri!.AbsoluteUri,
                request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken),
                request.Headers.Authorization?.ToString(), request.Headers.GetValues("anthropic-beta").Single(),
                request.Headers.TryGetValues("User-Agent", out var agent) ? string.Join(" ", agent) : null,
                request.Content?.Headers.ContentType?.MediaType));
            return request.Method == HttpMethod.Get
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Usage) }
                : respond(request, cancellationToken);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
