using System.IO;
using System.Security;
using Microsoft.Win32;

namespace VNotch.Services;

internal sealed class NotificationBannerSwitch(string registryRoot)
{
    internal const string WindowsRoot = @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";

    public void Hide(string appId, IDictionary<string, int?> restore)
    {
        if (!IsAppId(appId)) return;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"{registryRoot}\{appId}");
            if (!restore.ContainsKey(appId))
            {
                object? old = key.GetValue("ShowBanner");
                // Preserve values outside the DWORD contract rather than changing their type.
                if (old != null && (old is not int || key.GetValueKind("ShowBanner") != RegistryValueKind.DWord)) return;
                restore.Add(appId, old as int?);
            }
            key.SetValue("ShowBanner", 0, RegistryValueKind.DWord);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            RuntimeLog.Warn("NOTIFICATIONS", $"Could not hide Windows banner for {appId}: {ex.Message}");
        }
    }

    public void RestoreAll(IDictionary<string, int?> restore)
    {
        foreach (var entry in restore.ToArray())
        {
            if (!IsAppId(entry.Key)) { restore.Remove(entry.Key); continue; }
            try
            {
                string path = $@"{registryRoot}\{entry.Key}";
                using (var key = entry.Value.HasValue
                    ? Registry.CurrentUser.CreateSubKey(path)
                    : Registry.CurrentUser.OpenSubKey(path, writable: true))
                {
                    if (entry.Value is int original) key!.SetValue("ShowBanner", original, RegistryValueKind.DWord);
                    else key?.DeleteValue("ShowBanner", throwOnMissingValue: false);
                }
                // Only remove an empty app key; Windows or the user may have added other settings.
                using (var key = Registry.CurrentUser.OpenSubKey(path))
                {
                    if (!entry.Value.HasValue && key is { ValueCount: 0, SubKeyCount: 0 })
                        Registry.CurrentUser.DeleteSubKey(path, throwOnMissingSubKey: false);
                }
                restore.Remove(entry.Key);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
            {
                // Keep unsuccessful entries so a later attempt can restore them.
                RuntimeLog.Warn("NOTIFICATIONS", $"Could not restore Windows banner for {entry.Key}: {ex.Message}");
            }
        }
    }

    private static bool IsAppId(string appId) =>
        !string.IsNullOrWhiteSpace(appId) && appId.IndexOfAny(['\\', '/', '\0']) < 0;
}
