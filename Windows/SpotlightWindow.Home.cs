using System.Windows;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.Spotlight;

namespace VNotch;

// The empty Spotlight as Start menu: app grid plus the apps running in the background.
public partial class SpotlightWindow
{
    private int _homeVersion;
    private string _homeSignature = string.Empty;

    private void LoadHome()
    {
        int version = ++_homeVersion;
        _ = LoadHomeAsync(version);
    }

    private async Task LoadHomeAsync(int version)
    {
        try
        {
            var (apps, tray) = await Task.Run(() =>
            {
                var homeApps = _viewModel.BuildHomeApps();
                // Only icons the user keeps on the taskbar show up; the ones in the overflow stay hidden.
                var trayApps = TrayIconBridge.ListApps(SpotlightHome.ShowInTrayRow);
                return (homeApps, trayApps);
            });
            if (version != _homeVersion || Dispatcher.HasShutdownStarted) return;

            string signature = string.Join('\n', apps.Select(a => a.Id)) + "\n--\n" + string.Join('\n', tray.Select(t => t.Name));
            if (signature == _homeSignature) return;
            bool wasEmpty = !HomePanel.HasContent;
            _homeSignature = signature;
            HomePanel.SetApps(apps);
            HomePanel.SetTray(tray);
            RefreshStatus();
            if (wasEmpty && HomePanel.IsVisible) HomePanel.PlayReveal();
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("SPOTLIGHT-HOME", ex, "Home panel could not load");
        }
    }

    private void OpenTrayMenu(string name)
    {
        HideSpotlight();
        // Let Spotlight hand back the foreground before the app's menu asks for it.
        _ = Task.Run(async () =>
        {
            await Task.Delay(140);
            if (!TrayIconBridge.ShowMenu(name))
                RuntimeLog.Log("SPOTLIGHT-HOME", $"No tray menu opened for '{name}'");
        });
    }
}
