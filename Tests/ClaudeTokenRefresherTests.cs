using System.Text.Json.Nodes;
using VNotch.Services.ClaudeCode;
using Xunit;

namespace VNotch.Tests;

public sealed class ClaudeTokenRefresherTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000);

    private static JsonObject Credentials(long expiresAtMs) => (JsonObject)JsonNode.Parse($$"""
        {
          "claudeAiOauth": {
            "accessToken": "old-access",
            "refreshToken": "old-refresh",
            "expiresAt": {{expiresAtMs}},
            "scopes": ["user:inference", "user:profile"],
            "subscriptionType": "pro",
            "rateLimitTier": "default"
          },
          "mcpOAuth": { "server": { "accessToken": "keep-me" } }
        }
        """)!;

    [Fact]
    public void ATokenAboutToRunOutCountsAsGone()
    {
        var oauth = (JsonObject)Credentials(Now.AddMinutes(10).ToUnixTimeMilliseconds())["claudeAiOauth"]!;
        Assert.Equal("old-access", ClaudeTokenRefresher.UsableAccessToken(oauth, Now));
        Assert.Null(ClaudeTokenRefresher.UsableAccessToken(oauth, Now.AddMinutes(9).AddSeconds(45)));
    }

    [Fact]
    public void TheRenewalAsksLikeClaudeCode()
    {
        var oauth = (JsonObject)Credentials(0)["claudeAiOauth"]!;
        var body = ClaudeTokenRefresher.RefreshRequest(oauth, "old-refresh");
        Assert.Equal("refresh_token", (string?)body["grant_type"]);
        Assert.Equal("old-refresh", (string?)body["refresh_token"]);
        Assert.Equal("9d1c250a-e61b-44d9-88ed-5944d1962f5e", (string?)body["client_id"]);
        Assert.Equal("user:inference user:profile", (string?)body["scope"]);
    }

    [Fact]
    public void TheRenewedTokensReplaceOnlyTheirOwnFields()
    {
        var credentials = Credentials(0);
        var reply = (JsonObject)JsonNode.Parse("""
            { "access_token": "new-access", "refresh_token": "new-refresh", "expires_in": 28800,
              "refresh_token_expires_in": 2592000, "scope": "user:inference user:profile user:plugins" }
            """)!;
        long nowMs = Now.ToUnixTimeMilliseconds();
        Assert.Equal("new-access", ClaudeTokenRefresher.ApplyReply(credentials, reply, "old-refresh", nowMs));

        var oauth = (JsonObject)credentials["claudeAiOauth"]!;
        Assert.Equal("new-refresh", (string?)oauth["refreshToken"]);
        Assert.Equal(nowMs + 28_800_000, (long)oauth["expiresAt"]!);
        Assert.Equal(nowMs + 2_592_000_000, (long)oauth["refreshTokenExpiresAt"]!);
        Assert.Equal(3, ((JsonArray)oauth["scopes"]!).Count);
        Assert.Equal("pro", (string?)oauth["subscriptionType"]);
        Assert.Equal("keep-me", (string?)credentials["mcpOAuth"]!["server"]!["accessToken"]);
    }

    [Fact]
    public void AFileRenewedMeanwhileIsLeftAlone()
    {
        var credentials = Credentials(0);
        credentials["claudeAiOauth"]!["refreshToken"] = "someone-elses";
        var reply = (JsonObject)JsonNode.Parse("""{ "access_token": "new-access", "expires_in": 28800 }""")!;
        Assert.Null(ClaudeTokenRefresher.ApplyReply(credentials, reply, "old-refresh", Now.ToUnixTimeMilliseconds()));
        Assert.Equal("old-access", (string?)credentials["claudeAiOauth"]!["accessToken"]);
    }

    [Fact]
    public void WithoutANewRefreshTokenTheOldOneStays()
    {
        var credentials = Credentials(0);
        var reply = (JsonObject)JsonNode.Parse("""{ "access_token": "new-access", "expires_in": 3600 }""")!;
        ClaudeTokenRefresher.ApplyReply(credentials, reply, "old-refresh", Now.ToUnixTimeMilliseconds());
        Assert.Equal("old-refresh", (string?)credentials["claudeAiOauth"]!["refreshToken"]);
    }
}
