using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using VNotch.Models;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class SpotifyRemoteServiceTests
{
    [Theory]
    [InlineData("{\"join_session_token\":\"AbC123_-x\"}", "https://open.spotify.com/socialsession/AbC123_-x")]
    [InlineData("{\"join_session_uri\":\"spotify:socialsession:2nntah4YWidehwLM4kxDyQ\"}", "https://open.spotify.com/socialsession/2nntah4YWidehwLM4kxDyQ")]
    [InlineData("{\"join_session_url\":\"https://open.spotify.com/socialsession/Zz9\",\"join_session_token\":\"other\"}", "https://open.spotify.com/socialsession/Zz9")]
    [InlineData("{\"join_session_url\":\"https://evil.example/x\",\"join_session_token\":\"Tok1\"}", "https://open.spotify.com/socialsession/Tok1")]
    [InlineData("{\"join_session_token\":\"../../etc\"}", null)]
    [InlineData("{\"session_id\":\"abc\"}", null)]
    [InlineData("[]", null)]
    [InlineData("not json", null)]
    public void InviteLinksComeOnlyFromSpotifyHostsOrPlainTokens(string json, string? expected)
        => Assert.Equal(expected, SpotifyRemoteService.ParseInviteLink(json));

    [Fact]
    public async Task StartingAJamUsesTheWebPlayerTokenAndReturnsTheInviteLink()
    {
        var requests = new List<HttpRequestMessage>();
        using var service = new SpotifyRemoteService(new HttpClient(new StubHandler(request =>
        {
            requests.Add(request);
            return Route(request, jam: () => Json("{\"session_id\":\"s1\",\"join_session_token\":\"JamTok42\"}"));
        })));

        var result = await service.StartJamAsync("session-cookie");

        Assert.Equal(SpotifyJamStatus.Started, result.Status);
        Assert.Equal("https://open.spotify.com/socialsession/JamTok42", result.Link);
        var jam = Assert.Single(requests, r => r.RequestUri!.AbsolutePath.StartsWith("/social-connect/", StringComparison.Ordinal));
        Assert.Equal("Bearer spotify-token", jam.Headers.Authorization?.ToString());
        Assert.Equal("activate=true", jam.RequestUri!.Query.TrimStart('?'));
        Assert.Equal("spclient.wg.spotify.com", jam.RequestUri.Host);
    }

    [Fact]
    public async Task AFailingSpclientHostFallsBackToTheRegionalOne()
    {
        var hosts = new List<string>();
        using var service = new SpotifyRemoteService(new HttpClient(new StubHandler(request => Route(request, jam: () =>
        {
            hosts.Add(request.RequestUri!.Host);
            return request.RequestUri.Host == "spclient.wg.spotify.com"
                ? new HttpResponseMessage(HttpStatusCode.BadGateway)
                : Json("{\"join_session_token\":\"Second1\"}");
        }))));

        var result = await service.StartJamAsync("session-cookie");

        Assert.Equal(SpotifyJamStatus.Started, result.Status);
        Assert.Equal(new[] { "spclient.wg.spotify.com", "guc3-spclient.spotify.com" }, hosts);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, SpotifyJamStatus.SessionExpired)]
    [InlineData(HttpStatusCode.Forbidden, SpotifyJamStatus.Rejected)]
    [InlineData(HttpStatusCode.NotFound, SpotifyJamStatus.Failed)]
    public async Task ErrorsMapToAStatusTheNotchCanExplain(HttpStatusCode code, SpotifyJamStatus expected)
    {
        using var service = new SpotifyRemoteService(new HttpClient(new StubHandler(request =>
            Route(request, jam: () => new HttpResponseMessage(code)))));

        var result = await service.StartJamAsync("session-cookie");

        Assert.Equal(expected, result.Status);
        Assert.Null(result.Link);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task WithoutALoginNothingIsSent(string? cookie)
    {
        int calls = 0;
        using var service = new SpotifyRemoteService(new HttpClient(new StubHandler(_ => { calls++; return new HttpResponseMessage(HttpStatusCode.OK); })));
        var result = await service.StartJamAsync(cookie);
        Assert.Equal(SpotifyJamStatus.NotConnected, result.Status);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void OnlyLocalOnlyModeBlocksJams()
    {
        Assert.True(NetworkPrivacy.Allows(new NotchSettings { AllowOnlineCanvas = false, EnableSpotifyCanvas = false }, NetworkFeature.SpotifyRemote));
        Assert.False(NetworkPrivacy.Allows(new NotchSettings { EnableLocalOnlyMode = true }, NetworkFeature.SpotifyRemote));
    }

    [Theory]
    [InlineData("{\"active_device_id\":\"pc\",\"devices\":{\"pc\":{\"volume\":32768},\"phone\":{\"volume\":0}}}", "pc", 32768)]
    [InlineData("{\"active_device_id\":\"pc\",\"devices\":{\"pc\":{\"volume\":99999}}}", "pc", 65535)]
    [InlineData("{\"active_device_id\":\"pc\",\"devices\":{\"phone\":{\"volume\":10}}}", null, 0)]
    [InlineData("{\"devices\":{\"pc\":{\"volume\":10}}}", null, 0)]
    [InlineData("nope", null, 0)]
    public void VolumeComesFromTheActiveDevice(string json, string? device, int raw)
    {
        var volume = SpotifyRemoteService.ParseClusterVolume(json);
        Assert.Equal(device, volume?.DeviceId);
        if (device != null) Assert.Equal(raw, volume!.Raw);
    }

    [Fact]
    public void ClusterPushesUpdateTheVolume()
    {
        const string cluster = "{\"cluster\":{\"active_device_id\":\"pc\",\"devices\":{\"pc\":{\"volume\":13107}}},\"update_reason\":\"DEVICE_VOLUME_CHANGED\"}";
        Assert.True(SpotifyRemoteService.TryReadClusterPush(
            "{\"uri\":\"hm://connect-state/v1/cluster\",\"headers\":{\"content-type\":\"application/json\"},\"payloads\":[" + cluster + "]}",
            out var plain));
        Assert.Equal(new SpotifyRemoteService.ConnectVolume("pc", 13107), plain);

        using var zipped = new MemoryStream();
        using (var gzip = new GZipStream(zipped, CompressionMode.Compress, leaveOpen: true))
            gzip.Write(Encoding.UTF8.GetBytes(cluster));
        Assert.True(SpotifyRemoteService.TryReadClusterPush(
            "{\"uri\":\"hm://connect-state/v1/cluster\",\"headers\":{\"Transfer-Encoding\":\"gzip\"},\"payloads\":[\"" +
            Convert.ToBase64String(zipped.ToArray()) + "\"]}",
            out var packed));
        Assert.Equal(plain, packed);

        // Nothing active any more clears the volume; other pushes leave it alone.
        Assert.True(SpotifyRemoteService.TryReadClusterPush(
            "{\"uri\":\"hm://connect-state/v1/cluster\",\"payloads\":[{\"cluster\":{\"devices\":{}}}]}", out var idle));
        Assert.Null(idle);
        Assert.False(SpotifyRemoteService.TryReadClusterPush("{\"uri\":\"hm://pusher/v1/connections/x\",\"payloads\":[]}", out _));
        Assert.False(SpotifyRemoteService.TryReadClusterPush("{\"type\":\"pong\"}", out _));
    }

    [Fact]
    public void TheDealerConnectionIdComesFromTheFirstMessage()
    {
        Assert.Equal("abc", SpotifyRemoteService.ReadConnectionId("{\"headers\":{\"Spotify-Connection-Id\":\"abc\"},\"method\":\"PUT\"}"));
        Assert.Null(SpotifyRemoteService.ReadConnectionId("{\"type\":\"pong\"}"));
        Assert.Null(SpotifyRemoteService.ReadConnectionId(null));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(54, 35389)]
    [InlineData(100, 65535)]
    [InlineData(250, 65535)]
    [InlineData(-5, 0)]
    public void PercentMapsOntoSpotifysVolumeScale(int percent, int raw)
    {
        Assert.Equal(raw, SpotifyRemoteService.ToRaw(percent));
        Assert.Equal(Math.Clamp(percent, 0, 100), SpotifyRemoteService.ToPercent(raw));
    }

    private static HttpResponseMessage Route(HttpRequestMessage request, Func<HttpResponseMessage> jam)
    {
        string host = request.RequestUri!.Host;
        string path = request.RequestUri.AbsolutePath;
        if (host == "open.spotify.com" && path.EndsWith("/server-time", StringComparison.Ordinal))
            return Json("{\"serverTime\":1700000000}");
        if (host == "open.spotify.com" && path.EndsWith("/token", StringComparison.Ordinal))
            return Json("{\"accessToken\":\"spotify-token\",\"accessTokenExpirationTimestampMs\":4102444800000}");
        if (path == "/social-connect/v2/sessions/current_or_new")
            return jam();
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
