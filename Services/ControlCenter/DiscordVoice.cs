using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace VNotch.Services.ControlCenter;

/// <summary>
/// Discord's own mute and deafen, read over its local RPC pipe the way overlays and Stream Deck plugins do.
/// Needs an application of the user's own from the Discord Developer Portal with "Public Client" on, so no
/// secret is involved: only its id (<c>DiscordClientId</c>). The first connect asks once inside Discord; the
/// token is kept DPAPI-protected for this Windows user. Only the token exchange goes online (discord.com).
/// </summary>
internal sealed class DiscordVoice : IDisposable
{
    private const string LogTag = "DISCORD-VOICE";
    private const string RedirectUri = "http://127.0.0.1/callback";
    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan AfterRefusal = TimeSpan.FromMinutes(30);

    private const int OpHandshake = 0, OpFrame = 1, OpClose = 2, OpPing = 3, OpPong = 4;

    private readonly Func<string> _clientId;
    private readonly Func<bool> _offline;
    private readonly string _tokenFile;
    private readonly HttpClient _http = new(NetworkPrivacy.CreateHttpHandler(allowAutoRedirect: true)) { Timeout = TimeSpan.FromSeconds(15) };
    private readonly CancellationTokenSource _stop = new();
    private bool _started;

    /// <summary>Muted or deafened while in a voice channel; raised on a background thread.</summary>
    public event Action<bool>? MutedChanged;
    public bool IsMuted { get; private set; }
    private bool _selfMuted, _inVoice;

    public DiscordVoice(Func<string> clientId, Func<bool> offline, string tokenFile)
    {
        _clientId = clientId;
        _offline = offline;
        _tokenFile = tokenFile;
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        _ = Task.Run(() => RunAsync(_stop.Token));
    }

