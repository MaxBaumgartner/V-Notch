using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using VNotch.Controllers;
using VNotch.Services;
using VNotch.Services.ClaudeCode;

namespace VNotch;

public partial class MainWindow
{
    private static readonly TimeSpan CodexDoneLifetime = TimeSpan.FromSeconds(6);
    private static readonly Brush CodexJobMist = FrozenBrush(0x7F, 0xA6, 0xCC);
    private static readonly Brush CodexJobSage = FrozenBrush(0x9D, 0xBD, 0xB1);
    private static readonly Brush CodexJobRose = FrozenBrush(0xD2, 0x85, 0x7E);
    private static readonly Brush CodexJobLabel = Frozen(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF));
    internal static readonly ImageSource CodexJobIcon = CreateCodexJobIcon();

    private readonly Queue<(CodexJobResult Result, DateTimeOffset Expires)> _codexFinished = new();
    private readonly List<(CodexJob Job, TextBlock Clock)> _codexDetailClocks = new();
    private CodexJobWatcher? _codexJobWatcher;
    private DispatcherTimer? _codexJobClock, _codexHoverTimer;
    private IReadOnlyList<CodexJob> _codexRunning = [];
    private IReadOnlyList<CodexJob>? _codexShownJobs;
    private string? _codexPillId;

    private bool IsCodexJobPillShown => _isClaudeNotificationVisible && _codexPillId != null;

    private void InitializeCodexJobs()
    {
        _codexJobClock = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _codexJobClock.Tick += (_, _) => RefreshCodexJobPill();
        _codexHoverTimer = new DispatcherTimer(DispatcherPriority.Background);
        _codexHoverTimer.Tick += (_, _) =>
        {
            _codexHoverTimer.Stop();
            SetCodexJobDetails(NotchBorder.IsMouseOver);
        };
        _codexJobWatcher = new CodexJobWatcher(Dispatcher);
        _codexJobWatcher.Finished += result =>
        {
            if (_settings?.ShowCodexJobsInNotch != true) return;
            _codexFinished.Enqueue((result, DateTimeOffset.UtcNow + ClaudeDoneLifetime));
            SyncCodexJobs();
        };
        _codexJobWatcher.Changed += SyncCodexJobs;
    }

    private void DisposeCodexJobs()
    {
        _codexJobWatcher?.Dispose();
        _codexJobClock?.Stop();
        _codexHoverTimer?.Stop();
    }

    private void SyncCodexJobs()
    {
        if (_codexJobWatcher == null) return;
        _codexRunning = _codexJobWatcher.Running;
        bool enabled = _settings?.ShowCodexJobsInNotch == true;
        ClaudeCenter.SetCodexJobList(enabled ? _codexRunning : [], enabled ? _codexJobWatcher.RecentFinished : [],
            ClaudeViewMaxHeight - ClaudeContent.Margin.Top - ClaudeContent.Margin.Bottom);
        RefreshCodexJobPill();
    }

    private void RefreshCodexJobPill()
    {
        if (_cleanedUp) return;
        var now = DateTimeOffset.UtcNow;
        if (_settings?.ShowCodexJobsInNotch != true) _codexFinished.Clear();
        while (_codexFinished.TryPeek(out var pending) && pending.Expires <= now) _codexFinished.Dequeue();

        bool enabled = _settings?.ShowCodexJobsInNotch == true;
        if (enabled && (_codexRunning.Count > 0 || _codexFinished.Count > 0 || IsCodexJobPillShown)) _codexJobClock?.Start();
        else _codexJobClock?.Stop();

        // A live Claude session owns the notch: Codex workers are its helpers and never cover it.
        if (!enabled || !CanShowClaudeAttention || _isExpanded || _isMusicExpanded || _isAnimating || _isGreetingActive ||
            _claudeLiveSession != null)
        {
            if (IsCodexJobPillShown) AnimateClaudeNotificationOut();
            return;
        }
        if (IsCodexJobPillShown)
        {
            // A completion gets its full moment. The shared pill timer will release the face.
            if (_codexShownJobs == null) return;
        }
        else if (_isClaudeNotificationVisible || _compactPillArbiter.ActiveSlot != CompactPillSlot.None ||
            _isClaudeWaiting || _claudeDone.Exists(note => note.Until > now.UtcDateTime)) return;

        if (_codexFinished.TryPeek(out var finished))
        {
            if (ShowCodexJobFinished(finished.Result)) _codexFinished.Dequeue();
        }
        else if (_codexRunning.Count == 0)
        {
            if (IsCodexJobPillShown) AnimateClaudeNotificationOut();
        }
        else ShowCodexJobsRunning(_codexRunning, now);
    }

    private void ShowCodexJobsRunning(IReadOnlyList<CodexJob> jobs, DateTimeOffset now)
    {
        var lead = jobs[0];
        if (_codexShownJobs == null || !_codexShownJobs.SequenceEqual(jobs))
        {
            bool expanded = CodexJobDetails.Visibility == Visibility.Visible;
            if (!ShowCompactNotification(lead.Title, CodexJobStatus(jobs.Count, lead, now), CodexJobIcon,
                    isClaude: false, tap: null, doneSessionId: null, glyph: null, duration: null, accent: null, jobId: lead.Id)) return;
            _codexShownJobs = jobs;
            SetCodexJobDetails(expanded);
            if (!expanded && NotchBorder.IsMouseOver)
            {
                OnNotchHoverForClaudePeek(entered: true);
                OnCodexJobHover(entered: true);
            }
        }
        // The status is measured once per job change. Tabular numerals keep the clock fixed between ticks.
        ClaudeNotificationStatus.Text = CodexJobStatus(jobs.Count, lead, now);
        foreach (var (job, clock) in _codexDetailClocks) clock.Text = CodexJob.FormatElapsed(now - job.StartedAt);
    }

    private bool ShowCodexJobFinished(CodexJobResult result)
    {
        bool ok = result.Exit == 0 && !result.TimedOut;
        string status = result.TimedOut ? "timed out" : ok ? CodexJob.FormatElapsed(result.Duration) : "failed";
        if (!ShowCompactNotification(result.Job.Title, status, null, isClaude: false, tap: null, doneSessionId: null,
                glyph: ok ? "\uE73E" : "\uE711", duration: CodexDoneLifetime,
                accent: ok ? CodexJobSage : CodexJobRose, jobId: result.Job.Id)) return false;
        ClearCodexJobDetails();
        ClaudeNotificationStatus.Foreground = CodexJobLabel;
        FitNotchToPill();
        return true;
    }

    private static string CodexJobStatus(int count, CodexJob job, DateTimeOffset now) =>
        (count > 1 ? $"+{count - 1}  ·  " : "") + CodexJob.FormatElapsed(now - job.StartedAt);

    private void OnCodexJobHover(bool entered)
    {
        if (_codexShownJobs == null || _codexHoverTimer == null) return;
        _codexHoverTimer.Stop();
        _codexHoverTimer.Interval = entered ? ClaudePeekDelay : ClaudePeekLinger;
        _codexHoverTimer.Start();
    }

    private void SetCodexJobDetails(bool expanded)
    {
        if (_codexShownJobs is not { } jobs || !CanShowClaudeAttention) return;
        CodexJobDetails.Children.Clear();
        _codexDetailClocks.Clear();
        ClaudeNotificationStatus.Foreground = CodexJobLabel;
        Typography.SetNumeralAlignment(ClaudeNotificationStatus, FontNumeralAlignment.Tabular);
        // Leave room for the widest clock face so a transition from minutes to hours also stays in place.
        ClaudeNotificationStatus.MinWidth = jobs.Count > 1 ? 60 : 34;

        if (expanded)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var job in jobs)
            {
                var row = new Grid { Height = 20 };
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
                var title = CodexDetailText(job.Title, AllowanceWhite);
                title.Margin = new Thickness(0, 0, 7, 0);
                var model = CodexDetailText(job.Account is { Length: > 0 } slot ? $"{job.ModelFamily} · {slot}" : job.ModelFamily, CodexJobLabel);
                model.MaxWidth = 90;
                Grid.SetColumn(model, 1);
                var clock = CodexDetailText(CodexJob.FormatElapsed(now - job.StartedAt), CodexJobLabel);
                clock.TextAlignment = TextAlignment.Right;
                Typography.SetNumeralAlignment(clock, FontNumeralAlignment.Tabular);
                Grid.SetColumn(clock, 2);
                row.Children.Add(title);
                row.Children.Add(model);
                row.Children.Add(clock);
                CodexJobDetails.Children.Add(row);
                _codexDetailClocks.Add((job, clock));
            }
        }
        CodexJobDetails.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        CodexJobDetails.Margin = new Thickness(14, _collapsedHeight - 3, 14, 0);
        NotchPillRow.Height = _collapsedHeight - 6;
        NotchPillRow.VerticalAlignment = VerticalAlignment.Top;
        FitNotificationPill(expanded ? jobs.Count * 20 + 6 : 0, expanded ? 300 : 0);
    }

    private TextBlock CodexDetailText(string text, Brush ink) => new()
    {
        Text = text, Foreground = ink, FontFamily = ClaudeNotificationTitle.FontFamily,
        FontSize = ClaudeNotificationTitle.FontSize, VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis
    };

    private void ClearCodexJobDetails()
    {
        _codexShownJobs = null;
        _codexHoverTimer?.Stop();
        _codexDetailClocks.Clear();
        CodexJobDetails.Children.Clear();
        CodexJobDetails.Visibility = Visibility.Collapsed;
        NotchPillRow.Height = double.NaN;
        NotchPillRow.VerticalAlignment = VerticalAlignment.Center;
        ClaudeNotificationStatus.MinWidth = 0;
        Typography.SetNumeralAlignment(ClaudeNotificationStatus, FontNumeralAlignment.Normal);
    }

    private void ClearCodexPill()
    {
        if (_codexPillId == null) return;
        _codexPillId = null;
        ClearCodexJobDetails();
        ClaudeNotificationStatus.Foreground = DefaultPillStatus;
        ToolTipService.SetIsEnabled(ClaudeNotificationTitle, true);
        ToolTipService.SetIsEnabled(ClaudeNotificationStatus, true);
    }

    private static DrawingImage CreateCodexJobIcon()
    {
        var ink = new Pen(CodexJobMist, 1.2) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var icon = new DrawingImage(new GeometryDrawing(null, ink, Geometry.Parse(
            "M4,11.5 C1,11.5 .7,7.2 2.8,6 C2.5,3.4 5,1.7 7,3 C9.1,1.1 12,2.8 11.7,5.3 C14.6,6.5 14.2,11.5 11,11.5 Z M4.8,6.7 L6.4,8.1 L4.8,9.5 M8,9.5 L10,9.5")));
        icon.Freeze();
        return icon;
    }
}
