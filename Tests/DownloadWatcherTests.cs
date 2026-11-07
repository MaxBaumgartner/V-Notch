using VNotch.Services;
using VNotch.Services.ControlCenter;
using Xunit;

namespace VNotch.Tests;

public sealed class DownloadWatcherTests
{
    [Fact]
    public void SteamManifestsTellHowFarAnUpdateIs()
    {
        const string acf = """
        "AppState"
        {
            "appid"		"1145360"
            "name"		"Hades II"
            "StateFlags"		"1026"
            "BytesToDownload"		"2000000000"
            "BytesDownloaded"		"500000000"
            "UserConfig"
            {
                "language"		"german"
            }
        }
        """;
        var fields = DownloadWatcher.ParseAcf(acf);
        Assert.Equal("Hades II", fields["name"]);
        Assert.Equal("500000000", fields["BytesDownloaded"]);
        Assert.Equal("german", fields["language"]);
    }

    [Fact]
    public void TheRiotClientLogNamesWhatItPatches()
    {
        const string log = """
        013951.804|   OKAY| patch-proxy: HandleProductPatchlineStatusUpdate: productId: valorant, patchlineId: live, status: Updating, progress: 12
        013952.804|   OKAY| patch-proxy: HandleProductPatchlineStatusUpdate: productId: league_of_legends, patchlineId: live, status: NotInstalled, progress: 0
        013953.804|   OKAY| patch-proxy: HandleProductPatchlineStatusUpdate: productId: valorant, patchlineId: live, status: Updating, progress: 47.5
        013954.804|   OKAY| patch-proxy: HandleProductPatchlineStatusUpdate: productId: valorant, patchlineId: pbe, status: Updating, progress: 3
        """;
        var patching = DownloadWatcher.RiotPatching(log);
        var only = Assert.Single(patching);
        Assert.Equal("valorant", only.Product);
        Assert.Equal(47.5, only.Progress);

        // Done: the last line says so, nothing is listed any more.
        Assert.Empty(DownloadWatcher.RiotPatching(log + "\n013999.000|   OKAY| patch-proxy: HandleProductPatchlineStatusUpdate: productId: valorant, patchlineId: live, status: UpToDate, progress: 100"));
    }

    [Theory]
    [InlineData("setup.exe.crdownload", "setup.exe")]
    [InlineData("Unconfirmed 123456.crdownload", "Unconfirmed 123456")]
    [InlineData("film.mkv.part", "film.mkv")]
    public void BrowserFilesShowTheirFinalName(string partial, string expected) =>
        Assert.Equal(expected, DownloadWatcher.DisplayName(partial));

    [Fact]
    public void ADownloadTakesTheOrbButNeverTheNotch()
    {
        var (main, bubble) = LiveActivities.Arrange(music: false, claude: null, countdown: false, download: true);
        Assert.Equal(LiveActivity.None, main);
        Assert.Equal(LiveActivity.Download, bubble);

        // Music keeps the notch, the download goes beside it; a running timer outranks the download.
        Assert.Equal((LiveActivity.Music, LiveActivity.Download), LiveActivities.Arrange(true, null, false, download: true));
        Assert.Equal((LiveActivity.Music, LiveActivity.Countdown), LiveActivities.Arrange(true, null, true, download: true));
    }

    [Fact]
    public void TheOpenOrbSaysWhatDownloadsAndHowFast()
    {
        var words = MainWindow.DownloadWords(new[]
        {
            new DownloadInfo("steam:1", "Steam", "Hades II", 42.7, 30 * 1024 * 1024),
            new DownloadInfo("file:x", "Browser", "setup.exe", null, 2 * 1024 * 1024)
        });
        Assert.Equal("Hades II", words.Title);
        Assert.Contains("Steam", words.Subtitle);
        Assert.Contains("+1", words.Subtitle);
        Assert.Equal("42 %", words.Trailing);
    }
}
