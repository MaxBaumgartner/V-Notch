using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using VNotch.Controls;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.ControlCenter;

namespace VNotch;

public partial class MainWindow
{
    #region Pi dashboard tab and reminders

    // Same footprint as the controls tab; the list scrolls.
    private const double PiViewWidth = 720;
    private const double PiViewHeight = 378;
    private static readonly TimeSpan PiReminderPoll = TimeSpan.FromMinutes(1);

    private PiDashboardClient? _piClient;
    private DispatcherTimer? _piReminderTimer;
    private readonly HashSet<string> _seenPiReminders = new(StringComparer.Ordinal);
    private bool _piRemindersPrimed;
    private bool _isPiView;
    private static readonly ImageSource PiPillIcon = CreatePiPillIcon();

    private void InitializePi()
    {
        // Settings load after the window attaches its parts: read the address when it is asked for.
        _piClient = new PiDashboardClient(() => _settings?.PiDashboardUrl ?? "");
        PiCenter.AttachPi(_piClient, () => _settings?.EnableLocalOnlyMode ?? true, () => _settings != null && IsLiquidGlassEnabled,
            () => _settings?.SchoolMoodleUrl ?? "", () => _settings?.SchoolUntisUrl ?? "");
        PiCenter.KeyboardInputRequested += OnClaudeKeyboardInputRequested;
        _piReminderTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = PiReminderPoll };
        _piReminderTimer.Tick += async (_, _) => await PollPiRemindersAsync();
        _piReminderTimer.Start();
        InitializeLessonTimer();
    }

    private void DisposePi()
    {
        _piReminderTimer?.Stop();
        DisposeLessonTimer();
        PiCenter.KeyboardInputRequested -= OnClaudeKeyboardInputRequested;
        PiCenter.Dispose();
        _piClient?.Dispose();
    }

    private void PiIconButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!_isPiView) NavigateToNotchView(NotchView.Pi);
    }

    /// <summary>
    /// What the dashboard pushes to the phone (a new assignment, "leave at 06:21") also shows as a pill
    /// here. What was already sent before the notch started stays quiet. Several new at once take turns, one
    /// pill a minute, newest first.
    /// </summary>
    private async Task PollPiRemindersAsync()
    {
        if (_cleanedUp || _piClient == null || _settings == null || _settings.EnableLocalOnlyMode || !_piClient.IsConfigured) return;
        var sent = await _piClient.GetRemindersAsync();
        if (sent == null || _cleanedUp) return;
        PiReminder? latest = null;
        foreach (var reminder in sent)
        {
            if (!_piRemindersPrimed) _seenPiReminders.Add(reminder.Key);
            if (_seenPiReminders.Contains(reminder.Key)) continue;
            if (latest == null || reminder.At > latest.At) latest = reminder;
        }
        _piRemindersPrimed = true;
        if (latest == null) return;
        _seenPiReminders.Add(latest.Key);
        // First line only: the phone shows the rest.
        string message = latest.Message;
        int newline = message.IndexOf('\n');
        if (newline > 0) message = message[..newline];
        var shown = latest;
        ShowNotchPill(latest.Title, message, PiPillIcon, isClaude: false, tap: () =>
        {
            OpenPiReminderAsync(shown).SafeFireAndForget("PI-REMINDER");
            return true;
        });
    }

    /// <summary>
    /// A tap on a reminder goes where it points: the assignment to hand in, or Untis; the rest opens the Pi tab.
    /// </summary>
    private async Task OpenPiReminderAsync(PiReminder reminder)
    {
        PiSchool? school = SchoolLinks.NeedsSchool(reminder.Key) && _piClient != null ? await _piClient.GetSchoolAsync() : null;
        if (_cleanedUp) return;
        string moodle = _settings?.SchoolMoodleUrl ?? "", untis = _settings?.SchoolUntisUrl ?? "";
        string? url = SchoolLinks.ForReminder(reminder.Key, school, DateTimeOffset.Now, moodle, untis);
        // Moodle and Untis open signed in (the Pi hands out one-time sign-in links).
        if (url != null && _piClient != null) url = await _piClient.SignedInSchoolLinkAsync(url, moodle, untis);
        if (_cleanedUp) return;
        if (url == null || !SafeLauncher.TryOpenUrl(url))
            _transitionCoordinator.RequestView(NotchView.Pi, "PiReminder");
    }

    private static ImageSource CreatePiPillIcon()
    {
        var drawing = new DrawingImage(new GeometryDrawing(Brushes.White, null, PiGlyph.Data));
        drawing.Freeze();
        return drawing;
    }

    #endregion
}