    public void Dispose()
    {
        _stop.Cancel();
        _http.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TimeSpan wait = Retry;
            string id = (_clientId() ?? "").Trim();
            if (id.Length > 0)
            {
                try
                {
                    wait = await SessionAsync(id, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or JsonException or TimeoutException or InvalidOperationException or HttpRequestException)
                {
                    RuntimeLog.Debug(LogTag, $"Session ended: {ex.Message}");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                _selfMuted = _inVoice = false;
                Publish();
            }
            try { await Task.Delay(wait, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>One connection to the running Discord, until it closes; returns how long to wait before the next.</summary>
    private async Task<TimeSpan> SessionAsync(string clientId, CancellationToken ct)
    {
        using var pipe = await ConnectAsync(ct).ConfigureAwait(false);
        if (pipe == null) return Retry;   // Discord is not running

        await WriteAsync(pipe, OpHandshake, new JsonObject { ["v"] = 1, ["client_id"] = clientId }, ct).ConfigureAwait(false);
        var ready = await ReadAsync(pipe, ct).ConfigureAwait(false);
        if (ready.Op == OpClose) throw new InvalidOperationException($"handshake refused: {ready.Body}");

        string? token = await AccessTokenAsync(pipe, clientId, ct).ConfigureAwait(false);
        if (token == null) return AfterRefusal;

        var auth = await CommandAsync(pipe, "AUTHENTICATE", new JsonObject { ["access_token"] = token }, null, ct).ConfigureAwait(false);
        if (IsError(auth))
        {
            // Revoked or expired beyond refresh: forget it, ask again next time.
            RuntimeLog.Warn(LogTag, $"AUTHENTICATE failed: {ErrorText(auth)}");
            DeleteToken();
            return Retry;
        }

        Apply((await CommandAsync(pipe, "GET_VOICE_SETTINGS", new JsonObject(), null, ct).ConfigureAwait(false))?["data"]);
        ApplyChannel((await CommandAsync(pipe, "GET_SELECTED_VOICE_CHANNEL", new JsonObject(), null, ct).ConfigureAwait(false))?["data"]);
        await CommandAsync(pipe, "SUBSCRIBE", new JsonObject(), "VOICE_SETTINGS_UPDATE", ct).ConfigureAwait(false);
        await CommandAsync(pipe, "SUBSCRIBE", new JsonObject(), "VOICE_CHANNEL_SELECT", ct).ConfigureAwait(false);
        RuntimeLog.Log(LogTag, "Connected; following mute and deafen");

        while (!ct.IsCancellationRequested)
        {
            var frame = await ReadAsync(pipe, ct).ConfigureAwait(false);
            switch (frame.Op)
            {
                case OpClose:
                    return Retry;
                case OpPing:
                    await WriteAsync(pipe, OpPong, frame.Body, ct).ConfigureAwait(false);
                    break;
                case OpFrame when Str(frame.Body, "evt") == "VOICE_SETTINGS_UPDATE":
                    Apply(frame.Body?["data"]);
                    break;
                case OpFrame when Str(frame.Body, "evt") == "VOICE_CHANNEL_SELECT":
                    _inVoice = Str(frame.Body?["data"], "channel_id").Length > 0;
                    Publish();
                    break;
            }
        }
        return Retry;
    }

    internal static bool MutedFrom(JsonNode? data) =>
        data is JsonObject o && (Bool(o, "mute") || Bool(o, "deaf"));

    private void Apply(JsonNode? data)
    {
        if (data is not JsonObject) return;
        _selfMuted = MutedFrom(data);
        Publish();
    }

    /// <summary>GET_SELECTED_VOICE_CHANNEL answers with the channel, or null outside voice.</summary>
    private void ApplyChannel(JsonNode? data)
    {
        _inVoice = data is JsonObject channel && Str(channel, "id").Length > 0;
        Publish();
    }

    /// <summary>Muted outside a call matters to nobody: only muted in a voice channel counts.</summary>
    private void Publish()
    {
        bool muted = _selfMuted && _inVoice;
        if (muted == IsMuted) return;
        IsMuted = muted;
        MutedChanged?.Invoke(muted);
    }

    #region Authorization

    private sealed record StoredToken(string ClientId, string AccessToken, string RefreshToken, long ExpiresAt);

    /// <summary>The saved token, refreshed when it runs out; otherwise Discord asks the user once (AUTHORIZE).</summary>
    private async Task<string?> AccessTokenAsync(Stream pipe, string clientId, CancellationToken ct)
    {
        var saved = LoadToken();
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (saved != null && saved.ClientId == clientId)
        {
            if (saved.ExpiresAt - now > 3600) return saved.AccessToken;
            if (!_offline() && await ExchangeAsync(clientId, new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = saved.RefreshToken
                }, ct).ConfigureAwait(false) is { } refreshed)
                return refreshed.AccessToken;
        }
        if (_offline()) return null;

        // PKCE: a public client proves with the verifier that it asked for this code.
        string verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var reply = await CommandAsync(pipe, "AUTHORIZE", new JsonObject
        {
            ["client_id"] = clientId,
            ["scopes"] = new JsonArray("rpc", "rpc.voice.read"),
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256"
        }, null, ct, waitForUser: true).ConfigureAwait(false);
        string code = Str(reply?["data"], "code");
        if (IsError(reply) || code.Length == 0)
        {
            RuntimeLog.Warn(LogTag, $"AUTHORIZE not granted: {ErrorText(reply)}");
            return null;
        }

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["redirect_uri"] = RedirectUri
        };
        var token = await ExchangeAsync(clientId, form, ct).ConfigureAwait(false);
        if (token == null)
        {
            form.Remove("redirect_uri");
            token = await ExchangeAsync(clientId, form, ct).ConfigureAwait(false);
        }
        return token?.AccessToken;
    }

    private async Task<StoredToken?> ExchangeAsync(string clientId, Dictionary<string, string> form, CancellationToken ct)
    {
        form["client_id"] = clientId;
        using var response = await _http.PostAsync("https://discord.com/api/oauth2/token", new FormUrlEncodedContent(form), ct).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // The error names the problem (invalid_grant, invalid_client…), never a secret.
            RuntimeLog.Warn(LogTag, $"Token {form["grant_type"]} failed: {(int)response.StatusCode} {Str(JsonNode.Parse(body), "error")}");
            return null;
        }
        var json = JsonNode.Parse(body);
        string access = Str(json, "access_token");
        if (access.Length == 0) return null;
        long expires = json?["expires_in"] is JsonValue v && v.TryGetValue(out long s) ? s : 3600;
        var token = new StoredToken(clientId, access, Str(json, "refresh_token"), DateTimeOffset.UtcNow.ToUnixTimeSeconds() + expires);
        SaveToken(token);
        return token;
    }

    private StoredToken? LoadToken()
    {
        try
        {
            if (!File.Exists(_tokenFile)) return null;
            byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(_tokenFile), null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<StoredToken>(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or JsonException)
        {
            return null;
        }
    }

    private void SaveToken(StoredToken token)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_tokenFile)!);
            File.WriteAllBytes(_tokenFile, ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(token), null, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            RuntimeLog.Warn(LogTag, $"Token not saved: {ex.Message}");
        }
    }

