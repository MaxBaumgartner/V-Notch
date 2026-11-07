using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace VNotch.Services.ClaudeCode;

/// <summary>
/// Renews Claude Code's sign-in once it has run out, so the plan limits keep coming in while no Claude Code is
/// running to renew it (its access token lasts about eight hours). It follows Claude Code's own protocol: take its
/// refresh locks, read the file again under them, refresh against Anthropic's token endpoint, and write back only
/// if the file still holds the refresh token that was used, keeping every other field. The tokens never leave the
/// machine except to platform.claude.com.
/// </summary>
internal static class ClaudeTokenRefresher
{
    private const string LogTag = "CLAUDE-USAGE";
    private const string TokenUrl = "https://platform.claude.com/v1/oauth/token";
    // Claude Code's public OAuth client, used when the credentials do not name their own.
    private const string DefaultClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    // Same as Claude Code's lock: older than this, the holder is gone.
    private static readonly TimeSpan StaleLock = TimeSpan.FromSeconds(60);
    private const int LockAttempts = 5;

    /// <summary>The access token, or null when it has run out (or will within half a minute) or is missing.</summary>
    internal static string? UsableAccessToken(JsonObject oauth, DateTimeOffset now)
    {
        if (oauth["expiresAt"] is JsonValue expires && expires.TryGetValue(out long expiresMs) &&
            DateTimeOffset.FromUnixTimeMilliseconds(expiresMs) <= now.AddSeconds(30))
            return null;
        return oauth["accessToken"] is JsonValue token && token.TryGetValue(out string? value) && value.Length > 0 ? value : null;
    }

