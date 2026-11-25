using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace VNotch.Services;

/// <summary>Where a fork's build came from: the upstream repository, its branch, the upstream commit it contains, and a name to show.</summary>
internal sealed record UpstreamSource(string Repository, string Branch, string Commit, string Name);

/// <summary>How far the upstream branch has moved past the commit this build contains.</summary>
internal sealed record UpstreamAhead(int Count, string Head, string CompareUrl);

/// <summary>
/// For a fork that follows another repository: asks GitHub how many commits the upstream branch is ahead of the upstream
/// commit this build contains. Only a build that names both (assembly metadata from the build properties
/// UpstreamRepository, UpstreamBranch, UpstreamCommit, UpstreamName) ever asks; official builds carry none of it. One
/// unauthenticated GET to api.github.com with nothing about the user in it.
/// </summary>
internal sealed partial class UpstreamWatch(UpstreamSource source, HttpMessageHandler? handler = null) : IDisposable
{
    private const int MaxResponseBytes = 2 * 1024 * 1024;
    private readonly HttpClient _http = new(handler ?? NetworkPrivacy.CreateHttpHandler(allowAutoRedirect: true)) { Timeout = TimeSpan.FromSeconds(10) };

    public UpstreamSource Source { get; } = source;

    [GeneratedRegex("^[A-Za-z0-9-]{1,39}/[A-Za-z0-9._-]{1,100}$")]
    private static partial Regex RepositoryPattern();

    [GeneratedRegex("^[0-9a-f]{7,40}$")]
    private static partial Regex CommitPattern();

    [GeneratedRegex("^[A-Za-z0-9._/-]{1,100}$")]
    private static partial Regex BranchPattern();

    /// <summary>The build's upstream, or null when it names none (or names something that is not a plain repo, branch and commit).</summary>
    public static UpstreamSource? FromAssembly(Assembly assembly)
    {
        string? Meta(string key) => assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value;
        return Create(Meta("UpstreamRepository"), Meta("UpstreamBranch"), Meta("UpstreamCommit"), Meta("UpstreamName"));
    }

    internal static UpstreamSource? Create(string? repository, string? branch, string? commit, string? name)
    {
        repository = repository?.Trim();
        commit = commit?.Trim().ToLowerInvariant();
        branch = string.IsNullOrWhiteSpace(branch) ? "master" : branch.Trim();
        if (repository == null || commit == null || !RepositoryPattern().IsMatch(repository) || !CommitPattern().IsMatch(commit) ||
            !BranchPattern().IsMatch(branch) || branch.Contains("..", StringComparison.Ordinal)) return null;
        string shown = string.IsNullOrWhiteSpace(name) ? repository.Split('/')[0] : name.Trim();
        return new UpstreamSource(repository, branch, commit, shown);
    }

    internal string CompareApiUrl =>
        $"https://api.github.com/repos/{Source.Repository}/compare/{Source.Commit}...{Uri.EscapeDataString(Source.Branch)}";

    /// <summary>The upstream's lead, or null when GitHub could not be asked or did not answer as expected.</summary>
    public async Task<UpstreamAhead?> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, CompareApiUrl);
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            request.Headers.TryAddWithoutValidation("User-Agent", "V-Notch");
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            if (response.Content.Headers.ContentLength > MaxResponseBytes) return null;
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (body.Length > MaxResponseBytes) return null;
            return JsonNode.Parse(body) is JsonObject compare ? Parse(compare) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            RuntimeLog.Warn("UPSTREAM", $"Compare not read: {ex.GetType().Name}");
            return null;
        }
    }

    /// <summary>GitHub's compare answer: "ahead" with its count, the newest commit and the page that lists them.</summary>
    internal static UpstreamAhead? Parse(JsonObject compare)
    {
        string? status = compare["status"] is JsonValue s && s.TryGetValue(out string? text) ? text : null;
        if (status is not ("ahead" or "diverged" or "identical" or "behind")) return null;
        int ahead = compare["ahead_by"] is JsonValue a && a.TryGetValue(out int count) ? count : 0;
        if (ahead <= 0 || status is "identical" or "behind") return new UpstreamAhead(0, "", "");
        string head = compare["commits"] is JsonArray { Count: > 0 } commits && commits[^1] is JsonObject last &&
            last["sha"] is JsonValue sha && sha.TryGetValue(out string? id) ? id ?? "" : "";
        string url = compare["html_url"] is JsonValue h && h.TryGetValue(out string? link) &&
            link != null && link.StartsWith("https://github.com/", StringComparison.Ordinal) ? link : "";
        return new UpstreamAhead(ahead, head, url);
    }

    public void Dispose() => _http.Dispose();
}
