using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VNotch.Services;
using VNotch.Services.ControlCenter;

namespace VNotch;

public partial class MainWindow
{
    #region Windows notifications (compact pill)

    private static readonly TimeSpan AppNotificationPoll = TimeSpan.FromSeconds(2.5);
    // Over a fullscreen app nothing shows; looking less often still keeps what arrives meanwhile from popping up later.
    private static readonly TimeSpan AppNotificationPollFullscreen = TimeSpan.FromSeconds(10);
    private const string OwnAppName = "V-Notch";

    private const int RecentNotificationAppCount = 6;

    private readonly WindowsNotificationWatcher _appNotifications = new();
    private readonly Dictionary<string, ImageSource?> _appLogos = new(StringComparer.OrdinalIgnoreCase);
    // Apps that sent something lately, newest first, so the settings window can offer to mute them.
    private readonly List<NotificationApp> _recentNotificationApps = new();
    private DispatcherTimer? _appNotificationTimer;
    private bool _appNotificationPolling;
    private bool _appNotificationsAllowed;

    private void InitializeAppNotifications()
    {
        if (_cleanedUp) return;
        _appNotificationsAllowed = WindowsNotificationWatcher.IsAllowed;
        if (!_appNotificationsAllowed)
            RuntimeLog.Warn("NOTIFICATIONS", "Windows notification access is off; app notifications stay hidden.");
        _appNotificationTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = AppNotificationPoll };
        _appNotificationTimer.Tick += async (_, _) => await PollAppNotificationsAsync();
        // Settings are not loaded yet while the window attaches its parts: the first tick reads the switch.
        if (_appNotificationsAllowed) _appNotificationTimer.Start();
    }

    private void DisposeAppNotifications() => _appNotificationTimer?.Stop();

    /// <summary>For the settings window: whether Windows grants access, and who notified lately.</summary>
    private AppNotificationState ReadAppNotificationState() =>
        new(_appNotificationsAllowed, _recentNotificationApps.ToArray());

    /// <summary>The settings window flipped the main switch: start or stop looking for notifications.</summary>
    private void ApplyAppNotificationSetting()
    {
        if (_appNotificationTimer == null) return;
        if (_settings.ShowAppNotifications && _appNotificationsAllowed) _appNotificationTimer.Start();
        else _appNotificationTimer.Stop();
    }

    private HashSet<string> MutedNotificationApps() =>
        new(_settings.MutedNotificationApps.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);

    private static string AppKey(WindowsNotification notification) =>
        notification.AppUserModelId is { Length: > 0 } id ? id : notification.AppName;

    private void RememberNotificationApp(WindowsNotification notification)
    {
        string key = AppKey(notification);
        for (int i = 0; i < _recentNotificationApps.Count; i++)
        {
            if (!string.Equals(_recentNotificationApps[i].Key, key, StringComparison.OrdinalIgnoreCase)) continue;
            _recentNotificationApps.RemoveAt(i);
            break;
        }
        _recentNotificationApps.Insert(0, new NotificationApp(key, notification.AppName.Length > 0 ? notification.AppName : key));
        if (_recentNotificationApps.Count > RecentNotificationAppCount) _recentNotificationApps.RemoveAt(_recentNotificationApps.Count - 1);
    }

    private async Task PollAppNotificationsAsync()
    {
        if (_appNotificationPolling || _cleanedUp || _appNotificationTimer == null) return;
        if (_settings?.ShowAppNotifications != true)
        {
            if (_settings != null) _appNotificationTimer.Stop();
            return;
        }
        _appNotificationPolling = true;
        try
        {
            bool canShow = CanShowClaudeAttention;
            _appNotificationTimer.Interval = canShow ? AppNotificationPoll : AppNotificationPollFullscreen;
            var fresh = await _appNotifications.PollAsync();
            var muted = MutedNotificationApps();
            WindowsNotification? latest = null;
            int shown = 0;
            foreach (var notification in fresh)
            {
                if (string.Equals(notification.AppName, OwnAppName, StringComparison.OrdinalIgnoreCase)) continue;
                RememberNotificationApp(notification);
                if (muted.Contains(AppKey(notification))) continue;
                latest = notification;
                shown++;
            }
            if (fresh.Count > 0) _activeSettingsWindow?.AppNotificationAppsChanged();
            if (!canShow || latest == null) return;

            // Several at once: the newest speaks for all of them.
            int others = shown - 1;
            ImageSource? logo = await LoadAppLogoAsync(latest);
            string title = latest.AppName.Length > 0 ? latest.AppName : latest.Title;
            string status = latest.AppName.Length > 0 ? latest.Title : latest.Body ?? "";
            if (others > 0) status = $"{status}  +{others}";
            string? appId = latest.AppUserModelId;
            ShowNotchPill(title, status, logo, isClaude: false, tap: appId == null ? null : () => LaunchApp(appId));
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("NOTIFICATIONS", $"Reading notifications failed: {ex.Message}");
        }
        finally
        {
            _appNotificationPolling = false;
        }
    }

    private async Task<ImageSource?> LoadAppLogoAsync(WindowsNotification notification)
    {
        string key = notification.AppUserModelId ?? notification.AppName;
        if (_appLogos.TryGetValue(key, out var cached)) return cached;
        ImageSource? image = null;
        if (notification.Logo != null)
        {
            try
            {
                using var stream = await notification.Logo.OpenReadAsync();
                using var source = stream.AsStreamForRead();
                var copy = new MemoryStream();
                await source.CopyToAsync(copy);
                copy.Position = 0;
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 48;
                bitmap.StreamSource = copy;
                bitmap.EndInit();
                bitmap.Freeze();
                image = bitmap;
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
            {
                RuntimeLog.Warn("NOTIFICATIONS", $"No logo for {notification.AppName}: {ex.Message}");
            }
        }
        _appLogos[key] = image;
        return image;
    }

    /// <summary>Starts the app, or brings it up if it runs, the way its Start menu entry does.</summary>
    private static bool LaunchApp(string appUserModelId)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{appUserModelId}") { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            RuntimeLog.Warn("NOTIFICATIONS", $"Could not open {appUserModelId}: {ex.Message}");
            return false;
        }
    }

    #endregion
}
