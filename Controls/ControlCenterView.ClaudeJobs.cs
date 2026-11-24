using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using VNotch.Services;
using VNotch.Services.ClaudeCode;

namespace VNotch.Controls;

public partial class ControlCenterView
{
    private const double CodexJobsHeaderHeight = 34;
    private const string CodexJobMembership = "codex-jobs";
    private static readonly Brush CodexJobSummary = Frozen("#8CFFFFFF");
    private readonly Dictionary<string, CodexJobRow> _codexJobRows = new(StringComparer.Ordinal);
    private IReadOnlyList<CodexJob> _runningCodexJobs = [];
    private IReadOnlyList<CodexJobResult> _finishedCodexJobs = [];
    private StackPanel _codexJobs = null!;
    private TextBlock _codexJobsHeading = null!;
    private double _codexJobsHeightLimit;

    internal void SetCodexJobList(IReadOnlyList<CodexJob> running, IReadOnlyList<CodexJobResult> finished, double heightLimit)
    {
        if (_runningCodexJobs.SequenceEqual(running) && _finishedCodexJobs.SequenceEqual(finished) &&
            _codexJobsHeightLimit == heightLimit) return;
        _runningCodexJobs = running;
        _finishedCodexJobs = finished;
        _codexJobsHeightLimit = heightLimit;
        if (_accountsOpen && IsVisible) BuildClaudeSection();
    }

    private void BuildCodexJobList()
    {
        _codexJobsHeading = AccountText("", 14, weight: 570);
        var header = new Border
        {
            Height = CodexJobsHeaderHeight, BorderBrush = AccountLine, BorderThickness = new Thickness(0, 1, 0, 0),
            Child = _codexJobsHeading
        };
        _codexJobs = new StackPanel
        {
            Tag = CodexJobMembership, VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed,
            Children = { header }
        };
        _accountBack.Children.Add(_codexJobs);
    }

    private double PaintCodexJobList(DateTimeOffset now, double accountsBottom, double rowHeight)
    {
        if (!_accountsOpen || !IsVisible) return 0;

        int capacity = 0;
        if (_runningCodexJobs.Count > 0 || _finishedCodexJobs.Count > 0)
        {
            double room = _codexJobsHeightLimit - accountsBottom - CodexJobsHeaderHeight - 6;
            double detailsHeight = 0;
            foreach (var detail in _claudeSection.Children.OfType<FrameworkElement>().Skip(1))
            {
                detail.Measure(new Size(ClaudeFaceWidth, double.PositiveInfinity));
                detailsHeight += detail.DesiredSize.Height;
            }
            // Large session cards can already scroll. Keep at least one job visible when the shell has room for it.
            double reserved = Math.Min(detailsHeight, Math.Max(0, room - rowHeight));
            capacity = Math.Max(0, (int)Math.Floor((room - reserved) / rowHeight));
        }
        var running = _runningCodexJobs.DistinctBy(job => job.Id).Take(capacity).ToArray();
        var finished = _finishedCodexJobs.Where(result => result.EndedAt > now - CodexJobWatcher.HistoryLifetime &&
                running.All(job => job.Id != result.Job.Id))
            .DistinctBy(result => result.Job.Id).Take(Math.Min(3, capacity - running.Length));
        var jobs = running.Select(job => (Job: job, Result: (CodexJobResult?)null))
            .Concat(finished.Select(result => (result.Job, Result: (CodexJobResult?)result))).ToArray();

        var ids = jobs.Select(item => item.Job.Id).ToHashSet(StringComparer.Ordinal);
        bool membershipChanged = Seen.Changed(CodexJobMembership, string.Concat(ids.Order(StringComparer.Ordinal)
            .Select(id => FormattableString.Invariant($"{id.Length}:{id}"))));
        foreach (string id in _codexJobRows.Keys.Where(id => !ids.Contains(id)).ToArray())
        {
            var row = _codexJobRows[id];
            row.Stop();
            _codexJobs.Children.Remove(row.Host);
            _codexJobRows.Remove(id);
            Seen.Forget(CodexJobMembership + ":" + id);
        }

        _codexJobs.Visibility = jobs.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (jobs.Length == 0) return 0;

        _codexJobs.Margin = new Thickness(17, accountsBottom, 17, 0);
        _codexJobsHeading.Text = Say("Codex-Aufträge", "Codex jobs");
        for (int i = 0; i < jobs.Length; i++)
        {
            var (job, result) = jobs[i];
            bool added = !_codexJobRows.TryGetValue(job.Id, out var row);
            if (row == null) _codexJobRows[job.Id] = row = new CodexJobRow();
            int index = i + 1;
            if (index >= _codexJobs.Children.Count || _codexJobs.Children[index] != row.Host)
            {
                if (!added) _codexJobs.Children.Remove(row.Host);
                _codexJobs.Children.Insert(index, row.Host);
            }
            row.Host.Height = rowHeight;
            string state = result == null ? "running" : result.TimedOut ? "timeout" : result.Exit == 0 ? "success" : "failed";
            bool changed = Seen.Changed(CodexJobMembership + ":" + job.Id, state);
            row.Update(job, result, now);
            if (!AnimationConfig.ReduceMotion)
            {
                if (changed) row.SwapResult();
                if (added && membershipChanged) row.SlideIn();
            }
        }
        return CodexJobsHeaderHeight + jobs.Length * rowHeight;
    }

