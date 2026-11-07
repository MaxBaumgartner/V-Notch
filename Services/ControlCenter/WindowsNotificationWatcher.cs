using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace VNotch.Services.ControlCenter;

/// <summary>An app that sent notifications lately: the key mutes are stored by, and its name.</summary>
public sealed record NotificationApp(string Key, string Name);

/// <summary>What the settings window lists: whether Windows grants access, and the apps that notified lately.</summary>
public sealed record AppNotificationState(bool Allowed, IReadOnlyList<NotificationApp> Recent);

/// <summary>A toast another app showed: who sent it and its first two lines.</summary>
internal sealed record WindowsNotification(uint Id, string AppName, string? AppUserModelId, string Title, string? Body,
    RandomAccessStreamReference? Logo);

/// <summary>
/// Notices new Windows toasts by reading the notification list now and then (the change event is for
/// packaged apps only). What was already there when it started is never reported.
/// </summary>
internal sealed class WindowsNotificationWatcher
{
    private static readonly Size LogoSize = new(48, 48);
    private HashSet<uint> _seen = new();
    private bool _primed;

    /// <summary>False when Windows' "notification access" privacy switch is off for desktop apps.</summary>
    public static bool IsAllowed
    {
        get
        {
            try
            {
                return UserNotificationListener.Current.GetAccessStatus() == UserNotificationListenerAccessStatus.Allowed;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                return false;
            }
        }
    }

    /// <summary>Toasts that arrived since the last call, oldest first.</summary>
    public async Task<List<WindowsNotification>> PollAsync()
    {
        var fresh = new List<WindowsNotification>();
        IReadOnlyList<UserNotification> list;
        try
        {
            list = await UserNotificationListener.Current.GetNotificationsAsync(NotificationKinds.Toast);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return fresh;
        }

        var current = new HashSet<uint>();
        foreach (var notification in list)
        {
            current.Add(notification.Id);
            if (!_primed || _seen.Contains(notification.Id)) continue;
            if (Describe(notification) is { } described) fresh.Add(described);
        }
        _seen = current;
        _primed = true;
        return fresh;
    }

    private static WindowsNotification? Describe(UserNotification notification)
    {
        try
        {
            var binding = notification.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
            string title = "", body = "";
            if (binding != null)
            {
                var texts = binding.GetTextElements();
                if (texts.Count > 0) title = texts[0].Text ?? "";
                if (texts.Count > 1) body = texts[1].Text ?? "";
            }
            var app = notification.AppInfo;
            string name = app?.DisplayInfo?.DisplayName ?? "";
            if (name.Length == 0 && title.Length == 0) return null;
            RandomAccessStreamReference? logo = null;
            try
            {
                logo = app?.DisplayInfo?.GetLogo(LogoSize);
            }
            catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.COMException)
            {
                // Some desktop apps register no logo; the pill shows a bell instead.
            }
            return new WindowsNotification(notification.Id, name, app?.AppUserModelId, title.Trim(),
                body.Trim() is { Length: > 0 } text ? text : null, logo);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // Withdrawn while being read.
            return null;
        }
    }
}
