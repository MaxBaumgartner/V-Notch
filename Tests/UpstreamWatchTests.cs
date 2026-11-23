using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

/// <summary>A fork's build asks GitHub how far the repository it follows has moved on; nothing else is asked or sent.</summary>
public sealed class UpstreamWatchTests
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public void OnlyAPlainRepositoryBranchAndCommitAreAccepted()
    {
        var source = UpstreamWatch.Create("someone/Some-Repo", null, Commit.ToUpperInvariant(), null)!;
        Assert.Equal(("someone/Some-Repo", "master", Commit, "someone"), (source.Repository, source.Branch, source.Commit, source.Name));
        Assert.Equal("Friend", UpstreamWatch.Create("someone/repo", "main", Commit, " Friend ")!.Name);
        Assert.Null(UpstreamWatch.Create(null, "main", Commit, null));
        Assert.Null(UpstreamWatch.Create("someone/repo", "main", null, null));
        Assert.Null(UpstreamWatch.Create("someone/repo/../x", "main", Commit, null));
        Assert.Null(UpstreamWatch.Create("someone/repo", "main", "not-a-commit", null));
        Assert.Null(UpstreamWatch.Create("someone/repo", "../main", Commit, null));
        Assert.Null(UpstreamWatch.Create("someone/repo?x=1", "main", Commit, null));
    }

    [Fact]
    public void TheTestBuildNamesNoUpstreamSoItNeverAsks() =>
        Assert.Null(UpstreamWatch.FromAssembly(typeof(UpstreamWatchTests).Assembly));

    [Fact]
    public void GitHubsCompareAnswerGivesTheLeadTheNewestCommitAndItsPage()
    {
        var ahead = UpstreamWatch.Parse(Compare("ahead", 3, "https://github.com/someone/repo/compare/a...master"))!;
        Assert.Equal((3, "c3", "https://github.com/someone/repo/compare/a...master"), (ahead.Count, ahead.Head, ahead.CompareUrl));
        Assert.Equal(2, UpstreamWatch.Parse(Compare("diverged", 2, ""))!.Count);
        Assert.Equal(0, UpstreamWatch.Parse(Compare("identical", 0, ""))!.Count);
        Assert.Equal(0, UpstreamWatch.Parse(Compare("behind", 0, ""))!.Count);
        Assert.Null(UpstreamWatch.Parse(new JsonObject { ["message"] = "Not Found" }));
        // A link that leads anywhere but github.com is not offered.
        Assert.Equal("", UpstreamWatch.Parse(Compare("ahead", 1, "https://example.com/x"))!.CompareUrl);
    }

    [Fact]
    public async Task OneGetToTheCompareRouteWithNothingAboutTheUser()
    {
        var handler = new Answer(HttpStatusCode.OK, Compare("ahead", 2, "https://github.com/someone/repo/compare/x...master").ToJsonString());
        using var watch = new UpstreamWatch(UpstreamWatch.Create("someone/repo", "master", Commit, "Friend")!, handler);
        var ahead = await watch.CheckAsync();
        Assert.Equal(2, ahead!.Count);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal($"https://api.github.com/repos/someone/repo/compare/{Commit}...master", request.RequestUri!.ToString());
        Assert.Equal("V-Notch", string.Join(" ", request.Headers.GetValues("User-Agent")));
        Assert.Null(request.Content);
        Assert.False(request.Headers.Contains("Authorization"));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "{}")]
    [InlineData(HttpStatusCode.Forbidden, "{\"message\":\"rate limit\"}")]
    [InlineData(HttpStatusCode.OK, "not json")]
    public async Task AnythingUnexpectedIsNoNews(HttpStatusCode status, string body)
    {
        using var watch = new UpstreamWatch(UpstreamWatch.Create("someone/repo", "master", Commit, null)!, new Answer(status, body));
        Assert.Null(await watch.CheckAsync());
    }

    private static JsonObject Compare(string status, int ahead, string url) => new()
    {
        ["status"] = status, ["ahead_by"] = ahead, ["html_url"] = url,
        ["commits"] = new JsonArray(new JsonObject { ["sha"] = "c1" }, new JsonObject { ["sha"] = "c3" })
    };

    private sealed class Answer(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