    private void StopCodexJobMotion()
    {
        foreach (var row in _codexJobRows.Values) row.Stop();
    }

    private sealed class CodexJobRow
    {
        private readonly TranslateTransform _slide = new();
        private readonly TextBlock _title = AccountText("", 14, weight: 570);
        private readonly TextBlock _detail = AccountText("", 12, CodexJobSummary);
        private readonly TextBlock _clock = AccountText("", 12, weight: 550);
        private readonly Image _codex = new() { Source = MainWindow.CodexJobIcon, Width = 14, Height = 14 };
        private readonly TextBlock _mark = new()
        {
            FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        private readonly Grid _glyph = new() { Width = 18, VerticalAlignment = VerticalAlignment.Center };
        private DateTimeOffset? _startedAt;

        public Grid Host { get; } = new() { Tag = "codex-job-row" };

        public CodexJobRow()
        {
            Host.RenderTransform = _slide;
            Host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            Host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
            var rule = new Border { Background = AccountLine, Height = 1, VerticalAlignment = VerticalAlignment.Top };
            Grid.SetColumnSpan(rule, 3);
            Host.Children.Add(rule);
            _glyph.HorizontalAlignment = HorizontalAlignment.Left;
            _glyph.Children.Add(_codex);
            _glyph.Children.Add(_mark);
            Host.Children.Add(_glyph);

            _title.TextTrimming = _detail.TextTrimming = TextTrimming.CharacterEllipsis;
            var text = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center, Children = { _title, _detail }
            };
            Grid.SetColumn(text, 1);
            Host.Children.Add(text);
            _clock.HorizontalAlignment = HorizontalAlignment.Right;
            Typography.SetNumeralAlignment(_clock, FontNumeralAlignment.Tabular);
            Grid.SetColumn(_clock, 2);
            Host.Children.Add(_clock);
        }

        public void Update(CodexJob job, CodexJobResult? result, DateTimeOffset now)
        {
            _title.Text = job.Title;
            _startedAt = result == null ? job.StartedAt : null;
            _codex.Visibility = result == null ? Visibility.Visible : Visibility.Collapsed;
            _mark.Visibility = result == null ? Visibility.Collapsed : Visibility.Visible;
            if (result == null)
            {
                _detail.Text = string.IsNullOrWhiteSpace(job.Account) ? job.ModelFamily : $"{job.ModelFamily} · {job.Account}";
                UpdateClock(now);
                AutomationProperties.SetName(_glyph, Say("Läuft", "Running"));
            }
            else
            {
                bool ok = result.Exit == 0 && !result.TimedOut;
                _mark.Text = ok ? "\uE73E" : "\uE711";
                _mark.Foreground = ok ? AccountSage : AccountRose;
                _clock.Text = CodexJob.FormatElapsed(result.Duration);
                _detail.Text = string.Join(" ", (result.Summary ?? "").Split(['\r', '\n', '\u2028', '\u2029'],
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                AutomationProperties.SetName(_glyph, result.TimedOut ? Say("Zeitlimit erreicht", "Timed out") :
                    ok ? Say("Abgeschlossen", "Completed") : Say("Fehlgeschlagen", "Failed"));
            }
            _detail.Visibility = _detail.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        public void UpdateClock(DateTimeOffset now)
        {
            if (_startedAt is not { } started) return;
            var elapsed = now - started;
            _clock.Text = elapsed.TotalHours < 1 ? CodexJob.FormatElapsed(elapsed) : FormatElapsed(elapsed);
        }

        public void SlideIn() => Motion.To(_slide, TranslateTransform.XProperty, 0, Motion.Shift, from: 8);

        public void SwapResult()
        {
            Motion.To(_glyph, OpacityProperty, 1, Motion.Exit, from: .45);
            Motion.To(_clock, OpacityProperty, 1, Motion.Exit, from: .45);
        }

        public void Stop()
        {
            Motion.Stop(_slide, TranslateTransform.XProperty, 0);
            Motion.Stop(_glyph, OpacityProperty, 1);
            Motion.Stop(_clock, OpacityProperty, 1);
        }
    }
}
