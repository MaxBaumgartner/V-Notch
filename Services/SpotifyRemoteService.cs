using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using static VNotch.Services.SpotifyHttpResponseReader;

namespace VNotch.Services;

public enum SpotifyJamStatus { Started, NotConnected, SessionExpired, Rejected, Failed }

public sealed record SpotifyJamResult(SpotifyJamStatus Status, string? Link = null, int HttpStatus = 0);

/// <summary>
/// Controls the user's Spotify account with the web-player token from the sp_dc login:
/// starts a Jam (or returns the one already running) and hands back its invite link, and
/// reads/sets the volume of the active Spotify player, i.e. the slider inside the Spotify app.
/// Neither works over the public Web API with this token (Jams have none, the player API
/// answers HTTP 429 for a day), so both use the internal endpoints Spotify's own clients use.
/// </summary>
public sealed class SpotifyRemoteService : IDisposable
{
    private const string LogCategory = "SPOTIFY-JAM";
    private const string VolumeLogCategory = "SPOTIFY-VOLUME";
    private const string ConnectHost = "https://gew1-spclient.spotify.com";
    private const string DealerUri = "wss://dealer.spotify.com/?access_token=";
    private const string ClusterPushUri = "hm://connect-state/v1/cluster";
    private const int MaxConnectVolume = 65535;
    private const int MaxDealerMessageBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan DealerPingInterval = TimeSpan.FromSeconds(25); // the dealer drops silent sockets
    private static readonly TimeSpan VolumeTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxRateLimitWait = TimeSpan.FromMinutes(2);
    private const string ObserverRegistration =
        "{\"member_type\":\"CONNECT_STATE\",\"device\":{\"device_info\":{\"capabilities\":" +
        "{\"can_be_player\":false,\"hidden\":true,\"needs_full_player_state\":true}}}}";
    private const string WebUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/127.0.0.0 Safari/537.36";
    private const string SessionPath = "/social-connect/v2/sessions/current_or_new?activate=true";
    private const string InviteBase = "https://open.spotify.com/socialsession/";
    private const string SocialSessionUriPrefix = "spotify:socialsession:";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
    // The generic host first; the regional one is what the desktop client talks to.
    private static readonly string[] SpclientHosts = ["https://spclient.wg.spotify.com", "https://guc3-spclient.spotify.com"];

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly SpotifyTokenProvider _tokens;
    private DateTime _volumeNotBeforeUtc = DateTime.MinValue; // after HTTP 429
    private int _lastVolumeStatus = -1;

    // Spotify Connect: a hidden observer device on the dealer socket gets the active player's
    // volume pushed on every change and may move it.
    private readonly SemaphoreSlim _dealerLock = new(1, 1);
    private readonly string _deviceId = Convert.ToHexString(RandomNumberGenerator.GetBytes(20)).ToLowerInvariant();
    private ClientWebSocket? _dealer;
    private DateTime _lastPingUtc;
    private volatile ConnectVolume? _volume; // null without an active player

    internal sealed record ConnectVolume(string DeviceId, int Raw);

    public SpotifyRemoteService() : this(CreateHttpClient(), ownsHttpClient: true) { }

    internal SpotifyRemoteService(HttpClient http) : this(http, ownsHttpClient: false) { }

