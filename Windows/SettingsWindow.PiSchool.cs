using VNotch.Models;
using VNotch.Services;

namespace VNotch;

/// <summary>
/// The resting notch, the school lesson in the orb, and where the Pi tab, Moodle, Untis and Discord are found:
/// settings that used to live only in settings.json.
/// </summary>
public partial class SettingsWindow
{
    private void LoadPiSchoolSettings(NotchSettings settings)
    {
        SlimIdleNotchCheck.IsChecked = settings.SlimIdleNotch;
        LessonTimerCheck.IsChecked = settings.ShowLessonTimer;
        PiDashboardUrlTextBox.Text = settings.PiDashboardUrl;
        SchoolMoodleUrlTextBox.Text = settings.SchoolMoodleUrl;
        SchoolUntisUrlTextBox.Text = settings.SchoolUntisUrl;
        DiscordClientIdTextBox.Text = settings.DiscordClientId;
    }

    private void ReadPiSchoolSettings(NotchSettings snapshot)
    {
        snapshot.SlimIdleNotch = SlimIdleNotchCheck.IsChecked ?? true;
        snapshot.ShowLessonTimer = LessonTimerCheck.IsChecked ?? true;
        snapshot.PiDashboardUrl = PiDashboardUrlTextBox.Text?.Trim() ?? string.Empty;
        snapshot.SchoolMoodleUrl = SchoolMoodleUrlTextBox.Text?.Trim() ?? string.Empty;
        snapshot.SchoolUntisUrl = SchoolUntisUrlTextBox.Text?.Trim() ?? string.Empty;
        snapshot.DiscordClientId = DiscordClientIdTextBox.Text?.Trim() ?? string.Empty;
    }

    private void LocalizePiSchoolSettings()
    {
        SlimIdleNotchCheck.Content = Loc.Get("settings.slimIdleNotch");
        SlimIdleNotchHint.Text = Loc.Get("settings.slimIdleNotch.hint");
        LessonTimerCheck.Content = Loc.Get("settings.lessonTimer");
        LessonTimerHint.Text = Loc.Get("settings.lessonTimer.hint");
        PiDashboardUrlLabel.Text = Loc.Get("settings.piDashboardUrl");
        PiDashboardUrlHint.Text = Loc.Get("settings.piDashboardUrl.hint");
        SchoolMoodleUrlLabel.Text = Loc.Get("settings.schoolMoodleUrl");
        SchoolUntisUrlLabel.Text = Loc.Get("settings.schoolUntisUrl");
        DiscordClientIdLabel.Text = Loc.Get("settings.discordClientId");
        DiscordClientIdHint.Text = Loc.Get("settings.discordClientId.hint");
    }
}
