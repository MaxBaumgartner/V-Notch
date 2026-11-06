using System.IO;
using VNotch.Models;
using VNotch.Services.Spotlight;
using VNotch.Services.Spotlight.Providers;
using Xunit;

namespace VNotch.Tests;

/// <summary>Apps used often come before apps used less, whatever else matches the query; pins stay first.</summary>
public sealed class SpotlightUsageRankingTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 6, 18, 0, 0, DateTimeKind.Utc);
    private readonly string _directory = Directory.CreateTempSubdirectory("vnotch-ranking-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void OneUseOutweighsTheGapBetweenTwoRankingTiers() =>
        // A word match (800) of an app opened once beats a title-prefix match (~900) of one never opened.
        Assert.True(SpotlightUsageStore.ComputeBoost(1, Now, Now, remembered: false) > 100);

    [Fact]
    public void MoreUseMeansMoreBoostUpToACap()
    {
        double once = SpotlightUsageStore.ComputeBoost(1, Now, Now, false);
        double often = SpotlightUsageStore.ComputeBoost(10, Now, Now, false);
        double always = SpotlightUsageStore.ComputeBoost(10_000, Now, Now, false);
        Assert.True(often > once);
        Assert.Equal(660, always); // 600 for use, 60 for today
        Assert.True(SpotlightUsageStore.ComputeBoost(10, Now.AddDays(-40), Now, false) < often);
    }

    [Fact]
    public void TheItemChosenForThisQueryAndPinsBeatAnyUse()
    {
        double heavy = SpotlightUsageStore.ComputeBoost(10_000, Now, Now, false);
        Assert.True(SpotlightUsageStore.ComputeBoost(1, Now, Now, remembered: true) > heavy);
        Assert.True(SpotlightUsageStore.PinnedBoost > SpotlightUsageStore.ComputeBoost(10_000, Now, Now, true));
    }

    [Fact]
    public async Task OftenUsedAppsComeFirstThenLessUsedThenUnused()
    {
        // Scores as SpotlightRanker gives them for "se": the shortest title-prefix match would win alone.
        var setup = App("Setup", 1197);
        var services = App("services", 1194);
        var settings = App("Settings", 1100);
        using var usage = new SpotlightUsageStore(Path.Combine(_directory, "usage.json"), () => Now);
        for (int i = 0; i < 5; i++) usage.RecordLaunch(settings);
        usage.RecordLaunch(services);

        var results = await new SpotlightSearchService([new FixedProvider(setup, services, settings)], usage)
            .SearchAsync("se", 10, CancellationToken.None);

        Assert.Equal(new[] { "Settings", "services", "Setup" }, results.Select(item => item.Title));
    }

    [Theory]
    [InlineData("Drawing", "d", true)]
    [InlineData("Media Center", "c", true)]           // a word of the name
    [InlineData("Visual Studio Code", "studio c", true)]
    [InlineData("Calendar", "d", false)]              // only somewhere inside
    [InlineData("PowerPoint", "point", false)]
    public void UseLiftsFullyOnlyNamesStartingWithTheQuery(string title, string query, bool starts) =>
        Assert.Equal(starts, SpotlightUsageStore.NameStartsWith(title, query));

    [Fact]
    public async Task AnOftenUsedAppThatOnlyContainsTheLetterStaysBehindOnesStartingWithIt()
    {
        // Scores as SpotlightRanker gives them for "d".
        var docs = App("Docs", 1197);
        var drawing = App("Drawing", 1194);
        var calendar = App("Calendar", 1000);
        using var usage = new SpotlightUsageStore(Path.Combine(_directory, "usage.json"), () => Now);
        for (int i = 0; i < 10; i++) usage.RecordLaunch(calendar);
        for (int i = 0; i < 8; i++) usage.RecordLaunch(docs);
        for (int i = 0; i < 2; i++) usage.RecordLaunch(drawing);

        var results = await new SpotlightSearchService([new FixedProvider(calendar, drawing, docs)], usage)
            .SearchAsync("d", 10, CancellationToken.None);

        Assert.Equal(new[] { "Docs", "Drawing", "Calendar" }, results.Select(item => item.Title));
    }

    [Fact]
    public async Task APinnedAppStaysAboveTheMostUsedOne()
    {
        var pinned = App("Paint", 1000);
        var used = App("PowerShell", 1100);
        using var usage = new SpotlightUsageStore(Path.Combine(_directory, "usage.json"), () => Now);
        for (int i = 0; i < 50; i++) usage.RecordLaunch(used);
        usage.Preferences.SetApp(pinned, pinned: true, alias: "");

        var results = await new SpotlightSearchService([new FixedProvider(pinned, used)], usage)
            .SearchAsync("p", 10, CancellationToken.None);

        Assert.Equal("Paint", results[0].Title);
    }

    private static SpotlightSearchItem App(string title, double score) =>
        new SpotlightSearchItem($"app:{title}", SpotlightResultKind.Application, title, "Application", $@"C:\Apps\{title}.lnk")
        { Score = score };

    private sealed class FixedProvider(params SpotlightSearchItem[] results) : ISpotlightProvider
    {
        public bool IsAvailable => true;

        public Task<IReadOnlyList<SpotlightSearchItem>> SearchAsync(string query, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SpotlightSearchItem>>(results);
    }
}
