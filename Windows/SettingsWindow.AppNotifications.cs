using System.Text;
using System.Windows;
using System.Windows.Controls;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.ControlCenter;

namespace VNotch;

/// <summary>
/// Windows notifications of other apps, mirrored as a pill in the notch: the main switch and one box per app
/// that notified lately (or was muted before), under the system notifications switch on the Behavior page.
/// </summary>
public partial class SettingsWindow
{
    private readonly Func<AppNotificationState>? _appNotificationState;
    private CheckBox? _appNotifyCheck;
    private TextBlock? _appNotifyHint;
    private Button? _appNotifyAccessButton;
    private StackPanel? _appNotifyApps;
    // Keys the notch leaves out; the boxes edit this set and the snapshot reads it.
    private readonly HashSet<string> _appNotifyMuted = new(StringComparer.OrdinalIgnoreCase);
    private bool _appNotifyFilling;

    private void LoadAppNotifications(NotchSettings settings)
    {
        EnsureAppNotificationRow();
        _appNotifyFilling = true;
        try
        {
            _appNotifyCheck!.IsChecked = settings.ShowAppNotifications;
            _appNotifyMuted.Clear();
            foreach (string key in SplitMuted(settings.MutedNotificationApps)) _appNotifyMuted.Add(key);
        }
        finally
        {
            _appNotifyFilling = false;
        }
        RefreshAppNotificationApps();
        LocalizeAppNotifications();
    }

    private void ReadAppNotifications(NotchSettings settings)
    {
        if (_appNotifyCheck == null) return;
        settings.ShowAppNotifications = _appNotifyCheck.IsChecked == true;
        settings.MutedNotificationApps = string.Join('|', _appNotifyMuted);
    }

    private void ResetAppNotifications(NotchSettings defaults)
    {
        if (_appNotifyCheck == null) return;
        _appNotifyFilling = true;
        try
        {
            _appNotifyCheck.IsChecked = defaults.ShowAppNotifications;
            _appNotifyMuted.Clear();
        }
        finally
        {
            _appNotifyFilling = false;
        }
        RefreshAppNotificationApps();
    }

    private void LocalizeAppNotifications()
    {
        if (_appNotifyCheck == null || _appNotifyHint == null || _appNotifyAccessButton == null) return;
        _appNotifyCheck.Content = Loc.Get("settings.appNotify");
        bool allowed = _appNotificationState?.Invoke().Allowed ?? true;
        _appNotifyHint.Text = Loc.Get(allowed ? "settings.appNotify.hint" : "settings.appNotify.denied");
        _appNotifyAccessButton.Content = Loc.Get("settings.appNotify.openSettings");
    }

    /// <summary>Built once, right below the system notifications switch.</summary>
    private void EnsureAppNotificationRow()
    {
        if (_appNotifyCheck != null) return;
        _appNotifyCheck = new CheckBox();
        _appNotifyHint = new TextBlock
        {
            Style = (Style)FindResource("SettingHintText"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0)
        };
        _appNotifyAccessButton = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 96,
            Height = 34,
            Margin = new Thickness(0, 10, 0, 0),
            Visibility = Visibility.Collapsed
        };
        _appNotifyAccessButton.Click += (_, _) => OpenNotificationPrivacySettings();
        _appNotifyApps = new StackPanel { Margin = new Thickness(28, 6, 0, 0) };

        var panel = new StackPanel();
        panel.Children.Add(_appNotifyCheck);
        panel.Children.Add(_appNotifyHint);
        panel.Children.Add(_appNotifyAccessButton);
        panel.Children.Add(_appNotifyApps);
        var row = new Border { Style = (Style)FindResource("SettingRowBorder"), Margin = new Thickness(0, 0, 0, 4), Child = panel };

        _appNotifyCheck.Checked += AppNotifyCheck_Changed;
        _appNotifyCheck.Unchecked += AppNotifyCheck_Changed;

