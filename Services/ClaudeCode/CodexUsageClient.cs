using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VNotch.Services.ClaudeCode;

internal sealed record CodexResetCredit(string Id, DateTimeOffset? ExpiresAt);
internal sealed record CodexUsage(ClaudeLimitWindow? FiveHour, ClaudeLimitWindow? Week, IReadOnlyList<CodexResetCredit> Credits);

/// <summary>Reads Codex's existing sign-in without renewing or changing it. Call from a background thread.</summary>
internal sealed class CodexUsageClient : IDisposable
{
    private const string BaseUrl = "https://chatgpt.com/backend-api/wham/";
    private const string LogTag = "CODEX-USAGE";
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromMinutes(5);
    private readonly HttpClient _http;
    private readonly string _directory;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private int _redeeming;
    private DateTime _lastAttemptUtc = DateTime.MinValue;
    private DateTime _blockedUntilUtc = DateTime.MinValue;

    internal CodexUsageClient(HttpMessageHandler? handler = null, string? codexDirectory = null)
    {
        _http = new HttpClient(handler ?? new NativeWinHttpHandler()) { Timeout = TimeSpan.FromSeconds(10) };
        _directory = codexDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    }

    public CodexUsage? Latest { get; internal set; }
    public bool HasSource { get; private set; }

    public Task<CodexUsage?> RefreshAsync(TimeSpan minAge, CancellationToken ct = default, bool allowNetwork = true) =>
        RefreshAsync(minAge, ct, allowNetwork, afterRedeem: false);

    private async Task<CodexUsage?> RefreshAsync(TimeSpan minAge, CancellationToken ct, bool allowNetwork, bool afterRedeem)
    {
        if (afterRedeem) await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        else if (!await _refreshGate.WaitAsync(0, ct).ConfigureAwait(false)) return null;
        try
        {
            var now = DateTime.UtcNow;
            if (!afterRedeem && now - _lastAttemptUtc < minAge) return null;
            if (afterRedeem) _blockedUntilUtc = DateTime.MinValue;
            _lastAttemptUtc = now;
            bool hasAuth = File.Exists(Path.Combine(_directory, "auth.json"));
            var auth = ReadAuth();
            CodexUsage? usage = null;
            if (allowNetwork && auth != null && now >= _blockedUntilUtc)
            {
                try
                {
                    using var request = Request(HttpMethod.Get, "usage", auth.Value);
                    using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
                    string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                    {
                        usage = ParseUsage(body);
                        using var creditsRequest = Request(HttpMethod.Get, "rate-limit-reset-credits", auth.Value);
                        using var creditsResponse = await _http.SendAsync(creditsRequest, ct).ConfigureAwait(false);
                        string creditsBody = await creditsResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                        if (creditsResponse.IsSuccessStatusCode)
                            usage = (usage ?? new CodexUsage(null, null, Array.Empty<CodexResetCredit>())) with { Credits = ParseCredits(creditsBody) };
                        else
                            Failed(creditsResponse, creditsBody, auth.Value.Token);
                    }
                    else
                        Failed(response, body, auth.Value.Token);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
                {
                    _blockedUntilUtc = DateTime.UtcNow + FailureBackoff;
                    RuntimeLog.Warn(LogTag, ex.GetType().Name);
                }
            }

            usage ??= ReadRolloutFallback(Path.Combine(_directory, "sessions"));
            HasSource = hasAuth || usage != null;
            Latest = usage ?? (hasAuth ? new CodexUsage(null, null, Array.Empty<CodexResetCredit>()) : null);
            return Latest;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>Only called after the user confirms a specific credit. The sign-in file is always read-only.</summary>
    public async Task<string> RedeemAsync(string creditId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(creditId) || Interlocked.Exchange(ref _redeeming, 1) == 1) return "resetFailed";
        try
        {
            var auth = ReadAuth();
            if (auth == null) return "resetAuth";
            using var request = Request(HttpMethod.Post, "rate-limit-reset-credits/consume", auth.Value);
            request.Content = new StringContent(JsonSerializer.Serialize(new
            {
                credit_id = creditId,
                redeem_request_id = Guid.NewGuid().ToString()
            }), Encoding.UTF8, "application/json");
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Failed(response, body, auth.Value.Token);
                return response.StatusCode switch
                {
                    HttpStatusCode.TooManyRequests => "resetRetry",
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "resetAuth",
                    _ => "resetFailed"
                };
            }
            // Refresh right after spending the credit, even when a previous GET backed off.
            await RefreshAsync(TimeSpan.Zero, ct, allowNetwork: true, afterRedeem: true).ConfigureAwait(false);
            return "resetDone";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            RuntimeLog.Warn(LogTag, ex.GetType().Name);
            return "resetFailed";
        }
        finally
        {
            Volatile.Write(ref _redeeming, 0);
        }
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, (string Token, string Account) auth)
    {
        var request = new HttpRequestMessage(method, BaseUrl + path);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + auth.Token);
        request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", auth.Account);
        request.Headers.TryAddWithoutValidation("User-Agent", "codex_cli_rs");
        return request;
    }

