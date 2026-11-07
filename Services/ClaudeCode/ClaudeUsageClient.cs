using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace VNotch.Services.ClaudeCode;

/// <summary>One plan limit window. <see cref="UsedPercent"/> is 0–100.</summary>
internal sealed record ClaudeLimitWindow(double UsedPercent, DateTimeOffset? ResetsAt);

internal sealed record ClaudeLimits(ClaudeLimitWindow? FiveHour, ClaudeLimitWindow? Week);

/// <summary>
/// Plan limits from the endpoint Claude Code's <c>/usage</c> uses, signed in with the OAuth token Claude Code
/// keeps in <c>~/.claude/.credentials.json</c>. The token only ever goes to Anthropic. The endpoint is rate limited,
/// so callers ask at most once a minute and failures back off. When the sign-in has run out and no Claude Code is
/// running to renew it, <see cref="ClaudeTokenRefresher"/> renews it the way Claude Code does.
/// </summary>
internal sealed class ClaudeUsageClient : IDisposable
{
    private const string LogTag = "CLAUDE-USAGE";
    private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    private const string Beta = "oauth-2025-04-20";
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromHours(1);

    private readonly HttpClient _http = new(new NativeWinHttpHandler()) { Timeout = TimeSpan.FromSeconds(10) };
    private int _inFlight;
    private DateTime _lastAttemptUtc = DateTime.MinValue;
    private DateTime _blockedUntilUtc = DateTime.MinValue;

    public ClaudeLimits? Latest { get; internal set; }

    public static bool HasCredentials => File.Exists(CredentialsPath);

    private static string CredentialsPath => Path.Combine(ClaudeCodeSetup.ClaudeDirectory, ".credentials.json");

    /// <summary>Refreshes unless a request ran within <paramref name="minAge"/>. Null when nothing new came in.</summary>
    public async Task<ClaudeLimits?> RefreshAsync(TimeSpan minAge, CancellationToken ct = default)
    {
        DateTime now = DateTime.UtcNow;
        if (now < _blockedUntilUtc || now - _lastAttemptUtc < minAge) return null;
        if (Interlocked.Exchange(ref _inFlight, 1) == 1) return null;
        _lastAttemptUtc = now;
        try
        {
            string? token = ReadAccessToken(out bool expired);
            if (token == null && expired)
                token = await ClaudeTokenRefresher.RefreshAsync(_http, CredentialsPath, ct).ConfigureAwait(false);
            if (token == null)
            {
                _blockedUntilUtc = DateTime.UtcNow + FailureBackoff;
                return null;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            request.Headers.TryAddWithoutValidation("anthropic-beta", Beta);
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                TimeSpan wait = response.Headers.RetryAfter?.Delta ?? FailureBackoff;
                _blockedUntilUtc = DateTime.UtcNow + (wait > MaxRetryAfter ? MaxRetryAfter : wait);
                RuntimeLog.Warn(LogTag, $"HTTP {(int)response.StatusCode}");
                return null;
            }

            string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var limits = Parse(json);
            if (limits != null) Latest = limits;
            return limits;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException)
        {
            _blockedUntilUtc = DateTime.UtcNow + FailureBackoff;
            RuntimeLog.Warn(LogTag, ex.Message);
            return null;
        }
        finally
        {
            Volatile.Write(ref _inFlight, 0);
        }
    }

    internal static ClaudeLimits? Parse(string json)
    {
        if (JsonNode.Parse(json) is not JsonObject root) return null;
        var fiveHour = Window(root["five_hour"]);
        var week = Window(root["seven_day"]);
        return fiveHour == null && week == null ? null : new ClaudeLimits(fiveHour, week);
    }

    private static ClaudeLimitWindow? Window(JsonNode? node)
    {
        if (node is not JsonObject obj || obj["utilization"] is not JsonValue used || !used.TryGetValue(out double percent))
            return null;
        DateTimeOffset? resets = null;
        if (obj["resets_at"] is JsonValue at && at.TryGetValue(out string? text) &&
            DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            resets = parsed;
        return new ClaudeLimitWindow(Math.Clamp(percent, 0, 100), resets);
    }

    /// <summary>The current access token; <paramref name="expired"/> says a renewal could help when there is none.</summary>
    private static string? ReadAccessToken(out bool expired)
    {
        expired = false;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(CredentialsPath)) is not JsonObject root ||
                root["claudeAiOauth"] is not JsonObject oauth)
                return null;
            string? token = ClaudeTokenRefresher.UsableAccessToken(oauth, DateTimeOffset.UtcNow);
            expired = token == null && oauth["refreshToken"] != null;
            return token;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}
