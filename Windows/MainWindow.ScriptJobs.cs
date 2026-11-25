using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using VNotch.Controllers;
using VNotch.Services;

namespace VNotch;

public partial class MainWindow
{
    private static readonly DependencyProperty JobRingLevelProperty = DependencyProperty.Register(
        "JobRingLevel", typeof(double), typeof(MainWindow), new PropertyMetadata(0d, (d, e) =>
            ((MainWindow)d).BubbleJobArc.Data = TimerArcGeometry((double)e.NewValue)));
    private static readonly System.Windows.Media.Brush ScriptJobSand = FrozenBrush(0xCB, 0xB6, 0x8E);

    private readonly string? _scriptJobsDirectory;
    private ScriptJobWatcher? _scriptJobWatcher;
    private ScriptJobStatus? RunningScriptJob => _scriptJobWatcher?.Running.FirstOrDefault();
    private DispatcherTimer? _scriptJobDetailClock;
    private (string Id, DateTimeOffset? Start, double? Progress)? _scriptRingTarget;
    private readonly List<ScriptJobStatus> _scriptJobPending = new();
    private readonly HashSet<(string Id, DateTimeOffset? Start, DateTimeOffset End)> _scriptJobSeen = new();
    private ScriptJobStatus? _scriptJobPill;
    private ScriptJobStatus? _scriptFailureToRestore;
    private bool _scriptPillQueued;

    private bool IsScriptJobPillShown => _isClaudeNotificationVisible && _scriptJobPill != null;
    private bool IsJobPillShown => IsCodexJobPillShown || IsScriptJobPillShown;

    private void InitializeScriptJobs()
    {
        _compactPillArbiter.Released += QueueScriptJobPill;
        NotchWrapper.PreviewMouseRightButtonUp += DismissScriptJobFromRightClick;
    }

    private void ApplyScriptJobSetting()
    {
        if (_settings.ShowScriptJobsInNotch)
        {
            if (_scriptJobWatcher != null) return;
            _scriptJobWatcher = new(Dispatcher, _scriptJobsDirectory);
            _scriptJobWatcher.Changed += SyncScriptJobs;
        }
        else if (_scriptJobWatcher != null)
        {
            _scriptJobWatcher.Dispose();
            _scriptJobWatcher = null;
            _scriptJobPending.Clear();
            _scriptFailureToRestore = null;
            if (IsScriptJobPillShown) AnimateClaudeNotificationOut();
            ArrangeLiveActivities(_currentMediaInfo, applyCompact: true);
        }
    }

    private void DisposeScriptJobs()
    {
        _compactPillArbiter.Released -= QueueScriptJobPill;
        _scriptJobWatcher?.Dispose();
        _scriptJobDetailClock?.Stop();
    }

    private void SyncScriptJobs()
    {
        if (_cleanedUp || _scriptJobWatcher == null) return;
        var now = DateTimeOffset.UtcNow;
        _scriptJobSeen.RemoveWhere(result => result.End < now - ScriptJobStatus.MaxAge);
        foreach (var result in _scriptJobWatcher.Results)
        {
            if (!result.IsFreshResult(now) ||
                !_scriptJobSeen.Add((result.Id, result.StartedAt, result.EndedAt!.Value))) continue;

            // The final write can arrive after the PID check has reported a stop.
            if (!result.Stopped)
            {
                bool SupersedesStop(ScriptJobStatus job) => job.Stopped && job.Id == result.Id && job.StartedAt == result.StartedAt;
                _scriptJobPending.RemoveAll(job => SupersedesStop(job));
                if (_scriptFailureToRestore is { } failure && SupersedesStop(failure)) _scriptFailureToRestore = null;
                if (_scriptJobPill is { } shown && SupersedesStop(shown)) AnimateClaudeNotificationOut();
            }
            _scriptJobPending.Add(result);
        }
        if (RunningScriptJob != null || _scriptJobPending.Count > 0) WakeFromIdle();
        ArrangeLiveActivities(_currentMediaInfo, applyCompact: true);
        QueueScriptJobPill();
    }