    private void DeleteToken()
    {
        try { File.Delete(_tokenFile); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    #endregion

    #region Pipe

    private static async Task<NamedPipeClientStream?> ConnectAsync(CancellationToken ct)
    {
        for (int i = 0; i < 10; i++)
        {
            var pipe = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(400, ct).ConfigureAwait(false);
                return pipe;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
            }
        }
        return null;
    }

    private async Task<JsonNode?> CommandAsync(Stream pipe, string cmd, JsonObject args, string? evt, CancellationToken ct, bool waitForUser = false)
    {
        string nonce = Guid.NewGuid().ToString("N");
        var message = new JsonObject { ["cmd"] = cmd, ["args"] = args, ["nonce"] = nonce };
        if (evt != null) message["evt"] = evt;
        await WriteAsync(pipe, OpFrame, message, ct).ConfigureAwait(false);

        // The user may take a while to click Authorize in Discord.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(waitForUser ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(10));
        while (true)
        {
            var frame = await ReadAsync(pipe, timeout.Token).ConfigureAwait(false);
            if (frame.Op == OpClose) throw new IOException($"Discord closed the pipe: {Str(frame.Body, "message")}");
            if (frame.Op == OpPing) { await WriteAsync(pipe, OpPong, frame.Body, ct).ConfigureAwait(false); continue; }
            if (Str(frame.Body, "nonce") == nonce) return frame.Body;
            if (Str(frame.Body, "evt") == "VOICE_SETTINGS_UPDATE") Apply(frame.Body?["data"]);
            if (Str(frame.Body, "evt") == "VOICE_CHANNEL_SELECT") { _inVoice = Str(frame.Body?["data"], "channel_id").Length > 0; Publish(); }
        }
    }

    internal static byte[] Frame(int op, JsonNode? body)
    {
        byte[] json = Encoding.UTF8.GetBytes(body?.ToJsonString() ?? "{}");
        byte[] frame = new byte[8 + json.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, op);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), json.Length);
        json.CopyTo(frame, 8);
        return frame;
    }

    private static async Task WriteAsync(Stream pipe, int op, JsonNode? body, CancellationToken ct)
    {
        await pipe.WriteAsync(Frame(op, body), ct).ConfigureAwait(false);
        await pipe.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task<(int Op, JsonNode? Body)> ReadAsync(Stream pipe, CancellationToken ct)
    {
        byte[] header = new byte[8];
        await pipe.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        int op = BinaryPrimitives.ReadInt32LittleEndian(header);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        if (length < 0 || length > 1 << 20) throw new IOException($"odd frame length {length}");
        byte[] body = new byte[length];
        await pipe.ReadExactlyAsync(body, ct).ConfigureAwait(false);
        return (op, length == 0 ? null : JsonNode.Parse(body));
    }

    #endregion

    private static bool IsError(JsonNode? reply) => Str(reply, "evt") == "ERROR" || reply == null;

    private static string ErrorText(JsonNode? reply) =>
        reply?["data"] is JsonObject d ? $"{d["code"]} {Str(d, "message")}" : "no reply";

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Str(JsonNode? node, string key) =>
        node is JsonObject o && o[key] is JsonValue v && v.TryGetValue(out string? s) ? s : "";

    private static bool Bool(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue(out bool b) && b;
}
