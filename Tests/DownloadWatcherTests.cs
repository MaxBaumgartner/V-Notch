using System.IO;
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

    [Theory]
    [InlineData(".crdownload")]
    [InlineData(".part")]
    [InlineData(".opdownload")]
    [InlineData(".partial")]
    public void ADisappearedPartialMatchesItsFinalFileInTheSameFolder(string extension)
    {
        const string path = @"C:\Downloads\Report.pdf";
        string partial = path.ToLowerInvariant() + extension;
        var final = new DownloadWatcher.BrowserFile(path, 42, DateTime.UnixEpoch);
        var files = new Dictionary<string, DownloadWatcher.BrowserFile>(StringComparer.OrdinalIgnoreCase);
        const string otherPath = @"C:\Other\Report.pdf";
        files[otherPath] = final with { Path = otherPath };
        Assert.Null(DownloadWatcher.CompletedBrowserPath(partial, null, files));
        files[path] = final;
        files[partial] = final with { Path = partial };
        Assert.Null(DownloadWatcher.CompletedBrowserPath(partial, null, files));
        files.Remove(partial);
        Assert.Null(DownloadWatcher.CompletedBrowserPath(partial, final, files));
        Assert.Equal(path, DownloadWatcher.CompletedBrowserPath(partial, null, files));
        Assert.Equal(path, DownloadWatcher.CompletedBrowserPath(partial, final with { Bytes = 0 }, files));
        Assert.Null(DownloadWatcher.CompletedBrowserPath(path, null, files));
    }

    [Theory]
    [InlineData(".crdownload")]
    [InlineData(".part")]
    public void ACompletedDownloadSurvivesPollsForOneMinuteAndDoesNotSurviveRestart(string extension)
    {
        var folder = Directory.CreateTempSubdirectory();
        try
        {
            var now = DateTime.UnixEpoch;
            var watcher = new DownloadWatcher(() => now);
            string final = Path.Combine(folder.FullName, "My Report.pdf");
            string partial = final + extension;
            if (extension == ".part") File.WriteAllText(final, "");
            File.WriteAllText(partial, "first");
            Assert.Empty(watcher.PollBrowsers(folder.FullName));

            now = now.AddSeconds(3);
            File.AppendAllText(partial, " more");
            var running = Assert.Single(watcher.PollBrowsers(folder.FullName));
            Assert.False(running.IsFinished);
            Assert.Equal(partial, running.FilePath);

            now = now.AddSeconds(3);
            File.Move(partial, final, overwrite: true);
            var finished = Assert.Single(watcher.PollBrowsers(folder.FullName));
            Assert.True(finished.IsFinished);
            Assert.Equal("My Report.pdf", finished.Name);
            Assert.Equal(final, finished.FilePath);
            Assert.Empty(new DownloadWatcher(() => now).PollBrowsers(folder.FullName));

            now = now.AddSeconds(59);
            Assert.Equal(finished, Assert.Single(watcher.PollBrowsers(folder.FullName)));
            now = now.AddSeconds(1);
            Assert.Empty(watcher.PollBrowsers(folder.FullName));
        }
        finally { folder.Delete(recursive: true); }
    }

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