    private void QueueScriptJobPill()
    {
        if (_cleanedUp || _scriptPillQueued || (_scriptJobPending.Count == 0 && _scriptFailureToRestore == null)) return;
        _scriptPillQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _scriptPillQueued = false;
            RefreshScriptJobPill();
        });
    }

    private void RefreshScriptJobPill()
    {
        if (_cleanedUp || _scriptJobWatcher == null || !IsVisible ||
            _isClaudeNotificationVisible || _compactPillArbiter.ActiveSlot != CompactPillSlot.None ||
            _spotlightMorphOwnsNotchVisibility || _isClaudeWaiting || IsCountdownCompletionVisualActive) return;
        var now = DateTimeOffset.UtcNow;
        if (_claudeDone.Exists(note => note.Until > now.UtcDateTime)) return;
        _scriptJobPending.RemoveAll(job => !job.IsFreshResult(now));
        var result = _scriptFailureToRestore ?? _scriptJobPending.FirstOrDefault();
        if (result == null) return;

        bool failed = result.State == ScriptJobState.Failed;
        var (titleKey, glyph, mark) = result.State switch
        {
            ScriptJobState.Failed => ("scriptJobs.failed", "\uE711", CodexJobRose),
            ScriptJobState.Warning => ("scriptJobs.warning", "\uE7BA", ScriptJobSand),
            _ => ("scriptJobs.done", "\uE73E", CodexJobSage)
        };
        if (!ShowCompactNotification(Loc.Get(titleKey, result.Title),
                result.Stopped ? Loc.Get("scriptJobs.stopped") : result.Detail ?? "", null,
                isClaude: false, tap: null, doneSessionId: null, glyph, failed ? null : CodexDoneLifetime, mark,
                scriptJob: result)) return;
        _scriptJobPending.Remove(result);
        if (failed) _scriptFailureToRestore = result;
    }

    private void DismissScriptJobFromRightClick(object sender, MouseButtonEventArgs e)
    {
        if (!IsScriptJobPillShown || IsSpeakerDragSwitch) return;
        e.Handled = true;
        DismissScriptJobPill(openLog: false);
    }

    private void DismissScriptJobPill(bool openLog)
    {
        var log = _scriptJobPill?.Log;
        _scriptFailureToRestore = null;
        AnimateClaudeNotificationOut();
        if (!openLog || log == null) return;

        // The status file supplies data for the pill. Only local log or text files go to the shell.
        try
        {
            if (!Path.IsPathFullyQualified(log) || LocalPathPolicy.IsUncPath(log) || !File.Exists(log)) return;
            if (!string.Equals(Path.GetExtension(log), ".log", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(Path.GetExtension(log), ".txt", StringComparison.OrdinalIgnoreCase)) return;
            Process.Start(new ProcessStartInfo(log) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException)
        {
            RuntimeLog.Warn("SCRIPT-JOBS", $"Could not open job log: {ex.Message}");
        }
    }

    private (string Title, string Phase, string Trailing) ScriptJobWords(ScriptJobStatus job)
    {
        bool backup = string.Equals(job.Icon, "backup", StringComparison.OrdinalIgnoreCase);
        BubbleJobGlyph.Text = backup ? "\uE753" : "\uE756";
        BubbleJobUploadGlyph.Visibility = backup ? Visibility.Visible : Visibility.Collapsed;
        var target = (job.Id, job.StartedAt, job.Progress);
        if (_scriptRingTarget != target)
        {
            double fraction = job.Progress ?? 0.18;
            if (job.Progress.HasValue && _scriptRingTarget is { } previous &&
                (previous.Id, previous.Start) == (job.Id, job.StartedAt) && ActivityBubble.IsVisible)
                Motion.To(this, JobRingLevelProperty, fraction, Motion.Shift);
            else
                Motion.Stop(this, JobRingLevelProperty, fraction);
            _scriptRingTarget = target;
        }

        string percent = job.Progress is { } progress ? Loc.Get("scriptJobs.percent", progress * 100) : "";
        string elapsed = _bubbleOpen && job.StartedAt is { } started
            ? Loc.Get("scriptJobs.elapsed", Math.Max(0, (int)(DateTimeOffset.UtcNow - started).TotalMinutes)) : "";
        string trailing = percent.Length > 0 && elapsed.Length > 0 ? percent + "\n" + elapsed : percent + elapsed;
        return (job.Title, job.Phase ?? "", trailing);
    }

    private void UpdateScriptJobDetailClock()
    {
        if (!_bubbleOpen || !_bubbleShown || !ActivityBubble.IsVisible ||
            _bubbleActivity != LiveActivity.Job || RunningScriptJob?.StartedAt == null)
        {
            _scriptJobDetailClock?.Stop();
            return;
        }
        if (_scriptJobDetailClock == null)
        {
            _scriptJobDetailClock = new(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMinutes(1) };
            _scriptJobDetailClock.Tick += (_, _) => UpdateBubbleContent();
        }
        _scriptJobDetailClock.Start();
    }
}