        if (SystemNotifyCheck.Parent is FrameworkElement { Parent: Border systemRow } && systemRow.Parent is Panel host)
            host.Children.Insert(host.Children.IndexOf(systemRow) + 1, row);
        // The window is built ahead of time; the apps that notified meanwhile show up when it opens.
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) RefreshAppNotificationApps();
        };
    }

    /// <summary>An app notified for the first time since the notch started: list it while the window is open.</summary>
    internal void AppNotificationAppsChanged()
    {
        if (IsVisible) RefreshAppNotificationApps();
    }

    private void AppNotifyCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_appNotifyFilling) return;
        RefreshAppNotificationApps();
        if (!_isLoadingSettings) PushLivePreview();
    }

    /// <summary>One box per app: the ones that notified since the notch started, then the ones muted earlier.</summary>
    private void RefreshAppNotificationApps()
    {
        if (_appNotifyApps == null || _appNotifyCheck == null || _appNotifyAccessButton == null) return;
        var state = _appNotificationState?.Invoke();
        bool allowed = state?.Allowed ?? true;
        _appNotifyAccessButton.Visibility = allowed ? Visibility.Collapsed : Visibility.Visible;
        _appNotifyApps.Children.Clear();
        if (!allowed || _appNotifyCheck.IsChecked != true)
        {
            _appNotifyApps.Visibility = Visibility.Collapsed;
            LocalizeAppNotifications();
            return;
        }
        _appNotifyApps.Visibility = Visibility.Visible;

        var shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (state != null)
            foreach (var app in state.Recent)
                if (shown.Add(app.Key)) AddAppBox(app.Key, app.Name);
        foreach (string key in _appNotifyMuted)
            if (shown.Add(key)) AddAppBox(key, NotificationAppNames.FromKey(key));

        if (_appNotifyApps.Children.Count == 0)
        {
            _appNotifyApps.Children.Add(new TextBlock
            {
                Text = Loc.Get("settings.appNotify.empty"),
                Style = (Style)FindResource("SettingHintText"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0)
            });
        }
        LocalizeAppNotifications();
    }

    private void AddAppBox(string key, string name)
    {
        var box = new CheckBox
        {
            Content = name,
            IsChecked = !_appNotifyMuted.Contains(key),
            Margin = new Thickness(0, 8, 0, 0)
        };
        RoutedEventHandler changed = (_, _) =>
        {
            if (box.IsChecked == true) _appNotifyMuted.Remove(key);
            else _appNotifyMuted.Add(key);
            if (!_isLoadingSettings && !_appNotifyFilling) PushLivePreview();
        };
        box.Checked += changed;
        box.Unchecked += changed;
        _appNotifyApps!.Children.Add(box);
    }

    private static IEnumerable<string> SplitMuted(string? muted) =>
        (muted ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static void OpenNotificationPrivacySettings()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:privacy-notifications") { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            RuntimeLog.Warn("SETTINGS", $"Notification privacy settings did not open: {ex.Message}");
        }
    }
}

/// <summary>Turns a muted app's key (an AppUserModelID or a name) back into something readable.</summary>
internal static class NotificationAppNames
{
    /// <example>"E046963F.LenovoCompanion_k1h2ywk1493x8!App" → "Lenovo Companion", "Google.Play.Games" → "Google Play Games".</example>
    public static string FromKey(string key)
    {
        string id = key;
        int bang = id.IndexOf('!');
        if (bang > 0) id = id[..bang];
        int underscore = id.LastIndexOf('_');
        if (underscore > 0) id = id[..underscore];
        if (id.Contains('\\') || id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return System.IO.Path.GetFileNameWithoutExtension(id);

        var words = new List<string>();
        foreach (string part in id.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            // Store packages start with the publisher's hex id ("E046963F"): not part of the name.
            if (part.Length >= 6 && part.All(Uri.IsHexDigit) && part.Any(char.IsDigit)) continue;
            words.Add(SplitCamelCase(part));
        }
        return words.Count == 0 ? key : string.Join(' ', words);
    }

    private static string SplitCamelCase(string word)
    {
        var sb = new StringBuilder(word.Length + 4);
        for (int i = 0; i < word.Length; i++)
        {
            if (i > 0 && char.IsUpper(word[i]) && char.IsLower(word[i - 1])) sb.Append(' ');
            sb.Append(word[i]);
        }
        return sb.ToString();
    }
}
