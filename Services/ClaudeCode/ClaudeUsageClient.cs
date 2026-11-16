using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VNotch.Services.ClaudeCode;

/// <summary>One plan limit window. <see cref="UsedPercent"/> is 0–100.</summary>
internal sealed record ClaudeLimitWindow(double UsedPercent, DateTimeOffset? ResetsAt);

internal enum ClaudeResetKind { Fable, Full, FiveHour }

internal sealed record ClaudeResetGrant(string Id, ClaudeResetKind Kind, int ResetsLeft, bool UsableNow,
    bool UseRequiresLimit, string? UnavailableReason, DateTimeOffset? EndsAt);

internal sealed record ClaudeResetResult(bool Success, string? Reason = null);

internal sealed record ClaudeLimits(ClaudeLimitWindow? FiveHour, ClaudeLimitWindow? Week,
    IReadOnlyList<ClaudeResetGrant>? ResetGrants = null)
{
    public IReadOnlyList<ClaudeResetGrant> Grants => ResetGrants ?? Array.Empty<ClaudeResetGrant>();
}

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

    private readonly HttpClient _http;
    private readonly string _credentialsPath;
    private readonly string _accountPath;
    private readonly string _versionsPath;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private int _claimInFlight;
    private DateTime _lastAttemptUtc = DateTime.MinValue;
    private DateTime _blockedUntilUtc = DateTime.MinValue;

    public ClaudeLimits? Latest { get; internal set; }

    public static bool HasCredentials => File.Exists(CredentialsPath);

    private static string CredentialsPath => Path.Combine(ClaudeCodeSetup.ClaudeDirectory, ".credentials.json");

    public ClaudeUsageClient() : this(new NativeWinHttpHandler(), CredentialsPath,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "claude", "versions")) { }

    internal ClaudeUsageClient(HttpMessageHandler handler, string profileDirectory) : this(handler,
        Path.Combine(profileDirectory, ".claude", ".credentials.json"),
        Path.Combine(profileDirectory, ".claude.json"), Path.Combine(profileDirectory, ".local", "share", "claude", "versions")) { }

    internal ClaudeUsageClient(HttpMessageHandler handler, string credentialsPath, string accountPath, string versionsPath)
    {
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        _credentialsPath = credentialsPath;
        _accountPath = accountPath;
        _versionsPath = versionsPath;
    }

    /// <summary>Refreshes unless a request ran within <paramref name="minAge"/>. Null when nothing new came in.</summary>
    public Task<ClaudeLimits?> RefreshAsync(TimeSpan minAge, CancellationToken ct = default) => RefreshAsync(minAge, false, ct);

    private async Task<ClaudeLimits?> RefreshAsync(TimeSpan minAge, bool afterClaim, CancellationToken ct)
    {
        if (afterClaim) await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        else if (!await _refreshGate.WaitAsync(0, ct).ConfigureAwait(false)) return null;
        try
        {
            DateTime now = DateTime.UtcNow;
            if (!afterClaim && (now < _blockedUntilUtc || now - _lastAttemptUtc < minAge)) return null;
            if (afterClaim) _blockedUntilUtc = DateTime.MinValue;
            _lastAttemptUtc = now;
            string? token = ReadAccessToken(out bool expired);
            if (token == null && expired)
                token = await ClaudeTokenRefresher.RefreshAsync(_http, _credentialsPath, ct).ConfigureAwait(false);
            if (token == null)
            {
                _blockedUntilUtc = DateTime.UtcNow + FailureBackoff;
                return null;
            }

            string? cliVersion = FindCliVersion(_versionsPath);
            using var request = Request(HttpMethod.Get,
                UsageUrl + (cliVersion == null ? "" : "?cedar_ember=1&skip_spend=1"), token, cliVersion);
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
            if (cliVersion == null && limits != null) limits = limits with { ResetGrants = null };
            if (limits != null) Latest = limits;
            return limits;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or JsonException)
        {
            _blockedUntilUtc = DateTime.UtcNow + FailureBackoff;
            RuntimeLog.Warn(LogTag, ex.Message);
            return null;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    internal static ClaudeLimits? Parse(string json)
    {
        if (JsonNode.Parse(json) is not JsonObject root) return null;
        var fiveHour = Window(root["five_hour"]);
        var week = Window(root["seven_day"]);
        var grants = new List<ClaudeResetGrant>();
        if (root["cedar_ember"] is JsonObject program && Bool(program["eligible"]) && program["grants"] is JsonArray items)
        {
            foreach (var item in items.OfType<JsonObject>())
            {
                string? id = String(item["id"]);
                int left = item["resets_left"] is JsonValue value && value.TryGetValue(out int count) ? count : 0;
                if (id == null || left <= 0 || Bool(item["paused"])) continue;
                bool full = item["clears"] is JsonArray clears && clears.Any(c => String(c) == "seven_day");
                var kind = Bool(item["gives_back_weekly"]) ? ClaudeResetKind.Fable : full ? ClaudeResetKind.Full : ClaudeResetKind.FiveHour;
                grants.Add(new ClaudeResetGrant(id, kind, left, Bool(item["usable_now"]), Bool(item["use_requires_limit"]),
                    String(item["unavailable_reason"]), Date(item["ends_at"])));
            }
        }
        return fiveHour == null && week == null && grants.Count == 0 ? null : new ClaudeLimits(fiveHour, week, grants);
    }

    /// <summary>Consumes only the grant explicitly confirmed by the caller.</summary>
    public async Task<ClaudeResetResult> ClaimAsync(string grantId, CancellationToken ct = default)
    {
        if (!Regex.IsMatch(grantId, "\\A[a-z0-9_-]{1,40}\\z", RegexOptions.CultureInvariant))
            return new(false, "unavailable");
        if (Interlocked.Exchange(ref _claimInFlight, 1) == 1) return new(false, "unavailable");
        bool attempted = false;
        try
        {
            string? version = FindCliVersion(_versionsPath);
            if (version == null) return new(false, "unavailable");
            string? token = ReadAccessToken(out bool expired);
            if (token == null && expired)
                token = await ClaudeTokenRefresher.RefreshAsync(_http, _credentialsPath, ct).ConfigureAwait(false);
            if (token == null) return new(false, "auth");
            if (JsonNode.Parse(File.ReadAllText(_accountPath)) is not JsonObject account ||
                !Guid.TryParse(String(account["oauthAccount"]?["organizationUuid"]), out var org))
                return new(false, "auth");
            using var request = Request(HttpMethod.Post,
                $"https://api.anthropic.com/api/organizations/{org:D}/reset_rate_limits", token, version);
            request.Content = new StringContent(JsonSerializer.Serialize(new
            {
                program = "cedar_ember", grant_id = grantId, request_id = Guid.NewGuid().ToString("N")
            }), Encoding.UTF8, "application/json");
            attempted = true;
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests) return new(false, "retry");
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return new(false, "auth");
            if (!response.IsSuccessStatusCode)
            {
                RuntimeLog.Warn(LogTag, $"Reset HTTP {(int)response.StatusCode}");
                return new(false, "failed");
            }
            string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var reply = JsonNode.Parse(json) as JsonObject;
            string? result = String(reply?["result"]);
            bool success = result is "success" or "ok" or "reset" or "applied" or "reset_applied";
            if (!success) return new(false, String(reply?["reason"]) ?? "failed");
            return new(true);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            RuntimeLog.Warn(LogTag, $"Reset failed: {ex.GetType().Name}");
            return new(false, "failed");
        }
        finally
        {
            try
            {
                if (attempted)
                {
                    await RefreshAsync(TimeSpan.Zero, true, ct).ConfigureAwait(false);
                }
            }
            finally { Volatile.Write(ref _claimInFlight, 0); }
        }
    }

    private static HttpRequestMessage Request(HttpMethod method, string url, string token, string? version)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        request.Headers.TryAddWithoutValidation("anthropic-beta", Beta);
        if (version != null) request.Headers.TryAddWithoutValidation("User-Agent", $"claude-cli/{version} (external, cli)");
        return request;
    }

    internal static string? FindCliVersion(string directory)
    {
        const string number = "(?:0|[1-9][0-9]*)";
        const string pre = "(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)";
        const string build = "[0-9A-Za-z-]+";
        const string pattern = "\\A" + number + "\\." + number + "\\." + number +
            "(?:-" + pre + "(?:\\." + pre + ")*)?(?:\\+" + build + "(?:\\." + build + ")*)?\\z";
        try
        {
            // Claude Code keeps each version as a bare executable named after it (older installs used folders).
            return Directory.EnumerateFileSystemEntries(directory).Select(Path.GetFileName)
                .Where(name => name != null && Regex.IsMatch(name, pattern, RegexOptions.CultureInvariant))
                .OrderBy(name => name, Comparer<string?>.Create(CompareVersions)).LastOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static int CompareVersions(string? a, string? b)
    {
        string[] left = a!.Split('+')[0].Split('-', 2), right = b!.Split('+')[0].Split('-', 2);
        var l = left[0].Split('.');
        var r = right[0].Split('.');
        for (int i = 0; i < 3; i++)
        {
            int part = CompareNumeric(l[i], r[i]);
            if (part != 0) return part;
        }
        if (left.Length != right.Length) return left.Length == 1 ? 1 : -1;
        if (left.Length == 1) return 0;
        l = left[1].Split('.');
        r = right[1].Split('.');
        for (int i = 0; i < Math.Min(l.Length, r.Length); i++)
        {
            bool ln = l[i].All(char.IsAsciiDigit), rn = r[i].All(char.IsAsciiDigit);
            int part = ln && rn ? CompareNumeric(l[i], r[i]) : ln != rn ? (ln ? -1 : 1) : StringComparer.Ordinal.Compare(l[i], r[i]);
            if (part != 0) return part;
        }
        return l.Length.CompareTo(r.Length);
    }

    private static int CompareNumeric(string a, string b) => a.Length != b.Length ? a.Length.CompareTo(b.Length) : StringComparer.Ordinal.Compare(a, b);
    private static bool Bool(JsonNode? node) => node is JsonValue v && v.TryGetValue(out bool value) && value;
    private static string? String(JsonNode? node) => node is JsonValue v && v.TryGetValue(out string? value) ? value : null;
    private static DateTimeOffset? Date(JsonNode? node) => DateTimeOffset.TryParse(String(node), CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal, out var date) ? date : null;

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
    private string? ReadAccessToken(out bool expired)
    {
        expired = false;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(_credentialsPath)) is not JsonObject root ||
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