    /// <summary>The body Claude Code sends to renew: its scopes, its client.</summary>
    internal static JsonObject RefreshRequest(JsonObject oauth, string refreshToken)
    {
        string scopes = oauth["scopes"] is JsonArray list
            ? string.Join(' ', list.Select(s => s?.GetValue<string>()).Where(s => !string.IsNullOrEmpty(s)))
            : "";
        string clientId = oauth["clientId"] is JsonValue id && id.TryGetValue(out string? named) && named.Length > 0 ? named : DefaultClientId;
        var body = new JsonObject
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = clientId
        };
        if (scopes.Length > 0) body["scope"] = scopes;
        return body;
    }

    /// <summary>
    /// Puts the renewed tokens into the credentials, unless the file no longer holds <paramref name="usedRefreshToken"/>
    /// (someone renewed it meanwhile; theirs wins). Returns the new access token, or null when nothing was written.
    /// </summary>
    internal static string? ApplyReply(JsonObject credentials, JsonObject reply, string usedRefreshToken, long nowMs)
    {
        if (credentials["claudeAiOauth"] is not JsonObject oauth ||
            oauth["refreshToken"]?.GetValue<string>() != usedRefreshToken ||
            reply["access_token"] is not JsonValue accessNode || !accessNode.TryGetValue(out string? access) || access.Length == 0 ||
            reply["expires_in"] is not JsonValue expiresNode || !expiresNode.TryGetValue(out double expiresIn))
            return null;

        oauth["accessToken"] = access;
        oauth["refreshToken"] = reply["refresh_token"] is JsonValue r && r.TryGetValue(out string? renewed) && renewed.Length > 0 ? renewed : usedRefreshToken;
        oauth["expiresAt"] = nowMs + (long)(expiresIn * 1000);
        if (reply["refresh_token_expires_in"] is JsonValue rx && rx.TryGetValue(out double refreshExpiresIn))
            oauth["refreshTokenExpiresAt"] = nowMs + (long)(refreshExpiresIn * 1000);
        if (reply["scope"] is JsonValue sc && sc.TryGetValue(out string? scope) && scope.Length > 0)
            oauth["scopes"] = new JsonArray(scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
        return access;
    }

    /// <summary>Renews the sign-in in <paramref name="credentialsPath"/>; null when it could not (it then stays as it was).</summary>
    public static async Task<string?> RefreshAsync(HttpClient http, string credentialsPath, CancellationToken ct)
    {
        string claudeDir = Path.GetDirectoryName(credentialsPath)!;
        string lockPath = Path.Combine(claudeDir, ".oauth_refresh.lock");
        string legacyLockPath = claudeDir.TrimEnd('\\', '/') + ".lock";
        if (!await AcquireAsync(lockPath, ct).ConfigureAwait(false)) return null;
        bool legacyHeld = false;
        try
        {
            // Older Claude Code versions only know the second lock; both are held, in Claude Code's order.
            legacyHeld = TryLock(legacyLockPath);
            if (!legacyHeld) return null;

            if (ReadCredentials(credentialsPath) is not { } credentials || credentials["claudeAiOauth"] is not JsonObject oauth)
                return null;
            // Renewed by Claude Code while this waited for the lock.
            if (UsableAccessToken(oauth, DateTimeOffset.UtcNow) is { } fresh) return fresh;
            if (oauth["refreshToken"] is not JsonValue refreshNode || !refreshNode.TryGetValue(out string? refreshToken) || refreshToken.Length == 0)
                return null;
            if (oauth["refreshTokenExpiresAt"] is JsonValue rx && rx.TryGetValue(out long refreshExpiresMs) &&
                DateTimeOffset.FromUnixTimeMilliseconds(refreshExpiresMs) <= DateTimeOffset.UtcNow)
                return null; // Only a new sign-in in Claude Code helps now.

            using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
            {
                Content = new StringContent(RefreshRequest(oauth, refreshToken).ToJsonString(), Encoding.UTF8, "application/json")
            };
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                RuntimeLog.Warn(LogTag, $"Renewing the sign-in failed: HTTP {(int)response.StatusCode}");
                return null;
            }
            if (JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) is not JsonObject reply)
                return null;

            // Read once more right before writing, so nothing written meanwhile is lost.
            if (ReadCredentials(credentialsPath) is not { } latest) return null;
            string? access = ApplyReply(latest, reply, refreshToken, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            if (access == null) return null;
            WriteAtomically(credentialsPath, latest.ToJsonString());
            RuntimeLog.Info(LogTag, "Renewed Claude Code's sign-in for the plan limits");
            return access;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            RuntimeLog.Warn(LogTag, $"Renewing the sign-in failed: {ex.GetType().Name}");
            return null;
        }
        finally
        {
            if (legacyHeld) Unlock(legacyLockPath);
            Unlock(lockPath);
        }
    }

    private static JsonObject? ReadCredentials(string path) => JsonNode.Parse(File.ReadAllText(path)) as JsonObject;

    private static void WriteAtomically(string path, string json)
    {
        string temp = path + ".vnotch-" + Environment.ProcessId + ".tmp";
        File.WriteAllText(temp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        try
        {
            File.Replace(temp, path, destinationBackupFileName: null);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    // Claude Code's locks are directories (proper-lockfile): creating one is the atomic claim.
    private static async Task<bool> AcquireAsync(string path, CancellationToken ct)
    {
        for (int attempt = 0; attempt < LockAttempts; attempt++)
        {
            if (TryLock(path)) return true;
            await Task.Delay(1000 + Random.Shared.Next(1000), ct).ConfigureAwait(false);
        }
        return false;
    }

    private static bool TryLock(string path)
    {
        if (CreateDirectoryW(path, IntPtr.Zero)) return true;
        if (Marshal.GetLastWin32Error() != ErrorAlreadyExists) return false;
        try
        {
            if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(path) < StaleLock) return false;
            Directory.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        return CreateDirectoryW(path, IntPtr.Zero);
    }

    private static void Unlock(string path)
    {
        try { Directory.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private const int ErrorAlreadyExists = 183;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(string lpPathName, IntPtr lpSecurityAttributes);
}