    private void Failed(HttpResponseMessage response, string body, string token)
    {
        TimeSpan wait = response.Headers.RetryAfter?.Delta ?? FailureBackoff;
        _blockedUntilUtc = DateTime.UtcNow + (wait > TimeSpan.FromHours(1) ? TimeSpan.FromHours(1) : wait);
        RuntimeLog.Warn(LogTag, $"HTTP {(int)response.StatusCode}: {body.Replace(token, "[redacted]", StringComparison.Ordinal)}");
    }

    private (string Token, string Account)? ReadAuth()
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(_directory, "auth.json")));
            if (!Object(document.RootElement, "tokens", out var tokens)) return null;
            string? token = Text(tokens, "access_token"), account = Text(tokens, "account_id");
            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(account) || !TokenIsCurrent(token, DateTimeOffset.UtcNow)) return null;
            return (token, account);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    internal static bool TokenIsCurrent(string token, DateTimeOffset now)
    {
        try
        {
            string[] parts = token.Split('.');
            if (parts.Length != 3) return false;
            string payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            return document.RootElement.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out long seconds) && seconds > now.ToUnixTimeSeconds();
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    internal static CodexUsage? ParseUsage(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!Object(document.RootElement, "rate_limit", out var limits)) return null;
        var primary = Window(limits, "primary_window", "reset_at");
        var secondary = Window(limits, "secondary_window", "reset_at");
        return primary == null && secondary == null ? null : new CodexUsage(primary, secondary, Array.Empty<CodexResetCredit>());
    }

    internal static IReadOnlyList<CodexResetCredit> ParseCredits(string json)
    {
        using var document = JsonDocument.Parse(json);
        var credits = new List<CodexResetCredit>();
        if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("credits", out var list) || list.ValueKind != JsonValueKind.Array) return credits;
        foreach (var credit in list.EnumerateArray())
        {
            if (credit.ValueKind != JsonValueKind.Object || Text(credit, "status") != "available" ||
                !credit.TryGetProperty("is_supported_by_plan", out var supported) || supported.ValueKind != JsonValueKind.True ||
                Text(credit, "id") is not { Length: > 0 } id) continue;
            DateTimeOffset? expires = DateTimeOffset.TryParse(Text(credit, "expires_at"), out var parsed) ? parsed : null;
            credits.Add(new CodexResetCredit(id, expires));
        }
        return credits;
    }

    internal static CodexUsage? ParseRolloutLine(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            if (!Object(document.RootElement, "payload", out var payload) || !Object(payload, "rate_limits", out var limits)) return null;
            var primary = Window(limits, "primary", "resets_at");
            var secondary = Window(limits, "secondary", "resets_at");
            if (primary?.ResetsAt == null) primary = null;
            if (secondary?.ResetsAt == null) secondary = null;
            return primary == null && secondary == null ? null : new CodexUsage(primary, secondary, Array.Empty<CodexResetCredit>());
        }
        catch (JsonException) { return null; }
    }

    internal static CodexUsage? ReadRolloutFallback(string sessionsDirectory)
    {
        CodexUsage? latest = null;
        try
        {
            if (!Directory.Exists(sessionsDirectory)) return null;
            var files = new DirectoryInfo(sessionsDirectory).EnumerateFiles("rollout-*.jsonl", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true
            }).OrderByDescending(f => f.LastWriteTimeUtc).Take(10).Reverse().ToArray();
            foreach (var file in files)
            {
                try
                {
                    using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream);
                    while (reader.ReadLine() is { } line)
                        if (ParseRolloutLine(line) is { } usage) latest = usage;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return latest;
    }

    private static ClaudeLimitWindow? Window(JsonElement limits, string property, string resetProperty)
    {
        if (!Object(limits, property, out var window) || !window.TryGetProperty("used_percent", out var value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDouble(out double used) || !double.IsFinite(used)) return null;
        DateTimeOffset? resets = null;
        if (window.TryGetProperty(resetProperty, out var reset) && reset.ValueKind == JsonValueKind.Number && reset.TryGetInt64(out long seconds) &&
            seconds >= DateTimeOffset.MinValue.ToUnixTimeSeconds() && seconds <= DateTimeOffset.MaxValue.ToUnixTimeSeconds())
            resets = DateTimeOffset.FromUnixTimeSeconds(seconds);
        return new ClaudeLimitWindow(Math.Clamp(used, 0, 100), resets);
    }

    private static bool Object(JsonElement parent, string name, out JsonElement child)
    {
        child = default;
        return parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out child) && child.ValueKind == JsonValueKind.Object;
    }

    private static string? Text(JsonElement parent, string name) => parent.ValueKind == JsonValueKind.Object &&
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public void Dispose() => _http.Dispose();
}