    private SpotifyRemoteService(HttpClient http, bool ownsHttpClient)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _ownsHttpClient = ownsHttpClient;
        _tokens = new SpotifyTokenProvider(http);
    }

    public async Task<SpotifyJamResult> StartJamAsync(string? spotifySpDc, CancellationToken cancellationToken = default)
    {
        string? cookie = SpotifyCanvasService.NormalizeSessionCookie(spotifySpDc);
        if (cookie == null) return new(SpotifyJamStatus.NotConnected);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            string? accessToken = await _tokens.GetAccessTokenAsync(cookie, timeout.Token).ConfigureAwait(false);
            if (string.IsNullOrEmpty(accessToken))
            {
                RuntimeLog.Warn(LogCategory, "No web-player token for the saved Spotify login.");
                return new(SpotifyJamStatus.SessionExpired);
            }

            int lastStatus = 0;
            foreach (string host in SpclientHosts)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, host + SessionPath);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                request.Headers.Accept.ParseAdd("application/json");
                request.Headers.UserAgent.ParseAdd(
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                    "(KHTML, like Gecko) Chrome/127.0.0.0 Safari/537.36");
                request.Headers.TryAddWithoutValidation("app-platform", "WebPlayer");
                request.Headers.TryAddWithoutValidation("Origin", "https://open.spotify.com");
                request.Headers.Referrer = new Uri("https://open.spotify.com/");

                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                    .ConfigureAwait(false);
                lastStatus = (int)response.StatusCode;
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    _tokens.ClearCache();
                    return new(SpotifyJamStatus.SessionExpired, HttpStatus: lastStatus);
                }
                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    // Free accounts can join Jams but not host them.
                    return new(SpotifyJamStatus.Rejected, HttpStatus: lastStatus);
                }
                if (!response.IsSuccessStatusCode)
                {
                    RuntimeLog.Warn(LogCategory, $"Jam start returned HTTP {lastStatus} from {new Uri(host).Host}.");
                    continue;
                }

                byte[]? body = await ReadLimitedBytesAsync(response, timeout.Token).ConfigureAwait(false);
                string? link = body == null ? null : ParseInviteLink(Encoding.UTF8.GetString(body));
                if (link != null) return new(SpotifyJamStatus.Started, link, lastStatus);
                RuntimeLog.Warn(LogCategory, "Jam start answered without an invite token.");
                return new(SpotifyJamStatus.Failed, HttpStatus: lastStatus);
            }
            return new(SpotifyJamStatus.Failed, HttpStatus: lastStatus);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            RuntimeLog.Warn(LogCategory, "Jam start timed out.");
            return new(SpotifyJamStatus.Failed);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException)
        {
            RuntimeLog.Warn(LogCategory, $"Jam start failed: {ex.GetType().Name}: {ex.Message}");
            return new(SpotifyJamStatus.Failed);
        }
    }

    /// <summary>Volume (0–100) of the active Spotify player; null without a player, login or answer.</summary>
    public async Task<int?> GetVolumePercentAsync(string? spotifySpDc, CancellationToken cancellationToken = default)
    {
        if (!await EnsureDealerAsync(spotifySpDc, cancellationToken).ConfigureAwait(false)) return null;
        return _volume is { } volume ? ToPercent(volume.Raw) : null;
    }

    /// <summary>Moves the volume slider of the active Spotify player (Premium only).</summary>
    public async Task<bool> SetVolumePercentAsync(string? spotifySpDc, int percent, CancellationToken cancellationToken = default)
    {
        if (!await EnsureDealerAsync(spotifySpDc, cancellationToken).ConfigureAwait(false) || _volume is not { } volume)
            return false;
        int raw = ToRaw(percent);
        string uri = $"{ConnectHost}/connect-state/v1/connect/volume/from/{_deviceId}/to/{Uri.EscapeDataString(volume.DeviceId)}";
        if (await SendConnectAsync(uri, $"{{\"volume\":{raw}}}", connectionId: null, SpotifyCanvasService.NormalizeSessionCookie(spotifySpDc)!, cancellationToken)
                .ConfigureAwait(false) == null)
            return false;
        _volume = volume with { Raw = raw };
        return true;
    }

    // True once the observer is registered on an open dealer socket (and _volume is filled).
    private async Task<bool> EnsureDealerAsync(string? spotifySpDc, CancellationToken cancellationToken)
    {
        string? cookie = SpotifyCanvasService.NormalizeSessionCookie(spotifySpDc);
        if (cookie == null || DateTime.UtcNow < _volumeNotBeforeUtc) return false;

        await _dealerLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(VolumeTimeout);
        try
        {
            if (_dealer is { State: WebSocketState.Open } open)
            {
                if (DateTime.UtcNow - _lastPingUtc > DealerPingInterval)
                {
                    await open.SendAsync("{\"type\":\"ping\"}"u8.ToArray(), WebSocketMessageType.Text, true, timeout.Token)
                        .ConfigureAwait(false);
                    _lastPingUtc = DateTime.UtcNow;
                }
                return true;
            }

            DropDealer();
            CancellationToken permission = NetworkPrivacy.Current.Acquire(NetworkFeature.SpotifyRemote);
            string? accessToken = await _tokens.GetAccessTokenAsync(cookie, timeout.Token).ConfigureAwait(false);
            if (string.IsNullOrEmpty(accessToken)) return false;

            var socket = new ClientWebSocket();
            _dealer = socket;
            permission.Register(socket.Abort);
            await socket.ConnectAsync(new Uri(DealerUri + Uri.EscapeDataString(accessToken)), timeout.Token).ConfigureAwait(false);
            string? connectionId = ReadConnectionId(await ReceiveMessageAsync(socket, timeout.Token).ConfigureAwait(false));
            if (connectionId == null)
            {
                RuntimeLog.Warn(VolumeLogCategory, "Dealer sent no connection id.");
                DropDealer();
                return false;
            }
            _lastPingUtc = DateTime.UtcNow;

            string? cluster = await SendConnectAsync($"{ConnectHost}/connect-state/v1/devices/hobs_{_deviceId}",
                ObserverRegistration, connectionId, cookie, timeout.Token).ConfigureAwait(false);
            if (cluster == null)
            {
                DropDealer();
                return false;
            }
            _volume = ParseClusterVolume(cluster);
            _ = ReceivePushesAsync(socket);
            return true;
        }
        catch (Exception ex) when (ex is WebSocketException or HttpRequestException or IOException or OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested) throw;
            RuntimeLog.Debug(VolumeLogCategory, () => $"Dealer connect failed: {ex.GetType().Name}: {ex.Message}");
            DropDealer();
            return false;
        }
        finally
        {
            _dealerLock.Release();
        }
    }

    // Keeps _volume in step with the cluster pushes until the socket closes.
    private async Task ReceivePushesAsync(ClientWebSocket socket)
    {
        try
        {
            while (await ReceiveMessageAsync(socket, CancellationToken.None).ConfigureAwait(false) is { } message)
                if (TryReadClusterPush(message, out ConnectVolume? volume)) _volume = volume;
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException or ObjectDisposedException)
        {
            // Closed or aborted: the next volume call reconnects.
        }
    }

    // One whole text message, or null when the socket closes.
    private static async Task<string?> ReceiveMessageAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            message.Write(buffer, 0, result.Count);
            if (message.Length > MaxDealerMessageBytes) throw new IOException("Dealer message too large.");
            if (result.EndOfMessage) return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
        }
    }

    // A PUT to spclient: the body on success, null on any failure.
    private async Task<string?> SendConnectAsync(string uri, string json, string? connectionId, string cookie,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(VolumeTimeout);
        try
        {
            string? accessToken = await _tokens.GetAccessTokenAsync(cookie, timeout.Token).ConfigureAwait(false);
            if (string.IsNullOrEmpty(accessToken)) return null;

            using var request = new HttpRequestMessage(HttpMethod.Put, uri)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.UserAgent.ParseAdd(WebUserAgent);
            request.Headers.TryAddWithoutValidation("app-platform", "WebPlayer");
            if (connectionId != null) request.Headers.TryAddWithoutValidation("X-Spotify-Connection-Id", connectionId);

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            LogVolumeStatus(connectionId == null ? "set volume" : "observe", (int)response.StatusCode);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                _tokens.ClearCache();
                return null;
            }
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                TimeSpan wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5);
                RuntimeLog.Log(VolumeLogCategory, $"Rate limited, Retry-After {wait.TotalSeconds:0} s");
                _volumeNotBeforeUtc = DateTime.UtcNow + (wait > MaxRateLimitWait ? MaxRateLimitWait : wait);
                return null;
            }
            if (!response.IsSuccessStatusCode) return null;

            byte[]? body = await ReadLimitedBytesAsync(response, timeout.Token).ConfigureAwait(false);
            return body == null ? "" : Encoding.UTF8.GetString(body);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            RuntimeLog.Debug(VolumeLogCategory, () => $"Connect request failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private void DropDealer()
    {
        var socket = _dealer;
        _dealer = null;
        _volume = null;
        if (socket == null) return;
        socket.Abort();
        socket.Dispose();
    }

    private void LogVolumeStatus(string action, int status)
    {
        if (status == _lastVolumeStatus) return;
        _lastVolumeStatus = status;
        RuntimeLog.Log(VolumeLogCategory, $"{action}: HTTP {status}");
    }

    internal static int ToPercent(int raw) => (int)Math.Round(Math.Clamp(raw, 0, MaxConnectVolume) * 100.0 / MaxConnectVolume);

    internal static int ToRaw(int percent) => (int)Math.Round(Math.Clamp(percent, 0, 100) * MaxConnectVolume / 100.0);

    /// <summary>The connection id from the dealer's first message.</summary>
    internal static string? ReadConnectionId(string? message)
    {
        if (message == null) return null;
        try
        {
            using var document = JsonDocument.Parse(message);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("headers", out var headers) && headers.ValueKind == JsonValueKind.Object
                ? GetString(headers, "Spotify-Connection-Id")
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Active device and its volume from a connect-state cluster; null when nothing plays.</summary>
    internal static ConnectVolume? ParseClusterVolume(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return ParseClusterVolume(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ConnectVolume? ParseClusterVolume(JsonElement cluster)
    {
        if (cluster.ValueKind != JsonValueKind.Object) return null;
        string? active = GetString(cluster, "active_device_id");
        return !string.IsNullOrEmpty(active) &&
            cluster.TryGetProperty("devices", out var devices) && devices.ValueKind == JsonValueKind.Object &&
            devices.TryGetProperty(active, out var device) && device.ValueKind == JsonValueKind.Object &&
            device.TryGetProperty("volume", out var volume) && volume.ValueKind == JsonValueKind.Number &&
            volume.TryGetInt32(out int raw)
            ? new ConnectVolume(active, Math.Clamp(raw, 0, MaxConnectVolume))
            : null;
    }

    /// <summary>True for a cluster push; volume is then the new state (null: nothing plays).</summary>
    internal static bool TryReadClusterPush(string message, out ConnectVolume? volume)
    {
        volume = null;
        try
        {
            using var document = JsonDocument.Parse(message);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || GetString(root, "uri") != ClusterPushUri ||
                !root.TryGetProperty("payloads", out var payloads) || payloads.ValueKind != JsonValueKind.Array ||
                payloads.GetArrayLength() == 0)
                return false;

            var payload = payloads[0];
            if (payload.ValueKind == JsonValueKind.String)
            {
                // Big pushes come base64 encoded, gzipped when the header says so.
                byte[] bytes = Convert.FromBase64String(payload.GetString()!);
                if (root.TryGetProperty("headers", out var headers) && headers.ValueKind == JsonValueKind.Object &&
                    GetString(headers, "Transfer-Encoding") == "gzip")
                {
                    using var unzip = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
                    using var plain = new MemoryStream();
                    unzip.CopyTo(plain);
                    bytes = plain.ToArray();
                }
                using var inner = JsonDocument.Parse(bytes);
                volume = inner.RootElement.ValueKind == JsonValueKind.Object && inner.RootElement.TryGetProperty("cluster", out var packed) ? ParseClusterVolume(packed) : null;
                return true;
            }
            volume = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("cluster", out var cluster)
                ? ParseClusterVolume(cluster)
                : null;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>Builds the https invite link from a social-connect session response.</summary>
    internal static string? ParseInviteLink(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            string? url = GetString(root, "join_session_url");
            if (url != null && Uri.TryCreate(url, UriKind.Absolute, out var parsed) &&
                parsed.Scheme == Uri.UriSchemeHttps &&
                (parsed.Host.Equals("open.spotify.com", StringComparison.OrdinalIgnoreCase) ||
                 parsed.Host.Equals("spotify.link", StringComparison.OrdinalIgnoreCase)))
            {
                return parsed.AbsoluteUri;
            }

            string? token = GetString(root, "join_session_token");
            if (string.IsNullOrWhiteSpace(token))
            {
                string? uri = GetString(root, "join_session_uri");
                if (uri != null && uri.StartsWith(SocialSessionUriPrefix, StringComparison.Ordinal))
                    token = uri[SocialSessionUriPrefix.Length..];
            }
            return token != null && IsSafeToken(token) ? InviteBase + token : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool IsSafeToken(string token) =>
        token.Length is > 0 and <= 128 && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static HttpClient CreateHttpClient()
    {
        var handler = new NativeWinHttpHandler { AllowAutoRedirect = false };
        var client = new HttpClient(NetworkPrivacy.Handler(NetworkFeature.SpotifyRemote, handler)) { Timeout = RequestTimeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(AppVersion.UserAgent);
        return client;
    }

    public void Dispose()
    {
        DropDealer();
        _tokens.Dispose();
        if (_ownsHttpClient) _http.Dispose();
    }
}
