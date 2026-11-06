using System.IO;
using VNotch.Models;
using VNotch.Services.Spotlight;
using VNotch.Services.Spotlight.Providers;
using Xunit;

namespace VNotch.Tests;

public sealed class SpotlightUninstallTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("vnotch-uninstall-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static SpotlightSearchItem App(string target, SpotlightResultKind kind = SpotlightResultKind.Application) =>
        new($"app:{target}", kind, Path.GetFileNameWithoutExtension(target), "Application", target, target);

    [Fact]
    public void InstalledAppsOfferUninstall()
    {
        string shortcut = Path.Combine(_folder, "Steam.lnk");
        File.WriteAllText(shortcut, "");
        Assert.True(SpotlightLauncher.CanUninstall(App(shortcut)));
        Assert.True(SpotlightLauncher.CanUninstall(App(@"shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App")));
    }

    [Fact]
    public void FilesFoldersSettingsAndWindowsToolsDoNot()
    {
        string file = Path.Combine(_folder, "notes.txt");
        File.WriteAllText(file, "");
        Assert.False(SpotlightLauncher.CanUninstall(App(file, SpotlightResultKind.File)));
        Assert.False(SpotlightLauncher.CanUninstall(App(_folder)));
        Assert.False(SpotlightLauncher.CanUninstall(App(Path.Combine(_folder, "gone.lnk"))));
        var tool = SpotlightSystemCatalog.Entries.First(e => e.Key == "taskManager").CreateItem();
        Assert.False(SpotlightLauncher.CanUninstall(tool));
        var apps = SpotlightSystemCatalog.Find(SpotlightLauncher.UninstallPage)!.CreateItem();
        Assert.False(SpotlightLauncher.CanUninstall(apps));
    }

    [Fact]
    public void UninstallMenuLeadsToTheInstalledAppsPage()
    {
        var page = SpotlightSystemCatalog.Find(SpotlightLauncher.UninstallPage);
        Assert.NotNull(page);
        Assert.Equal(SpotlightResultKind.Settings, page.Kind);
        Assert.True(SpotlightLauncher.IsValidTarget(page.CreateItem()));
    }

    [Theory]
    [InlineData("uninstall")]
    [InlineData("deinstallieren")]
    [InlineData("programme und features")]
    public async Task SearchingForUninstallFindsTheInstalledAppsPage(string query)
    {
        var results = await new SystemCommandProvider().SearchAsync(query, 5, CancellationToken.None);
        Assert.Contains(results, r => r.Target == SpotlightLauncher.UninstallPage);
    }
}
